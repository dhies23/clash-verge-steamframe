// Clash Verge Rev × Steam Frame 管理器（Windows 图形界面）
//
// 功能：
//   1. 连接：支持 SSH 别名（免密）或「用户名 + 密码」直接登录
//      —— 密码登录时会自动生成本机密钥并装到设备上，之后免密
//      —— 设备重装系统后 authorized_keys / 主机密钥都被重置，程序会自动用保存的密码重建
//   2. 一键部署：把 install-clash-verge-frame.run 推到设备并远程安装
//   3. 订阅推送：本机下载订阅 -> 推到设备 -> 写入 Clash Verge 的 profiles.yaml -> 重启生效
//   4. 读取 / 删除设备上已有的订阅
//
// 技术要点：
//   * ssh.exe 的密码提示读的是终端，GUI 没有终端 —— 所以用 SSH_ASKPASS 机制：
//     把本程序自己当作 askpass 程序（--askpass 模式从环境变量 CVFRAME_PW 取密码写 stdout）。
//     实测 OpenSSH_for_Windows_9.5p1 下可用。
//   * 凭据用 DPAPI（CurrentUser 作用域）加密存放在 %LOCALAPPDATA%，磁盘上不是明文。
//   * 使用独立的 known_hosts 与 ssh 配置，不污染用户自己的 ~/.ssh/config；
//     遇到「主机密钥已变更」会自动清除本地记录并重试（重装系统的典型场景）。
//
// 编译（Roslyn）：
//   csc /nologo /codepage:65001 /target:winexe /platform:anycpu ^
//       /r:System.dll /r:System.Core.dll /r:System.Drawing.dll ^
//       /r:System.Windows.Forms.dll /r:System.Net.Http.dll /r:System.Security.dll ^
//       /resource:push-subscription.py ^
//       /out:ClashVergeFrameManager.exe manager.cs

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace ClashVergeFrame
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            // 注意：askpass 由独立的 askpass.exe 承担（见 AskPassPath）。
            // 不能让主程序兼任 —— ssh 调用 SSH_ASKPASS 时传的参数是"提示文本"，
            // 不是我们自定义的开关，主程序收到后无法分辨，会当成正常启动而挂住。

#if SUDO_DIALOG_PREVIEW
            // 仅用于开发期截图/目视检查：sudo 密码框只在密码不匹配时才出现，
            // 平时没法直接看到。带 /define:SUDO_DIALOG_PREVIEW 编译的临时构建
            // 才会包含这段；正式发布的 exe 里没有这些代码。
            MainForm.PreviewSudoDialog(args.Length > 0 && args[0] == "retry");
            return;
#endif


            // 命令行模式：复用与 GUI 完全相同的逻辑，便于脚本化与自动化测试
            if (args.Length > 0 && args[0] == "--cli")
            {
                try
                {
                    var stdout = new StreamWriter(Console.OpenStandardOutput());
                    stdout.AutoFlush = true;
                    Console.SetOut(stdout);
                }
                catch { }

                var form = new MainForm();
                Environment.ExitCode = form.RunCli(args);
                return;
            }

            if (args.Length > 0 && args[0] == "--help")
            {
                Console.WriteLine("GUI 版：直接双击运行。");
                Console.WriteLine("CLI 版：cvframe-cli.exe --cli --host <别名或主机> [--user steamos] [--password xxx]");
                Console.WriteLine("        [--test|--list|--push|--remove <uid>|--restart|--deploy]");
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // 界面线程上的异常默认会直接把进程干掉（用户只看到"程序已停止工作"）。
            // 这里兜住并写日志，至少留下可排查的现场。
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate (object s, ThreadExceptionEventArgs e) { CrashLog(e.Exception); };
            AppDomain.CurrentDomain.UnhandledException +=
                delegate (object s, UnhandledExceptionEventArgs e) { CrashLog(e.ExceptionObject as Exception); };

            try
            {
                Application.Run(new MainForm());
            }
            catch (Exception ex)
            {
                CrashLog(ex);
            }
        }

        /// <summary>把未处理异常写到数据目录旁的 crash.log，避免静默崩溃。</summary>
        private static void CrashLog(Exception ex)
        {
            try
            {
                string dir = Path.Combine(Path.GetTempPath(), "ClashVergeFrame");
                Directory.CreateDirectory(dir);
                string p = Path.Combine(dir, "crash.log");
                File.AppendAllText(p,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine +
                    (ex == null ? "(无异常对象)" : ex.ToString()) + Environment.NewLine + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch { }
        }
    }

    internal sealed class RunResult
    {
        public int ExitCode;
        public string Output = "";
        public bool Ok { get { return ExitCode == 0; } }
    }

    // ==================================================================== 主题
    //
    // 配色不是凭感觉调的，直接取自 Clash Verge Rev 前端的主题定义：
    //   src/pages/_theme.tsx                    defaultDarkTheme.primary_color = #0A84FF
    //   src/components/base/base-page.tsx       深色页面底色                  = #1E1F27
    //   src/components/home/enhanced-card.tsx   深色卡片                     = #282A36
    //   src/components/proxy/proxy-item.tsx     深色条目 / 输入框             = #24252F
    // 这样管理器跟设备上那个 Clash Verge 看起来是同一套东西。
    internal static class Theme
    {
        public static readonly Color Page     = Color.FromArgb(0x1E, 0x1F, 0x27);
        public static readonly Color Card     = Color.FromArgb(0x28, 0x2A, 0x36);
        public static readonly Color Item     = Color.FromArgb(0x24, 0x25, 0x2F);
        public static readonly Color Field    = Color.FromArgb(0x1B, 0x1C, 0x24);
        public static readonly Color BorderDim= Color.FromArgb(0x31, 0x34, 0x40);

        public static readonly Color Primary  = Color.FromArgb(0x0A, 0x84, 0xFF);
        public static readonly Color PrimaryHi= Color.FromArgb(0x3D, 0x9E, 0xFF);
        public static readonly Color PrimaryLo= Color.FromArgb(0x06, 0x6B, 0xD6);
        public static readonly Color Success  = Color.FromArgb(0x30, 0xD1, 0x58);
        public static readonly Color Error    = Color.FromArgb(0xFF, 0x45, 0x3A);
        public static readonly Color Warning  = Color.FromArgb(0xFF, 0x9F, 0x0A);

        public static readonly Color Text     = Color.FromArgb(0xFF, 0xFF, 0xFF);
        public static readonly Color TextDim  = Color.FromArgb(0xA8, 0xAC, 0xBC);
        public static readonly Color TextMute = Color.FromArgb(0x74, 0x78, 0x88);

        public static readonly Font Ui        = new Font("Microsoft YaHei UI", 9F);
        public static readonly Font UiSmall   = new Font("Microsoft YaHei UI", 8.25F);
        public static readonly Font AppTitle  = new Font("Microsoft YaHei UI", 12F, FontStyle.Bold);
        public static readonly Font CardTitle = new Font("Microsoft YaHei UI", 9.75F, FontStyle.Bold);
        public static readonly Font Mono      = new Font("Consolas", 9F);

        public static GraphicsPath Round(Rectangle r, int radius)
        {
            int d = radius * 2;
            var p = new GraphicsPath();
            if (d <= 0 || d > r.Width || d > r.Height) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    /// <summary>圆角卡片，外观对齐 Clash Verge 的 enhanced-card。</summary>
    internal class CardPanel : Panel
    {
        public string Title = "";
        private readonly int _radius;
        public CardPanel(int x, int y, int w, int h, string title, int radius = 10)
        {
            Location = new Point(x, y);
            Size = new Size(w, h);
            BackColor = Theme.Card;
            Title = title;
            _radius = radius;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Page);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = Theme.Round(r, _radius))
            using (var b = new SolidBrush(Theme.Card))
            using (var pen = new Pen(Theme.BorderDim))
            {
                g.FillPath(b, path);
                g.DrawPath(pen, path);
            }
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (Title.Length == 0) return;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // 左侧一根强调色竖条 + 标题，模仿 Clash Verge 的卡片头
            using (var b = new SolidBrush(Theme.Primary))
                g.FillRectangle(b, 20, 15, 3, 15);
            TextRenderer.DrawText(g, Title, Theme.CardTitle, new Point(31, 14), Theme.Text);
        }
    }

    /// <summary>扁平圆角按钮，带 hover / pressed / disabled 三态。</summary>
    internal sealed class FlatButton : Button
    {
        public enum Kind { Primary, Normal, Ghost, Danger }
        public Kind Variant;
        private bool _hover, _down;

        public FlatButton(string text, Kind kind, int x, int y, int w, int h = 32)
        {
            Text = text;
            Variant = kind;
            Location = new Point(x, y);
            Size = new Size(w, h);
            Font = Theme.Ui;
            Cursor = Cursors.Hand;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
            TabStop = false;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                   | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Card);

            Color fill, fg;
            Color border = Color.Empty;

            if (!Enabled)
            {
                fill = Theme.Item; fg = Theme.TextMute;
            }
            else
            {
                switch (Variant)
                {
                    case Kind.Primary:
                        fill = _down ? Theme.PrimaryLo : (_hover ? Theme.PrimaryHi : Theme.Primary);
                        fg = Color.White;
                        break;
                    case Kind.Danger:
                        fill = _down ? Color.FromArgb(0xC0, 0x2C, 0x24)
                                     : (_hover ? Color.FromArgb(0xFF, 0x5C, 0x52) : Color.FromArgb(0xD9, 0x35, 0x2C));
                        fg = Color.White;
                        break;
                    case Kind.Ghost:
                        fill = _down ? Theme.Field : (_hover ? Theme.Item : Color.Empty);
                        fg = _hover ? Theme.Text : Theme.TextDim;
                        border = Theme.BorderDim;
                        break;
                    default:
                        fill = _down ? Color.FromArgb(0x33, 0x36, 0x42)
                                     : (_hover ? Color.FromArgb(0x3B, 0x3E, 0x4C) : Theme.Item);
                        fg = Theme.Text;
                        border = Theme.BorderDim;
                        break;
                }
            }

            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = Theme.Round(r, 7))
            {
                if (fill != Color.Empty)
                    using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                if (border != Color.Empty)
                    using (var pen = new Pen(border)) g.DrawPath(pen, path);
            }
            TextRenderer.DrawText(g, Text, Font, r, fg,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }

    /// <summary>给 TextBox 套一个圆角深色底 + 聚焦高亮边框。</summary>
    internal sealed class FieldBox : Panel
    {
        private bool _focus;
        public FieldBox(TextBox box, int x, int y, int w, int h = 32)
        {
            Location = new Point(x, y);
            Size = new Size(w, h);
            BackColor = Theme.Card;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

            box.BorderStyle = BorderStyle.None;
            box.BackColor = Theme.Field;
            box.ForeColor = box.ReadOnly ? Theme.TextDim : Theme.Text;
            box.Font = Theme.Ui;
            box.Left = 10;
            box.Width = w - 20;
            box.Top = (h - box.PreferredHeight) / 2 + 1;
            box.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(box);

            box.GotFocus += delegate { _focus = true; Invalidate(); };
            box.LostFocus += delegate { _focus = false; Invalidate(); };
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Card);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = Theme.Round(r, 6))
            using (var b = new SolidBrush(Theme.Field))
            using (var pen = new Pen(_focus ? Theme.Primary : Theme.BorderDim))
            {
                g.FillPath(b, path);
                g.DrawPath(pen, path);
            }
        }
    }

    /// <summary>深色主题下的复选框：自绘方块 + 勾。</summary>
    internal sealed class DarkCheck : CheckBox
    {
        private bool _hover;
        public DarkCheck(string text, int x, int y, int w)
        {
            Text = text;
            Location = new Point(x, y);
            Size = new Size(w, 20);
            Font = Theme.Ui;
            ForeColor = Theme.TextDim;
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                   | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }
        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnCheckedChanged(EventArgs e) { Invalidate(); base.OnCheckedChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Card);

            var box = new Rectangle(0, (Height - 15) / 2, 15, 15);
            using (var path = Theme.Round(box, 4))
            {
                if (Checked)
                {
                    using (var b = new SolidBrush(Theme.Primary)) g.FillPath(b, path);
                    using (var pen = new Pen(Color.White, 1.8f))
                    {
                        pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                        g.DrawLines(pen, new[]
                        {
                            new Point(box.X + 3, box.Y + 8),
                            new Point(box.X + 6, box.Y + 11),
                            new Point(box.X + 12, box.Y + 4)
                        });
                    }
                }
                else
                {
                    using (var b = new SolidBrush(Theme.Field)) g.FillPath(b, path);
                    using (var pen = new Pen(_hover ? Theme.TextMute : Theme.BorderDim)) g.DrawPath(pen, path);
                }
            }
            TextRenderer.DrawText(g, Text, Font,
                new Rectangle(21, 0, Width - 21, Height), Enabled ? Theme.TextDim : Theme.TextMute,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }

    /// <summary>卡片里的小标题（字段名）。</summary>
    internal sealed class FieldLabel : Label
    {
        public FieldLabel(string text, int x, int y, int w)
        {
            Text = text;
            Location = new Point(x, y);
            Size = new Size(w, 20);
            AutoSize = false;
            Font = Theme.Ui;
            ForeColor = Theme.TextDim;
            BackColor = Color.Transparent;
            TextAlign = ContentAlignment.MiddleLeft;
        }
    }

    internal sealed class MainForm : Form
    {
        private const string HelperResourceName = "push-subscription.py"; // 内嵌资源名
        private const string HelperDiskName = "cvframe-helper.py";        // 落到本机/设备上的文件名
        private const string ShortcutHelperResource = "steam-shortcuts.py";
        private const string UninstallScriptResource = "uninstall-clash-verge-frame.sh";

        // ---- 本程序自己的 SSH 环境（与用户的 ~/.ssh 完全隔离）----
        // 目录必须"真的能写"：某些环境里 %LOCALAPPDATA% 会因为进程身份/权限而不可写，
        // 所以逐个候选目录做真实写入探测，而不是只 CreateDirectory 就当作可用。
        private static string _appDir;
        private static string AppDir
        {
            get
            {
                if (_appDir != null) return _appDir;
                string[] candidates =
                {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClashVergeFrame"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClashVergeFrame"),
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cvframe-data"),
                    Path.Combine(Environment.CurrentDirectory, "cvframe-data"),
                    Path.Combine(Path.GetTempPath(), "ClashVergeFrame")
                };
                foreach (string c in candidates)
                {
                    if (string.IsNullOrEmpty(c)) continue;
                    try
                    {
                        Directory.CreateDirectory(c);
                        string probe = Path.Combine(c, ".write-probe");
                        File.WriteAllText(probe, "x");
                        File.Delete(probe);
                        _appDir = c;
                        return c;
                    }
                    catch { }
                }
                _appDir = Path.Combine(Path.GetTempPath(), "ClashVergeFrame");
                return _appDir;
            }
        }

        private static string KeyPath { get { return Path.Combine(AppDir, "id_ed25519"); } }
        private static string KnownHostsPath { get { return Path.Combine(AppDir, "known_hosts"); } }
        private static string EmptyConfigPath { get { return Path.Combine(AppDir, "empty_ssh_config"); } }
        private static string CredPath { get { return Path.Combine(AppDir, "cred.dat"); } }
        private static string SelfPath { get { return Assembly.GetExecutingAssembly().Location; } }

        private static string _askPassPath;
        /// <summary>内嵌的专用 askpass 程序，首次使用时释放到数据目录。</summary>
        private static string AskPassPath
        {
            get
            {
                if (_askPassPath != null) return _askPassPath;
                string p = Path.Combine(AppDir, "cvframe-askpass.exe");
                try
                {
                    if (!File.Exists(p))
                    {
                        using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("askpass.exe"))
                        {
                            if (s == null) throw new Exception("内置 askpass.exe 资源缺失");
                            using (var fs = new FileStream(p, FileMode.Create, FileAccess.Write))
                                s.CopyTo(fs);
                        }
                    }
                }
                catch { }
                _askPassPath = p;
                return p;
            }
        }

        private bool _cliMode;
        private string _cliAction = "";
        private string _cliRemoveUid = "";
        private bool _accessChecked;

        private TextBox _host, _user, _password;
        private DarkCheck _chkRemember;
        private TextBox _runPath;
        private TextBox _subUrl, _subName, _userAgent;
        private DarkCheck _chkActivate, _chkFetchOnDevice;
        private TextBox _log;
        private Label _status;
        private Panel _content;
        private CardPanel _logCard;
        private FlatButton _btnTest, _btnSetupKey, _btnOpenKeyDir, _btnDeploy, _btnRestart,
                           _btnPush, _btnList, _btnRemove, _btnBrowse, _btnClear, _btnTestDl,
                           _btnUninstall;
        private bool _busy;

        // CLI 模式下的卸载选项（GUI 模式下由对话框收集）
        private bool _cliPurgeConfig;
        private bool _cliRemoveDeps;
        private bool _cliDryRun;

        // 内容区固定高度（卡片 + 间距 + 上下留白），用于窗口变大时把多余空间给日志卡片
        private const int ContentNeededHeight = 12 + 118 + 12 + 104 + 12 + 92 + 12 + 200 + 12 + 190 + 12;
        private const int LogCardMinHeight = 150;

        public MainForm()
        {
            EnsureAppDir();
            BuildUi();
            AutoLocateRunFile();
            LoadCreds();
        }

        private static void EnsureAppDir()
        {
            try
            {
                Directory.CreateDirectory(AppDir);
                if (!File.Exists(EmptyConfigPath)) File.WriteAllText(EmptyConfigPath, "");
                if (!File.Exists(KnownHostsPath)) File.WriteAllText(KnownHostsPath, "");
            }
            catch { }
        }
        // ------------------------------------------------------------------ UI

        private void BuildUi()
        {
            Text = "Clash Verge Rev × Steam Frame";
            ClientSize = new Size(920, 830);
            MinimumSize = new Size(880, 620);
            Font = Theme.Ui;
            BackColor = Theme.Page;
            ForeColor = Theme.Text;
            StartPosition = FormStartPosition.CenterScreen;
            DoubleBuffered = true;

            // ---- 顶部标题栏：左侧标题，右侧状态徽章 ----
            var header = new Panel { Dock = DockStyle.Top, Height = 56, BackColor = Theme.Page };
            header.Paint += delegate (object s, PaintEventArgs e)
            {
                using (var pen = new Pen(Theme.BorderDim))
                    e.Graphics.DrawLine(pen, 0, header.Height - 1, header.Width, header.Height - 1);
            };

            var appTitle = new Label
            {
                Text = "Clash Verge Rev × Steam Frame",
                Left = 20, Top = 15, AutoSize = true,
                Font = Theme.AppTitle, ForeColor = Theme.Text, BackColor = Color.Transparent
            };

            _status = new Label
            {
                Text = "●  就绪", AutoSize = true,
                Font = Theme.Ui, ForeColor = Theme.TextDim, BackColor = Color.Transparent
            };
            header.Controls.Add(appTitle);
            header.Controls.Add(_status);
            header.Resize += delegate
            {
                _status.Left = header.Width - _status.Width - 22;
                _status.Top = 21;
            };

            // ---- 内容区（可滚动，小屏也不会被裁掉）----
            _content = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                BackColor = Theme.Page
            };
            _content.Resize += delegate { RelayoutLogCard(); };

            Controls.Add(_content);
            Controls.Add(header);

            const int X = 16;
            const int W = 872;
            int y = 12;

            _content.Controls.Add(BuildConnectionCard(X, y, W)); y += 118 + 12;
            _content.Controls.Add(BuildDeployCard(X, y, W));     y += 104 + 12;
            _content.Controls.Add(BuildTunCard(X, y, W));        y += 92 + 12;
            _content.Controls.Add(BuildSubscriptionCard(X, y, W)); y += 200 + 12;

            _logCard = BuildLogCard(X, y, W, 190);
            _content.Controls.Add(_logCard);

            AppendLog("Clash Verge Rev × Steam Frame 管理器");
            AppendLog("两种连法：");
            AppendLog("  A) 只填主机（如 frame）—— 用你已有的 SSH 配置，免密直连；");
            AppendLog("  B) 填主机 + 用户名 + 密码 —— 程序自动生成密钥并装到设备，之后自动免密；");
            AppendLog("     设备重装系统后密钥会失效，程序会用保存的密码自动重建。");
            AppendLog("");
        }

        /// <summary>窗口变大时，把多余高度给日志卡片；变小时保持最小高度（由内容区滚动）。</summary>
        private void RelayoutLogCard()
        {
            if (_logCard == null || _content == null) return;
            int extra = _content.ClientSize.Height - ContentNeededHeight;
            int h = Math.Max(LogCardMinHeight, 190 + extra);
            if (_logCard.Height != h) _logCard.Height = h;
        }

        private CardPanel BuildConnectionCard(int x, int y, int w)
        {
            var card = new CardPanel(x, y, w, 118, "设备连接");

            card.Controls.Add(new FieldLabel("主机 / SSH 别名", 24, 52, 104));
            _host = new TextBox { Text = "frame" };
            card.Controls.Add(new FieldBox(_host, 132, 44, 210));

            card.Controls.Add(new FieldLabel("用户名", 358, 52, 52));
            _user = new TextBox { Text = "steamos" };
            card.Controls.Add(new FieldBox(_user, 410, 44, 110));

            card.Controls.Add(new FieldLabel("密码", 536, 52, 40));
            _password = new TextBox { UseSystemPasswordChar = true };
            card.Controls.Add(new FieldBox(_password, 576, 44, 272));

            _chkRemember = new DarkCheck("记住密码（DPAPI 加密，仅当前 Windows 用户可解密）", 24, 88, 470);
            _chkRemember.Checked = true;
            card.Controls.Add(_chkRemember);

            _btnTest = new FlatButton("测试连接", FlatButton.Kind.Primary, 520, 82, 96);
            _btnTest.Click += delegate { RunAsync("测试连接", TestConnection); };
            card.Controls.Add(_btnTest);

            _btnSetupKey = new FlatButton("配置免密登录", FlatButton.Kind.Normal, 624, 82, 112);
            _btnSetupKey.Click += delegate { RunAsync("配置免密登录", SetupKeyExplicit); };
            card.Controls.Add(_btnSetupKey);

            _btnOpenKeyDir = new FlatButton("打开密钥目录", FlatButton.Kind.Ghost, 744, 82, 104);
            _btnOpenKeyDir.Click += delegate { OpenKeyDir(); };
            card.Controls.Add(_btnOpenKeyDir);

            return card;
        }

        private CardPanel BuildDeployCard(int x, int y, int w)
        {
            var card = new CardPanel(x, y, w, 104, "安装 / 更新");

            card.Controls.Add(new FieldLabel("离线安装包", 24, 48, 80));
            _runPath = new TextBox { ReadOnly = true };
            card.Controls.Add(new FieldBox(_runPath, 110, 40, 650));

            _btnBrowse = new FlatButton("浏览…", FlatButton.Kind.Normal, 768, 40, 80);
            _btnBrowse.Click += delegate { BrowseRunFile(); };
            card.Controls.Add(_btnBrowse);

            _btnDeploy = new FlatButton("一键部署安装", FlatButton.Kind.Primary, 24, 78, 132);
            _btnDeploy.Click += delegate { Deploy(); };
            card.Controls.Add(_btnDeploy);

            _btnRestart = new FlatButton("重启 Clash Verge", FlatButton.Kind.Normal, 164, 78, 148);
            _btnRestart.Click += delegate { RunAsync("重启 Clash Verge", delegate { RestartApp(true); }); };
            card.Controls.Add(_btnRestart);

            _btnUninstall = new FlatButton("一键卸载", FlatButton.Kind.Danger, 320, 78, 110);
            _btnUninstall.Click += delegate { Uninstall(); };
            card.Controls.Add(_btnUninstall);

            return card;
        }

        private CardPanel BuildTunCard(int x, int y, int w)
        {
            var card = new CardPanel(x, y, w, 92, "让 SteamVR / 游戏模式里的 Steam 也走代理");

            var t = new Label
            {
                Left = 24, Top = 44, Width = 824, Height = 40,
                AutoSize = false, BackColor = Color.Transparent,
                Font = Theme.UiSmall, ForeColor = Theme.TextDim,
                Text = "系统代理只管桌面模式。游戏模式 / SteamVR 里的 Steam 要走代理，请在头显里打开 Clash Verge，"
                     + "开启「TUN 模式」；它会弹出权限请求，\r\n这时切到桌面模式输入系统密码即可，装好一次以后就不用再输了。"
            };
            card.Controls.Add(t);
            return card;
        }

        private CardPanel BuildSubscriptionCard(int x, int y, int w)
        {
            var card = new CardPanel(x, y, w, 200, "订阅推送");

            card.Controls.Add(new FieldLabel("订阅链接", 24, 48, 64));
            _subUrl = new TextBox();
            card.Controls.Add(new FieldBox(_subUrl, 96, 40, 752));

            card.Controls.Add(new FieldLabel("名称", 24, 88, 40));
            _subName = new TextBox();
            card.Controls.Add(new FieldBox(_subName, 72, 80, 190));

            card.Controls.Add(new FieldLabel("User-Agent", 278, 88, 84));
            _userAgent = new TextBox { Text = "clash-verge/v2.5.7" };
            card.Controls.Add(new FieldBox(_userAgent, 370, 80, 200));

            _chkActivate = new DarkCheck("推送后启用为当前配置", 24, 122, 200);
            _chkActivate.Checked = true;
            card.Controls.Add(_chkActivate);

            _chkFetchOnDevice = new DarkCheck("由设备自行下载（默认由本机下载后推送，可用本机代理）", 270, 122, 500);
            card.Controls.Add(_chkFetchOnDevice);

            _btnPush = new FlatButton("推送订阅", FlatButton.Kind.Primary, 24, 152, 120);
            _btnPush.Click += delegate { PushSubscription(); };
            card.Controls.Add(_btnPush);

            _btnList = new FlatButton("读取设备上的订阅", FlatButton.Kind.Normal, 152, 152, 152);
            _btnList.Click += delegate { RunAsync("读取订阅列表", ListSubscriptions); };
            card.Controls.Add(_btnList);

            _btnRemove = new FlatButton("删除订阅…", FlatButton.Kind.Normal, 312, 152, 110);
            _btnRemove.Click += delegate { RemoveSubscription(); };
            card.Controls.Add(_btnRemove);

            _btnTestDl = new FlatButton("在本机测试下载", FlatButton.Kind.Ghost, 430, 152, 136);
            _btnTestDl.Click += delegate { RunAsync("测试下载订阅", TestDownload); };
            card.Controls.Add(_btnTestDl);

            return card;
        }

        private CardPanel BuildLogCard(int x, int y, int w, int h)
        {
            var card = new CardPanel(x, y, w, h, "日志");

            _btnClear = new FlatButton("清空", FlatButton.Kind.Ghost, w - 24 - 72, 12, 72, 26);
            _btnClear.Click += delegate { _log.Clear(); };
            card.Controls.Add(_btnClear);

            var wrap = new Panel
            {
                Left = 20, Top = 46, Width = w - 40, Height = h - 46 - 16,
                BackColor = Theme.Field,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
                Padding = new Padding(8, 6, 8, 6)
            };

            _log = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.None,
                BackColor = Theme.Field,
                ForeColor = Theme.TextDim,
                Font = Theme.Mono
            };
            wrap.Controls.Add(_log);
            card.Controls.Add(wrap);

            return card;
        }


        // ------------------------------------------------------------------ 基础设施

        private string Host { get { return _host.Text.Trim(); } }
        private string UserName { get { return _user.Text.Trim(); } }
        private string Password { get { return _password.Text; } }
        private bool HasPassword { get { return Password.Length > 0; } }

        private bool _aliasFallback;   // 托管模式连不上时，回退用用户自己的 SSH 配置

        /// <summary>填了用户名（或主机里带 @）就走"程序托管密钥"模式。</summary>
        private bool ManagedMode
        {
            get { return !_aliasFallback && (UserName.Length > 0 || Host.IndexOf('@') >= 0); }
        }

        private string Target()
        {
            string h = Host;
            if (UserName.Length > 0 && h.IndexOf('@') < 0) return UserName + "@" + h;
            return h;
        }

        private void SetStatus(string s)
        {
            if (InvokeRequired) { BeginInvoke(new Action<string>(SetStatus), s); return; }
            _status.Text = "●  " + s;
            // 状态徽章按语义着色，跟 Clash Verge 的 success / error / warning 一致
            _status.ForeColor =
                s.IndexOf("失败", StringComparison.Ordinal) >= 0 ? Theme.Error
              : s.IndexOf("正常", StringComparison.Ordinal) >= 0 ? Theme.Success
              : s.IndexOf("完成", StringComparison.Ordinal) >= 0 ? Theme.Success
              : s.IndexOf("…", StringComparison.Ordinal) >= 0 ? Theme.Warning
              : Theme.TextDim;
        }

        private void AppendLog(string line)
        {
            if (InvokeRequired) { BeginInvoke(new Action<string>(AppendLog), line); return; }
            _log.AppendText(line + Environment.NewLine);
            _log.SelectionStart = _log.TextLength;
            _log.ScrollToCaret();
        }

        private void SetBusy(bool busy)
        {
            if (InvokeRequired) { BeginInvoke(new Action<bool>(SetBusy), busy); return; }
            _busy = busy;
            _btnTest.Enabled = !busy;
            _btnSetupKey.Enabled = !busy;
            _btnDeploy.Enabled = !busy;
            _btnRestart.Enabled = !busy;
            _btnPush.Enabled = !busy;
            _btnList.Enabled = !busy;
            _btnRemove.Enabled = !busy;
            _btnBrowse.Enabled = !busy;
            _btnTestDl.Enabled = !busy;
            _btnOpenKeyDir.Enabled = !busy;
            _btnUninstall.Enabled = !busy;
            Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
        }

        private void RunAsync(string title, Action work)
        {
            if (_busy) { AppendLog("已有任务在执行中，请稍候。"); return; }
            if (Host.Length == 0) { MessageBox.Show("请先填写主机。", title, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }

            var t = new Thread(delegate ()
            {
                SetBusy(true);
                SetStatus(title + "…");
                AppendLog("=== " + title + " ===");
                try { work(); }
                catch (Exception ex) { AppendLog("异常：" + ex.Message); }
                finally
                {
                    SetBusy(false);
                    SetStatus("就绪");
                    AppendLog("");
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        private static string Q(string s) { return "\"" + s.Replace("\"", "\\\"") + "\""; }
        private static string QSingle(string s) { return "'" + s.Replace("'", "'\\''") + "'"; }

        /// <summary>执行外部程序并捕获输出。stdin 不为 null 时写入标准输入。带超时保护。</summary>
        private RunResult Exec(string exe, string args, string stdin, bool passwordAuth)
        {
            return Exec(exe, args, stdin, passwordAuth, 180000);
        }

        private RunResult Exec(string exe, string args, string stdin, bool passwordAuth, int timeoutMs)
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // 必须始终重定向 stdin。否则子进程（ssh/scp）会继承本进程的控制台输入，
                // 把用户后面要输入的内容（例如 sudo 密码）先读走，导致 ReadLine 拿到
                // 空值或串行；在没有控制台时还可能永久阻塞。
                RedirectStandardInput = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            if (passwordAuth && HasPassword) ApplyPasswordEnv(psi);

            using (var p = Process.Start(psi))
            {
                var sbOut = new StringBuilder();
                var sbErr = new StringBuilder();
                // 两个流都必须并发读，否则管道缓冲写满会死锁
                var tOut = new Thread(delegate () { try { sbOut.Append(p.StandardOutput.ReadToEnd()); } catch { } });
                var tErr = new Thread(delegate () { try { sbErr.Append(p.StandardError.ReadToEnd()); } catch { } });
                tOut.IsBackground = true;
                tErr.IsBackground = true;
                tOut.Start();
                tErr.Start();

                // 没有输入时也要立刻关掉，让子进程读到 EOF，而不是等一个永远不来的输入
                try
                {
                    if (stdin != null) p.StandardInput.Write(stdin);
                    p.StandardInput.Close();
                }
                catch { }

                bool exited = p.WaitForExit(timeoutMs);
                if (!exited)
                {
                    try { p.Kill(); } catch { }
                    try { p.WaitForExit(5000); } catch { }
                }
                tOut.Join(3000);
                tErr.Join(3000);

                var r = new RunResult();
                r.ExitCode = exited ? p.ExitCode : -1;
                r.Output = sbOut.ToString();
                if (sbErr.Length > 0) r.Output += (r.Output.Length > 0 ? "\n" : "") + sbErr;
                if (!exited) r.Output += Environment.NewLine + "[超时] 命令超过 " + (timeoutMs / 1000) + " 秒未结束，已终止。";
                return r;
            }
        }

        /// <summary>
        /// 让 ssh 用我们内嵌的 askpass 程序取密码。密码通过环境变量传给子进程，
        /// 不落盘、不进命令行（不会被其它进程看到）。
        /// </summary>
        private void ApplyPasswordEnv(ProcessStartInfo psi)
        {
            psi.EnvironmentVariables["SSH_ASKPASS"] = AskPassPath;
            psi.EnvironmentVariables["SSH_ASKPASS_REQUIRE"] = "force";
            psi.EnvironmentVariables["CVFRAME_PW"] = Password;
            psi.EnvironmentVariables["DISPLAY"] = "cvframe"; // 某些 OpenSSH 版本要求 DISPLAY 存在才启用 askpass
        }

        private static bool IsHostKeyChanged(string o)
        {
            if (string.IsNullOrEmpty(o)) return false;
            return o.IndexOf("REMOTE HOST IDENTIFICATION HAS CHANGED", StringComparison.OrdinalIgnoreCase) >= 0
                || o.IndexOf("Host key verification failed", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>执行并在"主机密钥已变更"时清掉本地 known_hosts 重试一次（重装系统的典型情况）。</summary>
        private RunResult ExecRetry(string exe, string args, string stdin, bool passwordAuth)
        {
            return ExecRetry(exe, args, stdin, passwordAuth, 180000);
        }

        private RunResult ExecRetry(string exe, string args, string stdin, bool passwordAuth, int timeoutMs)
        {
            RunResult r = Exec(exe, args, stdin, passwordAuth, timeoutMs);
            if (IsHostKeyChanged(r.Output))
            {
                AppendLog("检测到设备主机密钥已变更（通常是重装了系统），清除本地记录后重试…");
                try { File.WriteAllText(KnownHostsPath, ""); } catch { }
                r = Exec(exe, args, stdin, passwordAuth, timeoutMs);
            }
            return r;
        }

        // ---- ssh 选项 ----

        private string BaseOpts()
        {
            var sb = new StringBuilder();
            sb.Append("-o UserKnownHostsFile=").Append(Q(KnownHostsPath)).Append(" ");
            sb.Append("-o StrictHostKeyChecking=accept-new ");
            sb.Append("-o ConnectTimeout=15 ");
            return sb.ToString();
        }

        /// <summary>密钥认证用的选项。托管模式下完全绕开用户的 ssh 配置，行为可预期。</summary>
        private string KeyOpts()
        {
            // BatchMode：认证不可用时直接报错退出，而不是弹出密码提示。
            // 标准输入被重定向（CLI 重定向、后台任务、GUI 子进程）时，
            // 交互式提示会永久阻塞 —— 宁可快速失败也不要卡住。
            var sb = new StringBuilder("-o BatchMode=yes ");
            sb.Append(BaseOpts());
            if (ManagedMode)
            {
                sb.Append("-F ").Append(Q(EmptyConfigPath)).Append(" ");
                if (File.Exists(KeyPath))
                    sb.Append("-o IdentitiesOnly=yes -i ").Append(Q(KeyPath)).Append(" ");
            }
            return sb.ToString();
        }

        /// <summary>密码认证用的选项（配合 SSH_ASKPASS）。</summary>
        private string PasswordOpts()
        {
            var sb = new StringBuilder(BaseOpts());
            sb.Append("-F ").Append(Q(EmptyConfigPath)).Append(" ");
            sb.Append("-o PreferredAuthentications=password -o PubkeyAuthentication=no ");
            sb.Append("-o NumberOfPasswordPrompts=1 -o BatchMode=no ");
            return sb.ToString();
        }

        private RunResult SshKey(string remoteCommand)
        {
            return ExecRetry("ssh", "-o BatchMode=yes " + KeyOpts() + " " + Q(Target()) + " " + Q(remoteCommand), null, false);
        }

        // ------------------------------------------------------------------ 密钥与凭据

        private void EnsureKeyPair()
        {
            EnsureAppDir();
            if (File.Exists(KeyPath)) return;
            AppendLog("生成新的 SSH 密钥对：" + KeyPath);
            RunResult r = Exec("ssh-keygen",
                "-t ed25519 -N \"\" -C \"cvframe-manager\" -f " + Q(KeyPath), null, false);
            if (!r.Ok || !File.Exists(KeyPath))
                throw new Exception("ssh-keygen 失败：" + r.Output.Trim());
            AppendLog("密钥已生成。");
        }

        private const string InstallKeyScript =
            "set -e\n" +
            "umask 077\n" +
            "mkdir -p \"$HOME/.ssh\"\n" +
            "f=\"$HOME/.ssh/authorized_keys\"\n" +
            "key=\"$(tr -d '\\r\\n' < \"$HOME/cvframe-key.pub\")\"\n" +
            "touch \"$f\"\n" +
            "if grep -qxF \"$key\" \"$f\" 2>/dev/null; then echo KEY_ALREADY_PRESENT; else printf '%s\\n' \"$key\" >> \"$f\"; echo KEY_INSTALLED; fi\n" +
            "chmod 700 \"$HOME/.ssh\"\n" +
            "chmod 600 \"$f\"\n" +
            "rm -f \"$HOME/cvframe-key.pub\"\n" +
            "echo KEY_SETUP_DONE\n";

        /// <summary>用密码登录，把本机公钥装到设备的 authorized_keys。</summary>
        private bool BootstrapKey()
        {
            if (!HasPassword)
            {
                AppendLog("需要密码才能装公钥，但密码为空。");
                return false;
            }

            EnsureKeyPair();
            string pub = KeyPath + ".pub";
            if (!File.Exists(pub)) { AppendLog("找不到公钥文件：" + pub); return false; }

            AppendLog("用密码登录并推送公钥…");
            RunResult r = ExecRetry("scp",
                PasswordOpts() + " " + Q(pub) + " " + Q(Target() + ":~/cvframe-key.pub"), null, true);
            if (!r.Ok)
            {
                AppendLog("推送公钥失败：" + r.Output.Trim());
                return false;
            }

            AppendLog("在设备上安装公钥…");
            r = ExecRetry("ssh",
                PasswordOpts() + " " + Q(Target()) + " " + Q("bash -s"), InstallKeyScript, true);
            AppendLog(r.Output.Trim());
            if (!r.Ok) return false;

            SaveCredsIfNeeded();
            return true;
        }

        /// <summary>
        /// 保证拿到可用的访问方式：先试密钥；不行且有密码就用密码重建密钥。
        /// 这就是"设备重装系统后密钥自动更新"的实现。
        /// </summary>
        /// <summary>
        /// 打开托管数据目录（密钥、known_hosts、凭据都在这里）。
        /// 原实现直接 Process.Start("explorer.exe", 路径) 且 catch 全吞，出问题时用户只看到
        /// 一个来历不明的系统弹窗。这里改成多重回退，并且失败时把路径和原因显示出来。
        /// </summary>
        private void OpenKeyDir()
        {
            string dir = AppDir;
            var errors = new StringBuilder();

            try
            {
                Directory.CreateDirectory(dir);
            }
            catch (Exception ex)
            {
                errors.AppendLine("  创建目录失败：" + ex.Message);
            }

            // 方式 1：ShellExecute 直接打开目录（最标准，交给系统默认处理器）
            try
            {
                Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
                return;
            }
            catch (Exception ex) { errors.AppendLine("  ShellExecute 打开失败：" + ex.Message); }

            // 方式 2：显式交给 explorer，路径加引号
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + dir + "\"") { UseShellExecute = true });
                return;
            }
            catch (Exception ex) { errors.AppendLine("  explorer 打开失败：" + ex.Message); }

            // 都失败：把路径给用户，并放进剪贴板
            try { Clipboard.SetText(dir); } catch { }
            MessageBox.Show(
                "打不开这个目录：\n\n" + dir + "\n\n原因：\n" + errors +
                "\n路径已复制到剪贴板，可粘到资源管理器地址栏。",
                "打开密钥目录", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        /// <summary>按当前模式试一次密钥登录。</summary>
        private bool TryKey()
        {
            RunResult r = SshKey("echo __CVFRAME_OK__");
            return r.Ok && r.Output.IndexOf("__CVFRAME_OK__", StringComparison.Ordinal) >= 0;
        }

        private bool EnsureAccess()
        {
            if (_accessChecked) return true;

            // 1) 先按当前模式试密钥
            if (TryKey())
            {
                AppendLog("已通过密钥登录：" + Target());
                _accessChecked = true;
                return true;
            }

            // 2) 有密码 → 用密码重建托管密钥（设备重装系统后就是走这条）
            if (HasPassword)
            {
                AppendLog("密钥登录不可用（设备重装系统后 authorized_keys 会被清空，这是预期情况）。");
                AppendLog("改用密码登录并自动重建密钥…");
                if (BootstrapKey() && TryKey())
                {
                    AppendLog("公钥已装好，后续操作免密。");
                    _accessChecked = true;
                    return true;
                }
                AppendLog("装好公钥后仍无法免密登录。");
                return false;
            }

            // 3) 托管模式下没给密码 → 退回用用户自己的 SSH 配置试一次。
            //    用户名框预填了 steamos，所以很多人只会填主机；不这样回退的话，
            //    明明 %USERPROFILE%\.ssh\config 里配好了也会连不上。
            if (ManagedMode)
            {
                AppendLog("程序托管的密钥不可用，改用你已有的 SSH 配置试一次…");
                _aliasFallback = true;
                if (TryKey())
                {
                    AppendLog("已通过你已有的 SSH 配置登录：" + Target());
                    _accessChecked = true;
                    return true;
                }
                _aliasFallback = false;
            }

            // 4) 都失败
            AppendLog("登录失败。可以：");
            AppendLog("  * 确认主机别名正确（如 frame 已在 %USERPROFILE%\\.ssh\\config 里配好）；");
            AppendLog("  * 或者填上「密码」，程序会自动配好免密登录。");
            return false;
        }

        private void SetupKeyExplicit()
        {
            if (!ManagedMode)
            {
                AppendLog("当前是「别名模式」，直接用你已有的 SSH 配置即可，无需配置密钥。");
                AppendLog("如果要让程序托管密钥，请填上用户名（例如 steamos）。");
                return;
            }
            if (!HasPassword)
            {
                AppendLog("请先填入密码。");
                return;
            }

            // 强制重建：先删掉本地密钥，确保这次会重新生成并上传
            try { if (File.Exists(KeyPath)) File.Delete(KeyPath); } catch { }
            try { if (File.Exists(KeyPath + ".pub")) File.Delete(KeyPath + ".pub"); } catch { }
            _accessChecked = false;

            if (EnsureAccess())
                AppendLog("配置完成，以后无需再输密码。");
            else
                AppendLog("配置失败。");
        }

        private void SaveCredsIfNeeded()
        {
            if (!_chkRemember.Checked) return;
            try
            {
                EnsureAppDir();
                string data = UserName + "\n" + Password;
                byte[] enc = ProtectedData.Protect(
                    Encoding.UTF8.GetBytes(data), null, DataProtectionScope.CurrentUser);
                File.WriteAllBytes(CredPath, enc);
                AppendLog("凭据已用 DPAPI 加密保存（仅当前 Windows 用户可解密）。");
            }
            catch (Exception ex) { AppendLog("保存凭据失败：" + ex.Message); }
        }

        private void LoadCreds()
        {
            try
            {
                if (!File.Exists(CredPath)) return;
                byte[] dec = ProtectedData.Unprotect(
                    File.ReadAllBytes(CredPath), null, DataProtectionScope.CurrentUser);
                string s = Encoding.UTF8.GetString(dec);
                int i = s.IndexOf('\n');
                if (i > 0)
                {
                    _user.Text = s.Substring(0, i);
                    _password.Text = s.Substring(i + 1);
                    _chkRemember.Checked = true;
                }
            }
            catch { }
            // 连不上旧已知主机时，重复的 known_hosts 会被自动重建
        }

        // ------------------------------------------------------------------ 连接测试

        private void TestConnection()
        {
            if (!EnsureAccess())
            {
                SetStatus("连接失败");
                return;
            }

            RunResult r = SshKey("echo __OK__; uname -m; . /etc/os-release 2>/dev/null && echo \"$PRETTY_NAME variant=$VARIANT_ID\"; "
                               + "pacman -Q webkit2gtk-4.1 2>/dev/null || echo 'webkit2gtk-4.1 MISSING'; "
                               + "test -x ~/.local/bin/clash-verge && echo 'clash-verge INSTALLED' || echo 'clash-verge NOT-INSTALLED'; "
                               // 必须用 -x 精确匹配进程名：用 -f 会匹配到 ssh 自己的 bash -c 命令行，产生误报
                               + "pgrep -x clash-verge >/dev/null && echo 'app RUNNING' || echo 'app STOPPED'; "
                               + "pgrep -x verge-mihomo >/dev/null && echo 'core RUNNING' || echo 'core STOPPED'");

            AppendLog(r.Output.TrimEnd());
            if (!r.Ok) { SetStatus("连接失败"); return; }
            SetStatus("连接正常");
        }

        // ------------------------------------------------------------------ 部署

        private void AutoLocateRunFile()
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            string[] candidates =
            {
                Path.Combine(dir, "install-clash-verge-frame.run"),
                Path.Combine(dir, "dist", "install-clash-verge-frame.run"),
                Path.Combine(Environment.CurrentDirectory, "install-clash-verge-frame.run")
            };
            foreach (string c in candidates)
            {
                if (File.Exists(c)) { _runPath.Text = Path.GetFullPath(c); return; }
            }
        }

        private void BrowseRunFile()
        {
            using (var d = new OpenFileDialog())
            {
                d.Title = "选择 install-clash-verge-frame.run";
                d.Filter = "安装包 (*.run)|*.run|所有文件 (*.*)|*.*";
                string dir = AppDomain.CurrentDomain.BaseDirectory;
                if (Directory.Exists(dir)) d.InitialDirectory = dir;
                if (d.ShowDialog(this) == DialogResult.OK) _runPath.Text = d.FileName;
            }
        }

        // ------------------------------------------------------------------ 需要 sudo 的远端操作
        //
        // 目标行为（按用户要求）：
        //   1. 默认直接把 SSH 连接密码当成 sudo 密码喂过去，绝大多数机器上两者是同一个；
        //   2. 只有被 sudo 拒绝、或压根没有 SSH 密码时，才弹一个**明文显示**输入内容的窗口问用户；
        //   3. 拿到密码后重发命令继续安装/卸载。
        // 密码通过"临时文件 + 600 权限"传给设备（不放进命令行，避免出现在 ps 里），
        // 远端脚本读完立刻删除。

        private const string SudoNeededMarker = "CVFRAME_SUDO_PW_NEEDED";
        private const string SudoRejectedMarker = "CVFRAME_SUDO_PW_REJECTED";
        private string _sudoPassword;   // 仅内存缓存，不落盘：一次问过之后本次运行不再重复问

        /// <summary>把 sudo 密码写到设备上的 ~/.cvframe-sudo-pw（600），脚本读完自行删除。</summary>
        private void PushSudoPassword(string pw)
        {
            string local = WriteAuxFileToDisk(".cvframe-sudo-pw", pw);
            RunResult r = ExecRetry("scp",
                KeyOpts() + " " + Q(local) + " " + Q(Target() + ":~/.cvframe-sudo-pw"), null, false);
            try { File.Delete(local); } catch { }
            if (!r.Ok) throw new Exception("无法把 sudo 密码传到设备：" + r.Output.Trim());
            SshKey("chmod 600 ~/.cvframe-sudo-pw 2>/dev/null; true");
        }

        private string RunRemote(string remoteCommand)
        {
            // 安装要解 120 MB 载荷，给足时间
            RunResult r = ExecRetry("ssh",
                "-o BatchMode=yes " + KeyOpts() + " " + Q(Target()) + " " + Q(remoteCommand),
                null, false, 900000);
            return r.Output ?? "";
        }

        private static bool SudoRejected(string output)
        {
            return output != null && output.IndexOf(SudoRejectedMarker, StringComparison.Ordinal) >= 0;
        }

        private static bool SudoNeeded(string output)
        {
            return output != null && (output.IndexOf(SudoNeededMarker, StringComparison.Ordinal) >= 0
                                   || output.IndexOf(SudoRejectedMarker, StringComparison.Ordinal) >= 0);
        }

        /// <summary>
        /// 执行一个可能需要 sudo 的远端命令：先自动用 SSH 密码，失败再问用户。
        /// 返回远端输出；返回 null 表示用户取消。
        /// </summary>
        private string RunWithSudo(string remoteCommand, string what)
        {
            // 本次会话里已经问过并成功过，就直接复用
            if (_sudoPassword != null)
            {
                PushSudoPassword(_sudoPassword);
                string cached = RunRemote(remoteCommand);
                if (!SudoNeeded(cached)) return cached;
                AppendLog("之前输入的 sudo 密码已失效，需要重新输入。");
                _sudoPassword = null;
            }

            if (HasPassword)
            {
                AppendLog("先用 SSH 连接密码尝试 sudo …");
                PushSudoPassword(Password);
                string first = RunRemote(remoteCommand);
                if (!SudoNeeded(first))
                {
                    _sudoPassword = Password;   // 两者相同，记下来
                    AppendLog("SSH 密码同时通过了 sudo 验证。");
                    return first;
                }
                AppendLog(SudoRejected(first)
                    ? "SSH 连接密码不是这台机器的 sudo 密码。"
                    : "这台机器需要单独提供 sudo 密码。");
            }
            else
            {
                string first = RunRemote(remoteCommand);
                if (!SudoNeeded(first)) return first;
                AppendLog("当前没有可用的 SSH 密码，需要单独提供 sudo 密码。");
            }

            for (int attempt = 0; attempt < 3; attempt++)
            {
                string pw = AskSudoPassword(attempt > 0);
                if (pw == null)
                {
                    AppendLog("已取消" + what + "。");
                    return null;
                }
                PushSudoPassword(pw);
                string output = RunRemote(remoteCommand);
                if (!SudoNeeded(output))
                {
                    _sudoPassword = pw;
                    AppendLog("sudo 密码验证通过。");
                    return output;
                }
            }

            AppendLog("连续 3 次都没能通过 sudo 验证，已停止" + what + "。");
            return "";
        }

        private string AskSudoPassword(bool retry)
        {
            // CLI 模式下没有消息循环，弹不了窗口，改成终端读一行
            if (_cliMode)
            {
                Console.WriteLine(retry ? "sudo 密码不对，请重新输入（直接回车取消）："
                                        : "需要 sudo 密码（直接回车取消）：");
                Console.Write("> ");
                string line = Console.ReadLine();
                return string.IsNullOrEmpty(line) ? null : line;
            }

            using (var dlg = new SudoPasswordForm(retry))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return null;
                return dlg.Password;
            }
        }

        private void Deploy()
        {
            string run = _runPath.Text.Trim();
            if (run.Length == 0 || !File.Exists(run))
            {
                MessageBox.Show("找不到安装包。请点「浏览…」选择 install-clash-verge-frame.run。",
                    "一键部署", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            RunAsync("部署安装", delegate { DeployCore(run); });
        }

        /// <summary>
        /// 推送安装包并远程执行。sudo 密码默认自动用 SSH 密码，不行才弹窗问 ——
        /// 所以这里不再需要开独立控制台窗口。
        /// </summary>
        private void DeployCore(string runFile)
        {
            if (!EnsureAccess()) return;

            long size = new FileInfo(runFile).Length;
            AppendLog("推送安装包（" + (size / 1048576) + " MB），请稍候…");
            RunResult r = ExecRetry("scp",
                KeyOpts() + " " + Q(runFile) + " " + Q(Target() + ":~/install-clash-verge-frame.run"),
                null, false, 1800000);
            if (!r.Ok)
            {
                AppendLog("推送失败：" + r.Output.Trim());
                return;
            }
            AppendLog("安装包已就位。");

            string remote = "chmod +x ~/install-clash-verge-frame.run && ~/install-clash-verge-frame.run --with-tun";
            string output = RunWithSudo(remote, "安装");
            if (output == null) return;

            AppendLog("");
            AppendLog(output.TrimEnd());
            AppendLog("");
            DedupeSteamShortcuts();
            AppendLog("安装流程结束，正在回读设备状态…");
            _accessChecked = false;
            TestConnection();
        }

        /// <summary>
        /// 安装器每次都会调 steamos-add-to-steam 新增一条非 Steam 快捷方式（它不去重），
        /// 重装几次库里就会堆出好几条同名条目。这里顺手清成一条。
        /// Steam 运行期间写进去的改动会被它退出时覆盖，所以这种情况下只提示、不假装成功。
        /// </summary>
        private void DedupeSteamShortcuts()
        {
            try
            {
                string helper = PushEmbeddedScript(ShortcutHelperResource, "steam-shortcuts.py");

                // 先看 Steam 在不在跑：在跑的话写进去也会被它退出时覆盖，白费力气
                string steam = RunRemote("pgrep -x steam >/dev/null 2>&1 && echo RUNNING || echo STOPPED").Trim();
                if (steam.IndexOf("RUNNING", StringComparison.Ordinal) >= 0)
                {
                    string cnt = RunRemote("python3 " + helper + " --list 2>&1 | grep -c 'Clash Verge'").Trim();
                    AppendLog("Steam 正在运行（库里有 " + cnt + " 条 Clash Verge）。"
                            + "它在退出时会覆盖快捷方式配置，重复条目要等 Steam 完全退出后再清。");
                    return;
                }

                string before = RunRemote("python3 " + helper + " --list 2>&1 | grep -c 'Clash Verge'").Trim();
                string deduped = RunRemote("python3 " + helper + " --dedupe 2>&1");

                string summary = "";
                foreach (string line in deduped.Split('\n'))
                {
                    string t = line.Trim();
                    if (t.StartsWith("处理完成") || t.StartsWith("将删除") || t.Contains("去重后剩余"))
                        summary += (summary.Length == 0 ? "" : "；") + t;
                }
                AppendLog("Steam 库条目检查：原有 " + before + " 条 Clash Verge"
                        + (summary.Length > 0 ? "，" + summary : "，无需改动"));
            }
            catch (Exception ex)
            {
                AppendLog("Steam 库条目去重跳过：" + ex.Message);
            }
        }

        // ------------------------------------------------------------------ 卸载

        /// <summary>
        /// 一键卸载：先让用户确认，再到后台把内嵌的卸载脚本与 Steam 快捷方式助手推到设备并执行。
        /// 确认框必须在任何网络动作之前弹出 —— 否则点下去界面会先卡住几秒（SSH 是同步的），
        /// 看起来像没反应。
        /// </summary>
        private void Uninstall()
        {
            bool purgeConfig = _cliPurgeConfig;
            bool removeDeps = _cliRemoveDeps;

            if (!_cliMode)
            {
                using (var dlg = new UninstallForm())
                {
                    if (dlg.ShowDialog(this) != DialogResult.OK)
                    {
                        AppendLog("已取消卸载。");
                        return;
                    }
                    purgeConfig = dlg.PurgeConfig;
                    removeDeps = dlg.RemoveDeps;
                }
                RunAsync("卸载 Clash Verge", delegate { UninstallCore(purgeConfig, removeDeps); });
                return;
            }

            UninstallCore(purgeConfig, removeDeps);
        }

        private void UninstallCore(bool purgeConfig, bool removeDeps)
        {
            if (!EnsureAccess()) return;

            AppendLog("=== 卸载 Clash Verge ===");
            AppendLog("配置与订阅：" + (purgeConfig ? "一并删除" : "保留"));
            AppendLog("系统依赖 webkit2gtk-4.1：" + (removeDeps ? "一并卸载" : "保留"));

            string script, shortcuts;
            try
            {
                script = PushEmbeddedScript(UninstallScriptResource, "uninstall-clash-verge-frame.sh");
                // 卸载脚本要按二进制 VDF 解析 Steam 的 shortcuts.vdf，需要这个助手
                shortcuts = PushEmbeddedScript(ShortcutHelperResource, "steam-shortcuts.py");
            }
            catch (Exception ex)
            {
                AppendLog("准备卸载脚本失败：" + ex.Message);
                return;
            }
            AppendLog("卸载脚本已就位：" + script + " / " + shortcuts);

            string remote = "bash " + script + " --yes"
                          + (purgeConfig ? " --purge-config" : "")   // 删配置必须显式声明；默认保留
                          + (removeDeps ? " --remove-deps" : "")
                          + (_cliDryRun ? " --dry-run" : "");

            AppendLog("执行：" + remote);

            string output;
            if (_cliDryRun)
            {
                // dry-run 不需要提权，直接跑并把输出打到终端
                RunResult dr = ExecRetry("ssh", "-o BatchMode=yes " + KeyOpts() + " "
                                       + Q(Target()) + " " + Q(remote), null, false, 300000);
                output = dr.Output;
            }
            else
            {
                output = RunWithSudo(remote, "卸载");
                if (output == null) return;
            }

            AppendLog("");
            AppendLog(output.TrimEnd());
            AppendLog("");
            AppendLog("正在回读设备状态…");
            _accessChecked = false;
            TestConnection();
        }

        // ------------------------------------------------------------------ 订阅

        private static string WriteAuxFileToDisk(string name, string text)
        {
            string[] dirs =
            {
                Path.GetTempPath(),
                AppDomain.CurrentDomain.BaseDirectory,
                Environment.CurrentDirectory
            };
            Exception last = null;
            foreach (string d in dirs)
            {
                try
                {
                    string p = Path.Combine(d, name);
                    File.WriteAllText(p, text, new UTF8Encoding(false));
                    return p;
                }
                catch (Exception ex) { last = ex; }
            }
            throw new Exception("无法把 " + name + " 写到磁盘：" + (last == null ? "未知原因" : last.Message));
        }

        /// <summary>把内嵌的脚本释放到本机再 scp 到设备，返回设备上的路径（~/xxx）。</summary>
        private string PushEmbeddedScript(string resourceName, string diskName)
        {
            string text;
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
            {
                if (s == null) throw new Exception("内置的 " + resourceName + " 资源缺失");
                using (var sr = new StreamReader(s, Encoding.UTF8)) text = sr.ReadToEnd();
            }

            // 远端脚本按 LF 存；write 出来的可能是 CRLF，bash 对 CRLF 很敏感
            text = text.Replace("\r\n", "\n").Replace("\r", "\n");
            string local = WriteAuxFileToDisk(diskName, text);
            RunResult r = ExecRetry("scp",
                KeyOpts() + " " + Q(local) + " " + Q(Target() + ":~/" + diskName), null, false);
            if (!r.Ok)
            {
                AppendLog("推送 " + diskName + " 失败：" + r.Output.Trim());
                throw new Exception("无法把 " + diskName + " 传到设备");
            }
            return "~/" + diskName;
        }

        private string EnsureHelperOnDevice()
        {
            return PushEmbeddedScript(HelperResourceName, HelperDiskName);
        }

        private string DownloadSubscription(string url)
        {
            var handler = new HttpClientHandler
            {
                UseProxy = true,
                Proxy = WebRequest.DefaultWebProxy,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };
            using (var client = new HttpClient(handler))
            {
                client.Timeout = TimeSpan.FromSeconds(60);
                client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", _userAgent.Text.Trim());
                HttpResponseMessage resp = client.GetAsync(url).GetAwaiter().GetResult();
                if (!resp.IsSuccessStatusCode)
                    throw new Exception("HTTP " + (int)resp.StatusCode + " " + resp.ReasonPhrase);
                return resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            }
        }

        private void TestDownload()
        {
            string url = _subUrl.Text.Trim();
            if (url.Length == 0) { AppendLog("请先填订阅链接。"); return; }
            try
            {
                string body = DownloadSubscription(url);
                AppendLog("本机下载成功：" + Encoding.UTF8.GetByteCount(body) + " 字节");
                AppendLog("--- 前 20 行 ---");
                string[] lines = body.Replace("\r\n", "\n").Split('\n');
                for (int i = 0; i < lines.Length && i < 20; i++) AppendLog(lines[i]);
                bool looksClash = body.IndexOf("proxies:", StringComparison.OrdinalIgnoreCase) >= 0
                               || body.IndexOf("proxy-providers:", StringComparison.OrdinalIgnoreCase) >= 0;
                AppendLog("");
                AppendLog(looksClash ? "看起来是 Clash 配置（含 proxies / proxy-providers）。"
                                     : "警告：内容里没有 proxies / proxy-providers，Clash Verge 会拒绝导入。");
            }
            catch (Exception ex) { AppendLog("下载失败：" + ex.Message); }
        }

        private void StopApp()
        {
            AppendLog("停止设备上的 Clash Verge（必须先停，否则退出时会覆盖我们写的配置）…");
            // 用 -x 而不是 -f：-f 会匹配到本次 ssh 的 bash -c 命令行，把自己的 shell 一起杀掉
            SshKey("pkill -x clash-verge 2>/dev/null; sleep 3; pgrep -x clash-verge >/dev/null && echo 'still running' || echo 'stopped'");
        }

        private void StartApp(bool wait)
        {
            AppendLog("启动 Clash Verge…");
            SshKey("XDG_RUNTIME_DIR=/run/user/1000 DISPLAY=:0 GDK_BACKEND=x11 "
                 + "setsid nohup \"$HOME/.local/bin/clash-verge\" >/tmp/clash-verge-manager.log 2>&1 < /dev/null & "
                 + "sleep 1; echo started");
            if (wait)
            {
                AppendLog("等待前端渲染与内核接管（约 20 秒）…");
                Thread.Sleep(20000);
                RunResult r = SshKey("pgrep -x clash-verge >/dev/null && echo 'app RUNNING' || echo 'app STOPPED'; "
                                   + "pgrep -x verge-mihomo >/dev/null && echo 'core RUNNING' || echo 'core STOPPED'; "
                                   + "grep -oE 'Core running mode changed: [A-Za-z]+ -> [A-Za-z]+' /tmp/clash-verge-manager.log | tail -1");
                AppendLog(r.Output.TrimEnd());
            }
        }

        private void RestartApp(bool wait)
        {
            if (!EnsureAccess()) return;
            StopApp();
            StartApp(wait);
        }

        private void PushSubscription()
        {
            string url = _subUrl.Text.Trim();
            string name = _subName.Text.Trim();
            if (url.Length == 0) { AppendLog("请先填订阅链接。"); return; }
            if (name.Length == 0) name = "导入的订阅";
            if (!EnsureAccess()) return;

            bool activate = _chkActivate.Checked;
            bool fetchOnDevice = _chkFetchOnDevice.Checked;
            string content = null;

            if (!fetchOnDevice)
            {
                AppendLog("在本机下载订阅…");
                content = DownloadSubscription(url);
                AppendLog("本机下载成功：" + Encoding.UTF8.GetByteCount(content) + " 字节");

                bool looksClash = content.IndexOf("proxies:", StringComparison.OrdinalIgnoreCase) >= 0
                               || content.IndexOf("proxy-providers:", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!looksClash)
                {
                    AppendLog("警告：内容里没有 proxies / proxy-providers。");
                    if (!_cliMode &&
                        MessageBox.Show("下载到的内容看起来不是 Clash 配置（没有 proxies / proxy-providers）。\n仍然继续推送吗？",
                            "订阅推送", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                        return;
                }
            }

            string helper = EnsureHelperOnDevice();
            StopApp();

            string remote = "python3 " + helper
                          + " --name " + QSingle(name)
                          + " --url " + QSingle(url)
                          + (activate ? " --activate" : "")
                          + (fetchOnDevice ? " --fetch" : "")
                          + " --user-agent " + QSingle(_userAgent.Text.Trim());

            AppendLog("写入设备上的 Clash Verge 配置…");
            RunResult r = SshKey(remote);
            AppendLog(r.Output.TrimEnd());

            if (!r.Ok)
            {
                AppendLog("推送失败（退出码 " + r.ExitCode + "）。");
                StartApp(false);
                return;
            }

            foreach (string line in r.Output.Replace("\r\n", "\n").Split('\n'))
            {
                if (line.StartsWith("uid"))
                    AppendLog(">>> 已导入，uid = " + line.Substring(line.IndexOf(':') + 1).Trim());
            }

            StartApp(activate);
            AppendLog(activate ? "已推送并启用。" : "已推送（未启用）。");
        }

        private void ListSubscriptions()
        {
            if (!EnsureAccess()) return;
            string helper = EnsureHelperOnDevice();
            RunResult r = SshKey("python3 " + helper + " --list");
            AppendLog(r.Output.TrimEnd());
        }

        private void RemoveSubscription()
        {
            if (!EnsureAccess()) return;
            string helper = EnsureHelperOnDevice();
            RunResult list = SshKey("python3 " + helper + " --list");
            AppendLog(list.Output.TrimEnd());

            var uids = new List<string>();
            foreach (string line in list.Output.Replace("\r\n", "\n").Split('\n'))
            {
                int i = line.IndexOf("uid=", StringComparison.Ordinal);
                if (i < 0 || line.IndexOf("[remote]") < 0) continue;
                string rest = line.Substring(i + 4);
                int j = rest.IndexOf(' ');
                uids.Add(j < 0 ? rest.Trim() : rest.Substring(0, j).Trim());
            }

            if (uids.Count == 0) { AppendLog("设备上还没有 remote 订阅可删。"); return; }

            string pick = null;
            if (_cliMode)
            {
                if (_cliRemoveUid.Length == 0) { AppendLog("CLI 模式请用 --remove <uid>"); return; }
                pick = _cliRemoveUid;
            }
            else
            {
                using (var dlg = new PickForm(uids))
                {
                    if (dlg.ShowDialog(this) == DialogResult.OK) pick = dlg.SelectedUid;
                }
                if (pick == null) { AppendLog("已取消。"); return; }
            }

            StopApp();
            RunResult r = SshKey("python3 " + helper + " --remove " + QSingle(pick));
            AppendLog(r.Output.TrimEnd());
            StartApp(false);
        }

        // ------------------------------------------------------------------ 命令行模式

        public int RunCli(string[] args)
        {
            _cliMode = true;
            string runFile = null;

            for (int i = 1; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--host": _host.Text = args[++i]; break;
                    case "--user": _user.Text = args[++i]; break;
                    case "--password": _password.Text = args[++i]; break;
                    case "--url": _subUrl.Text = args[++i]; break;
                    case "--name": _subName.Text = args[++i]; break;
                    case "--ua": _userAgent.Text = args[++i]; break;
                    case "--run": runFile = args[++i]; break;
                    case "--no-activate": _chkActivate.Checked = false; break;
                    case "--device-fetch": _chkFetchOnDevice.Checked = true; break;
                    case "--test": _cliAction = "test"; break;
                    case "--list": _cliAction = "list"; break;
                    case "--push": _cliAction = "push"; break;
                    case "--restart": _cliAction = "restart"; break;
                    case "--setup-key": _cliAction = "setupkey"; break;
                    case "--deploy": _cliAction = "deploy"; break;
                    case "--uninstall": _cliAction = "uninstall"; break;
                    case "--purge-config": _cliPurgeConfig = true; break;
                    case "--remove-deps": _cliRemoveDeps = true; break;
                    case "--dry-run": _cliDryRun = true; break;
                    case "--remove": _cliAction = "remove"; _cliRemoveUid = args[++i]; break;
                    default:
                        Console.WriteLine("未知参数: " + args[i]);
                        return 2;
                }
            }

            if (runFile != null) _runPath.Text = runFile;
            if (Host.Length == 0) { Console.WriteLine("缺少 --host"); return 2; }

            Console.WriteLine("== Clash Verge Rev × Steam Frame 管理器 (CLI) ==");
            Console.WriteLine("target  : " + Target() + "   mode: " + (ManagedMode ? "托管密钥" : "SSH 配置别名"));
            Console.WriteLine("data dir: " + AppDir);
            Console.WriteLine("");

            try
            {
                switch (_cliAction)
                {
                    case "test": TestConnection(); break;
                    case "setupkey": SetupKeyExplicit(); break;
                    case "list": ListSubscriptions(); break;
                    case "push": PushSubscription(); break;
                    case "restart": RestartApp(true); break;
                    case "remove": RemoveSubscription(); break;
                    case "deploy": DeployCli(); break;
                    case "uninstall": Uninstall(); break;
                    default:
                        Console.WriteLine("请指定动作：--test | --setup-key | --list | --push | --remove <uid> | --restart"
                                        + " | --deploy | --uninstall [--purge-config] [--remove-deps]");
                        return 2;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("异常：" + ex.Message);
                return 1;
            }

            Console.WriteLine(_log.Text);
            return 0;
        }

        /// <summary>CLI 部署：先建立登录（托管密钥或已有 SSH 配置），再推送并远程安装。</summary>
        private void DeployCli()
        {
            string run = _runPath.Text.Trim();
            if (run.Length == 0 || !File.Exists(run))
            {
                Console.WriteLine("找不到安装包：" + run);
                Environment.ExitCode = 3;
                return;
            }

            // 必须先建立登录：否则托管密钥还没生成，KeyOpts() 不会带 -i，
            // scp/ssh 会退回交互式密码提示，而在重定向了标准输入的场景下会永久卡住。
            if (!EnsureAccess()) { Environment.ExitCode = 4; return; }

            Console.WriteLine("[1/2] 推送 " + run);
            RunResult pr = ExecRetry("scp",
                KeyOpts() + " " + Q(run) + " " + Q(Target() + ":~/install-clash-verge-frame.run"),
                null, false, 1800000);
            if (!pr.Ok) { Console.WriteLine("推送失败：" + pr.Output.Trim()); Environment.ExitCode = 1; return; }

            Console.WriteLine("[2/2] 远程安装（sudo 密码默认自动用 SSH 密码）");
            string output = RunWithSudo(
                "chmod +x ~/install-clash-verge-frame.run && ~/install-clash-verge-frame.run --with-tun", "安装");
            if (output == null) { Environment.ExitCode = 5; return; }
            Console.WriteLine(output.TrimEnd());

            Console.WriteLine("部署完成");
            _accessChecked = false;
            TestConnection();
        }

        // ------------------------------------------------------------------ 小对话框

#if SUDO_DIALOG_PREVIEW
        /// <summary>开发期预览入口，见 Program.Main 里的同名条件编译块。</summary>
        internal static void PreviewSudoDialog(bool retry)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (var f = new SudoPasswordForm(retry))
            {
                Application.Run(f);
            }
        }
#endif

        /// <summary>
        /// 询问设备的 sudo 密码。按用户要求：输入内容**明文显示**（不遮成圆点），
        /// 方便边看边核对，避免盲打输错。
        /// </summary>
        private sealed class SudoPasswordForm : Form        {
            public string Password;
            private TextBox _box;

            public SudoPasswordForm(bool retry)
            {
                Text = "需要 sudo 密码";
                ClientSize = new Size(520, 250);
                StartPosition = FormStartPosition.CenterParent;
                FormBorderStyle = FormBorderStyle.FixedDialog;
                MaximizeBox = false;
                MinimizeBox = false;
                ShowInTaskbar = false;
                BackColor = Theme.Page;
                ForeColor = Theme.Text;
                Font = Theme.Ui;

                var title = new Label
                {
                    Text = retry ? "密码不对，请重新输入" : "安装 / 卸载需要这台机器的 sudo 密码",
                    Left = 24, Top = 20, AutoSize = true,
                    Font = Theme.CardTitle,
                    ForeColor = retry ? Theme.Warning : Theme.Text,
                    BackColor = Color.Transparent
                };

                var body = new Label
                {
                    Left = 24, Top = 54, Width = 472, Height = 60, AutoSize = false,
                    BackColor = Color.Transparent, Font = Theme.Ui, ForeColor = Theme.TextDim,
                    Text = "程序已经先试过 SSH 连接密码，没有通过。\r\n" +
                           "请在这里输入 Steam Frame 上 steamos 用户的登录密码。"
                };

                _box = new TextBox
                {
                    UseSystemPasswordChar = false,   // 明文显示，用户明确要求
                    Font = Theme.Ui
                };
                var field = new FieldBox(_box, 24, 122, 472, 34);

                var hint = new Label
                {
                    Left = 24, Top = 164, Width = 472, Height = 20, AutoSize = false,
                    BackColor = Color.Transparent, Font = Theme.UiSmall, ForeColor = Theme.TextMute,
                    Text = "输入内容明文显示，方便核对。密码只用于本次提权，不会写到磁盘上。"
                };

                var ok = new FlatButton("继续", FlatButton.Kind.Primary, 300, 196, 96);
                ok.Click += delegate
                {
                    Password = _box.Text;
                    DialogResult = DialogResult.OK;
                };

                var cancel = new FlatButton("取消", FlatButton.Kind.Normal, 404, 196, 92);
                cancel.Click += delegate { DialogResult = DialogResult.Cancel; };

                Controls.AddRange(new Control[] { title, body, field, hint, ok, cancel });
                AcceptButton = ok;
                CancelButton = cancel;
            }
        }

        /// <summary>卸载前的选项确认框。深色主题，与主界面一致。</summary>
        private sealed class UninstallForm : Form
        {
            public bool PurgeConfig;
            public bool RemoveDeps;
            private DarkCheck _chkConfig, _chkDeps;

            public UninstallForm()
            {
                Text = "卸载 Clash Verge";
                ClientSize = new Size(500, 344);
                StartPosition = FormStartPosition.CenterParent;
                FormBorderStyle = FormBorderStyle.FixedDialog;
                MaximizeBox = false;
                MinimizeBox = false;
                ShowInTaskbar = false;
                BackColor = Theme.Page;
                ForeColor = Theme.Text;
                Font = Theme.Ui;

                var title = new Label
                {
                    Text = "将从设备上移除以下内容",
                    Left = 24, Top = 20, AutoSize = true,
                    Font = Theme.CardTitle, ForeColor = Theme.Text, BackColor = Color.Transparent
                };

                var body = new Label
                {
                    Left = 24, Top = 52, Width = 452, Height = 132, AutoSize = false,
                    BackColor = Color.Transparent, Font = Theme.Ui, ForeColor = Theme.TextDim,
                    Text = "  ·  Clash Verge 程序与启动器（~/.local/opt、~/.local/bin）\r\n" +
                           "  ·  桌面菜单项与图标（桌面模式的应用列表）\r\n" +
                           "  ·  Steam「非 Steam 游戏库」里的条目\r\n" +
                           "  ·  官方系统服务 clash-verge-service（需要 sudo）\r\n\r\n" +
                           "卸载过程中会先退出 Steam —— Steam 在运行时不会把快捷方式的\r\n" +
                           "改动写回配置文件，不退就删不掉库里的条目。"
                };

                _chkConfig = new DarkCheck("同时删除配置、订阅与日志（删掉后订阅链接需要重新填）", 24, 196, 456);
                _chkDeps   = new DarkCheck("同时卸载系统依赖 webkit2gtk-4.1（若没有别的程序在用它）", 24, 224, 456);

                var hint = new Label
                {
                    Left = 24, Top = 252, Width = 452, Height = 32, AutoSize = false,
                    BackColor = Color.Transparent, Font = Theme.UiSmall, ForeColor = Theme.TextMute,
                    Text = "上面两项默认都不勾选：只卸载程序，保留订阅与系统依赖。\r\n想彻底清干净就都勾上。"
                };

                var ok = new FlatButton("开始卸载", FlatButton.Kind.Danger, 268, 294, 108);
                // 不要用 Button.DialogResult：只要该按钮被"点击"（包括意外的合成输入）就会
                // 让 ShowDialog 直接返回 OK，等于绕过确认。改成只在真正点它时才显式结束对话框。
                ok.Click += delegate
                {
                    PurgeConfig = _chkConfig.Checked;
                    RemoveDeps = _chkDeps.Checked;
                    DialogResult = DialogResult.OK;
                };

                var cancel = new FlatButton("取消", FlatButton.Kind.Normal, 384, 294, 92);
                cancel.Click += delegate { DialogResult = DialogResult.Cancel; };

                Controls.AddRange(new Control[] { title, body, _chkConfig, _chkDeps, hint, ok, cancel });
                AcceptButton = cancel;   // 默认落在"取消"，避免误按回车直接卸载
                CancelButton = cancel;
            }
        }

        private sealed class PickForm : Form
        {
            public string SelectedUid;
            private ListBox _list;

            public PickForm(List<string> uids)
            {
                Text = "选择要删除的订阅";
                ClientSize = new Size(420, 260);
                StartPosition = FormStartPosition.CenterParent;
                FormBorderStyle = FormBorderStyle.FixedDialog;
                MaximizeBox = false;
                MinimizeBox = false;
                Font = new Font("Microsoft YaHei UI", 9F);

                _list = new ListBox { Left = 12, Top = 12, Width = 396, Height = 190 };
                foreach (string u in uids) _list.Items.Add(u);
                if (_list.Items.Count > 0) _list.SelectedIndex = 0;

                var ok = new Button { Text = "删除", Left = 240, Top = 212, Width = 80, DialogResult = DialogResult.OK };
                var cancel = new Button { Text = "取消", Left = 328, Top = 212, Width = 80, DialogResult = DialogResult.Cancel };
                ok.Click += delegate { SelectedUid = (string)_list.SelectedItem; };

                Controls.AddRange(new Control[] { _list, ok, cancel });
                AcceptButton = ok;
                CancelButton = cancel;
            }
        }
    }
}
