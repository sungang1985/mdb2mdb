using System;
using System.Collections.Generic;

namespace Mdb2Mdb
{
    /// <summary>从 DAO 读到的字段信息。</summary>
    public sealed class ColumnInfo
    {
        public string Name;
        public int DaoType;
        public int Size;
        public int Attributes;

        /// <summary>Access 字段的“必需”属性，true 表示不允许为空。</summary>
        public bool Required;

        public bool IsAutoNumber
        {
            get { return (Attributes & Dao.dbAutoIncrField) != 0; }
        }

        public bool IsTextLike
        {
            get { return DaoType == Dao.dbText || DaoType == Dao.dbMemo || DaoType == Dao.dbChar; }
        }
    }

    /// <summary>对一个字段要做的处理。</summary>
    public sealed class FieldPlan
    {
        public ColumnInfo Column;

        /// <summary>为 null 表示该字段需要删除。</summary>
        public FieldSpec Spec;

        public bool Delete
        {
            get { return Spec == null; }
        }

        /// <summary>数据类型或文本长度与标准不一致。</summary>
        public bool NeedTypeChange;
    }

    /// <summary>按标准设置“是否允许为空”的判定结果。</summary>
    public sealed class NullabilityDecision
    {
        /// <summary>需要把字段的“必需”属性设为 RequiredValue。</summary>
        public bool SetRequired;
        public bool RequiredValue;

        /// <summary>处理后字段实际是否必需（不允许为空）。</summary>
        public bool FinalRequired;

        /// <summary>按标准应不允许为空，但存在空值，未能设置。</summary>
        public bool Failed;

        /// <summary>与原始状态相比是否有变化（计入“修改是否允许为空”）。</summary>
        public bool Changed;

        /// <summary>日志说明，无变化时为 null。</summary>
        public string Note;
    }

    public static class SchemaPlanner
    {
        /// <summary>
        /// 判断表是否需要处理：跳过 Access 系统表、ArcGIS 地理数据库系统表（GDB_*）、
        /// 空间索引表（*_Shape_Index）以及链接表。
        /// </summary>
        public static bool ShouldProcessTable(string name, int attributes, string connect)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if ((attributes & Dao.dbSystemObject) != 0) return false;
            if ((attributes & (Dao.dbAttachedTable | Dao.dbAttachedODBC)) != 0) return false;
            if (!string.IsNullOrEmpty(connect)) return false;
            if (name.StartsWith("MSys", StringComparison.OrdinalIgnoreCase)) return false;
            if (name.StartsWith("~", StringComparison.Ordinal)) return false;
            if (name.StartsWith("GDB_", StringComparison.OrdinalIgnoreCase)) return false;
            if (name.EndsWith("_Shape_Index", StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        public static List<FieldPlan> Plan(IEnumerable<ColumnInfo> columns)
        {
            var plans = new List<FieldPlan>();
            foreach (var c in columns)
            {
                if (FieldSpecs.IsFieldToDelete(c.Name))
                {
                    plans.Add(new FieldPlan { Column = c, Spec = null });
                    continue;
                }

                var spec = FieldSpecs.Find(c.Name);
                if (spec == null) continue;

                plans.Add(new FieldPlan
                {
                    Column = c,
                    Spec = spec,
                    NeedTypeChange = !Matches(c, spec)
                });
            }
            return plans;
        }

        /// <summary>
        /// 决定如何按标准设置“是否允许为空”。
        /// </summary>
        /// <param name="originalRequired">输入文件中字段原来的“必需”属性</param>
        /// <param name="currentRequired">当前（修改类型之后）的“必需”属性；逐条转换类型时会被临时清除</param>
        /// <param name="specNullable">标准中是否允许为空</param>
        /// <param name="nullCount">当前字段中的空值记录数（仅在需要设为不允许为空时才有意义）</param>
        public static NullabilityDecision DecideNullability(bool originalRequired, bool currentRequired, bool specNullable, int nullCount)
        {
            var d = new NullabilityDecision { FinalRequired = currentRequired };
            bool wanted = !specNullable;
            if (currentRequired != wanted)
            {
                if (wanted && nullCount > 0)
                {
                    d.Failed = true;
                }
                else
                {
                    d.SetRequired = true;
                    d.RequiredValue = wanted;
                    d.FinalRequired = wanted;
                }
            }

            // 日志与计数以输入文件的原始状态为准，而不是类型转换后的中间状态
            d.Changed = d.FinalRequired != originalRequired;
            if (d.Changed)
                d.Note = "允许为空：" + (originalRequired ? "否 → 是" : "是 → 否") + (d.Failed ? "（存在空值，未达标准）" : "");
            return d;
        }

        public static bool Matches(ColumnInfo c, FieldSpec spec)
        {
            if (c.DaoType != TargetDaoType(spec.Type)) return false;
            if (spec.Type == SpecType.Text && c.Size != spec.Length.Value) return false;
            return true;
        }

        public static int TargetDaoType(SpecType t)
        {
            switch (t)
            {
                case SpecType.Text: return Dao.dbText;
                case SpecType.Long: return Dao.dbLong;
                case SpecType.Float: return Dao.dbSingle;
                case SpecType.Double: return Dao.dbDouble;
                case SpecType.Date: return Dao.dbDate;
                default: throw new ArgumentOutOfRangeException("t");
            }
        }

        /// <summary>Jet SQL（DAO / ANSI-89）中的类型写法。注意 Jet 的 FLOAT 是双精度，单精度要写 SINGLE。</summary>
        public static string JetSqlType(FieldSpec spec)
        {
            switch (spec.Type)
            {
                case SpecType.Text: return "TEXT(" + spec.Length.Value + ")";
                case SpecType.Long: return "LONG";
                case SpecType.Float: return "SINGLE";
                case SpecType.Double: return "DOUBLE";
                case SpecType.Date: return "DATETIME";
                default: throw new ArgumentOutOfRangeException("spec");
            }
        }

        /// <summary>ArcGIS 地理数据库中对应的字段类型（写入 GDB_Items 定义 XML）。</summary>
        public static string EsriFieldType(SpecType t)
        {
            switch (t)
            {
                case SpecType.Text: return "esriFieldTypeString";
                case SpecType.Long: return "esriFieldTypeInteger";
                case SpecType.Float: return "esriFieldTypeSingle";
                case SpecType.Double: return "esriFieldTypeDouble";
                case SpecType.Date: return "esriFieldTypeDate";
                default: throw new ArgumentOutOfRangeException("t");
            }
        }

        /// <summary>日志中显示的字段类型，与标准表的写法保持一致。</summary>
        public static string DescribeDaoType(int daoType, int size)
        {
            switch (daoType)
            {
                case Dao.dbText: return "TEXT(" + size + ")";
                case Dao.dbChar: return "CHAR(" + size + ")";
                case Dao.dbMemo: return "MEMO";
                case Dao.dbLong: return "LONG";
                case Dao.dbInteger: return "SHORT";
                case Dao.dbByte: return "BYTE";
                case Dao.dbSingle: return "FLOAT";
                case Dao.dbDouble: return "DOUBLE";
                case Dao.dbDate: return "DATE";
                case Dao.dbCurrency: return "CURRENCY";
                case Dao.dbBoolean: return "BOOLEAN";
                case Dao.dbDecimal: return "DECIMAL";
                case Dao.dbNumeric: return "NUMERIC";
                case Dao.dbBigInt: return "BIGINT";
                case Dao.dbGUID: return "GUID";
                case Dao.dbLongBinary: return "OLE";
                case Dao.dbBinary:
                case Dao.dbVarBinary: return "BINARY";
                default: return "类型" + daoType;
            }
        }
    }
}
