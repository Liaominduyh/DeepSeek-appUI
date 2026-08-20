# DeepSeek Harness 桌面端

DeepSeek Harness（DSH）的 Windows 桌面壳子：C# WinForms + WebView2 内嵌界面，隐藏命令行启动后端服务。

## 功能特性

- 🐳 启动动画：DeepSeek 鲸鱼 logo + 气泡浮动效果
- 📡 实时状态反馈：npx 下载/更新依赖的日志实时显示在加载动画上，不再"假死"
- 🖥️ 平滑过渡：splash 淡出后切到主界面，WebView 深色背景避免白屏闪烁
- 🧹 干净退出：关闭窗口时用 .NET 原生 API 清理整个服务进程树（cmd → npx → node）

## 运行依赖

- .NET 10（运行时随发布目录自带）
- WebView2 Runtime（Windows 10/11 自带）
- Node.js / npm（启动时通过 `npx -y @deepseek-ai/dsh` 拉取后端服务）

## 构建

```bash
dotnet publish -c Release -o publish
```

产物在 `publish/` 目录，把整个目录复制到任意位置即可运行。

## 工作原理

1. 启动后先显示 splash 动画页，同时后台执行 `npx -y @deepseek-ai/dsh web` 启动服务
2. npx 的 stdout/stderr 被重定向并实时转发到 splash 状态栏，下载依赖时有真实进度可见
3. 轮询 `http://127.0.0.1:3080` 直到服务就绪
4. splash 淡出 → 跳转主界面（WebView 背景为深蓝色，与 splash 一致，避免白闪）

## 技术栈

- .NET 10 (net10.0-windows)
- WinForms
- Microsoft.Web.WebView2 (1.0.4129.50)
