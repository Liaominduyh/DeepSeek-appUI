# DeepSeek Harness 桌面端

DeepSeek Harness（DSH）的 Windows 桌面壳子：C# WinForms + WebView2 内嵌界面，隐藏命令行启动后端服务。

## 功能特性

- 🐳 启动动画：DeepSeek 鲸鱼 logo + 气泡浮动效果
- 📊 四步启动指示：0 检查 DSH → 1 安装依赖 → 2 启动服务 → 3 连接（快启时自动跳过安装步，灰显展示）
- 📡 实时日志面板：npm verbose 输出按行着色批量注入（warn 黄 / error 红 / http 蓝灰），可选中复制、向上翻阅不拉回
- 📥 真实下载进度：轮询 npm 缓存目录增长，显示已下载 MB 数（不定态进度条）
- 🪟 自绘标题栏：无边框窗口 + 纯托管边缘拉伸（四边/四角），拖动 + 双击最大化，DWM 非客户区渲染禁用（无系统边框/黄线）
- ⚙ 设置菜单：链接打开方式（系统默认浏览器 / 壳子内置）、打开数据目录（~/.dsh）、重启后端服务（带状态浮层反馈）
- 🎨 两阶段配色：splash 蓝色主题 → 主界面窗框自动切换为网页暗色同调（无黑边/白边）
- 🖥️ 平滑过渡：splash 淡出后切到主界面，WebView 深色背景避免白屏闪烁
- 🔇 启动用 `dsh web --no-open`：不自动拉起系统默认浏览器，只在壳子内展示
- 🧹 干净退出：进程树清理 + 3080 端口兜底释放放后台线程，关闭窗口无卡顿

## 运行依赖

- .NET 10（运行时随发布目录自带）
- WebView2 Runtime（Windows 10/11 自带）
- Node.js / npm（启动时通过 `npx -y @deepseek-ai/dsh` 拉取后端服务）

## 通过 npm 安装（推荐）

```bash
npm install -g dsh-appui
```

安装时自动把应用部署到 `~/.dsh/deepseek-appUI/` 并创建桌面快捷方式「DeepSeek Harness」。首次启动自动下载服务（splash 显示进度），后续直接启动。

详细说明见 [packaging/README.md](packaging/README.md)。

## 构建

```bash
dotnet publish -c Release -o publish
```

产物在 `publish/` 目录，把整个目录复制到任意位置即可运行。

构建 npm 包（dotnet publish 直出 `packaging/app/` 并生成 tgz）：

```bash
build.bat
```

发布：`cd packaging && npm publish`

## 工作原理

1. 启动后先显示 splash 动画页，同时后台执行 `npx -y @deepseek-ai/dsh web` 启动服务
2. npx 的 stdout/stderr 被重定向并实时转发到 splash 状态栏，下载依赖时有真实进度可见
3. 轮询 `http://127.0.0.1:3080` 直到服务就绪
4. splash 淡出 → 跳转主界面（WebView 背景为深蓝色，与 splash 一致，避免白闪）

## 技术栈

- .NET 10 (net10.0-windows)
- WinForms
- Microsoft.Web.WebView2 (1.0.4129.50)
