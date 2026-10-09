using System.Collections.Generic;
using Mdb2Mdb;
using Xunit;

namespace Mdb2Mdb.Tests
{
    public class GdbDefinitionPatcherTests
    {
        // 与 ArcGIS 10.x 个人地理数据库 GDB_Items.Definition 结构相同的示例（已精简）
        private const string Xml =
            "<DEFeatureClassInfo xsi:type='typens:DEFeatureClassInfo' xmlns:xsi='http://www.w3.org/2001/XMLSchema-instance' " +
            "xmlns:xs='http://www.w3.org/2001/XMLSchema' xmlns:typens='http://www.esri.com/schemas/ArcGIS/10.1'>" +
            "<CatalogPath>\\BXZA</CatalogPath><Name>BXZA</Name><DatasetType>esriDTFeatureClass</DatasetType>" +
            "<HasOID>true</HasOID><OIDFieldName>ObjectID</OIDFieldName>" +
            "<GPFieldInfoExs xsi:type='typens:ArrayOfGPFieldInfoEx'>" +
            "<GPFieldInfoEx xsi:type='typens:GPFieldInfoEx'><Name>ObjectID</Name><ModelName>ObjectID</ModelName><FieldType>esriFieldTypeOID</FieldType><IsNullable>false</IsNullable><Required>true</Required><Editable>false</Editable></GPFieldInfoEx>" +
            "<GPFieldInfoEx xsi:type='typens:GPFieldInfoEx'><Name>Shape</Name><ModelName>Shape</ModelName><FieldType>esriFieldTypeGeometry</FieldType><IsNullable>true</IsNullable><Required>true</Required></GPFieldInfoEx>" +
            "<GPFieldInfoEx xsi:type='typens:GPFieldInfoEx'><Name>GB</Name><AliasName>分类代码</AliasName><ModelName>GB</ModelName><FieldType>esriFieldTypeSmallInteger</FieldType><IsNullable>true</IsNullable></GPFieldInfoEx>" +
            "<GPFieldInfoEx xsi:type='typens:GPFieldInfoEx'><Name>NAME</Name><AliasName>名称</AliasName><ModelName>NAME</ModelName><FieldType>esriFieldTypeString</FieldType><IsNullable>true</IsNullable></GPFieldInfoEx>" +
            "<GPFieldInfoEx xsi:type='typens:GPFieldInfoEx'><Name>Shape_Length</Name><ModelName>Shape_Length</ModelName><FieldType>esriFieldTypeDouble</FieldType><IsNullable>true</IsNullable><Required>true</Required><Editable>false</Editable></GPFieldInfoEx>" +
            "<GPFieldInfoEx xsi:type='typens:GPFieldInfoEx'><Name>Shape_Area</Name><ModelName>Shape_Area</ModelName><FieldType>esriFieldTypeDouble</FieldType><IsNullable>true</IsNullable><Required>true</Required><Editable>false</Editable></GPFieldInfoEx>" +
            "</GPFieldInfoExs><CLSID>{52353152-891A-11D0-BEC6-00805F7C4268}</CLSID><AliasName>BXZA</AliasName>" +
            "<FeatureType>esriFTSimple</FeatureType><ShapeType>esriGeometryPolygon</ShapeType><ShapeFieldName>Shape</ShapeFieldName>" +
            "<HasM>false</HasM><HasZ>false</HasZ><HasSpatialIndex>true</HasSpatialIndex>" +
            "<AreaFieldName>Shape_Area</AreaFieldName><LengthFieldName>Shape_Length</LengthFieldName></DEFeatureClassInfo>";

        [Fact]
        public void RemovesDeletedFieldsAndClearsAreaLengthReferences()
        {
            string patched = GdbDefinitionPatcher.Patch(Xml, null, new[] { "Shape_Length", "Shape_Area" });

            Assert.DoesNotContain("<Name>Shape_Length</Name>", patched);
            Assert.DoesNotContain("<Name>Shape_Area</Name>", patched);
            Assert.Contains("<AreaFieldName></AreaFieldName>", patched);
            Assert.Contains("<LengthFieldName></LengthFieldName>", patched);
            // 其他字段及别名原样保留
            Assert.Contains("<Name>GB</Name><AliasName>分类代码</AliasName>", patched);
            Assert.Contains("<Name>NAME</Name><AliasName>名称</AliasName>", patched);
            Assert.Contains("<ShapeFieldName>Shape</ShapeFieldName>", patched);
            Assert.Equal(4, Count(patched, "<GPFieldInfoEx "));
            Assert.EndsWith("</DEFeatureClassInfo>", patched);
        }

        [Fact]
        public void UpdatesFieldTypeOfChangedFieldOnly()
        {
            var types = new Dictionary<string, string> { { "gb", "esriFieldTypeInteger" } };
            string patched = GdbDefinitionPatcher.Patch(Xml, types, null);

            Assert.Contains("<Name>GB</Name><AliasName>分类代码</AliasName><ModelName>GB</ModelName><FieldType>esriFieldTypeInteger</FieldType>", patched);
            Assert.Contains("<FieldType>esriFieldTypeOID</FieldType>", patched);
            Assert.Contains("<AreaFieldName>Shape_Area</AreaFieldName>", patched);
            Assert.Equal(Xml.Length - "SmallInteger".Length + "Integer".Length, patched.Length);
        }

        [Fact]
        public void SetsIsNullableOfListedFields()
        {
            var nullable = new Dictionary<string, bool> { { "GB", false }, { "name", true } };
            string patched = GdbDefinitionPatcher.Patch(Xml, null, null, nullable);

            Assert.Contains("<Name>GB</Name><AliasName>分类代码</AliasName><ModelName>GB</ModelName><FieldType>esriFieldTypeSmallInteger</FieldType><IsNullable>false</IsNullable>", patched);
            Assert.Contains("<Name>NAME</Name><AliasName>名称</AliasName><ModelName>NAME</ModelName><FieldType>esriFieldTypeString</FieldType><IsNullable>true</IsNullable>", patched);
            // 未列出的字段不变
            Assert.Contains("<Name>ObjectID</Name><ModelName>ObjectID</ModelName><FieldType>esriFieldTypeOID</FieldType><IsNullable>false</IsNullable>", patched);
            Assert.Equal(Xml.Length + 1, patched.Length); // true → false
        }

        [Fact]
        public void InsertsIsNullableWhenMissing()
        {
            const string xml = "<GPFieldInfoExs><GPFieldInfoEx xsi:type='typens:GPFieldInfoEx'><Name>CLASS</Name><ModelName>CLASS</ModelName>" +
                               "<FieldType>esriFieldTypeString</FieldType></GPFieldInfoEx></GPFieldInfoExs>";
            string patched = GdbDefinitionPatcher.Patch(xml, null, null, new Dictionary<string, bool> { { "CLASS", false } });
            Assert.Contains("<FieldType>esriFieldTypeString</FieldType><IsNullable>false</IsNullable></GPFieldInfoEx>", patched);
        }

        [Fact]
        public void TypeNullabilityAndDeletionTogether()
        {
            string patched = GdbDefinitionPatcher.Patch(Xml,
                new Dictionary<string, string> { { "GB", "esriFieldTypeInteger" } },
                new[] { "Shape_Length", "Shape_Area" },
                new Dictionary<string, bool> { { "GB", false } });
            Assert.Contains("<Name>GB</Name><AliasName>分类代码</AliasName><ModelName>GB</ModelName><FieldType>esriFieldTypeInteger</FieldType><IsNullable>false</IsNullable>", patched);
            Assert.DoesNotContain("Shape_Length</Name>", patched);
            Assert.Contains("<LengthFieldName></LengthFieldName>", patched);
        }

        [Fact]
        public void NothingToDoReturnsSameText()
        {
            Assert.Equal(Xml, GdbDefinitionPatcher.Patch(Xml, new Dictionary<string, string>(), new string[0]));
            Assert.Equal(Xml, GdbDefinitionPatcher.Patch(Xml, new Dictionary<string, string> { { "XXX", "esriFieldTypeString" } }, new[] { "YYY" }));
        }

        private static int Count(string s, string sub)
        {
            int n = 0, i = 0;
            while ((i = s.IndexOf(sub, i, System.StringComparison.Ordinal)) >= 0) { n++; i += sub.Length; }
            return n;
        }
    }
}
