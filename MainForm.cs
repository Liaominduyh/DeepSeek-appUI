using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace DeepSeekHarness;

/// <summary>DeepSeek Harness 桌面壳子：隐藏启动服务 + WebView2 内嵌界面。</summary>
public class MainForm : Form
{
    private readonly WebView2 _webView = new() { Dock = DockStyle.Fill };
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
    private volatile bool _splashReady;   // splash 页面已加载，可以注入状态文本
    private string? _pendingStatus;       // 页面就绪前积压的最新状态（仅 UI 线程访问）
    // npx/npm 在非交互环境下可能输出 ANSI 颜色码，剥掉避免界面显示乱码
    private static readonly Regex AnsiRegex = new("\x1b\\[[0-9;]*[A-Za-z]", RegexOptions.Compiled);

    public MainForm()
    {
        Text = "DeepSeek Harness";
        Size = new Size(1280, 800);
        MinimumSize = new Size(960, 600);
        StartPosition = FormStartPosition.CenterScreen;
        Controls.Add(_webView);
        Controls.Add(_statusLabel);
        Load += OnLoad;
        FormClosing += OnFormClosing;
    }

    private async void OnLoad(object? sender, EventArgs e)
    {
        _cts = new CancellationTokenSource();
        try
        {
            // 先显示鲸鱼启动动画，同时后台启动服务
            Controls.Remove(_statusLabel);
            await _webView.EnsureCoreWebView2Async();
            // WebView 背景设为与 splash 一致的深蓝色，跳转新页面时不会闪白屏
            _webView.DefaultBackgroundColor = Color.FromArgb(10, 22, 51);
            _webView.CoreWebView2.NavigationCompleted += OnSplashNavigationCompleted;
            _webView.CoreWebView2.Navigate(Path.Combine(AppContext.BaseDirectory, "splash.html"));

            StartServer();
            await WaitForServerAsync(_cts.Token);
            if (_cts.IsCancellationRequested) return;

            // splash 淡出后再跳转主界面，避免直接切换的突兀感
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

    /// <summary>splash 加载完成后补发积压的状态文本（npx 输出可能早于页面就绪）。</summary>
    private void OnSplashNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        _splashReady = true;
        if (_pendingStatus != null) PushStatus(_pendingStatus);
    }

    /// <summary>隐藏启动 dsh 服务（npx 缓存已存在时启动很快）。工作区为 exe 所在目录。</summary>
    private void StartServer()
    {
        var psi = new ProcessStartInfo("cmd.exe", "/c npx -y @deepseek-ai/dsh web")
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
        _serverProcess = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _serverProcess.OutputDataReceived += OnServerOutput;
        _serverProcess.ErrorDataReceived += OnServerOutput;
        _serverProcess.Exited += OnServerExited;
        _serverProcess.Start();
        _serverProcess.BeginOutputReadLine();
        _serverProcess.BeginErrorReadLine();
    }

    /// <summary>把 npx/服务输出转发到 splash 状态栏，下载或更新依赖时能看到真实进度。</summary>
    private void OnServerOutput(object? sender, DataReceivedEventArgs e)
    {
        var line = e.Data;
        if (string.IsNullOrWhiteSpace(line)) return;
        line = AnsiRegex.Replace(line.Trim('\r'), "").Trim();
        if (line.Length == 0) return;
        PushStatus(line);
    }

    /// <summary>服务进程意外退出（下载失败、网络错误等）时提示用户，而不是无限转圈。</summary>
    private void OnServerExited(object? sender, EventArgs e)
    {
        if (_serverReady) return; // 服务已就绪后的退出（正常关窗清理）不提示
        var code = _serverProcess?.ExitCode ?? 0;
        PushStatus($"服务进程已退出（退出码 {code}），启动失败。请检查网络后重新打开应用");
    }

    /// <summary>更新 splash 页面上的状态文本（跨线程安全）。</summary>
    private void PushStatus(string text)
    {
        try
        {
            if (InvokeRequired)
            {
                BeginInvoke(() => PushStatus(text));
                return;
            }
            _pendingStatus = text;
            if (!_splashReady) return;
            _ = _webView.CoreWebView2?.ExecuteScriptAsync(
                $"document.getElementById('status').textContent = {JsonSerializer.Serialize(text)};");
        }
        catch
        {
            // 窗体关闭等竞态，忽略
        }
    }

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

    /// <summary>关闭窗口时终止整个服务进程树（cmd → npx → node）。</summary>
    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        _cts?.Cancel();
        if (_serverProcess is { HasExited: false })
        {
            try
            {
                // 用 .NET 原生 API 杀整个进程树，不依赖外部 taskkill.exe
                // （taskkill 在 DLL 加载链被污染的机器上会启动失败，弹出 0xc0000142）
                _serverProcess.Kill(entireProcessTree: true);
                _serverProcess.WaitForExit(5000);
            }
            catch
            {
                // 清理失败不影响应用退出
            }
        }
    }
}
