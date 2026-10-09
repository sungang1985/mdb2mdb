using System.Collections.Generic;
using System.Linq;
using Mdb2Mdb;
using Xunit;

namespace Mdb2Mdb.Tests
{
    public class FieldSpecsTests
    {
        [Fact]
        public void TableHasAll37DistinctItems()
        {
            Assert.Equal(37, FieldSpecs.All.Length);
            Assert.Equal(37, FieldSpecs.All.Select(s => s.Name.ToUpperInvariant()).Distinct().Count());
        }

        [Theory]
        [InlineData("ANGLE", SpecType.Float, 4, 1)]
        [InlineData("BG", SpecType.Float, 3, 1)]
        [InlineData("ELEV", SpecType.Double, 9, 3)]
        [InlineData("GB", SpecType.Long, 6, null)]
        [InlineData("NAME", SpecType.Text, 60, null)]
        [InlineData("PINYIN", SpecType.Text, 120, null)]
        [InlineData("SAREA", SpecType.Double, 12, null)]
        [InlineData("SDTF", SpecType.Text, 2, null)]
        [InlineData("FTIME", SpecType.Date, null, null)]
        [InlineData("WIDTH", SpecType.Float, 3, 1)]
        [InlineData("XZNAME", SpecType.Text, 60, null)]
        public void SpotCheckAgainstStandard(string name, SpecType type, int? length, int? scale)
        {
            var s = FieldSpecs.Find(name);
            Assert.NotNull(s);
            Assert.Equal(type, s.Type);
            Assert.Equal(length, s.Length);
            Assert.Equal(scale, s.Scale);
        }

        [Fact]
        public void TextSpecsAllHaveLength()
        {
            Assert.All(FieldSpecs.All.Where(s => s.Type == SpecType.Text), s => Assert.True(s.Length > 0));
        }

        [Fact]
        public void LookupIsCaseInsensitive()
        {
            Assert.Same(FieldSpecs.Find("NAME"), FieldSpecs.Find("name"));
            Assert.True(FieldSpecs.IsFieldToDelete("SHAPE_LENGTH"));
            Assert.True(FieldSpecs.IsFieldToDelete("Shape_Area"));
            Assert.False(FieldSpecs.IsFieldToDelete("Shape"));
            Assert.Null(FieldSpecs.Find("PREIOD"));
        }
    }

    public class SchemaPlannerTests
    {
        private static ColumnInfo Col(string name, int type, int size = 0, int attributes = 0, bool required = false)
        {
            return new ColumnInfo { Name = name, DaoType = type, Size = size, Attributes = attributes, Required = required };
        }

        [Fact]
        public void PlansDeletesTypeChangesAndSkipsUnknownFields()
        {
            var columns = new List<ColumnInfo>
            {
                Col("ObjectID", Dao.dbLong, 4, Dao.dbAutoIncrField),
                Col("Shape", Dao.dbLongBinary),
                Col("GB", Dao.dbLong, 4),          // 已符合
                Col("NAME", Dao.dbText, 50),       // 长度不符
                Col("ANGLE", Dao.dbDouble, 8),     // 应为 FLOAT(Single)
                Col("PAC", Dao.dbText, 9),         // 应为 LONG
                Col("FTIME", Dao.dbText, 20),      // 应为 DATE
                Col("类型", Dao.dbText, 20),        // 不在标准中
                Col("Shape_Length", Dao.dbDouble, 8),
                Col("Shape_Area", Dao.dbDouble, 8),
            };

            var plans = SchemaPlanner.Plan(columns).ToDictionary(p => p.Column.Name);

            Assert.Equal(new[] { "GB", "NAME", "ANGLE", "PAC", "FTIME", "Shape_Length", "Shape_Area" },
                plans.Keys.ToArray());
            Assert.False(plans["GB"].NeedTypeChange);
            Assert.True(plans["NAME"].NeedTypeChange);
            Assert.True(plans["ANGLE"].NeedTypeChange);
            Assert.True(plans["PAC"].NeedTypeChange);
            Assert.True(plans["FTIME"].NeedTypeChange);
            Assert.True(plans["Shape_Length"].Delete);
            Assert.True(plans["Shape_Area"].Delete);
            Assert.False(plans["GB"].Delete);
        }

        [Fact]
        public void MatchingFieldsNeedNoChange()
        {
            var columns = new[]
            {
                Col("ANGLE", Dao.dbSingle, 4), Col("ELEV", Dao.dbDouble, 8), Col("TYPE", Dao.dbText, 20),
                Col("FTIME", Dao.dbDate, 8), Col("PAC", Dao.dbLong, 4)
            };
            Assert.All(SchemaPlanner.Plan(columns), p => Assert.False(p.NeedTypeChange));
        }

        // DecideNullability(原始必需, 当前必需, 标准允许为空, 空值数)

        [Fact]
        public void NullableToNotNullWhenNoNulls()
        {
            var d = SchemaPlanner.DecideNullability(false, false, false, 0);
            Assert.True(d.SetRequired);
            Assert.True(d.RequiredValue);
            Assert.True(d.FinalRequired);
            Assert.True(d.Changed);
            Assert.False(d.Failed);
            Assert.Equal("允许为空：是 → 否", d.Note);
        }

        [Fact]
        public void NotNullFailsWhenNullsExist()
        {
            var d = SchemaPlanner.DecideNullability(false, false, false, 3);
            Assert.False(d.SetRequired);
            Assert.False(d.FinalRequired);
            Assert.True(d.Failed);
            Assert.False(d.Changed);
            Assert.Null(d.Note);
        }

        [Fact]
        public void RequiredToNullable()
        {
            var d = SchemaPlanner.DecideNullability(true, true, true, 0);
            Assert.True(d.SetRequired);
            Assert.False(d.RequiredValue);
            Assert.True(d.Changed);
            Assert.Equal("允许为空：否 → 是", d.Note);
        }

        [Fact]
        public void AlreadyConformingIsNoChange()
        {
            Assert.False(SchemaPlanner.DecideNullability(false, false, true, 0).SetRequired);
            Assert.Null(SchemaPlanner.DecideNullability(false, false, true, 0).Note);
            Assert.False(SchemaPlanner.DecideNullability(true, true, false, 0).SetRequired);
            Assert.Null(SchemaPlanner.DecideNullability(true, true, false, 0).Note);
        }

        [Fact]
        public void TempColumnConversionClearedRequiredOnNotNullSpec()
        {
            // 原本必需，逐条转换类型时被临时清除，标准要求不允许为空：恢复为必需，但与输入相比无变化
            var d = SchemaPlanner.DecideNullability(true, false, false, 0);
            Assert.True(d.SetRequired);
            Assert.True(d.FinalRequired);
            Assert.False(d.Changed);
            Assert.Null(d.Note);
        }

        [Fact]
        public void TempColumnConversionClearedRequiredOnNullableSpec()
        {
            // 原本必需，转换后已是允许为空且符合标准：无需再设置，但与输入相比有变化，需要记录
            var d = SchemaPlanner.DecideNullability(true, false, true, 0);
            Assert.False(d.SetRequired);
            Assert.False(d.FinalRequired);
            Assert.True(d.Changed);
            Assert.Equal("允许为空：否 → 是", d.Note);
        }

        [Fact]
        public void ConversionProducedNullsOnNotNullSpec()
        {
            // 原本必需，转换时无法转换的值被置空，无法恢复为必需
            var d = SchemaPlanner.DecideNullability(true, false, false, 2);
            Assert.False(d.SetRequired);
            Assert.True(d.Failed);
            Assert.True(d.Changed);
            Assert.Equal("允许为空：否 → 是（存在空值，未达标准）", d.Note);
        }

        [Fact]
        public void OnlyGbAndClassAreNotNullable()
        {
            Assert.Equal(new[] { "CLASS", "GB" },
                FieldSpecs.All.Where(s => !s.Nullable).Select(s => s.Name).OrderBy(n => n).ToArray());
        }

        [Fact]
        public void ShortIntegerAndMemoAreNotConsideredMatching()
        {
            Assert.False(SchemaPlanner.Matches(Col("GB", Dao.dbInteger, 2), FieldSpecs.Find("GB")));
            Assert.False(SchemaPlanner.Matches(Col("NAME", Dao.dbMemo, 0), FieldSpecs.Find("NAME")));
        }

        [Theory]
        [InlineData("MSysObjects", unchecked((int)0x80000002), "", false)]
        [InlineData("MSysACEs", 2, "", false)]
        [InlineData("GDB_Items", 0, "", false)]
        [InlineData("CPTP_Shape_Index", 0, "", false)]
        [InlineData("CPTP", 0, "", true)]
        [InlineData("道路", 0, "", true)]
        [InlineData("Linked", 0x40000000, ";DATABASE=x.mdb", false)]
        public void TableFilter(string name, int attributes, string connect, bool expected)
        {
            Assert.Equal(expected, SchemaPlanner.ShouldProcessTable(name, attributes, connect));
        }

        [Fact]
        public void JetSqlTypesUseSingleForFloat()
        {
            Assert.Equal("SINGLE", SchemaPlanner.JetSqlType(FieldSpecs.Find("ANGLE")));
            Assert.Equal("DOUBLE", SchemaPlanner.JetSqlType(FieldSpecs.Find("ELEV")));
            Assert.Equal("LONG", SchemaPlanner.JetSqlType(FieldSpecs.Find("GB")));
            Assert.Equal("TEXT(120)", SchemaPlanner.JetSqlType(FieldSpecs.Find("PINYIN")));
            Assert.Equal("DATETIME", SchemaPlanner.JetSqlType(FieldSpecs.Find("FTIME")));
        }
    }
}
