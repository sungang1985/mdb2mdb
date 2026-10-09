using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Mdb2Mdb
{
    internal static class Program
    {
        /// <summary>
        /// 用法：
        ///   mdb2mdb.exe                              打开图形界面
        ///   mdb2mdb.exe 输入.mdb [输出.mdb]          打开图形界面并直接开始处理（可把 mdb 文件拖到 exe 上）
        ///   mdb2mdb.exe --cli 输入.mdb [输出.mdb]    命令行模式，不显示界面，返回码 0=成功 1=有错误 2=失败
        /// </summary>
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length > 0 && (args[0] == "--cli" || args[0] == "/cli"))
                return RunCli(args);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm(args.Length > 0 ? args[0] : null, args.Length > 1 ? args[1] : null));
            return 0;
        }

        private static int RunCli(string[] args)
        {
            AttachConsole(AttachParentProcess);

            if (args.Length < 2)
            {
                Console.WriteLine("用法：mdb2mdb.exe --cli 输入.mdb [输出.mdb]");
                return 2;
            }

            string input = args[1];
            string output = args.Length > 2 ? args[2] : MdbProcessor.DefaultOutputPath(input);
            var log = new Logger(Console.WriteLine);
            int code;
            try
            {
                new MdbProcessor(log).Run(input, output, true);
                code = log.Errors > 0 ? 1 : 0;
            }
            catch (Exception ex)
            {
                log.Error("处理失败：" + ex.Message);
                code = 2;
            }

            try
            {
                string logPath = MdbProcessor.LogPathFor(output);
                log.SaveTo(logPath);
                Console.WriteLine("日志已保存：" + logPath);
            }
            catch (Exception ex)
            {
                Console.WriteLine("日志保存失败：" + ex.Message);
            }
            return code;
        }

        private const int AttachParentProcess = -1;

        [DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int processId);
    }
}
