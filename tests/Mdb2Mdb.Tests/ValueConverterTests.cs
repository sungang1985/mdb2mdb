using System;
using Mdb2Mdb;
using Xunit;

namespace Mdb2Mdb.Tests
{
    public class ValueConverterTests
    {
        private static object Conv(object v, SpecType t, int? len, out ConversionIssue issue)
        {
            return ValueConverter.Convert(v, t, len, out issue);
        }

        [Fact]
        public void NullStaysNull()
        {
            ConversionIssue issue;
            Assert.IsType<DBNull>(Conv(DBNull.Value, SpecType.Long, null, out issue));
            Assert.IsType<DBNull>(Conv(null, SpecType.Text, 10, out issue));
            Assert.Equal(ConversionIssue.None, issue);
        }

        [Fact]
        public void TextIsTruncatedToLength()
        {
            ConversionIssue issue;
            Assert.Equal("abc", Conv("abc", SpecType.Text, 4, out issue));
            Assert.Equal(ConversionIssue.None, issue);
            Assert.Equal("江苏省南", Conv("江苏省南京市", SpecType.Text, 4, out issue));
            Assert.Equal(ConversionIssue.Truncated, issue);
        }

        [Fact]
        public void NumbersBecomeInvariantText()
        {
            ConversionIssue issue;
            Assert.Equal("660100", Conv(660100, SpecType.Text, 20, out issue));
            Assert.Equal("12.5", Conv(12.5, SpecType.Text, 20, out issue));
            Assert.Equal("1.1", Conv(1.1f, SpecType.Text, 20, out issue));
            Assert.Equal(ConversionIssue.None, issue);
        }

        [Fact]
        public void TextToLong()
        {
            ConversionIssue issue;
            Assert.Equal(660100, Conv(" 660100 ", SpecType.Long, null, out issue));
            Assert.Equal(ConversionIssue.None, issue);
            Assert.IsType<DBNull>(Conv("", SpecType.Long, null, out issue));
            Assert.Equal(ConversionIssue.None, issue);
            Assert.IsType<DBNull>(Conv("abc", SpecType.Long, null, out issue));
            Assert.Equal(ConversionIssue.Invalid, issue);
            Assert.IsType<DBNull>(Conv("99999999999", SpecType.Long, null, out issue));
            Assert.Equal(ConversionIssue.Invalid, issue);
        }

        [Fact]
        public void FractionToLongIsRoundedAndReported()
        {
            ConversionIssue issue;
            Assert.Equal(13, Conv(12.5, SpecType.Long, null, out issue));
            Assert.Equal(ConversionIssue.Rounded, issue);
            Assert.Equal(12, Conv(12.0, SpecType.Long, null, out issue));
            Assert.Equal(ConversionIssue.None, issue);
            Assert.Equal(7, Conv((short)7, SpecType.Long, null, out issue));
        }

        [Fact]
        public void FloatAndDouble()
        {
            ConversionIssue issue;
            Assert.Equal(1.5f, Conv("1.5", SpecType.Float, 4, out issue));
            Assert.Equal(1.25, Conv(1.25f, SpecType.Double, 9, out issue));
            Assert.Equal(123.456, Conv("123.456", SpecType.Double, 9, out issue));
            Assert.Equal(ConversionIssue.None, issue);
            Assert.IsType<DBNull>(Conv(1e300, SpecType.Float, 4, out issue));
            Assert.Equal(ConversionIssue.Invalid, issue);
        }

        [Fact]
        public void TextToDate()
        {
            ConversionIssue issue;
            Assert.Equal(new DateTime(2025, 3, 8, 14, 5, 9), Conv("2025/03/08 14:05:09", SpecType.Date, null, out issue));
            Assert.Equal(new DateTime(2025, 3, 8), Conv("20250308", SpecType.Date, null, out issue));
            Assert.Equal(new DateTime(2025, 3, 8), Conv("2025年3月8日", SpecType.Date, null, out issue));
            Assert.Equal(ConversionIssue.None, issue);
            Assert.IsType<DBNull>(Conv("不是日期", SpecType.Date, null, out issue));
            Assert.Equal(ConversionIssue.Invalid, issue);
        }

        [Fact]
        public void DateToText()
        {
            ConversionIssue issue;
            Assert.Equal("2025/03/08 14:05:09", Conv(new DateTime(2025, 3, 8, 14, 5, 9), SpecType.Text, 20, out issue));
        }

        [Fact]
        public void BinaryCannotBecomeText()
        {
            ConversionIssue issue;
            Assert.IsType<DBNull>(Conv(new byte[] { 1, 2 }, SpecType.Text, 20, out issue));
            Assert.Equal(ConversionIssue.Invalid, issue);
        }
    }
}
