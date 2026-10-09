using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Mdb2Mdb
{
    public sealed class ProcessSummary
    {
        public int TablesScanned;
        public int FieldsMatched;
        public int FieldsAltered;
        public int FieldsDeleted;
        public int PropertiesSet;
        public int NullabilitySet;
        public int NullabilityFailed;
        public int LengthRulesSet;
        public int LengthRulesFailed;
        public int GdbDefinitionsUpdated;
    }

    /// <summary>
    /// 复制输入 mdb 到输出路径，在输出文件上：
    /// 1. 遍历所有用户表，字段名与标准属性项同名的，修改为标准的数据类型/长度，设置小数位数，
    ///    并按标准设置是否允许为空；
    /// 2. 删除 Shape_Length、Shape_Area 字段；
    /// 3. 若为 ArcGIS 个人地理数据库，同步修改 GDB_Items 中的要素类定义；
    /// 4. 压缩数据库。
    /// 输入文件不做任何修改。
    /// </summary>
    public sealed class MdbProcessor
    {
        private const string TempColumn = "MDB2MDB_TMP";

        private readonly Logger _log;
        private readonly ProcessSummary _summary = new ProcessSummary();
        private object _db;

        public MdbProcessor(Logger log)
        {
            _log = log;
        }

        public static string DefaultOutputPath(string input)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(input));
            return Path.Combine(dir, Path.GetFileNameWithoutExtension(input) + "_更新.mdb");
        }

        public static string LogPathFor(string output)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(output));
            return Path.Combine(dir, Path.GetFileNameWithoutExtension(output) + "_处理日志.txt");
        }

        public ProcessSummary Run(string input, string output, bool compact)
        {
            input = Path.GetFullPath(input);
            output = Path.GetFullPath(output);

            if (!File.Exists(input))
                throw new FileNotFoundException("输入文件不存在：" + input);
            if (string.Equals(input, output, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("输出文件不能与输入文件相同。");

            _log.Info("输入文件：" + input);
            _log.Info("输出文件：" + output);
            if (File.Exists(Path.ChangeExtension(input, ".ldb")))
                _log.Warn("检测到输入文件的锁文件(.ldb)，文件可能正被 ArcMap/Access 打开，建议关闭后再处理。");

            // Jet 只认系统 ANSI 代码页能表示的路径，先在 ASCII 文件名的工作副本上处理，
            // 全部成功后再移动到输出路径
            string work = WorkPathFor(output);
            File.Copy(input, work, true);
            File.SetAttributes(work, File.GetAttributes(work) & ~FileAttributes.ReadOnly);

            object engine = null;
            try
            {
                string progId;
                engine = Dao.CreateEngine(out progId);
                _log.Info("数据库引擎：" + progId);

                _db = Dao.Call(engine, "OpenDatabase", work, true, false);
                try
                {
                    ProcessDatabase();
                }
                finally
                {
                    Dao.Call(_db, "Close");
                    Dao.Release(_db);
                    _db = null;
                    // 释放所有残留的 DAO 对象，确保文件已关闭再压缩
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                }

                if (compact) Compact(engine, work);

                if (File.Exists(output)) File.Delete(output);
                File.Move(work, output);
            }
            catch
            {
                // 处理中途出错时不留下不完整的文件
                TryDelete(work);
                throw;
            }
            finally
            {
                Dao.Release(engine);
            }

            _log.Info("");
            _log.Info(string.Format(
                "完成：检查表 {0} 个，匹配标准字段 {1} 个，修改类型/长度 {2} 个，设置数值长度限制 {3} 个{4}，设置小数位数/格式 {5} 处，" +
                "修改是否允许为空 {6} 个{7}，删除字段 {8} 个，同步 GDB 要素类定义 {9} 个；警告 {10} 条，错误 {11} 条。",
                _summary.TablesScanned, _summary.FieldsMatched, _summary.FieldsAltered,
                _summary.LengthRulesSet,
                _summary.LengthRulesFailed > 0 ? "（另有 " + _summary.LengthRulesFailed + " 个因已有数据超长未能设置）" : "",
                _summary.PropertiesSet,
                _summary.NullabilitySet,
                _summary.NullabilityFailed > 0 ? "（另有 " + _summary.NullabilityFailed + " 个因存在空值未能设为不允许为空）" : "",
                _summary.FieldsDeleted, _summary.GdbDefinitionsUpdated, _log.Warnings, _log.Errors));

            return _summary;
        }

        // ---------------------------------------------------------------- 遍历

        private void ProcessDatabase()
        {
            var tableNames = new List<string>();
            bool hasGdbItems = false;

            object tableDefs = Dao.Get(_db, "TableDefs");
            int n = Dao.Count(tableDefs);
            for (int i = 0; i < n; i++)
            {
                object td = Dao.Item(tableDefs, i);
                string name = (string)Dao.Get(td, "Name");
                int attributes = Convert.ToInt32(Dao.Get(td, "Attributes"));
                string connect = Dao.Get(td, "Connect") as string;
                if (string.Equals(name, "GDB_Items", StringComparison.OrdinalIgnoreCase)) hasGdbItems = true;
                if (SchemaPlanner.ShouldProcessTable(name, attributes, connect)) tableNames.Add(name);
            }

            if (hasGdbItems) _log.Info("识别为 ArcGIS 个人地理数据库，将同步更新 GDB_Items 中的要素类定义。");

            // 每个表处理后的结果，用于同步 GDB_Items 中的要素类定义
            var results = new Dictionary<string, TableResult>(StringComparer.OrdinalIgnoreCase);

            foreach (var table in tableNames)
            {
                _summary.TablesScanned++;
                var result = new TableResult();
                ProcessTable(table, result);
                if (!result.IsEmpty) results[table] = result;
            }

            if (hasGdbItems && results.Count > 0)
                UpdateGdbDefinitions(results);
        }

        private sealed class TableResult
        {
            /// <summary>字段名 → 新的 esriFieldType</summary>
            public readonly Dictionary<string, string> TypeChanges = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            /// <summary>已删除的字段</summary>
            public readonly List<string> Deleted = new List<string>();

            /// <summary>标准字段处理后实际的“是否允许为空”</summary>
            public readonly Dictionary<string, bool> IsNullable = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            public bool IsEmpty
            {
                get { return TypeChanges.Count == 0 && Deleted.Count == 0 && IsNullable.Count == 0; }
            }
        }

        private void ProcessTable(string table, TableResult result)
        {
            var columns = ReadColumns(table);
            var plans = SchemaPlanner.Plan(columns);

            _log.Info("");
            if (plans.Count == 0)
            {
                _log.Info("[" + table + "] 无标准属性项字段，跳过");
                return;
            }
            _log.Info("[" + table + "]");

            foreach (var plan in plans)
            {
                var c = plan.Column;
                try
                {
                    if (plan.Delete)
                    {
                        DropColumn(table, c.Name);
                        result.Deleted.Add(c.Name);
                        _summary.FieldsDeleted++;
                        _log.Info("  " + c.Name + "：已删除");
                        continue;
                    }

                    _summary.FieldsMatched++;
                    var spec = plan.Spec;
                    string before = SchemaPlanner.DescribeDaoType(c.DaoType, c.Size);
                    var notes = new List<string>();

                    if (c.IsAutoNumber)
                    {
                        _log.Warn(table + "." + c.Name + " 是自动编号字段，不修改类型。");
                    }
                    else if (plan.NeedTypeChange)
                    {
                        ChangeType(table, c, spec);
                        result.TypeChanges[c.Name] = SchemaPlanner.EsriFieldType(spec.Type);
                        _summary.FieldsAltered++;
                        notes.Add(before + " → " + spec.Describe());
                    }
                    else
                    {
                        notes.Add(before + " 已符合");
                    }

                    if (spec.Scale.HasValue && (spec.Type == SpecType.Float || spec.Type == SpecType.Double))
                    {
                        if (SetFieldProperty(table, c.Name, "DecimalPlaces", Dao.dbByte, (byte)spec.Scale.Value))
                        {
                            _summary.PropertiesSet++;
                            notes.Add("小数位数设为 " + spec.Scale.Value);
                        }
                    }
                    else if (spec.Type == SpecType.Date)
                    {
                        if (SetFieldProperty(table, c.Name, "Format", Dao.dbText, FieldSpec.DateFormat))
                        {
                            _summary.PropertiesSet++;
                            notes.Add("格式设为 " + FieldSpec.DateFormat);
                        }
                    }

                    string lengthNote = ApplyLengthRule(table, c, spec);
                    if (lengthNote != null) notes.Add(lengthNote);

                    string nullNote = ApplyNullability(table, c, spec, result);
                    if (nullNote != null) notes.Add(nullNote);

                    _log.Info("  " + c.Name + "：" + string.Join("；", notes.ToArray()));
                }
                catch (Exception ex)
                {
                    _log.Error(table + "." + c.Name + " 处理失败：" + ex.Message);
                }
            }
        }

        private List<ColumnInfo> ReadColumns(string table)
        {
            var list = new List<ColumnInfo>();
            object fields = Dao.Get(TableDef(table), "Fields");
            int n = Dao.Count(fields);
            for (int i = 0; i < n; i++)
            {
                object f = Dao.Item(fields, i);
                list.Add(new ColumnInfo
                {
                    Name = (string)Dao.Get(f, "Name"),
                    DaoType = Convert.ToInt32(Dao.Get(f, "Type")),
                    Size = Convert.ToInt32(Dao.Get(f, "Size")),
                    Attributes = Convert.ToInt32(Dao.Get(f, "Attributes")),
                    Required = (bool)Dao.Get(f, "Required")
                });
            }
            return list;
        }

        // ---------------------------------------------------------------- 删除字段

        private void DropColumn(string table, string column)
        {
            EnsureNotInRelation(table, column);
            foreach (var idx in DropIndexesContaining(table, column))
                _log.Info("  （索引 " + idx.Name + " 包含字段 " + column + "，已随字段一同删除）");
            Execute("ALTER TABLE " + Dao.Q(table) + " DROP COLUMN " + Dao.Q(column));
        }

        // ---------------------------------------------------------------- 修改类型

        private void ChangeType(string table, ColumnInfo c, FieldSpec spec)
        {
            EnsureNotInRelation(table, c.Name);

            var saved = FieldProps.Capture(Field(table, c.Name));
            string sqlType = SchemaPlanner.JetSqlType(spec);

            // 先只读预检：没有任何值会丢失/改变时才让 Jet 直接 ALTER COLUMN，
            // 否则由程序逐条转换，并在日志中写明受影响的记录数
            var issues = ScanConversion(table, c.Name, spec);
            if (issues.Total > 0)
                _log.Warn(table + "." + c.Name + " 转换为 " + spec.Describe() + " 时有数据变化：" + issues.Describe());

            var indexes = DropIndexesContaining(table, c.Name);
            try
            {
                bool done = false;
                if (issues.Total == 0)
                {
                    try
                    {
                        Execute("ALTER TABLE " + Dao.Q(table) + " ALTER COLUMN " + Dao.Q(c.Name) + " " + sqlType);
                        done = true;
                    }
                    catch (Exception ex)
                    {
                        _log.Info("  （" + c.Name + " 直接修改类型失败：" + ex.Message + "，改为逐条转换数据）");
                    }
                }

                if (!done) ConvertViaTempColumn(table, c.Name, spec, sqlType, saved.Required);

                saved.Restore(Field(table, c.Name), c.IsTextLike && spec.Type == SpecType.Text, _log, table + "." + c.Name);
            }
            finally
            {
                RecreateIndexes(table, indexes);
            }
        }

        private sealed class IssueCounts
        {
            public int Truncated, Rounded, Invalid;

            public int Total
            {
                get { return Truncated + Rounded + Invalid; }
            }

            public void Add(ConversionIssue issue)
            {
                if (issue == ConversionIssue.Truncated) Truncated++;
                else if (issue == ConversionIssue.Rounded) Rounded++;
                else if (issue == ConversionIssue.Invalid) Invalid++;
            }

            public string Describe()
            {
                var parts = new List<string>();
                if (Truncated > 0) parts.Add(Truncated + " 个值超长被截断");
                if (Rounded > 0) parts.Add(Rounded + " 个小数被四舍五入为整数");
                if (Invalid > 0) parts.Add(Invalid + " 个值无法转换，置为空值");
                return string.Join("，", parts.ToArray());
            }
        }

        /// <summary>只读扫描，统计转换后会丢失/改变的值。</summary>
        private IssueCounts ScanConversion(string table, string column, FieldSpec spec)
        {
            var counts = new IssueCounts();
            object rs = Dao.Call(_db, "OpenRecordset",
                "SELECT " + Dao.Q(column) + " FROM " + Dao.Q(table) + " WHERE " + Dao.Q(column) + " IS NOT NULL",
                Dao.dbOpenDynaset);
            try
            {
                object field = Dao.Item(Dao.Get(rs, "Fields"), 0);
                while (!(bool)Dao.Get(rs, "EOF"))
                {
                    ConversionIssue issue;
                    ValueConverter.Convert(Dao.Get(field, "Value"), spec.Type, spec.Length, out issue);
                    counts.Add(issue);
                    Dao.Call(rs, "MoveNext");
                }
            }
            finally
            {
                Dao.Call(rs, "Close");
                Dao.Release(rs);
            }
            return counts;
        }

        /// <summary>
        /// 由程序逐条转换：新增临时字段写入转换后的值 → 原字段清空后改类型（保持字段位置）
        /// → 写回 → 删除临时字段。
        /// </summary>
        private void ConvertViaTempColumn(string table, string column, FieldSpec spec, string sqlType, bool wasRequired)
        {
            string tmp = TempColumn;
            for (int i = 1; ColumnExists(table, tmp); i++) tmp = TempColumn + i;

            Execute("ALTER TABLE " + Dao.Q(table) + " ADD COLUMN " + Dao.Q(tmp) + " " + sqlType);

            object rs = Dao.Call(_db, "OpenRecordset",
                "SELECT " + Dao.Q(column) + ", " + Dao.Q(tmp) + " FROM " + Dao.Q(table) + " WHERE " + Dao.Q(column) + " IS NOT NULL",
                Dao.dbOpenDynaset);
            try
            {
                object fields = Dao.Get(rs, "Fields");
                object src = Dao.Item(fields, 0);
                object dst = Dao.Item(fields, 1);
                while (!(bool)Dao.Get(rs, "EOF"))
                {
                    ConversionIssue issue;
                    object v = ValueConverter.Convert(Dao.Get(src, "Value"), spec.Type, spec.Length, out issue);
                    if (!(v is DBNull))
                    {
                        Dao.Call(rs, "Edit");
                        Dao.Set(dst, "Value", v);
                        Dao.Call(rs, "Update");
                    }
                    Dao.Call(rs, "MoveNext");
                }
            }
            finally
            {
                Dao.Call(rs, "Close");
                Dao.Release(rs);
            }

            if (wasRequired) Dao.Set(Field(table, column), "Required", false);
            Execute("UPDATE " + Dao.Q(table) + " SET " + Dao.Q(column) + " = NULL");
            try
            {
                Execute("ALTER TABLE " + Dao.Q(table) + " ALTER COLUMN " + Dao.Q(column) + " " + sqlType);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "修改类型失败（" + ex.Message + "），该字段在输出文件中已被清空，转换后的数据保留在临时字段 " + tmp + " 中。", ex);
            }
            Execute("UPDATE " + Dao.Q(table) + " SET " + Dao.Q(column) + " = " + Dao.Q(tmp));
            Execute("ALTER TABLE " + Dao.Q(table) + " DROP COLUMN " + Dao.Q(tmp));
        }

        /// <summary>
        /// 修改类型后需要恢复的字段属性（Jet 的 ALTER COLUMN 会重置这些属性）。
        /// “必需”属性不在此恢复，由 ApplyNullability 按标准设置。
        /// </summary>
        private sealed class FieldProps
        {
            private bool _required;
            private bool _allowZeroLength;
            private string _defaultValue;
            private readonly Dictionary<string, object> _custom = new Dictionary<string, object>();
            private static readonly string[] CustomNames = { "Description", "Caption" };

            public bool Required
            {
                get { return _required; }
            }

            public static FieldProps Capture(object field)
            {
                var p = new FieldProps();
                p._required = (bool)Dao.Get(field, "Required");
                p._allowZeroLength = TryGetBool(field, "AllowZeroLength");
                p._defaultValue = Dao.Get(field, "DefaultValue") as string;
                foreach (var name in CustomNames)
                {
                    object v = TryGetProperty(field, name);
                    if (v != null && !(v is DBNull)) p._custom[name] = v;
                }
                return p;
            }

            public void Restore(object field, bool textToText, Logger log, string label)
            {
                if (textToText) TrySet(field, "AllowZeroLength", _allowZeroLength, log, label);
                if (!string.IsNullOrEmpty(_defaultValue)) TrySet(field, "DefaultValue", _defaultValue, log, label);
                foreach (var kv in _custom)
                {
                    try
                    {
                        if (TryGetProperty(field, kv.Key) == null)
                        {
                            object props = Dao.Get(field, "Properties");
                            Dao.Call(props, "Append", Dao.Call(field, "CreateProperty", kv.Key, Dao.dbText, kv.Value));
                        }
                    }
                    catch (Exception ex)
                    {
                        log.Warn(label + " 的 " + kv.Key + " 属性未能保留：" + ex.Message);
                    }
                }
            }

            private static void TrySet(object field, string name, object value, Logger log, string label)
            {
                try
                {
                    object cur = Dao.Get(field, name);
                    if (!Equals(cur, value)) Dao.Set(field, name, value);
                }
                catch (Exception ex)
                {
                    log.Warn(label + " 的 " + name + " 属性未能恢复：" + ex.Message);
                }
            }

            private static bool TryGetBool(object field, string name)
            {
                try { return (bool)Dao.Get(field, name); }
                catch (DaoException) { return false; }
            }
        }

        // ---------------------------------------------------------------- 是否允许为空

        /// <summary>
        /// 按标准设置字段的“必需”属性（不允许为空 = 必需）。已有空值的字段无法设为不允许为空，
        /// 保持允许为空并给出警告。返回日志说明，与输入相比无变化时返回 null。
        /// </summary>
        private string ApplyNullability(string table, ColumnInfo c, FieldSpec spec, TableResult result)
        {
            object field = Field(table, c.Name);
            bool current = (bool)Dao.Get(field, "Required");
            int nulls = !spec.Nullable && !current ? CountWhere(table, Dao.Q(c.Name) + " IS NULL") : 0;
            var d = SchemaPlanner.DecideNullability(c.Required, current, spec.Nullable, nulls);

            if (d.SetRequired) Dao.Set(field, "Required", d.RequiredValue);
            if (d.Failed)
            {
                _summary.NullabilityFailed++;
                _log.Warn(table + "." + c.Name + " 按标准不允许为空，但有 " + nulls + " 条记录为空值" +
                          (c.Required ? "（原数据中无法转换为新类型的值已被置空）" : "") +
                          "，未能设为不允许为空（请补全数据后重新处理）。");
            }
            else if (d.Changed)
            {
                _summary.NullabilitySet++;
            }

            result.IsNullable[c.Name] = !d.FinalRequired;
            return d.Note;
        }

        // ---------------------------------------------------------------- 数值长度限制

        /// <summary>
        /// 数值型字段在 Access/Jet 中没有“长度”属性，用字段的有效性规则限制数值位数
        /// （Access 录入、ArcGIS 编辑时都由 Jet 引擎强制检查）。已有数据超出范围时不设置并给出警告。
        /// 返回日志说明，无改动时返回 null。
        /// </summary>
        private string ApplyLengthRule(string table, ColumnInfo c, FieldSpec spec)
        {
            string rule = SchemaPlanner.LengthValidationRule(spec);
            if (rule == null) return null;

            object field = Field(table, c.Name);
            string current = Dao.Get(field, "ValidationRule") as string ?? "";
            if (current == rule) return null;

            string max = SchemaPlanner.MaxAbsValue(spec).Value.ToString(CultureInfo.InvariantCulture);
            string col = Dao.Q(c.Name);
            int over = CountWhere(table, col + " < -" + max + " OR " + col + " > " + max);
            if (over > 0)
            {
                _summary.LengthRulesFailed++;
                _log.Warn(table + "." + c.Name + " 有 " + over + " 条记录超出标准长度（" + SchemaPlanner.LengthDescription(spec) +
                          "），未能设置长度限制（请修正数据后重新处理）。");
                return null;
            }

            Dao.Set(field, "ValidationRule", rule);
            Dao.Set(field, "ValidationText", SchemaPlanner.LengthValidationText(spec));
            _summary.LengthRulesSet++;
            return "长度限制：" + SchemaPlanner.LengthDescription(spec) +
                   (current.Length > 0 ? "（替换原有效性规则 " + current + "）" : "");
        }

        private int CountWhere(string table, string where)
        {
            object rs = Dao.Call(_db, "OpenRecordset",
                "SELECT COUNT(*) FROM " + Dao.Q(table) + " WHERE " + where, Dao.dbOpenSnapshot);
            try
            {
                return Convert.ToInt32(Dao.Get(Dao.Item(Dao.Get(rs, "Fields"), 0), "Value"));
            }
            finally
            {
                Dao.Call(rs, "Close");
                Dao.Release(rs);
            }
        }

        // ---------------------------------------------------------------- 字段属性（小数位数/格式）

        /// <summary>设置 Access 字段属性（如“小数位数”DecimalPlaces、“格式”Format），返回是否有改动。</summary>
        private bool SetFieldProperty(string table, string column, string name, int daoType, object value)
        {
            object field = Field(table, column);
            object current = TryGetProperty(field, name);
            if (current != null)
            {
                if (Convert.ToString(current) == Convert.ToString(value)) return false;
                Dao.Set(Dao.Item(Dao.Get(field, "Properties"), name), "Value", value);
                return true;
            }
            object prop = Dao.Call(field, "CreateProperty", name, daoType, value);
            Dao.Call(Dao.Get(field, "Properties"), "Append", prop);
            return true;
        }

        /// <summary>读取 DAO 对象的属性值，属性不存在时返回 null。</summary>
        private static object TryGetProperty(object daoObject, string name)
        {
            try
            {
                return Dao.Get(Dao.Item(Dao.Get(daoObject, "Properties"), name), "Value");
            }
            catch (DaoException ex)
            {
                if (ex.DaoErrorNumber == Dao.ErrPropertyNotFound) return null;
                throw;
            }
        }

        // ---------------------------------------------------------------- 索引与关系

        private sealed class IndexDef
        {
            public string Name;
            public bool Primary, Unique, IgnoreNulls, Required;
            public readonly List<KeyValuePair<string, int>> Fields = new List<KeyValuePair<string, int>>();
        }

        private List<IndexDef> DropIndexesContaining(string table, string column)
        {
            var result = new List<IndexDef>();
            object indexes = Dao.Get(TableDef(table), "Indexes");
            int n = Dao.Count(indexes);
            for (int i = 0; i < n; i++)
            {
                object idx = Dao.Item(indexes, i);
                var def = new IndexDef
                {
                    Name = (string)Dao.Get(idx, "Name"),
                    Primary = (bool)Dao.Get(idx, "Primary"),
                    Unique = (bool)Dao.Get(idx, "Unique"),
                    IgnoreNulls = (bool)Dao.Get(idx, "IgnoreNulls"),
                    Required = (bool)Dao.Get(idx, "Required")
                };
                bool foreign = (bool)Dao.Get(idx, "Foreign");
                bool contains = false;
                object fields = Dao.Get(idx, "Fields");
                int fc = Dao.Count(fields);
                for (int j = 0; j < fc; j++)
                {
                    object f = Dao.Item(fields, j);
                    string fname = (string)Dao.Get(f, "Name");
                    def.Fields.Add(new KeyValuePair<string, int>(fname, Convert.ToInt32(Dao.Get(f, "Attributes"))));
                    if (string.Equals(fname, column, StringComparison.OrdinalIgnoreCase)) contains = true;
                }
                if (!contains) continue;
                if (foreign)
                    throw new InvalidOperationException("字段属于外键索引 " + def.Name + "，无法修改。");
                result.Add(def);
            }

            foreach (var def in result)
                Execute("DROP INDEX " + Dao.Q(def.Name) + " ON " + Dao.Q(table));
            return result;
        }

        private void RecreateIndexes(string table, List<IndexDef> indexes)
        {
            foreach (var def in indexes)
            {
                try
                {
                    object td = TableDef(table);
                    object idx = Dao.Call(td, "CreateIndex", def.Name);
                    object idxFields = Dao.Get(idx, "Fields");
                    foreach (var f in def.Fields)
                    {
                        object fld = Dao.Call(idx, "CreateField", f.Key);
                        if (f.Value != 0) Dao.Set(fld, "Attributes", f.Value);
                        Dao.Call(idxFields, "Append", fld);
                    }
                    Dao.Set(idx, "Primary", def.Primary);
                    Dao.Set(idx, "Unique", def.Unique);
                    Dao.Set(idx, "IgnoreNulls", def.IgnoreNulls);
                    Dao.Set(idx, "Required", def.Required);
                    Dao.Call(Dao.Get(td, "Indexes"), "Append", idx);
                }
                catch (Exception ex)
                {
                    _log.Warn(table + " 的索引 " + def.Name + " 重建失败：" + ex.Message);
                }
            }
        }

        private void EnsureNotInRelation(string table, string column)
        {
            object relations = Dao.Get(_db, "Relations");
            int n = Dao.Count(relations);
            for (int i = 0; i < n; i++)
            {
                object rel = Dao.Item(relations, i);
                string primary = (string)Dao.Get(rel, "Table");
                string foreign = (string)Dao.Get(rel, "ForeignTable");
                object fields = Dao.Get(rel, "Fields");
                int fc = Dao.Count(fields);
                for (int j = 0; j < fc; j++)
                {
                    object f = Dao.Item(fields, j);
                    bool hit =
                        (string.Equals(primary, table, StringComparison.OrdinalIgnoreCase) &&
                         string.Equals((string)Dao.Get(f, "Name"), column, StringComparison.OrdinalIgnoreCase)) ||
                        (string.Equals(foreign, table, StringComparison.OrdinalIgnoreCase) &&
                         string.Equals((string)Dao.Get(f, "ForeignName"), column, StringComparison.OrdinalIgnoreCase));
                    if (hit)
                        throw new InvalidOperationException("字段参与表间关系 " + Dao.Get(rel, "Name") + "，无法修改。");
                }
            }
        }

        // ---------------------------------------------------------------- GDB 元数据

        private void UpdateGdbDefinitions(Dictionary<string, TableResult> results)
        {
            _log.Info("");
            _log.Info("[GDB_Items] 同步要素类定义");
            object rs = Dao.Call(_db, "OpenRecordset",
                "SELECT [Name], [PhysicalName], [Definition] FROM [GDB_Items] WHERE [Definition] IS NOT NULL",
                Dao.dbOpenDynaset);
            try
            {
                object fields = Dao.Get(rs, "Fields");
                object fName = Dao.Item(fields, "Name");
                object fPhysical = Dao.Item(fields, "PhysicalName");
                object fDef = Dao.Item(fields, "Definition");
                while (!(bool)Dao.Get(rs, "EOF"))
                {
                    string table = Dao.Get(fPhysical, "Value") as string;
                    if (string.IsNullOrEmpty(table)) table = Dao.Get(fName, "Value") as string;

                    TableResult result;
                    if (results.TryGetValue(table ?? "", out result))
                    {
                        try
                        {
                            string xml = Dao.Get(fDef, "Value") as string;
                            string patched = GdbDefinitionPatcher.Patch(xml, result.TypeChanges, result.Deleted, result.IsNullable);
                            if (patched != xml)
                            {
                                Dao.Call(rs, "Edit");
                                Dao.Set(fDef, "Value", patched);
                                Dao.Call(rs, "Update");
                                _summary.GdbDefinitionsUpdated++;
                                _log.Info("  " + table + "：已更新");
                            }
                        }
                        catch (Exception ex)
                        {
                            _log.Error("GDB_Items 中 " + table + " 的定义更新失败：" + ex.Message);
                        }
                    }
                    Dao.Call(rs, "MoveNext");
                }
            }
            finally
            {
                Dao.Call(rs, "Close");
                Dao.Release(rs);
            }
        }

        // ---------------------------------------------------------------- 压缩

        private void Compact(object engine, string path)
        {
            string tmp = Path.Combine(Path.GetDirectoryName(path),
                Path.GetFileNameWithoutExtension(path) + "_compact.mdb");
            try
            {
                TryDelete(tmp);
                Dao.Call(engine, "CompactDatabase", path, tmp);
            }
            catch (Exception ex)
            {
                TryDelete(tmp);
                _log.Warn("压缩数据库失败（不影响处理结果）：" + ex.Message);
                return;
            }
            File.Delete(path);
            File.Move(tmp, path);
            _log.Info("");
            _log.Info("数据库已压缩。");
        }

        /// <summary>与输出文件同目录（该目录路径 Jet 无法识别时用系统临时目录）的 ASCII 文件名工作副本。</summary>
        private static string WorkPathFor(string output)
        {
            string dir = Path.GetDirectoryName(output);
            if (!IsAnsiSafe(dir)) dir = Path.GetTempPath();
            return Path.Combine(dir, "mdb2mdb_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".mdb");
        }

        private static bool IsAnsiSafe(string path)
        {
            var ansi = System.Text.Encoding.Default;
            return ansi.GetString(ansi.GetBytes(path)) == path;
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        // ---------------------------------------------------------------- 工具

        private void Execute(string sql)
        {
            Dao.Call(_db, "Execute", sql, Dao.dbFailOnError);
        }

        /// <summary>取最新的 TableDef（DDL 之后需要刷新集合）。</summary>
        private object TableDef(string table)
        {
            object tableDefs = Dao.Get(_db, "TableDefs");
            Dao.Call(tableDefs, "Refresh");
            object td = Dao.Item(tableDefs, table);
            Dao.Call(Dao.Get(td, "Fields"), "Refresh");
            Dao.Call(Dao.Get(td, "Indexes"), "Refresh");
            return td;
        }

        private object Field(string table, string column)
        {
            return Dao.Item(Dao.Get(TableDef(table), "Fields"), column);
        }

        private bool ColumnExists(string table, string column)
        {
            foreach (var c in ReadColumns(table))
                if (string.Equals(c.Name, column, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }
}
