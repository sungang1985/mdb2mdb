using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Mdb2Mdb
{
    /// <summary>
    /// 同步修改 ArcGIS 10.x 个人地理数据库 GDB_Items.Definition 中的要素类定义 XML。
    /// 只做最小范围的文本替换，不重新序列化，保持 ArcGIS 写出的原始格式。
    /// </summary>
    public static class GdbDefinitionPatcher
    {
        private static readonly Regex FieldInfoBlock = new Regex(
            @"<GPFieldInfoEx\b[^>]*>.*?</GPFieldInfoEx>", RegexOptions.Singleline | RegexOptions.CultureInvariant);

        private static readonly Regex NameElement = new Regex(
            @"<Name>(.*?)</Name>", RegexOptions.Singleline | RegexOptions.CultureInvariant);

        private static readonly Regex FieldTypeElement = new Regex(
            @"<FieldType>(.*?)</FieldType>", RegexOptions.Singleline | RegexOptions.CultureInvariant);

        /// <param name="xml">原定义 XML</param>
        /// <param name="newFieldTypes">字段名 → 新的 esriFieldType</param>
        /// <param name="deletedFields">已删除的字段名</param>
        /// <returns>修改后的 XML；无需修改时返回原字符串</returns>
        public static string Patch(string xml, IDictionary<string, string> newFieldTypes, ICollection<string> deletedFields)
        {
            if (string.IsNullOrEmpty(xml)) return xml;

            var types = new Dictionary<string, string>(newFieldTypes ?? new Dictionary<string, string>(),
                StringComparer.OrdinalIgnoreCase);
            var deleted = new HashSet<string>(deletedFields ?? new string[0], StringComparer.OrdinalIgnoreCase);

            string result = FieldInfoBlock.Replace(xml, m =>
            {
                var nm = NameElement.Match(m.Value);
                if (!nm.Success) return m.Value;
                string name = XmlUnescape(nm.Groups[1].Value);

                if (deleted.Contains(name)) return string.Empty;

                string esriType;
                if (types.TryGetValue(name, out esriType))
                {
                    if (FieldTypeElement.IsMatch(m.Value))
                        return FieldTypeElement.Replace(m.Value, "<FieldType>" + esriType + "</FieldType>", 1);
                }
                return m.Value;
            });

            foreach (var element in new[] { "AreaFieldName", "LengthFieldName" })
            {
                var re = new Regex("<" + element + ">(.*?)</" + element + ">", RegexOptions.Singleline | RegexOptions.CultureInvariant);
                result = re.Replace(result, m =>
                    deleted.Contains(XmlUnescape(m.Groups[1].Value).Trim())
                        ? "<" + element + "></" + element + ">"
                        : m.Value);
            }

            return result;
        }

        private static string XmlUnescape(string s)
        {
            return s.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"")
                    .Replace("&apos;", "'").Replace("&amp;", "&");
        }
    }
}
