using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Mdb2Mdb.E2E
{
    internal static class Program
    {
        private const int dbOpenSnapshot = 4;
        private const string dbLangGeneral = ";LANGID=0x0409;CP=1252;COUNTRY=0";
        private const int dbVersion40 = 64;

        private static readonly List<string> Failures = new List<string>();
        private static object _engine;

        private const string RoadXml =
            "<DEFeatureClassInfo xsi:type='typens:DEFeatureClassInfo' xmlns:xsi='http://www.w3.org/2001/XMLSchema-instance' " +
            "xmlns:xs='http://www.w3.org/2001/XMLSchema' xmlns:typens='http://www.esri.com/schemas/ArcGIS/10.1'>" +
            "<CatalogPath>\\ROAD</CatalogPath><Name>ROAD</Name><HasOID>true</HasOID><OIDFieldName>ObjectID</OIDFieldName>" +
            "<GPFieldInfoExs xsi:type='typens:ArrayOfGPFieldInfoEx'>" +
            "<GPFieldInfoEx xsi:type='typens:GPFieldInfoEx'><Name>ObjectID</Name><ModelName>ObjectID</ModelName><FieldType>esriFieldTypeOID</FieldType><IsNullable>false</IsNullable><Required>true</Required><Editable>false</Editable></GPFieldInfoEx>" +
            "<GPFieldInfoEx xsi:type='typens:GPFieldInfoEx'><Name>Shape</Name><ModelName>Shape</ModelName><FieldType>esriFieldTypeGeometry</FieldType><IsNullable>true</IsNullable><Required>true</Required></GPFieldInfoEx>" +
            "<GPFieldInfoEx xsi:type='typens:GPFieldInfoEx'><Name>GB</Name><AliasName>分类代码</AliasName><ModelName>GB</ModelName><FieldType>esriFieldTypeString</FieldType><IsNullable>true</IsNullable></GPFieldInfoEx>" +
            "<GPFieldInfoEx xsi:type='typens:GPFieldInfoEx'><Name>NAME</Name><AliasName>名称</AliasName><ModelName>NAME</ModelName><FieldType>esriFieldTypeString</FieldType><IsNullable>true</IsNullable></GPFieldInfoEx>" +
            "<GPFieldInfoEx xsi:type='typens:GPFieldInfoEx'><Name>ANGLE</Name><ModelName>ANGLE</ModelName><FieldType>esriFieldTypeDouble</FieldType><IsNullable>true</IsNullable></GPFieldInfoEx>" +
            "<GPFieldInfoEx xsi:type='typens:GPFieldInfoEx'><Name>CLASS</Name><ModelName>CLASS</ModelName><FieldType>esriFieldTypeString</FieldType><IsNullable>true</IsNullable></GPFieldInfoEx>" +
            "<GPFieldInfoEx xsi:type='typens:GPFieldInfoEx'><Name>Shape_Length</Name><ModelName>Shape_Length</ModelName><FieldType>esriFieldTypeDouble</FieldType><IsNullable>true</IsNullable><Required>true</Required><Editable>false</Editable></GPFieldInfoEx>" +
            "<GPFieldInfoEx xsi:type='typens:GPFieldInfoEx'><Name>Shape_Area</Name><ModelName>Shape_Area</ModelName><FieldType>esriFieldTypeDouble</FieldType><IsNullable>true</IsNullable><Required>true</Required><Editable>false</Editable></GPFieldInfoEx>" +
            "</GPFieldInfoExs><ShapeType>esriGeometryPolygon</ShapeType><ShapeFieldName>Shape</ShapeFieldName>" +
            "<AreaFieldName>Shape_Area</AreaFieldName><LengthFieldName>Shape_Length</LengthFieldName></DEFeatureClassInfo>";

        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("用法：Mdb2Mdb.E2E.exe <mdb2mdb.exe> <工作目录>");
                return 2;
            }
            // 让 CI 日志中的中文正常显示
            try { Console.OutputEncoding = new UTF8Encoding(false); } catch (IOException) { }

            string exe = Path.GetFullPath(args[0]);
            string dir = Path.GetFullPath(args[1]);
            Directory.CreateDirectory(dir);

            string input = Path.Combine(dir, "测试输入.mdb");
            string output = Path.Combine(dir, "测试输出.mdb");
            string cliOutput = Path.Combine(dir, "命令行输出.mdb");
            foreach (var f in new[] { input, output, cliOutput }) if (File.Exists(f)) File.Delete(f);

            string progId;
            _engine = Dao.CreateEngine(out progId);
            Console.WriteLine("DAO 引擎：" + progId);

            // 中文文件名：Jet 在非中文系统上无法直接打开，正好检验程序的处理方式
            string build = Path.Combine(dir, "e2e_build.mdb");
            if (File.Exists(build)) File.Delete(build);
            CreateTestDatabase(build);
            File.Move(build, input);
            string hashBefore = Hash(input);

            // 1. 进程内运行处理流程
            var lines = new List<string>();
            var log = new Logger(l => { Console.WriteLine(l); lines.Add(l); });
            var summary = new MdbProcessor(log).Run(input, output, true);
            Check(log.Errors == 0, "处理过程中不应有错误，实际 " + log.Errors);

            // 日志与计数按“输入原始状态 → 最终状态”统计：CLASS 是→否、KV 否→是、LANE 否→是（逐条转换路径），
            // ROAD.GB 因空值未能设置，PLAIN.GB 原本就是不允许为空
            Check(summary.NullabilitySet == 3, "修改是否允许为空应为 3 个，实际 " + summary.NullabilitySet);
            Check(summary.NullabilityFailed == 1, "未能设为不允许为空应为 1 个，实际 " + summary.NullabilityFailed);
            Check(lines.Contains("  LANE：DOUBLE → LONG；允许为空：否 → 是"), "LANE 日志应记录 否 → 是");
            Check(lines.Contains("  CLASS：TEXT(3) 已符合；允许为空：是 → 否"), "CLASS 日志应记录 是 → 否");
            Check(lines.Contains("  GB：DOUBLE → LONG；长度限制：最多 6 位数字"), "PLAIN.GB 日志：长度限制，且无允许为空的变化");

            // 数值长度限制：ROAD 的 GB/ANGLE/PAC/WIDTH/LANE 与 PLAIN 的 ELEV/GB 共 7 个；PLAIN.WEIGHT 已有超长数据未能设置
            Check(summary.LengthRulesSet == 7, "设置数值长度限制应为 7 个，实际 " + summary.LengthRulesSet);
            Check(summary.LengthRulesFailed == 1, "未能设置长度限制应为 1 个，实际 " + summary.LengthRulesFailed);
            Check(lines.Contains("  GB：TEXT(10) → LONG；长度限制：最多 6 位数字"), "ROAD.GB 日志应记录长度限制");
            Check(lines.Contains("  ANGLE：DOUBLE → FLOAT(小数1位)；小数位数设为 1；长度限制：最多 3 位整数、1 位小数"), "ANGLE 日志应记录长度限制");
            Check(Hash(input) == hashBefore, "输入文件不应被修改");
            Verify(output);

            // 2. 命令行方式运行 exe
            var psi = new ProcessStartInfo(exe, "--cli \"" + input + "\" \"" + cliOutput + "\"") { UseShellExecute = false };
            using (var p = Process.Start(psi))
            {
                p.WaitForExit(120000);
                Check(p.HasExited && p.ExitCode == 0, "mdb2mdb.exe --cli 返回码应为 0，实际 " + (p.HasExited ? p.ExitCode.ToString() : "未退出"));
            }
            Check(File.Exists(cliOutput), "命令行模式应生成输出文件");
            Check(File.Exists(MdbProcessor.LogPathFor(cliOutput)), "命令行模式应生成日志文件");
            if (File.Exists(cliOutput)) Verify(cliOutput);

            VerifyEnforcement(output);

            Console.WriteLine();
            if (Failures.Count == 0)
            {
                Console.WriteLine("E2E 全部通过");
                return 0;
            }
            Console.WriteLine("E2E 失败 " + Failures.Count + " 项：");
            foreach (var f in Failures) Console.WriteLine("  - " + f);
            return 1;
        }

        private static void CreateTestDatabase(string path)
        {
            object db = Dao.Call(_engine, "CreateDatabase", path, dbLangGeneral, dbVersion40);
            try
            {
                Exec(db, "CREATE TABLE [GDB_Items] ([ObjectID] COUNTER, [Name] TEXT(160), [PhysicalName] TEXT(160), [Type] TEXT(38), [Definition] MEMO)");
                Exec(db, "INSERT INTO [GDB_Items] ([Name], [PhysicalName], [Type], [Definition]) VALUES ('ROAD', 'ROAD', '{70737809-852C-4A03-9E222CECEA5B9BFA}', " + Dao.Str(RoadXml) + ")");

                // 要素类：多种不符合标准的字段
                Exec(db, "CREATE TABLE [ROAD] ([ObjectID] COUNTER CONSTRAINT [PK_ROAD] PRIMARY KEY, [Shape] LONGBINARY, " +
                         "[GB] TEXT(10), [NAME] TEXT(50), [TYPE] TEXT(50), [ANGLE] DOUBLE, [FTIME] TEXT(30), [PAC] DOUBLE, " +
                         "[WIDTH] SINGLE, [LANE] DOUBLE NOT NULL, [KV] LONG NOT NULL, [类型] TEXT(20), [CLASS] TEXT(3), [Shape_Length] DOUBLE, [Shape_Area] DOUBLE)");
                Exec(db, "CREATE INDEX [IX_NAME] ON [ROAD] ([NAME])");
                Exec(db, "CREATE INDEX [IX_SL] ON [ROAD] ([Shape_Length])");
                Exec(db, "INSERT INTO [ROAD] ([GB], [NAME], [TYPE], [ANGLE], [FTIME], [PAC], [WIDTH], [LANE], [KV], [类型], [CLASS], [Shape_Length], [Shape_Area]) " +
                         "VALUES ('660100', '大型', '一二三四五六七八九十一二三四五六七八九十一二三四五', 12.34, '2025/03/08 14:05:09', 321311106, 3.5, 2.5, 220, '甲', 'A01', 357.675449, 8289.822516)");
                Exec(db, "INSERT INTO [ROAD] ([GB], [NAME], [TYPE], [ANGLE], [FTIME], [PAC], [WIDTH], [LANE], [KV], [类型], [CLASS], [Shape_Length], [Shape_Area]) " +
                         "VALUES ('abc', NULL, '短', NULL, NULL, NULL, NULL, 2, 35, NULL, 'B02', 1, 2)");

                // 普通表（非要素类）
                // GB：DOUBLE 且 NOT NULL → 改为 LONG 后仍应为不允许为空
                // WEIGHT：标准长度 2，已有 123 超长 → 不能加长度限制
                Exec(db, "CREATE TABLE [PLAIN] ([ID] LONG, [ELEV] TEXT(20), [GB] DOUBLE NOT NULL, [WEIGHT] LONG, [Shape_Area] DOUBLE)");
                Exec(db, "INSERT INTO [PLAIN] ([ID], [ELEV], [GB], [WEIGHT]) VALUES (1, '12.5', 110101, 5)");
                Exec(db, "INSERT INTO [PLAIN] ([ID], [ELEV], [GB], [WEIGHT]) VALUES (2, '', 110102, 123)");
                Exec(db, "INSERT INTO [PLAIN] ([ID], [ELEV], [GB], [WEIGHT]) VALUES (3, 'n/a', 110103, NULL)");
            }
            finally
            {
                Dao.Call(db, "Close");
                Dao.Release(db);
            }
        }

        private static void Verify(string path)
        {
            Console.WriteLine();
            Console.WriteLine("核对 " + Path.GetFileName(path));
            string copy = Path.Combine(Path.GetTempPath(), "e2e_verify_" + Guid.NewGuid().ToString("N") + ".mdb");
            File.Copy(path, copy);
            object db = Dao.Call(_engine, "OpenDatabase", copy, false, true);
            try
            {
                // 字段顺序、类型、长度
                ExpectFields(db, "ROAD", new[]
                {
                    "ObjectID:LONG", "Shape:OLE", "GB:LONG", "NAME:TEXT(60)", "TYPE:TEXT(20)", "ANGLE:FLOAT",
                    "FTIME:DATE", "PAC:LONG", "WIDTH:FLOAT", "LANE:LONG", "KV:TEXT(8)", "类型:TEXT(20)", "CLASS:TEXT(3)"
                });
                ExpectFields(db, "PLAIN", new[] { "ID:LONG", "ELEV:DOUBLE", "GB:LONG", "WEIGHT:LONG" });
                ExpectFields(db, "GDB_Items", new[] { "ObjectID:LONG", "Name:TEXT(160)", "PhysicalName:TEXT(160)", "Type:TEXT(38)", "Definition:MEMO" });

                // 数据
                var rows = ReadRows(db, "SELECT * FROM [ROAD] ORDER BY [ObjectID]");
                Check(rows.Count == 2, "ROAD 应有 2 条记录");
                if (rows.Count == 2)
                {
                    var r = rows[0];
                    Eq(r["GB"], 660100, "GB 文本转 LONG");
                    Eq(r["NAME"], "大型", "NAME 数据保留");
                    Eq(r["TYPE"], "一二三四五六七八九十一二三四五六七八九十", "TYPE 截断为 20 字");
                    Eq(r["ANGLE"], 12.34f, "ANGLE 转单精度");
                    Eq(r["FTIME"], new DateTime(2025, 3, 8, 14, 5, 9), "FTIME 文本转日期");
                    Eq(r["PAC"], 321311106, "PAC 转 LONG");
                    Eq(r["WIDTH"], 3.5f, "WIDTH 保留");
                    Eq(r["LANE"], 3, "LANE 2.5 四舍五入为 3");
                    Eq(r["KV"], "220", "KV 转文本");
                    Eq(r["类型"], "甲", "非标准字段不变");
                    Eq(rows[1]["GB"], DBNull.Value, "GB 无法转换的值置空");
                    Eq(rows[1]["LANE"], 2, "LANE 整数值保留");
                    Eq(rows[1]["KV"], "35", "KV 第二条");
                }
                var plain = ReadRows(db, "SELECT * FROM [PLAIN] ORDER BY [ID]");
                if (plain.Count == 3)
                {
                    Eq(plain[0]["ELEV"], 12.5, "ELEV 文本转 DOUBLE");
                    Eq(plain[1]["ELEV"], DBNull.Value, "ELEV 空串转空值");
                    Eq(plain[2]["ELEV"], DBNull.Value, "ELEV 非数字置空");
                    Eq(plain[0]["GB"], 110101, "PLAIN.GB 转 LONG");
                }
                else Check(false, "PLAIN 应有 3 条记录");

                // 索引、必填、字段属性
                object road = Dao.Item(Dao.Get(db, "TableDefs"), "ROAD");
                var indexes = new List<string>();
                object idxs = Dao.Get(road, "Indexes");
                for (int i = 0; i < Dao.Count(idxs); i++) indexes.Add((string)Dao.Get(Dao.Item(idxs, i), "Name"));
                Check(indexes.Contains("IX_NAME"), "NAME 上的索引应被重建，现有：" + string.Join(",", indexes.ToArray()));
                Check(indexes.Contains("PK_ROAD"), "主键应保留");
                Check(!indexes.Contains("IX_SL"), "Shape_Length 上的索引应随字段删除");

                // 是否允许为空：GB、CLASS 为“否”，其余为“是”
                object fields = Dao.Get(road, "Fields");
                Check((bool)Dao.Get(Dao.Item(fields, "CLASS"), "Required"), "CLASS 应设为不允许为空");
                Check(!(bool)Dao.Get(Dao.Item(fields, "GB"), "Required"), "ROAD.GB 有空值，应保持允许为空");
                Check(!(bool)Dao.Get(Dao.Item(fields, "KV"), "Required"), "KV 原为必填，应按标准改为允许为空");
                Check(!(bool)Dao.Get(Dao.Item(fields, "NAME"), "Required"), "NAME 应允许为空");
                Check(!(bool)Dao.Get(Dao.Item(fields, "LANE"), "Required"), "LANE 原为必填，逐条转换后应按标准为允许为空");
                Check(!(bool)Dao.Get(Dao.Item(fields, "类型"), "Required"), "非标准字段不受影响");
                Eq(FieldProp(road, "ANGLE", "DecimalPlaces"), (byte)1, "ANGLE 小数位数");
                Eq(FieldProp(road, "WIDTH", "DecimalPlaces"), (byte)1, "WIDTH 小数位数");
                Eq(FieldProp(road, "FTIME", "Format"), FieldSpec.DateFormat, "FTIME 格式");
                object plainTd = Dao.Item(Dao.Get(db, "TableDefs"), "PLAIN");
                Check(FieldProp(plainTd, "ID", "DecimalPlaces") == null, "非标准字段不设置小数位数");
                Check((bool)Dao.Get(Dao.Item(Dao.Get(plainTd, "Fields"), "GB"), "Required"), "PLAIN.GB 改类型后应为不允许为空");
                Eq(Dao.Get(Dao.Item(fields, "GB"), "ValidationRule"), "Is Null Or Between -999999 And 999999", "ROAD.GB 有效性规则");
                Eq(Dao.Get(Dao.Item(fields, "ANGLE"), "ValidationRule"), "Is Null Or Between -999.95 And 999.95", "ROAD.ANGLE 有效性规则");
                Eq(Dao.Get(Dao.Item(fields, "NAME"), "ValidationRule"), "", "文本字段不加有效性规则（长度由字段大小限制）");
                Eq(Dao.Get(Dao.Item(Dao.Get(plainTd, "Fields"), "WEIGHT"), "ValidationRule"), "", "PLAIN.WEIGHT 已有超长数据，不设置有效性规则");

                // GDB 定义
                var items = ReadRows(db, "SELECT [Definition] FROM [GDB_Items] WHERE [PhysicalName] = 'ROAD'");
                string xml = items.Count == 1 ? items[0]["Definition"] as string : null;
                Check(xml != null, "GDB_Items 中应有 ROAD 定义");
                if (xml != null)
                {
                    Check(xml.Contains("<Name>GB</Name><AliasName>分类代码</AliasName><ModelName>GB</ModelName><FieldType>esriFieldTypeInteger</FieldType><IsNullable>true</IsNullable>"), "GB 的 FieldType 应更新为 esriFieldTypeInteger，IsNullable 与实际一致(true)");
                    Check(xml.Contains("<Name>CLASS</Name><ModelName>CLASS</ModelName><FieldType>esriFieldTypeString</FieldType><IsNullable>false</IsNullable>"), "CLASS 的 IsNullable 应更新为 false");
                    Check(xml.Contains("<Name>ANGLE</Name><ModelName>ANGLE</ModelName><FieldType>esriFieldTypeSingle</FieldType>"), "ANGLE 的 FieldType 应更新为 esriFieldTypeSingle");
                    Check(!xml.Contains("<Name>Shape_Length</Name>") && !xml.Contains("<Name>Shape_Area</Name>"), "定义中应删除 Shape_Length/Shape_Area");
                    Check(xml.Contains("<AreaFieldName></AreaFieldName>") && xml.Contains("<LengthFieldName></LengthFieldName>"), "AreaFieldName/LengthFieldName 应清空");
                }
            }
            finally
            {
                Dao.Call(db, "Close");
                Dao.Release(db);
                File.Delete(copy);
            }
        }

        /// <summary>在输出文件的副本上实际录入数据，检验 Jet 是否强制执行长度限制和不允许为空。</summary>
        private static void VerifyEnforcement(string path)
        {
            Console.WriteLine();
            Console.WriteLine("录入检验 " + Path.GetFileName(path));
            string copy = Path.Combine(Path.GetTempPath(), "e2e_enforce_" + Guid.NewGuid().ToString("N") + ".mdb");
            File.Copy(path, copy);
            object db = Dao.Call(_engine, "OpenDatabase", copy, false, false);
            try
            {
                Accepts(db, "INSERT INTO [ROAD] ([GB], [CLASS]) VALUES (720100, 'C01')", "GB 录入 6 位数字");
                Rejects(db, "INSERT INTO [ROAD] ([GB], [CLASS]) VALUES (7201009, 'C02')", "GB 录入 7 位数字");
                Rejects(db, "INSERT INTO [ROAD] ([GB], [CLASS]) VALUES (-7201009, 'C03')", "GB 录入 -7201009");
                Accepts(db, "INSERT INTO [ROAD] ([CLASS]) VALUES ('C04')", "GB 为空（ROAD.GB 有空值，仍允许为空）");
                Accepts(db, "INSERT INTO [ROAD] ([GB], [CLASS], [ANGLE]) VALUES (720100, 'C05', 999.9)", "ANGLE 录入 999.9");
                Rejects(db, "INSERT INTO [ROAD] ([GB], [CLASS], [ANGLE]) VALUES (720100, 'C06', 1000)", "ANGLE 录入 1000");
                Rejects(db, "INSERT INTO [ROAD] ([GB], [CLASS], [LANE]) VALUES (720100, 'C07', 100)", "LANE 录入 3 位数字");
                Rejects(db, "INSERT INTO [ROAD] ([GB]) VALUES (720100)", "CLASS 不允许为空");
                Rejects(db, "INSERT INTO [ROAD] ([GB], [CLASS]) VALUES (720100, 'ABCD')", "CLASS 录入超过 3 个字符");
                Accepts(db, "INSERT INTO [PLAIN] ([ID], [GB], [WEIGHT]) VALUES (9, 110101, 999)", "PLAIN.WEIGHT 未设置限制");

                // 与截图相同的操作：在已有记录上把 GB 改成 7 位数
                object rs = Dao.Call(db, "OpenRecordset", "SELECT [GB] FROM [ROAD] WHERE [GB] = 660100", Dao.dbOpenDynaset);
                try
                {
                    object gb = Dao.Item(Dao.Get(rs, "Fields"), 0);
                    bool rejected = false;
                    string message = null;
                    Dao.Call(rs, "Edit");
                    try
                    {
                        Dao.Set(gb, "Value", 7201009);
                        Dao.Call(rs, "Update");
                    }
                    catch (DaoException ex)
                    {
                        rejected = true;
                        message = ex.Message;
                        try { Dao.Call(rs, "CancelUpdate"); } catch (DaoException) { }
                    }
                    Check(rejected, "在已有记录上把 GB 改为 7201009 应被拒绝" + (message != null ? "（" + message + "）" : ""));
                }
                finally
                {
                    Dao.Call(rs, "Close");
                    Dao.Release(rs);
                }
            }
            finally
            {
                Dao.Call(db, "Close");
                Dao.Release(db);
                File.Delete(copy);
            }
        }

        private static void Accepts(object db, string sql, string what)
        {
            try
            {
                Exec(db, sql);
                Check(true, what + " 应允许");
            }
            catch (DaoException ex)
            {
                Check(false, what + " 应允许，但被拒绝：" + ex.Message);
            }
        }

        private static void Rejects(object db, string sql, string what)
        {
            try
            {
                Exec(db, sql);
                Check(false, what + " 应被拒绝，但录入成功");
            }
            catch (DaoException ex)
            {
                Check(true, what + " 被拒绝：" + ex.Message);
            }
        }

        private static void ExpectFields(object db, string table, string[] expected)
        {
            object fields = Dao.Get(Dao.Item(Dao.Get(db, "TableDefs"), table), "Fields");
            var actual = new List<string>();
            for (int i = 0; i < Dao.Count(fields); i++)
            {
                object f = Dao.Item(fields, i);
                actual.Add((string)Dao.Get(f, "Name") + ":" +
                           SchemaPlanner.DescribeDaoType(Convert.ToInt32(Dao.Get(f, "Type")), Convert.ToInt32(Dao.Get(f, "Size"))));
            }
            string a = string.Join(", ", actual.ToArray()), e = string.Join(", ", expected);
            Check(a == e, table + " 字段应为 [" + e + "]，实际 [" + a + "]");
        }

        private static List<Dictionary<string, object>> ReadRows(object db, string sql)
        {
            var rows = new List<Dictionary<string, object>>();
            object rs = Dao.Call(db, "OpenRecordset", sql, dbOpenSnapshot);
            try
            {
                object fields = Dao.Get(rs, "Fields");
                int n = Dao.Count(fields);
                while (!(bool)Dao.Get(rs, "EOF"))
                {
                    var row = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < n; i++)
                    {
                        object f = Dao.Item(fields, i);
                        row[(string)Dao.Get(f, "Name")] = Dao.Get(f, "Value");
                    }
                    rows.Add(row);
                    Dao.Call(rs, "MoveNext");
                }
            }
            finally
            {
                Dao.Call(rs, "Close");
                Dao.Release(rs);
            }
            return rows;
        }

        private static object FieldProp(object tableDef, string field, string prop)
        {
            try
            {
                object f = Dao.Item(Dao.Get(tableDef, "Fields"), field);
                return Dao.Get(Dao.Item(Dao.Get(f, "Properties"), prop), "Value");
            }
            catch (DaoException)
            {
                return null;
            }
        }

        private static void Exec(object db, string sql)
        {
            Dao.Call(db, "Execute", sql, Dao.dbFailOnError);
        }

        private static void Eq(object actual, object expected, string what)
        {
            Check(Equals(actual, expected),
                what + "：期望 " + Show(expected) + "，实际 " + Show(actual));
        }

        private static string Show(object o)
        {
            if (o == null) return "null";
            if (o is DBNull) return "Null";
            return o + " (" + o.GetType().Name + ")";
        }

        private static void Check(bool ok, string what)
        {
            Console.WriteLine((ok ? "  [通过] " : "  [失败] ") + what);
            if (!ok) Failures.Add(what);
        }

        private static string Hash(string path)
        {
            using (var md5 = MD5.Create())
            using (var s = File.OpenRead(path))
                return BitConverter.ToString(md5.ComputeHash(s));
        }
    }
}
