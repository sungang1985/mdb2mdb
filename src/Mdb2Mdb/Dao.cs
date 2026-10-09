using System;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Mdb2Mdb
{
    /// <summary>
    /// 通过后期绑定(IDispatch)调用 DAO，避免依赖互操作程序集。
    /// 优先使用 Windows 自带的 DAO 3.6(Jet 4.0)，没有时再尝试 Office 的 ACE DAO。
    /// </summary>
    internal static class Dao
    {
        // DataTypeEnum
        public const int dbBoolean = 1;
        public const int dbByte = 2;
        public const int dbInteger = 3;
        public const int dbLong = 4;
        public const int dbCurrency = 5;
        public const int dbSingle = 6;
        public const int dbDouble = 7;
        public const int dbDate = 8;
        public const int dbBinary = 9;
        public const int dbText = 10;
        public const int dbLongBinary = 11;
        public const int dbMemo = 12;
        public const int dbGUID = 15;
        public const int dbBigInt = 16;
        public const int dbVarBinary = 17;
        public const int dbChar = 18;
        public const int dbNumeric = 19;
        public const int dbDecimal = 20;
        public const int dbFloat = 21;

        // TableDefAttributeEnum
        public const int dbSystemObject = unchecked((int)0x80000002);
        public const int dbHiddenObject = 1;
        public const int dbAttachedTable = 0x40000000;
        public const int dbAttachedODBC = 0x20000000;

        // FieldAttributeEnum
        public const int dbAutoIncrField = 16;

        // IndexField attributes
        public const int dbDescending = 1;

        // RecordsetTypeEnum / RecordsetOptionEnum
        public const int dbOpenDynaset = 2;
        public const int dbOpenSnapshot = 4;
        public const int dbFailOnError = 128;

        // Jet "Property not found"
        public const int ErrPropertyNotFound = 3270;

        public static object CreateEngine(out string progId)
        {
            Exception last = null;
            foreach (var id in new[] { "DAO.DBEngine.36", "DAO.DBEngine.120" })
            {
                try
                {
                    var t = Type.GetTypeFromProgID(id, false);
                    if (t == null) continue;
                    var engine = Activator.CreateInstance(t);
                    progId = id;
                    return engine;
                }
                catch (Exception ex)
                {
                    last = ex;
                }
            }
            throw new InvalidOperationException(
                "无法创建 DAO 数据库引擎（DAO.DBEngine.36 / DAO.DBEngine.120）。" +
                "请确认本机已安装 Microsoft Jet 4.0 或 Access 数据库引擎（32 位）。", last);
        }

        public static object Get(object target, string name, params object[] args)
        {
            return Invoke(target, name, BindingFlags.GetProperty, args);
        }

        public static void Set(object target, string name, object value)
        {
            Invoke(target, name, BindingFlags.SetProperty, new[] { value });
        }

        public static object Call(object target, string name, params object[] args)
        {
            return Invoke(target, name, BindingFlags.InvokeMethod, args);
        }

        /// <summary>集合按序号或名称取元素（DAO 集合的默认属性 Item）。</summary>
        public static object Item(object collection, object key)
        {
            return Get(collection, "Item", key);
        }

        public static int Count(object collection)
        {
            return Convert.ToInt32(Get(collection, "Count"));
        }

        private static object Invoke(object target, string name, BindingFlags flags, object[] args)
        {
            try
            {
                return target.GetType().InvokeMember(name, flags, null, target, args);
            }
            catch (TargetInvocationException ex)
            {
                if (ex.InnerException != null)
                {
                    // 保留 DAO 原始错误信息
                    throw new DaoException(name, ex.InnerException);
                }
                throw;
            }
            catch (COMException ex)
            {
                throw new DaoException(name, ex);
            }
        }

        public static void Release(object o)
        {
            if (o != null && Marshal.IsComObject(o))
            {
                try { Marshal.ReleaseComObject(o); } catch { }
            }
        }

        /// <summary>SQL 中的标识符，用方括号括起。</summary>
        public static string Q(string identifier)
        {
            return "[" + identifier.Replace("]", "]]") + "]";
        }

        /// <summary>SQL 字符串常量。</summary>
        public static string Str(string s)
        {
            return "'" + s.Replace("'", "''") + "'";
        }
    }

    internal sealed class DaoException : Exception
    {
        public DaoException(string member, Exception inner)
            : base(inner.Message, inner)
        {
            Member = member;
            var com = inner as COMException;
            ErrorCode = com != null ? com.ErrorCode : 0;
        }

        public string Member { get; private set; }

        public int ErrorCode { get; private set; }

        /// <summary>DAO/Jet 错误号（HRESULT 低 16 位），如 3270 = 找不到属性。</summary>
        public int DaoErrorNumber
        {
            get { return ErrorCode & 0xFFFF; }
        }
    }
}
