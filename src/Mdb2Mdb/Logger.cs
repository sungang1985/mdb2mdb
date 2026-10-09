using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Mdb2Mdb
{
    /// <summary>处理日志：同时转发给界面/控制台，并在结束后写入日志文件。</summary>
    public sealed class Logger
    {
        private readonly Action<string> _sink;
        private readonly List<string> _lines = new List<string>();
        private readonly object _lock = new object();

        public Logger(Action<string> sink)
        {
            _sink = sink;
        }

        public int Warnings { get; private set; }
        public int Errors { get; private set; }

        public void Info(string message)
        {
            Write(message);
        }

        public void Warn(string message)
        {
            lock (_lock) Warnings++;
            Write("【警告】" + message);
        }

        public void Error(string message)
        {
            lock (_lock) Errors++;
            Write("【错误】" + message);
        }

        private void Write(string line)
        {
            lock (_lock) _lines.Add(line);
            if (_sink != null) _sink(line);
        }

        public void SaveTo(string path)
        {
            string[] lines;
            lock (_lock) lines = _lines.ToArray();
            // 带 BOM 的 UTF-8，记事本可直接正确显示中文
            File.WriteAllLines(path, lines, new UTF8Encoding(true));
        }
    }
}
