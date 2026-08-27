# DeepSeek Harness 桌面端

DeepSeek Harness 的 Windows 壳子：双击启动，自动拉起本地 `dsh web` 服务并把官方界面嵌进窗口。不用自己开终端，也不用盯着首次下载。

<p align="center">
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-blue.svg" alt="MIT License" /></a>
  <img src="https://img.shields.io/badge/platform-Windows%2010%2F11-lightgrey" alt="Windows 10/11" />
  <a href="https://www.npmjs.com/package/dsh-appui"><img src="https://img.shields.io/badge/npm-dsh--appui-cb3837" alt="npm" /></a>
</p>

## 为什么做这个

Harness 官方只有 CLI。日常用就得自己 `npx -y @deepseek-ai/dsh web`，或者自己写启动脚本——首次下载依赖时终端里跑几行进度条，关掉窗口服务也就停了。

这个项目做的事只有一件：把启动过程包起来。首次运行自动下载依赖（进度和日志都有显示），装过之后直接启动。界面就是官方 `dsh web`，没有另做一套聊天页。

## 界面

启动页是个四步指示：**0 检查 DSH → 1 安装依赖 → 2 启动服务 → 3 连接**。装过的机器跳过第 1 步（灰显），几秒内直接到连接。

右侧是下载日志面板：

- npm 输出按行着色（warn 黄、error 红、http 蓝灰），可选中复制
- 往上翻历史时，新日志不会把你拉回底部
- 进度按 npm 缓存目录的实际增长算，显示已下载 MB 数，不是猜的

窗口本身是自绘标题栏：

- 按住标题栏拖动，双击最大化，四边四角 4px 可拉伸
- 窗框颜色分两段：加载中跟着启动页的蓝色走，进入主界面后自动切成网页的暗色调
- ⚙ 菜单三个设置：链接打开方式（系统浏览器 / 壳子内打开）、打开数据目录、重启后端服务

设置持久化在 `~/.dsh/settings.json`。

## 安装

需要 Windows 10/11 和 Node.js（WebView2 系统自带）。

```bash
npm install -g dsh-appui
dsh-appui
```

`dsh-appui` 把应用部署到 `~/.dsh/deepseek-appUI/` 并在桌面创建快捷方式「DeepSeek Harness」。npm 12 默认阻止依赖的安装脚本，所以装完需要手动跑一次这个命令（或者安装时加 `--allow-scripts=dsh-appui` 跳过这一步）；之后点快捷方式启动即可。

卸载：`npm uninstall -g dsh-appui`，再手动删掉 `~/.dsh/deepseek-appUI` 和桌面快捷方式。

## 构建

```bash
build.bat
```

`dotnet publish` 直出 `packaging/app/` 并生成 npm tgz。发布：`cd packaging && npm publish`。

技术栈：.NET 10 + WinForms + WebView2，没有其他依赖。

## 已知边界

- 只支持 Windows。
- 端口固定 3080（Harness 默认）。被占用时启动会失败；「重启后端服务」会先清理 3080 监听再拉起。
- 首次运行需要联网下载 `@deepseek-ai/dsh`，之后离线可用。
- 服务随壳子关闭而退出，不是常驻托盘的后台服务。

## License

MIT
