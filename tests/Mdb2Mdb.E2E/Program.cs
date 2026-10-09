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

            CreateTestDatabase(input);
            string hashBefore = Hash(input);

            // 1. 进程内运行处理流程
            var log = new Logger(Console.WriteLine);
            new MdbProcessor(log).Run(input, output, true);
            Check(log.Errors == 0, "处理过程中不应有错误，实际 " + log.Errors);
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
                         "[WIDTH] SINGLE, [LANE] DOUBLE, [KV] LONG NOT NULL, [类型] TEXT(20), [Shape_Length] DOUBLE, [Shape_Area] DOUBLE)");
                Exec(db, "CREATE INDEX [IX_NAME] ON [ROAD] ([NAME])");
                Exec(db, "CREATE INDEX [IX_SL] ON [ROAD] ([Shape_Length])");
                Exec(db, "INSERT INTO [ROAD] ([GB], [NAME], [TYPE], [ANGLE], [FTIME], [PAC], [WIDTH], [LANE], [KV], [类型], [Shape_Length], [Shape_Area]) " +
                         "VALUES ('660100', '大型', '一二三四五六七八九十一二三四五六七八九十一二三四五', 12.34, '2025/03/08 14:05:09', 321311106, 3.5, 2.5, 220, '甲', 357.675449, 8289.822516)");
                Exec(db, "INSERT INTO [ROAD] ([GB], [NAME], [TYPE], [ANGLE], [FTIME], [PAC], [WIDTH], [LANE], [KV], [类型], [Shape_Length], [Shape_Area]) " +
                         "VALUES ('abc', NULL, '短', NULL, NULL, NULL, NULL, 2, 35, NULL, 1, 2)");

                // 普通表（非要素类）
                Exec(db, "CREATE TABLE [PLAIN] ([ID] LONG, [ELEV] TEXT(20), [Shape_Area] DOUBLE)");
                Exec(db, "INSERT INTO [PLAIN] ([ID], [ELEV]) VALUES (1, '12.5')");
                Exec(db, "INSERT INTO [PLAIN] ([ID], [ELEV]) VALUES (2, '')");
                Exec(db, "INSERT INTO [PLAIN] ([ID], [ELEV]) VALUES (3, 'n/a')");
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
            object db = Dao.Call(_engine, "OpenDatabase", path, false, true);
            try
            {
                // 字段顺序、类型、长度
                ExpectFields(db, "ROAD", new[]
                {
                    "ObjectID:LONG", "Shape:OLE", "GB:LONG", "NAME:TEXT(60)", "TYPE:TEXT(20)", "ANGLE:FLOAT",
                    "FTIME:DATE", "PAC:LONG", "WIDTH:FLOAT", "LANE:LONG", "KV:TEXT(8)", "类型:TEXT(20)"
                });
                ExpectFields(db, "PLAIN", new[] { "ID:LONG", "ELEV:DOUBLE" });
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

                object fields = Dao.Get(road, "Fields");
                Check((bool)Dao.Get(Dao.Item(fields, "KV"), "Required"), "KV 的必填属性应恢复");
                Eq(FieldProp(road, "ANGLE", "DecimalPlaces"), (byte)1, "ANGLE 小数位数");
                Eq(FieldProp(road, "WIDTH", "DecimalPlaces"), (byte)1, "WIDTH 小数位数");
                Eq(FieldProp(road, "FTIME", "Format"), FieldSpec.DateFormat, "FTIME 格式");
                object plainTd = Dao.Item(Dao.Get(db, "TableDefs"), "PLAIN");
                Check(FieldProp(plainTd, "ID", "DecimalPlaces") == null, "非标准字段不设置小数位数");

                // GDB 定义
                var items = ReadRows(db, "SELECT [Definition] FROM [GDB_Items] WHERE [PhysicalName] = 'ROAD'");
                string xml = items.Count == 1 ? items[0]["Definition"] as string : null;
                Check(xml != null, "GDB_Items 中应有 ROAD 定义");
                if (xml != null)
                {
                    Check(xml.Contains("<Name>GB</Name><AliasName>分类代码</AliasName><ModelName>GB</ModelName><FieldType>esriFieldTypeInteger</FieldType>"), "GB 的 FieldType 应更新为 esriFieldTypeInteger");
                    Check(xml.Contains("<Name>ANGLE</Name><ModelName>ANGLE</ModelName><FieldType>esriFieldTypeSingle</FieldType>"), "ANGLE 的 FieldType 应更新为 esriFieldTypeSingle");
                    Check(!xml.Contains("<Name>Shape_Length</Name>") && !xml.Contains("<Name>Shape_Area</Name>"), "定义中应删除 Shape_Length/Shape_Area");
                    Check(xml.Contains("<AreaFieldName></AreaFieldName>") && xml.Contains("<LengthFieldName></LengthFieldName>"), "AreaFieldName/LengthFieldName 应清空");
                }
            }
            finally
            {
                Dao.Call(db, "Close");
                Dao.Release(db);
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
