using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace DeepSeekHarness;

/// <summary>DeepSeek Harness 桌面壳子：隐藏启动服务 + WebView2 内嵌界面 + 自绘标题栏。</summary>
public class MainForm : Form
{
    private readonly WebView2 _webView = new() { Dock = DockStyle.Fill, Margin = new Padding(4) };
    private readonly Label _statusLabel = new()
    {
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleCenter,
        Font = new Font("Microsoft YaHei UI", 14),
        ForeColor = Color.DimGray,
        Text = "正在启动 DeepSeek Harness 服务…"
    };
    private Process? _serverProcess;
    private CancellationTokenSource? _cts;
    private volatile bool _serverReady;   // 3080 端口已就绪
    private volatile bool _splashReady;   // splash 页面已加载，可以注入 JS
    // splash 页面就绪前积压的 JS 调用（按序补发，仅 UI 线程访问）
    private readonly List<string> _pendingScripts = new();
    // 安装标记：记录 dsh CLI 的缓存入口，后续启动跳过 npx 联网检查直接运行
    private static readonly string MarkerDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");
    private static readonly string MarkerFile = Path.Combine(MarkerDir, "dsh-installed.json");
    private static readonly string SettingsFile = Path.Combine(MarkerDir, "settings.json");
    private enum DshLaunchMode { Online, Offline }
    private DshLaunchMode _launchMode;
    private bool _retriedOnline;          // 防止离线失败回退后再次回退的循环
    // npx/npm 在非交互环境下可能输出 ANSI 颜色码，剥掉避免界面显示乱码
    private static readonly Regex AnsiRegex = new("\x1b\\[[0-9;]*[A-Za-z]", RegexOptions.Compiled);
    // 日志批量注入：npx verbose 输出量大，先入队，250ms 一次批量发给页面（避免逐行 JS 卡顿）
    private readonly ConcurrentQueue<string> _logQueue = new();
    private System.Windows.Forms.Timer? _logFlusher;

    // ---- 标题栏 ----
    private readonly Panel _titleBar = new()
    {
        Dock = DockStyle.Top,
        Height = 38,
        Margin = new Padding(4, 4, 4, 0),
        BackColor = Color.FromArgb(13, 26, 61)
    };
    private readonly Label _btnGear, _btnMin, _btnMax, _btnClose;
    private readonly Label _brandLabel;   // 标题栏品牌名（splash 蓝 / 暗色模式两种前景色）
    private Color _tbSeparator = Color.FromArgb(36, 126, 162, 255);   // 标题栏底部分隔线
    private readonly ContextMenuStrip _gearMenu = new();
    private readonly ToolStripMenuItem _miSystem, _miBuiltin;

    // ---- 链接打开方式设置（持久化到 ~/.dsh/settings.json） ----
    private string _openLinksIn = "system";   // "system" 默认浏览器 | "builtin" 壳子内置

    // ---- 重启后端服务的反馈浮层 ----
    private Form? _restartOverlay;
    private Label? _overlayStatus;

    // ---- Win32 ----
    private const int WM_NCLBUTTONDOWN = 0xA1, HTCAPTION = 2;

    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wp, IntPtr lp);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
    private const int DWMWA_NCRENDERING_POLICY = 2;
    private const int DWMNCRP_DISABLED = 1;

    /// <summary>Win10 上无边框窗口仍会被 DWM 画 1px 边框线（颜色随系统强调色，此机渲染为黄色），
    /// PrintWindow 也测不到（DWM 合成层）。禁用非客户区渲染：边框线与阴影一并关闭。</summary>
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var policy = DWMNCRP_DISABLED;
        DwmSetWindowAttribute(Handle, DWMWA_NCRENDERING_POLICY, ref policy, sizeof(int));
    }

    // ---- 无边框拉伸（纯托管）：8 个透明热区面板 + 鼠标捕获改 Bounds，不依赖任何系统边框机制 ----
    private const int SZ_EDGE = 4;   // 热区宽度（与四周 4px 边距一致；再窄拉伸难命中）
    private const int SIDE_LEFT = 1, SIDE_TOP = 2, SIDE_RIGHT = 4, SIDE_BOTTOM = 8;
    private readonly Panel[] _zones = new Panel[9];   // 8 个热区：1..8 = 左上、上、右上、左、右、左下、下、右下
    private int _resizeSide;                          // 正在拉伸的方向（SIDE_* 组合），0 = 未拉伸
    private Point _resizeStartMouse;                  // 按下时鼠标屏幕坐标
    private Rectangle _resizeStartBounds;             // 按下时窗口矩形

    public MainForm()
    {
        FormBorderStyle = FormBorderStyle.None;   // 自绘标题栏
        Text = "DeepSeek Harness";
        Size = new Size(1280, 800);
        MinimumSize = new Size(960, 600);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(13, 26, 61);   // 与标题栏同色：四周 4px 边带视觉上与标题栏融为一体（Form 无法真透明）

        _openLinksIn = ReadOpenLinksSetting();

        // 标题栏：按钮组 Dock=Right 依次排列（后添加的先布局，btnClose 最后添加 → 最靠右），
        // 品牌区 Dock=Left + AutoSize。全部用 Dock 布局期动态计算，避免固定宽度在高 DPI 下溢出。
        _btnGear = MakeTbButton("");   // Segoe MDL2 齿轮（设置）
        _btnMin = MakeTbButton("");    // 最小化
        _btnMax = MakeTbButton("");    // 最大化
        _btnClose = MakeTbButton("");  // 关闭
        _btnClose.Dock = _btnMax.Dock = _btnMin.Dock = _btnGear.Dock = DockStyle.Right;

        // 左侧品牌：小图标（deepseek.ico）+ 名称，内容垂直居中
        var icon = new PictureBox { Size = new Size(18, 18), SizeMode = PictureBoxSizeMode.Zoom, Margin = new Padding(0) };
        try
        {
            var icoPath = Path.Combine(AppContext.BaseDirectory, "deepseek.ico");
            if (File.Exists(icoPath)) icon.Image = new Icon(icoPath, 18, 18).ToBitmap();
        }
        catch { /* 图标缺失不影响标题栏 */ }
        _brandLabel = new Label
        {
            Text = "DeepSeek Harness",
            AutoSize = true,
            ForeColor = Color.FromArgb(219, 230, 255),
            Font = new Font("Microsoft YaHei UI", 9.5f),
            Margin = new Padding(8, 0, 0, 0)
        };
        var brandBox = new FlowLayoutPanel
        {
            Dock = DockStyle.Left,
            AutoSize = true,
            Padding = new Padding(14, (38 - 18) / 2, 0, 0),   // 顶部留白实现垂直居中
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false
        };
        brandBox.Controls.Add(icon);
        brandBox.Controls.Add(_brandLabel);

        _titleBar.Controls.Add(brandBox);
        _titleBar.Controls.Add(_btnGear);
        _titleBar.Controls.Add(_btnMin);
        _titleBar.Controls.Add(_btnMax);
        _titleBar.Controls.Add(_btnClose);
        _titleBar.Paint += (_, e) =>
        {
            // 底部 1px 分隔线，与 splash 边框呼应
            using var pen = new Pen(_tbSeparator);
            e.Graphics.DrawLine(pen, 0, _titleBar.Height - 1, _titleBar.Width, _titleBar.Height - 1);
        };
        // 拖动窗口 + 双击最大化：标题栏及其子控件（图标/文字所在区）都要响应
        foreach (var c in new Control[] { _titleBar, brandBox, icon, _brandLabel })
        {
            c.MouseDown += (_, e) =>
            {
                if (e.Button != MouseButtons.Left || WindowState == FormWindowState.Maximized) return;
                ReleaseCapture();
                SendMessage(Handle, WM_NCLBUTTONDOWN, new IntPtr(HTCAPTION), IntPtr.Zero);
            };
            c.MouseDoubleClick += (_, e) => ToggleMaximize();
        }

        // 切换行为：菜单打开时再点按钮 = 关闭。
        // 菜单开着时点按钮，AutoClose 会先触发 Closing（点击外部）再触发按钮 Click——
        // 用 Closing 时刻鼠标是否在按钮上来区分"点按钮关闭"与"点外部关闭"。
        var gearCloseByBtn = false;
        _gearMenu.Closing += (_, _) =>
        {
            var mousePos = _btnGear.PointToClient(Control.MousePosition);
            gearCloseByBtn = _btnGear.ClientRectangle.Contains(mousePos);
        };
        _btnGear.Click += (_, _) =>
        {
            if (gearCloseByBtn) { gearCloseByBtn = false; return; }   // 本次点击是关闭动作，不重开
            _gearMenu.Show(_btnGear, new Point(_btnGear.Width - _gearMenu.Width, _btnGear.Height));
        };
        _btnMin.Click += (_, _) => WindowState = FormWindowState.Minimized;
        _btnMax.Click += (_, _) => ToggleMaximize();
        _btnClose.Click += (_, _) => Close();

        // ⚙ 设置菜单
        // 菜单用中性深灰黑（splash 蓝色阶段与暗色主页阶段都协调）
        // 关掉 ToolStrip 视觉样式渲染：启用时下拉菜单的边框/hover 走系统主题
        // （VisualStyleElement，强调色=黄 → 黄边框/黄底，DWM API 对它无效——layered 窗口），
        // 关闭后全部走 ProfessionalColorTable 自控颜色。应用内仅此一个 ToolStrip，全局无副作用。
        ToolStripManager.VisualStylesEnabled = false;
        _gearMenu.BackColor = Color.FromArgb(18, 18, 22);
        _gearMenu.ForeColor = Color.FromArgb(213, 214, 222);
        _gearMenu.Renderer = new DarkMenuRenderer();
        // 菜单是独立 Popup 顶层窗口，同样会被 DWM 画 1px 黄边线 → 禁非客户区渲染
        _gearMenu.HandleCreated += (_, _) =>
        {
            var policy = DWMNCRP_DISABLED;
            DwmSetWindowAttribute(_gearMenu.Handle, DWMWA_NCRENDERING_POLICY, ref policy, sizeof(int));
        };
        var group = new ToolStripLabel("链接打开方式") { ForeColor = Color.FromArgb(128, 130, 140), Font = new Font("Microsoft YaHei UI", 8f) };
        _miSystem = new ToolStripMenuItem("电脑默认浏览器", null, (_, _) => SetOpenLinks("system"));
        _miBuiltin = new ToolStripMenuItem("壳子内置浏览器", null, (_, _) => SetOpenLinks("builtin"));
        var miOpenDir = new ToolStripMenuItem("打开数据目录（~/.dsh）", null, (_, _) => OpenDataDir());
        var miRestart = new ToolStripMenuItem("重启后端服务", null, async (_, _) => await RestartServerAsync());
        var ver = new ToolStripLabel("v0.1.0") { ForeColor = Color.FromArgb(95, 97, 105), Font = new Font("Microsoft YaHei UI", 8f) };
        foreach (var item in new ToolStripItem[] { _miSystem, _miBuiltin, miOpenDir, miRestart })
        {
            item.ForeColor = Color.FromArgb(213, 214, 222);
            item.BackColor = Color.FromArgb(18, 18, 22);
        }
        _gearMenu.Items.AddRange(new ToolStripItem[]
        {
            group,
            _miSystem, _miBuiltin,
            new ToolStripSeparator(),
            miOpenDir, miRestart,
            new ToolStripSeparator(),
            ver
        });
        ApplyOpenLinksMenu();

        Controls.Add(_webView);
        Controls.Add(_titleBar);
        Controls.Add(_statusLabel);
        BuildResizeZones();
        Load += OnLoad;
        FormClosing += OnFormClosing;
    }

    // 注意：这里刻意不 override CreateParams。曾经试过 WS_THICKFRAME（DWM 画灰白系统边框 → 顶部白边）、
    // CS_DROPSHADOW（Win10 上无边框窗口边缘被 DWM 画 1px 强调色线）——都不要。
    // 纯 FormBorderStyle.None：客户区 = 整个窗口，系统无边框可画；拉伸由热区面板纯托管实现。

    private const int WM_GETMINMAXINFO = 0x0024;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINTAPI { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINTAPI ptReserved;
        public POINTAPI ptMaxSize;
        public POINTAPI ptMaxPosition;
        public POINTAPI ptMinTrackSize;
        public POINTAPI ptMaxTrackSize;
    }

    protected override void WndProc(ref Message m)
    {
        // 无边框窗口（FormBorderStyle.None）最大化默认盖满全屏（含任务栏）。
        // 这里修正最大化边界到工作区。
        if (m.Msg == WM_GETMINMAXINFO)
        {
            var mmi = (MINMAXINFO)Marshal.PtrToStructure(m.LParam, typeof(MINMAXINFO))!;
            var work = Screen.FromHandle(Handle).WorkingArea;
            mmi.ptMaxPosition.X = work.Left;
            mmi.ptMaxPosition.Y = work.Top;
            mmi.ptMaxSize.X = work.Width;
            mmi.ptMaxSize.Y = work.Height;
            Marshal.StructureToPtr(mmi, m.LParam, true);
            m.Result = IntPtr.Zero;
            return;
        }
        base.WndProc(ref m);
    }

    // ================= 无边框拉伸（纯托管：透明热区面板 + 鼠标捕获改 Bounds） =================

    /// <summary>构建 8 个透明热区面板。必须在窗口控件全部 Add 之后调用，
    /// 且面板只占据四周 8px 空白深蓝边带（标题栏有 Margin，WebView 有 Margin），不遮挡任何内容。</summary>
    private void BuildResizeZones()
    {
        _zones[1] = MakeZone(SIDE_TOP | SIDE_LEFT, Cursors.SizeNWSE);
        _zones[2] = MakeZone(SIDE_TOP, Cursors.SizeNS);
        _zones[3] = MakeZone(SIDE_TOP | SIDE_RIGHT, Cursors.SizeNESW);
        _zones[4] = MakeZone(SIDE_LEFT, Cursors.SizeWE);
        _zones[5] = MakeZone(SIDE_RIGHT, Cursors.SizeWE);
        _zones[6] = MakeZone(SIDE_BOTTOM | SIDE_LEFT, Cursors.SizeNESW);
        _zones[7] = MakeZone(SIDE_BOTTOM, Cursors.SizeNS);
        _zones[8] = MakeZone(SIDE_BOTTOM | SIDE_RIGHT, Cursors.SizeNWSE);
        LayoutResizeZones();
        Resize += (_, _) => LayoutResizeZones();
    }

    private Panel MakeZone(int side, Cursor cursor)
    {
        var p = new Panel { Tag = side, BackColor = Color.Transparent, Cursor = cursor, TabStop = false };
        p.MouseDown += ZoneMouseDown;
        p.MouseMove += ZoneMouseMove;
        p.MouseUp += ZoneMouseUp;
        Controls.Add(p);
        p.BringToFront();   // 面板必须盖在标题栏/WebView 之上才能收到边缘鼠标事件
        return p;
    }

    private void LayoutResizeZones()
    {
        var w = ClientSize.Width; var h = ClientSize.Height; var e = SZ_EDGE;
        _zones[1].SetBounds(0, 0, e, e);             // 左上
        _zones[2].SetBounds(e, 0, w - 2 * e, e);     // 上
        _zones[3].SetBounds(w - e, 0, e, e);         // 右上
        _zones[4].SetBounds(0, e, e, h - 2 * e);     // 左
        _zones[5].SetBounds(w - e, e, e, h - 2 * e); // 右
        _zones[6].SetBounds(0, h - e, e, e);         // 左下
        _zones[7].SetBounds(e, h - e, w - 2 * e, e); // 下
        _zones[8].SetBounds(w - e, h - e, e, e);     // 右下
    }

    /// <summary>按下：记录基准矩形。后续 MouseMove/MouseUp 会自动派发到面板（MouseDown 后控件获得捕获）。</summary>
    private void ZoneMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || WindowState == FormWindowState.Maximized) return;
        _resizeSide = (int)((Panel)sender!).Tag!;
        _resizeStartMouse = Control.MousePosition;
        _resizeStartBounds = Bounds;
    }

    /// <summary>拖动：按方向修改窗口矩形；左/上边拖动时反向边固定，尺寸受 MinimumSize 钳制。</summary>
    private void ZoneMouseMove(object? sender, MouseEventArgs e)
    {
        if (_resizeSide == 0) return;
        var mouse = Control.MousePosition;
        var dx = mouse.X - _resizeStartMouse.X;
        var dy = mouse.Y - _resizeStartMouse.Y;
        var b = _resizeStartBounds;
        int x = b.X, y = b.Y, w = b.Width, h = b.Height;
        if ((_resizeSide & SIDE_LEFT) != 0)
        {
            w = Math.Max(MinimumSize.Width, b.Width - dx);
            x = b.Right - w;
        }
        if ((_resizeSide & SIDE_TOP) != 0)
        {
            h = Math.Max(MinimumSize.Height, b.Height - dy);
            y = b.Bottom - h;
        }
        if ((_resizeSide & SIDE_RIGHT) != 0) w = Math.Max(MinimumSize.Width, b.Width + dx);
        if ((_resizeSide & SIDE_BOTTOM) != 0) h = Math.Max(MinimumSize.Height, b.Height + dy);
        SetBounds(x, y, w, h);
    }

    private void ZoneMouseUp(object? sender, MouseEventArgs e) => _resizeSide = 0;

    private void ToggleMaximize()
    {
        WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
        _btnMax.Text = WindowState == FormWindowState.Maximized ? "" : "";  // 还原 / 最大化
    }

    /// <summary>标题栏方形按钮：Segoe MDL2 图标 + Label 自绘 hover。
    /// 不用 Button：启用视觉样式时（ApplicationConfiguration.Initialize 自动开启），
    /// FlatStyle.Flat 的 hover 走系统主题渲染（VisualStyleElement.Button.PushButton.Hot），
    /// FlatAppearance.MouseOverBackColor 被忽略 → 系统强调色为黄时渲染成黄底
    /// （dotnet/winforms #3770 / #13897 已知问题）。Label 无视觉样式路径，颜色完全自控。</summary>
    private Label MakeTbButton(string glyph)
    {
        var hover = Color.FromArgb(52, 52, 58);    // 中性灰 hover（splash 蓝 / 暗色两种标题栏都协调）
        var down = Color.FromArgb(66, 66, 72);     // 按下更深
        var b = new Label
        {
            Text = glyph,
            Font = new Font("Segoe MDL2 Assets", 10),
            Size = new Size(46, 38),
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter,
            BackColor = Color.Transparent,
            ForeColor = Color.FromArgb(200, 214, 245),
            TabStop = false,
            Cursor = Cursors.Hand
        };
        b.MouseEnter += (_, _) => b.BackColor = hover;
        b.MouseLeave += (_, _) => b.BackColor = Color.Transparent;
        b.MouseDown += (_, _) => b.BackColor = down;
        b.MouseUp += (_, _) => b.BackColor = hover;
        return b;
    }

    // ================= 设置（~/.dsh/settings.json） =================

    private static string ReadOpenLinksSetting()
    {
        try
        {
            if (!File.Exists(SettingsFile)) return "system";
            using var doc = JsonDocument.Parse(File.ReadAllText(SettingsFile));
            return doc.RootElement.TryGetProperty("openLinksIn", out var p) && p.GetString() == "builtin"
                ? "builtin" : "system";
        }
        catch { return "system"; }
    }

    private void WriteSettings()
    {
        try
        {
            Directory.CreateDirectory(MarkerDir);
            File.WriteAllText(SettingsFile, JsonSerializer.Serialize(new { openLinksIn = _openLinksIn }));
        }
        catch { /* 设置写失败不影响使用 */ }
    }

    private void SetOpenLinks(string mode)
    {
        _openLinksIn = mode;
        ApplyOpenLinksMenu();
        WriteSettings();
    }

    private void ApplyOpenLinksMenu()
    {
        _miSystem.Checked = _openLinksIn == "system";
        _miBuiltin.Checked = _openLinksIn == "builtin";
    }

    // ================= 启动流程 =================

    private async void OnLoad(object? sender, EventArgs e)
    {
        _cts = new CancellationTokenSource();
        try
        {
            // 先显示鲸鱼启动动画，同时后台启动服务
            Controls.Remove(_statusLabel);
            await _webView.EnsureCoreWebView2Async();
            // WebView 背景设为与 splash 一致的深蓝色，跳转新页面时不会闪白屏
            _webView.DefaultBackgroundColor = Color.FromArgb(13, 26, 61);
            _webView.CoreWebView2.NavigationCompleted += OnSplashNavigationCompleted;
            // 壳子内点开新窗口链接：按设置用默认浏览器或内置打开
            _webView.CoreWebView2.NewWindowRequested += OnNewWindowRequested;
            _webView.CoreWebView2.Navigate(Path.Combine(AppContext.BaseDirectory, "splash.html"));

            // 日志批量注入定时器（UI 线程）
            _logFlusher = new System.Windows.Forms.Timer { Interval = 250 };
            _logFlusher.Tick += FlushLogs;
            _logFlusher.Start();

            // 步骤 0：检查本地是否已安装 DSH 服务
            SetStage(0, "正在检查 DeepSeek Harness 是否已安装…");
            EnqueueLog("[dsh] 正在检测本地是否已安装 DSH 服务...");

            var installed = TryGetInstalledDshCli(out var cli);
            if (installed)
            {
                EnqueueLog("[dsh] 已检测到本地安装（~/.dsh/dsh-installed.json），跳过在线安装");
                StartDsh(DshLaunchMode.Offline, cli);
            }
            else
            {
                EnqueueLog("[dsh] 未找到本地安装，开始联网下载 @deepseek-ai/dsh");
                StartDsh(DshLaunchMode.Online, null);
                // 在线安装期间轮询 npm 缓存增长，显示真实下载进度
                _ = MonitorDownloadAsync(_cts.Token);
            }
            await WaitForServerAsync(_cts.Token);
            if (_cts.IsCancellationRequested) return;
            // 在线安装成功后记录 CLI 入口，下次启动跳过联网
            if (_launchMode == DshLaunchMode.Online) WriteInstalledMarker();

            // splash 就绪后：步骤 3 短暂停留，再淡出跳转主界面
            SetStage(3, "连接成功，正在进入…");
            await Task.Delay(350);
            await FadeOutSplashAsync();
            _webView.CoreWebView2.Navigate("http://127.0.0.1:3080");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"启动失败：{ex.Message}";
        }
    }

    /// <summary>splash 加载完成后补发积压的 JS 调用（npx 输出可能早于页面就绪）。
    /// 导航到主界面（3080 网页）后，窗框切换为与网页暗色主题一致的深色，保证与 splash 蓝色阶段风格一致。</summary>
    private void OnSplashNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (_webView.CoreWebView2?.Source.StartsWith("http://127.0.0.1:3080") == true)
        {
            ApplyDarkChrome();
            return;
        }
        _splashReady = true;
        foreach (var script in _pendingScripts)
            _ = _webView.CoreWebView2?.ExecuteScriptAsync(script);
        _pendingScripts.Clear();
    }

    /// <summary>主界面（3080 网页）加载完成：窗框从 splash 的蓝色主题切换为与网页暗色主题一致的深色，
    /// 保证两个阶段"外层窗框 ≈ 内层内容"。幂等（重启后端后再次导航也会触发）。</summary>
    private void ApplyDarkChrome()
    {
        BackColor = Color.FromArgb(20, 20, 24);              // 四周边带 = 标题栏同色，视觉融为一体
        _titleBar.BackColor = Color.FromArgb(20, 20, 24);    // 标题栏
        _brandLabel.ForeColor = Color.FromArgb(228, 230, 238);
        _tbSeparator = Color.FromArgb(34, 255, 255, 255);   // 白色 13% 透明（A,R,G,B 顺序！）
        foreach (var b in new[] { _btnGear, _btnMin, _btnMax, _btnClose })
            b.ForeColor = Color.FromArgb(206, 208, 216);
        _webView.DefaultBackgroundColor = Color.FromArgb(20, 20, 24);
        _titleBar.Invalidate();
    }

    /// <summary>壳子内链接：按设置用系统默认浏览器或内置 WebView 打开。
    /// 采用 deferral 模式：Handled=true 后挂起、事件返回后再导航——若在事件中同步 Navigate，
    /// WebView2 可能仍执行默认动作（再弹一次系统浏览器），出现"壳子+浏览器双开"。</summary>
    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        var deferral = e.GetDeferral();
        var uri = e.Uri;
        BeginInvoke(() =>
        {
            try
            {
                if (_openLinksIn == "builtin")
                    _webView.CoreWebView2?.Navigate(uri);
                else
                    OpenExternal(uri);
            }
            finally
            {
                deferral.Complete();
            }
        });
    }

    private void OpenExternal(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { /* 打开失败忽略 */ }
    }

    /// <summary>启动 dsh 服务：已安装则 node 离线直接启动，否则 npx 在线安装（verbose 输出详情）。</summary>
    private void StartDsh(DshLaunchMode mode, string? cli)
    {
        _launchMode = mode;
        ProcessStartInfo psi;
        if (mode == DshLaunchMode.Offline && !string.IsNullOrEmpty(cli))
        {
            InvokeJs("window.dshUX.setLogTitle('运行日志');");
            InvokeJs("window.dshUX.setHint('已安装：直接启动本地服务，无需联网下载');");
            SetStage(2, "已检测到服务已安装，正在启动…", new[] { 1 });
            EnqueueLog($"[dsh] CLI 入口校验通过：{cli}");
            psi = new ProcessStartInfo("node")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Application.StartupPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            // ArgumentList 自动处理带空格路径的引号，不用字符串拼接
            psi.ArgumentList.Add(cli);
            psi.ArgumentList.Add("web");
            // dsh web 默认会自动打开系统默认浏览器；壳子内已有 WebView 导航，禁用掉避免弹浏览器窗口
            psi.ArgumentList.Add("--no-open");
        }
        else
        {
            InvokeJs("window.dshUX.setLogTitle('依赖下载日志');");
            InvokeJs("window.dshUX.setHint('首次运行需下载依赖，通常需要几分钟，请勿关闭窗口');");
            SetStage(1, "未检测到服务，正在安装 DeepSeek Harness 服务…");
            // --loglevel=verbose：逐包输出下载/解析日志，日志面板才能看到详情
            // --no-open：dsh web 默认自动打开系统默认浏览器，壳子内已有 WebView 导航，禁用掉
            psi = new ProcessStartInfo("cmd.exe", "/c npx -y --loglevel=verbose @deepseek-ai/dsh web --no-open")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Application.StartupPath,
                // 重定向输出：npx 下载/更新依赖的过程实时转发到界面，避免看起来像卡死
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
        }
        _serverProcess = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _serverProcess.OutputDataReceived += OnServerOutput;
        _serverProcess.ErrorDataReceived += OnServerOutput;
        _serverProcess.Exited += OnServerExited;
        _serverProcess.Start();
        _serverProcess.BeginOutputReadLine();
        _serverProcess.BeginErrorReadLine();
    }

    /// <summary>把 npx/服务输出入队（批量注入页面），下载或更新依赖时能看到真实日志。</summary>
    private void OnServerOutput(object? sender, DataReceivedEventArgs e)
    {
        var line = e.Data;
        if (string.IsNullOrWhiteSpace(line)) return;
        line = AnsiRegex.Replace(line.Trim('\r'), "").Trim();
        if (line.Length == 0) return;
        _logQueue.Enqueue(line);
    }

    /// <summary>每 250ms 把队列里的日志批量发给 splash（避免 verbose 大输出逐行 JS 卡顿）。</summary>
    private void FlushLogs(object? sender, EventArgs e)
    {
        if (_logQueue.IsEmpty) return;
        var batch = new List<string>();
        while (batch.Count < 120 && _logQueue.TryDequeue(out var line)) batch.Add(line);
        if (batch.Count > 0)
            InvokeJs($"window.dshUX.addLogs({JsonSerializer.Serialize(batch)});");
    }

    private void EnqueueLog(string line) => _logQueue.Enqueue(line);

    /// <summary>服务进程意外退出（下载失败、网络错误等）时提示用户，而不是无限转圈。</summary>
    private void OnServerExited(object? sender, EventArgs e)
    {
        if (_serverReady) return; // 服务已就绪后的退出（正常关窗清理）不提示
        // 离线启动失败（如 npm 缓存被清理）：自动回退在线重装一次，避免死循环
        if (_launchMode == DshLaunchMode.Offline && !_retriedOnline)
        {
            _retriedOnline = true;
            SetStage(1, "已安装的服务不可用，正在重新下载…");
            StartDsh(DshLaunchMode.Online, null);
            return;
        }
        var code = _serverProcess?.ExitCode ?? 0;
        SetError($"服务进程已退出（退出码 {code}），启动失败。请检查网络后重新打开应用");
    }

    /// <summary>统一的 JS 调用通道：后台线程自动转 UI 线程，页面未就绪时积压按序补发。</summary>
    private void InvokeJs(string script)
    {
        try
        {
            if (InvokeRequired)
            {
                BeginInvoke(() => InvokeJs(script));
                return;
            }
            if (!_splashReady)
            {
                _pendingScripts.Add(script);
                return;
            }
            _ = _webView.CoreWebView2?.ExecuteScriptAsync(script);
        }
        catch
        {
            // 窗体关闭等竞态，忽略
        }
    }

    /// <summary>步骤指示器高亮（0 检查 / 1 安装 / 2 启动 / 3 连接）+ 主状态文字；skip 声明被跳过的中间步骤。</summary>
    private void SetStage(int stage, string label, int[]? skip = null) =>
        InvokeJs($"window.dshUX.setStage({stage}, {JsonSerializer.Serialize(label)}, {(skip == null ? "null" : JsonSerializer.Serialize(skip))});");

    /// <summary>下载进度条：已下载 MB 数；pct 传 null 时为不定态（总量未知，只显示 MB 增长）。</summary>
    private void SetProgress(double mb, double? pct = null) =>
        InvokeJs($"window.dshUX.setProgress({mb.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)}, {(pct == null ? "null" : pct.Value.ToString("F1", System.Globalization.CultureInfo.InvariantCulture))});");

    /// <summary>启动失败：整页错误提示。</summary>
    private void SetError(string msg) =>
        InvokeJs($"window.dshUX.setError({JsonSerializer.Serialize(msg)});");

    /// <summary>splash 页面淡出（350ms），等待过渡完成后再跳转，避免突兀切换。</summary>
    private async Task FadeOutSplashAsync()
    {
        try
        {
            await _webView.CoreWebView2.ExecuteScriptAsync(
                "document.body.style.transition='opacity 0.35s ease'; document.body.style.opacity='0';");
            await Task.Delay(400);
        }
        catch
        {
            // 页面异常时直接跳转，不影响启动
        }
    }

    /// <summary>轮询 3080 端口直到服务就绪。</summary>
    private async Task WaitForServerAsync(CancellationToken token)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var resp = await client.GetAsync("http://127.0.0.1:3080", token);
                if (resp.IsSuccessStatusCode)
                {
                    _serverReady = true;
                    return;
                }
            }
            catch
            {
                // 服务尚未就绪，继续等待
            }
            await Task.Delay(500, token);
        }
    }

    /// <summary>读取安装标记，校验 dsh CLI 入口文件仍存在。</summary>
    private static bool TryGetInstalledDshCli(out string? cli)
    {
        cli = null;
        try
        {
            if (!File.Exists(MarkerFile)) return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(MarkerFile));
            if (!doc.RootElement.TryGetProperty("dshCli", out var prop)) return false;
            cli = prop.GetString();
            return !string.IsNullOrEmpty(cli) && File.Exists(cli);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>在线安装成功后，把 dsh CLI 入口写入标记，下次启动跳过联网。</summary>
    private void WriteInstalledMarker()
    {
        try
        {
            if (!TryFindDshCli(out var cli) || cli == null)
            {
                EnqueueLog("服务已就绪，但未找到缓存入口，下次启动将重新检查");
                return;
            }
            Directory.CreateDirectory(MarkerDir);
            var marker = new
            {
                schemaVersion = 1,
                dshCli = cli,
                installedAt = DateTimeOffset.Now
            };
            File.WriteAllText(MarkerFile,
                JsonSerializer.Serialize(marker, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // 标记写失败不影响本次使用，下次启动重新在线安装
        }
    }

    /// <summary>npm 缓存根目录：环境变量 > 新版 npm 默认 > 老版兜底（取第一个存在的）。</summary>
    private static string? FindNpmCacheRoot()
    {
        var roots = new List<string>();
        var env = Environment.GetEnvironmentVariable("npm_config_cache");
        if (!string.IsNullOrEmpty(env)) roots.Add(env);
        roots.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "npm-cache"));
        roots.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".npm"));
        foreach (var root in roots)
        {
            if (Directory.Exists(root)) return root;
        }
        return null;
    }

    /// <summary>递归统计目录总大小（MB，含小数）。目录被清理/权限问题时返回 0。</summary>
    private static double DirSizeMB(string dir)
    {
        try
        {
            long total = 0;
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                total += new FileInfo(file).Length;
            return total / 1024.0 / 1024.0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>轮询 npm 缓存增长显示真实下载进度；检测到 dsh 安装完成时切换到步骤 2。</summary>
    private async Task MonitorDownloadAsync(CancellationToken token)
    {
        var cacheRoot = FindNpmCacheRoot();
        if (cacheRoot == null) return;
        var cacache = Path.Combine(cacheRoot, "_cacache");
        var baseline = DirSizeMB(cacache);
        var lastMb = 0.0;
        SetProgress(0, null);
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(1500, token);
                var mb = DirSizeMB(cacache) - baseline;
                if (mb > 0) lastMb = mb;
                SetProgress(lastMb, null); // 总量未知，不定态进度条 + 真实 MB 数
            }
            catch (OperationCanceledException)
            {
                return;
            }
            // 安装完成信号：npx 缓存解压出 @deepseek-ai/dsh
            if (TryFindDshCli(out _))
            {
                SetProgress(lastMb, 100);
                SetStage(2, "依赖安装完成，正在启动服务…");
                return;
            }
        }
    }

    /// <summary>在 npx 缓存中查找 @deepseek-ai/dsh 的 CLI 入口（多个命中取最新的包）。</summary>
    private static bool TryFindDshCli(out string? cli)
    {
        cli = null;
        string? bestDir = null;
        // 候选缓存根目录与 FindNpmCacheRoot 一致，但需遍历全部可能根
        var roots = new List<string>();
        var env = Environment.GetEnvironmentVariable("npm_config_cache");
        if (!string.IsNullOrEmpty(env)) roots.Add(env);
        roots.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "npm-cache"));
        roots.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".npm"));
        foreach (var root in roots)
        {
            var npxDir = Path.Combine(root, "_npx");
            if (!Directory.Exists(npxDir)) continue;
            foreach (var hashDir in Directory.GetDirectories(npxDir))
            {
                var dshDir = Path.Combine(hashDir, "node_modules", "@deepseek-ai", "dsh");
                if (!File.Exists(Path.Combine(dshDir, "package.json"))) continue;
                if (bestDir == null || Directory.GetLastWriteTime(dshDir) > Directory.GetLastWriteTime(bestDir))
                    bestDir = dshDir;
            }
        }
        if (bestDir == null) return false;
        // 动态读 package.json 的 bin 字段（字符串或对象取第一个），版本升级不影响
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(bestDir, "package.json")));
            if (!doc.RootElement.TryGetProperty("bin", out var bin)) return false;
            string? rel = bin.ValueKind switch
            {
                JsonValueKind.String => bin.GetString(),
                JsonValueKind.Object when bin.EnumerateObject().Any() =>
                    bin.EnumerateObject().First().Value.GetString(),
                _ => null
            };
            if (string.IsNullOrEmpty(rel)) return false;
            cli = Path.GetFullPath(Path.Combine(bestDir, rel));
            return File.Exists(cli);
        }
        catch
        {
            return false;
        }
    }

    // ================= ⚙ 设置菜单动作 =================

    /// <summary>打开数据目录（资源管理器）。</summary>
    private void OpenDataDir()
    {
        try
        {
            Directory.CreateDirectory(MarkerDir);
            Process.Start(new ProcessStartInfo("explorer.exe", MarkerDir) { UseShellExecute = true });
        }
        catch { /* 打开失败忽略 */ }
    }

    /// <summary>重启后端服务：终止当前服务与 3080 监听，按标记离线重启，就绪后回到主界面。</summary>
    private async Task RestartServerAsync()
    {
        ShowRestartOverlay();
        _serverReady = false;
        // 终止当前进程树（cmd 包装层已退出的场景由 KillPort3080 兜底）
        SetOverlayStatus("正在停止后端服务…");
        try
        {
            if (_serverProcess is { HasExited: false })
                _serverProcess.Kill(entireProcessTree: true);
        }
        catch { }
        // 端口兜底清理放后台线程：powershell 冷启动 1-3 秒，不能卡住重启浮层
        await Task.Run(KillPort3080);
        _retriedOnline = false;
        var installed = TryGetInstalledDshCli(out var cli);
        SetOverlayStatus("正在启动进程…");
        StartDsh(installed ? DshLaunchMode.Offline : DshLaunchMode.Online, cli);
        if (_launchMode == DshLaunchMode.Online)
            _ = MonitorDownloadAsync(_cts?.Token ?? CancellationToken.None);
        try
        {
            if (_cts == null) return;
            await WaitForServerAsync(_cts.Token);
            if (_cts.IsCancellationRequested) return;
            SetOverlayStatus("服务已就绪，正在返回主界面…");
            _webView.CoreWebView2.Navigate("http://127.0.0.1:3080");
            CloseRestartOverlay();
        }
        catch { CloseRestartOverlay(); }
    }

    /// <summary>重启反馈浮层：深色迷你卡片（鲸鱼图标 + 状态文字），提示重启进程。</summary>
    private void ShowRestartOverlay()
    {
        var ico = new PictureBox
        {
            Size = new Size(44, 44),
            SizeMode = PictureBoxSizeMode.Zoom,
            Location = new Point(158, 26)
        };
        try
        {
            var icoPath = Path.Combine(AppContext.BaseDirectory, "deepseek.ico");
            if (File.Exists(icoPath))
            {
                using var icon = new Icon(icoPath, 44, 44);   // Icon 持有 HICON 句柄，用完必须释放
                ico.Image = icon.ToBitmap();
            }
        }
        catch { }
        // Font 持有 GDI HFONT 句柄，控件 Dispose 不会自动释放自定义 Font，浮层销毁时一并释放
        var statusFont = new Font("Microsoft YaHei UI", 12);
        var titleFont = new Font("Microsoft YaHei UI", 10);
        _overlayStatus = new Label
        {
            Text = "正在重启后端服务…",
            TextAlign = ContentAlignment.MiddleCenter,
            Font = statusFont,
            ForeColor = Color.FromArgb(195, 212, 247),
            Bounds = new Rectangle(0, 84, 360, 40),
            Dock = DockStyle.None
        };
        var title = new Label
        {
            Text = "DeepSeek Harness",
            TextAlign = ContentAlignment.MiddleCenter,
            Font = titleFont,
            ForeColor = Color.FromArgb(126, 162, 255),
            Bounds = new Rectangle(0, 128, 360, 24)
        };
        _restartOverlay = new Form
        {
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.CenterScreen,
            Size = new Size(360, 172),
            BackColor = Color.FromArgb(10, 22, 51),
            ShowInTaskbar = false,
            TopMost = true,
            Padding = new Padding(0)
        };
        _restartOverlay.Controls.Add(ico);
        _restartOverlay.Controls.Add(_overlayStatus);
        _restartOverlay.Controls.Add(title);
        _restartOverlay.Shown += (_, _) => _restartOverlay.BringToFront();
        // 浮层销毁时释放 Font（Show() 显示的 Form 不会自动 Dispose，见 CloseRestartOverlay）
        _restartOverlay.Disposed += (_, _) => { statusFont.Dispose(); titleFont.Dispose(); };
        _restartOverlay.Show(this);
        // 浮层是独立顶层窗口，同样会被 DWM 画 1px 黄边线 → 禁非客户区渲染
        var policy = DWMNCRP_DISABLED;
        DwmSetWindowAttribute(_restartOverlay.Handle, DWMWA_NCRENDERING_POLICY, ref policy, sizeof(int));
    }

    private void SetOverlayStatus(string text)
    {
        try
        {
            if (_overlayStatus is { IsDisposed: false })
                _overlayStatus.Invoke(() => _overlayStatus.Text = text);
        }
        catch { /* 浮层已关闭等竞态 */ }
    }

    private void CloseRestartOverlay()
    {
        try
        {
            if (_restartOverlay is { IsDisposed: false })
            {
                _restartOverlay.Invoke(() =>
                {
                    _restartOverlay.Close();
                    _restartOverlay.Dispose();   // Show() 的 Form 不会自动 Dispose，手动释放控件与 Font
                });
                _restartOverlay = null;
            }
        }
        catch { /* 已关闭 */ }
    }

    /// <summary>释放 3080 端口监听进程（服务硬编码 3080，不会误伤其他应用）。</summary>
    private void KillPort3080()
    {
        try
        {
            var psi = new ProcessStartInfo("powershell.exe",
                "-NoProfile -ExecutionPolicy Bypass -Command \"" +
                "Get-NetTCPConnection -LocalPort 3080 -State Listen -ErrorAction SilentlyContinue | " +
                "ForEach-Object { Stop-Process -Id $_.OwningProcess -Force -ErrorAction SilentlyContinue }\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            };
            using var ps = Process.Start(psi);
            ps?.WaitForExit(3000);
        }
        catch
        {
            // 清理失败不影响退出
        }
    }

    /// <summary>关闭窗口时终止服务进程：先杀进程树（同步，通常 <300ms），
    /// 端口兜底清理放后台线程——powershell 冷启动 1-3 秒是关闭卡顿的主因，不能阻塞 UI。</summary>
    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        _cts?.Cancel();
        _logFlusher?.Stop();
        try
        {
            // 用 .NET 原生 API 杀整个进程树，不依赖外部 taskkill.exe
            // （taskkill 在 DLL 加载链被污染的机器上会启动失败，弹出 0xc0000142）。
            // 不 WaitForExit：Kill 返回后进程树终止由 OS 异步完成，端口随之释放，无需等待。
            if (_serverProcess is { HasExited: false })
                _serverProcess.Kill(entireProcessTree: true);
        }
        catch
        {
            // 清理失败走下方兜底
        }
        // 兜底：npx 安装完成后 cmd/npx 包装层已退出，进程树杀不到 dsh 服务，直接释放 3080 端口。
        // 后台线程执行，窗口立即关闭；应用退出太快时该清理可能被中断，可接受（兜底而已）。
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { KillPort3080(); } catch { }
        });
    }

    /// <summary>深色主题菜单渲染器（中性深灰黑，splash 蓝色阶段与暗色主页阶段都协调）。</summary>
    private class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new DarkMenuColors()) { }
    }

    private class DarkMenuColors : ProfessionalColorTable
    {
        // UseSystemColors=false：强制渲染器使用本颜色表，杜绝回退系统强调色
        public DarkMenuColors() { UseSystemColors = false; }

        // hover/按下渐变：不覆盖时回退系统高亮渐变（强调色=黄 → 黄底），强制中性深灰
        public override Color MenuItemSelectedGradientBegin => Color.FromArgb(58, 58, 64);
        public override Color MenuItemSelectedGradientEnd => Color.FromArgb(42, 42, 48);
        public override Color MenuItemPressedGradientBegin => Color.FromArgb(66, 66, 72);
        public override Color MenuItemPressedGradientEnd => Color.FromArgb(48, 48, 54);
        public override Color MenuItemSelected => Color.FromArgb(26, 255, 255, 255);
        public override Color MenuItemBorder => Color.FromArgb(26, 255, 255, 255);
        public override Color ToolStripDropDownBackground => Color.FromArgb(18, 18, 22);
        public override Color MenuBorder => Color.FromArgb(40, 255, 255, 255);
        public override Color ImageMarginGradientBegin => Color.FromArgb(18, 18, 22);
        public override Color ImageMarginGradientMiddle => Color.FromArgb(18, 18, 22);
        public override Color ImageMarginGradientEnd => Color.FromArgb(18, 18, 22);
        public override Color SeparatorDark => Color.FromArgb(40, 255, 255, 255);
        public override Color SeparatorLight => Color.FromArgb(16, 255, 255, 255);
        public override Color CheckBackground => Color.FromArgb(80, 77, 107, 254);
        public override Color CheckSelectedBackground => Color.FromArgb(100, 77, 107, 254);
    }
}
