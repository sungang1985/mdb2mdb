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
