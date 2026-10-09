using System;
using System.Collections.Generic;

namespace Mdb2Mdb
{
    /// <summary>标准中的数据类型。</summary>
    public enum SpecType
    {
        Text,
        Long,
        Float,
        Double,
        Date
    }

    /// <summary>一条属性项定义（B.2 属性项定义与内容要求）。</summary>
    public sealed class FieldSpec
    {
        public FieldSpec(string name, string meaning, SpecType type, bool nullable, int? length, int? scale)
        {
            Name = name;
            Meaning = meaning;
            Type = type;
            Nullable = nullable;
            Length = length;
            Scale = scale;
        }

        public string Name { get; private set; }
        public string Meaning { get; private set; }
        public SpecType Type { get; private set; }
        public bool Nullable { get; private set; }

        /// <summary>长度。TEXT 为字符数；数值型为总位数（mdb 中仅能作说明，无法存储）。</summary>
        public int? Length { get; private set; }

        /// <summary>小数位数，表中为“—”的为 null。</summary>
        public int? Scale { get; private set; }

        /// <summary>DATE 类型的显示格式（表中“长度”列 yyyy/mm/dd hh:mm:ss，Access 中分钟写作 nn）。</summary>
        public const string DateFormat = "yyyy/mm/dd hh:nn:ss";

        public string Describe()
        {
            switch (Type)
            {
                case SpecType.Text: return "TEXT(" + Length + ")";
                case SpecType.Long: return "LONG";
                case SpecType.Float: return Scale.HasValue ? "FLOAT(小数" + Scale + "位)" : "FLOAT";
                case SpecType.Double: return Scale.HasValue ? "DOUBLE(小数" + Scale + "位)" : "DOUBLE";
                case SpecType.Date: return "DATE";
                default: return Type.ToString();
            }
        }
    }

    /// <summary>图片 1 中的属性项定义表，按属性项名字母顺序排列。</summary>
    public static class FieldSpecs
    {
        /// <summary>图片 2 红框中需要删除的字段。</summary>
        public static readonly string[] FieldsToDelete = { "Shape_Length", "Shape_Area" };

        public static readonly FieldSpec[] All =
        {
            new FieldSpec("ANGLE", "角度", SpecType.Float, true, 4, 1),
            new FieldSpec("BG", "比高", SpecType.Float, true, 3, 1),
            new FieldSpec("BNO", "界桩号", SpecType.Text, true, 20, null),
            new FieldSpec("BRGLEV", "层数", SpecType.Text, true, 4, null),
            new FieldSpec("CLASS", "地名分类码", SpecType.Text, false, 3, null),
            new FieldSpec("ELEV", "高程值", SpecType.Double, true, 9, 3),
            new FieldSpec("GB", "分类代码", SpecType.Long, false, 6, null),
            new FieldSpec("HYDC", "水系名称代码", SpecType.Text, true, 8, null),
            new FieldSpec("ITYPE", "群岛或列岛名称代码", SpecType.Long, true, 3, null),
            new FieldSpec("KM", "公里数", SpecType.Long, true, 3, null),
            new FieldSpec("KV", "电压值", SpecType.Text, true, 8, null),
            new FieldSpec("LANE", "车道数", SpecType.Long, true, 2, null),
            new FieldSpec("LENGTH", "长度", SpecType.Long, true, 4, null),
            new FieldSpec("MATRL", "铺设材料", SpecType.Text, true, 6, null),
            new FieldSpec("NAME", "名称", SpecType.Text, true, 60, null),
            new FieldSpec("NAMEID", "名称代码", SpecType.Text, true, 20, null),
            new FieldSpec("PAC", "行政区划代码", SpecType.Long, true, 9, null),
            new FieldSpec("PASS", "通行情况", SpecType.Text, true, 12, null),
            new FieldSpec("PERIOD", "时段", SpecType.Text, true, 10, null),
            new FieldSpec("PINYIN", "汉语拼音", SpecType.Text, true, 120, null),
            new FieldSpec("PN", "点号", SpecType.Text, true, 6, null),
            new FieldSpec("RDPAC", "道路行政归属", SpecType.Long, true, 6, null),
            new FieldSpec("RN", "道路编码", SpecType.Text, true, 20, null),
            new FieldSpec("RTEG", "公路技术等级", SpecType.Text, true, 4, null),
            new FieldSpec("SAREA", "面积", SpecType.Double, true, 12, null),
            new FieldSpec("SDTF", "单/双行线", SpecType.Text, true, 2, null),
            new FieldSpec("SPEED", "速度", SpecType.Float, true, 3, 1),
            new FieldSpec("TEGR", "测量控制点等级", SpecType.Text, true, 4, null),
            new FieldSpec("FTIME", "时间", SpecType.Date, true, null, null),
            new FieldSpec("TYPE", "类型", SpecType.Text, true, 20, null),
            new FieldSpec("TYPE2", "类型", SpecType.Text, true, 20, null),
            new FieldSpec("USAGE", "用途", SpecType.Text, true, 20, null),
            new FieldSpec("VOL", "库容量", SpecType.Long, true, 5, null),
            new FieldSpec("WEIGHT", "载重", SpecType.Long, true, 2, null),
            new FieldSpec("WIDTH", "宽度", SpecType.Float, true, 3, 1),
            new FieldSpec("WQL", "水质", SpecType.Text, true, 4, null),
            new FieldSpec("XZNAME", "所属乡镇名", SpecType.Text, true, 60, null),
        };

        // Access 字段名不区分大小写，这里同样不区分
        private static readonly Dictionary<string, FieldSpec> ByName = BuildIndex();

        private static Dictionary<string, FieldSpec> BuildIndex()
        {
            var d = new Dictionary<string, FieldSpec>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in All) d.Add(s.Name, s);
            return d;
        }

        public static FieldSpec Find(string fieldName)
        {
            FieldSpec s;
            return ByName.TryGetValue(fieldName, out s) ? s : null;
        }

        public static bool IsFieldToDelete(string fieldName)
        {
            foreach (var n in FieldsToDelete)
                if (string.Equals(n, fieldName, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }
}
