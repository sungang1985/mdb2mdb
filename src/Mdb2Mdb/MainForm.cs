using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace Mdb2Mdb
{
    internal sealed class MainForm : Form
    {
        private readonly TextBox _txtInput = new TextBox { Dock = DockStyle.Fill };
        private readonly TextBox _txtOutput = new TextBox { Dock = DockStyle.Fill };
        private readonly Button _btnInput = new Button { Text = "浏览…", AutoSize = true };
        private readonly Button _btnOutput = new Button { Text = "浏览…", AutoSize = true };
        private readonly CheckBox _chkCompact = new CheckBox { Text = "处理完成后压缩数据库", Checked = true, AutoSize = true };
        private readonly Button _btnRun = new Button { Text = "开始处理", AutoSize = true, Anchor = AnchorStyles.Right };
        private readonly TextBox _txtLog = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            BackColor = SystemColors.Window,
            Font = new Font("宋体", 9f)
        };

        private readonly bool _autoRun;

        public MainForm(string input, string output)
        {
            Text = "MDB 字段属性规范化工具 (mdb2mdb)";
            Size = new Size(820, 560);
            MinimumSize = new Size(560, 360);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("微软雅黑", 9f);
            AllowDrop = true;

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 4, Padding = new Padding(8) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            layout.Controls.Add(MakeLabel("输入 MDB："), 0, 0);
            layout.Controls.Add(_txtInput, 1, 0);
            layout.Controls.Add(_btnInput, 2, 0);
            layout.Controls.Add(MakeLabel("输出 MDB："), 0, 1);
            layout.Controls.Add(_txtOutput, 1, 1);
            layout.Controls.Add(_btnOutput, 2, 1);
            layout.Controls.Add(_chkCompact, 1, 2);
            layout.Controls.Add(_btnRun, 2, 2);
            layout.Controls.Add(_txtLog, 0, 3);
            layout.SetColumnSpan(_txtLog, 3);
            Controls.Add(layout);

            _btnInput.Click += delegate { BrowseInput(); };
            _btnOutput.Click += delegate { BrowseOutput(); };
            _btnRun.Click += delegate { StartRun(); };
            DragEnter += OnDragEnter;
            DragDrop += OnDragDrop;

            AppendLog("把 .mdb 文件拖到窗口上，或点击“浏览…”选择输入文件，然后点击“开始处理”。");
            AppendLog("处理在输出文件上进行，输入文件不会被修改。");

            if (!string.IsNullOrEmpty(input))
            {
                SetInput(input);
                if (!string.IsNullOrEmpty(output)) _txtOutput.Text = output;
                _autoRun = true;
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (_autoRun) StartRun();
        }

        private static Label MakeLabel(string text)
        {
            return new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 3, 3) };
        }

        private void SetInput(string path)
        {
            _txtInput.Text = path;
            _txtOutput.Text = MdbProcessor.DefaultOutputPath(path);
        }

        private void BrowseInput()
        {
            using (var dlg = new OpenFileDialog { Filter = "Access 数据库 (*.mdb)|*.mdb|所有文件 (*.*)|*.*", Title = "选择输入 MDB 文件" })
            {
                if (dlg.ShowDialog(this) == DialogResult.OK) SetInput(dlg.FileName);
            }
        }

        private void BrowseOutput()
        {
            using (var dlg = new SaveFileDialog { Filter = "Access 数据库 (*.mdb)|*.mdb", Title = "选择输出 MDB 文件", OverwritePrompt = false })
            {
                if (!string.IsNullOrEmpty(_txtOutput.Text))
                {
                    dlg.InitialDirectory = Path.GetDirectoryName(_txtOutput.Text);
                    dlg.FileName = Path.GetFileName(_txtOutput.Text);
                }
                if (dlg.ShowDialog(this) == DialogResult.OK) _txtOutput.Text = dlg.FileName;
            }
        }

        private void OnDragEnter(object sender, DragEventArgs e)
        {
            e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        }

        private void OnDragDrop(object sender, DragEventArgs e)
        {
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files != null && files.Length > 0 && _btnRun.Enabled) SetInput(files[0]);
        }

        private void StartRun()
        {
            string input = _txtInput.Text.Trim();
            string output = _txtOutput.Text.Trim();
            if (input.Length == 0 || !File.Exists(input))
            {
                MessageBox.Show(this, "请选择存在的输入 MDB 文件。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (output.Length == 0) output = MdbProcessor.DefaultOutputPath(input);
            if (string.Equals(Path.GetFullPath(input), Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this, "输出文件不能与输入文件相同。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (File.Exists(output) &&
                MessageBox.Show(this, "输出文件已存在，是否覆盖？\r\n" + output, Text,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            _txtOutput.Text = output;
            _txtLog.Clear();
            SetBusy(true);
            bool compact = _chkCompact.Checked;

            // DAO 是单线程单元(STA) COM 组件，在独立的 STA 线程中处理，避免界面卡死
            var thread = new Thread(() => Work(input, output, compact)) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        private void Work(string input, string output, bool compact)
        {
            var log = new Logger(line => BeginInvoke(new Action<string>(AppendLog), line));
            string message;
            MessageBoxIcon icon;
            try
            {
                new MdbProcessor(log).Run(input, output, compact);
                if (log.Errors > 0)
                {
                    message = "处理完成，但有 " + log.Errors + " 个错误，请查看日志。";
                    icon = MessageBoxIcon.Warning;
                }
                else
                {
                    message = "处理完成。\r\n输出文件：" + output;
                    icon = MessageBoxIcon.Information;
                }
            }
            catch (Exception ex)
            {
                log.Error("处理失败：" + ex.Message);
                message = "处理失败：" + ex.Message;
                icon = MessageBoxIcon.Error;
            }

            try
            {
                string logPath = MdbProcessor.LogPathFor(output);
                log.SaveTo(logPath);
                log.Info("日志已保存：" + logPath);
            }
            catch (Exception ex)
            {
                log.Info("日志保存失败：" + ex.Message);
            }

            BeginInvoke(new Action(() =>
            {
                SetBusy(false);
                MessageBox.Show(this, message, Text, MessageBoxButtons.OK, icon);
            }));
        }

        private void SetBusy(bool busy)
        {
            _btnRun.Enabled = _btnInput.Enabled = _btnOutput.Enabled = !busy;
            _txtInput.ReadOnly = _txtOutput.ReadOnly = busy;
            _chkCompact.Enabled = !busy;
            UseWaitCursor = busy;
            _btnRun.Text = busy ? "处理中…" : "开始处理";
        }

        private void AppendLog(string line)
        {
            _txtLog.AppendText(line + Environment.NewLine);
        }
    }
}
