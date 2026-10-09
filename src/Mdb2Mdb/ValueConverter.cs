using System;
using System.Globalization;

namespace Mdb2Mdb
{
    public enum ConversionIssue
    {
        None,
        /// <summary>文本超长被截断。</summary>
        Truncated,
        /// <summary>小数转整数时被四舍五入。</summary>
        Rounded,
        /// <summary>无法转换（非数字、超出范围、无效日期等），结果为空值。</summary>
        Invalid
    }

    /// <summary>
    /// 字段类型转换时逐个值的转换规则。用于先预检数据是否会丢失，
    /// 以及在 Jet 无法直接 ALTER COLUMN 时由程序自行转换数据。
    /// </summary>
    public static class ValueConverter
    {
        private static readonly string[] DateFormats =
        {
            "yyyy/M/d H:m:s", "yyyy-M-d H:m:s", "yyyy/M/d", "yyyy-M-d",
            "yyyyMMddHHmmss", "yyyyMMdd", "yyyy.M.d", "yyyy年M月d日"
        };

        /// <summary>转换一个值。null / DBNull / 空白字符串转换为 DBNull。</summary>
        public static object Convert(object value, SpecType type, int? length, out ConversionIssue issue)
        {
            issue = ConversionIssue.None;
            if (value == null || value is DBNull) return DBNull.Value;

            switch (type)
            {
                case SpecType.Text:
                    {
                        string s;
                        if (!TryToText(value, out s))
                        {
                            issue = ConversionIssue.Invalid;
                            return DBNull.Value;
                        }
                        if (length.HasValue && s.Length > length.Value)
                        {
                            s = s.Substring(0, length.Value);
                            issue = ConversionIssue.Truncated;
                        }
                        return s;
                    }

                case SpecType.Long:
                    {
                        double d;
                        if (IsBlank(value)) return DBNull.Value;
                        if (!TryToDouble(value, out d)) { issue = ConversionIssue.Invalid; return DBNull.Value; }
                        double r = Math.Round(d, MidpointRounding.AwayFromZero);
                        if (r < int.MinValue || r > int.MaxValue) { issue = ConversionIssue.Invalid; return DBNull.Value; }
                        if (r != d) issue = ConversionIssue.Rounded;
                        return (int)r;
                    }

                case SpecType.Float:
                    {
                        double d;
                        if (IsBlank(value)) return DBNull.Value;
                        if (!TryToDouble(value, out d) || Math.Abs(d) > float.MaxValue)
                        {
                            issue = ConversionIssue.Invalid;
                            return DBNull.Value;
                        }
                        return (float)d;
                    }

                case SpecType.Double:
                    {
                        double d;
                        if (IsBlank(value)) return DBNull.Value;
                        if (!TryToDouble(value, out d)) { issue = ConversionIssue.Invalid; return DBNull.Value; }
                        return d;
                    }

                case SpecType.Date:
                    {
                        DateTime dt;
                        if (IsBlank(value)) return DBNull.Value;
                        if (!TryToDate(value, out dt)) { issue = ConversionIssue.Invalid; return DBNull.Value; }
                        return dt;
                    }

                default:
                    throw new ArgumentOutOfRangeException("type");
            }
        }

        private static bool IsBlank(object value)
        {
            var s = value as string;
            return s != null && s.Trim().Length == 0;
        }

        private static bool TryToText(object value, out string s)
        {
            s = null;
            if (value is string) { s = (string)value; return true; }
            if (value is DateTime) { s = ((DateTime)value).ToString("yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture); return true; }
            if (value is double) { s = ((double)value).ToString("R", CultureInfo.InvariantCulture); return true; }
            if (value is float) { s = ((float)value).ToString("R", CultureInfo.InvariantCulture); return true; }
            if (value is bool) { s = (bool)value ? "-1" : "0"; return true; }
            if (value is byte[] || value is Guid) return false;
            var f = value as IFormattable;
            s = f != null ? f.ToString(null, CultureInfo.InvariantCulture) : value.ToString();
            return true;
        }

        private static bool TryToDouble(object value, out double d)
        {
            d = 0;
            if (value is string)
            {
                var s = ((string)value).Trim();
                const NumberStyles style = NumberStyles.Float | NumberStyles.AllowThousands;
                if (!double.TryParse(s, style, CultureInfo.InvariantCulture, out d) &&
                    !double.TryParse(s, style, CultureInfo.CurrentCulture, out d))
                    return false;
                return !double.IsNaN(d) && !double.IsInfinity(d);
            }
            // Access 中 True 存储为 -1
            if (value is bool) { d = (bool)value ? -1 : 0; return true; }
            if (value is DateTime) { d = ((DateTime)value).ToOADate(); return true; }
            if (value is byte || value is short || value is int || value is long || value is float ||
                value is double || value is decimal || value is sbyte || value is ushort || value is uint || value is ulong)
            {
                d = System.Convert.ToDouble(value, CultureInfo.InvariantCulture);
                return !double.IsNaN(d) && !double.IsInfinity(d);
            }
            return false;
        }

        // Jet 日期范围：100-01-01 至 9999-12-31
        private const double MinOaDate = -657434;
        private const double MaxOaDate = 2958465.99999;

        private static bool TryToDate(object value, out DateTime dt)
        {
            dt = DateTime.MinValue;
            if (value is DateTime) { dt = (DateTime)value; return true; }
            if (value is string)
            {
                var s = ((string)value).Trim();
                return DateTime.TryParseExact(s, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out dt)
                    || DateTime.TryParse(s, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out dt)
                    || DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out dt);
            }
            double d;
            if (value is bool || !TryToDouble(value, out d)) return false;
            if (d < MinOaDate || d > MaxOaDate) return false;
            dt = DateTime.FromOADate(d);
            return true;
        }
    }
}
