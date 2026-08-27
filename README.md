# dsh-appui

DeepSeek Harness 桌面壳子（Windows）：C# WinForms + WebView2 桌面应用，隐藏启动 DeepSeek Harness 后端服务并提供启动动画。

## 安装

```bash
npm install -g dsh-appui
```

安装时自动完成两件事：

1. 把应用文件复制到 `~/.dsh/deepseek-appUI/`
2. 在桌面创建「DeepSeek Harness」快捷方式（图标为 DeepSeek 鲸鱼）

> **npm 12+ 注意**：npm 12 起默认阻止依赖的安装脚本（供应链安全特性）。若安装日志出现 `install scripts blocked` 警告，请手动执行一次部署：

```bash
dsh-appui          # 部署应用并创建快捷方式
dsh-appui --run    # 部署后立即启动应用
```

或安装时显式允许脚本：

```bash
npm install -g --allow-scripts=dsh-appui dsh-appui
```

## 使用

双击桌面快捷方式启动。

- **首次启动**：自动下载 DeepSeek Harness 服务（splash 动画显示实时进度），完成后进入主界面
- **后续启动**：检测到服务已安装，直接启动，不再联网检查

## 手动运行

不通过快捷方式时，可运行：

```
~/.dsh/deepseek-appUI/DeepSeekHarness.exe
```

## 卸载

```bash
npm uninstall -g dsh-appui
```

注意：卸载只移除 npm 包本身，**不会**删除 `~/.dsh/` 下的应用文件与桌面快捷方式，如有需要请手动清理。

## 强制重新安装服务

删除 `~/.dsh/dsh-installed.json` 后重启应用，会强制重新在线安装/更新 DeepSeek Harness 服务。

## 要求

- Windows 10/11（WebView2 Runtime 系统自带）
- Node.js（首次启动时通过 npx 拉取服务）
