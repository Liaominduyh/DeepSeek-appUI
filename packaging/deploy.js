#!/usr/bin/env node
// dsh-appui 部署逻辑（bin 命令入口）：
// 1. 把 app/ 复制到 ~/.dsh/deepseek-appUI/
// 2. 创建桌面快捷方式（WScript.Shell，系统自带，无第三方依赖）
// 3. （命令行运行时可加 --run 参数启动应用）
// 仅支持 Windows；零 npm 依赖（只用 Node 内置模块）。
const os = require('os');
const path = require('path');
const fs = require('fs');
const { execFileSync, spawn } = require('child_process');

const SRC = path.join(__dirname, 'app');
const APP_DIR = path.join(os.homedir(), '.dsh', 'deepseek-appUI');
const EXE = path.join(APP_DIR, 'DeepSeekHarness.exe');
const ICO = path.join(APP_DIR, 'deepseek.ico');
const SHORTCUT_NAME = 'DeepSeek Harness.lnk';

/** 递归复制目录，copyFileSync 天然覆盖，重复安装幂等。 */
function copyDir(src, dest) {
  fs.mkdirSync(dest, { recursive: true });
  for (const entry of fs.readdirSync(src, { withFileTypes: true })) {
    const s = path.join(src, entry.name);
    const d = path.join(dest, entry.name);
    if (entry.isDirectory()) copyDir(s, d);
    else fs.copyFileSync(s, d);
  }
}

/** 获取真实桌面路径（正确处理 OneDrive 重定向），失败兜底 %USERPROFILE%\Desktop。 */
function getDesktopPath() {
  try {
    // Windows PowerShell 5.1 管道输出是 UTF-16LE，用 Buffer 检测 \0 判断编码
    const buf = execFileSync(
      'powershell.exe',
      ['-NoProfile', '-Command', "[Environment]::GetFolderPath('Desktop')"],
      { encoding: null, windowsHide: true }
    );
    const text = buf.includes(0) ? buf.toString('utf16le') : buf.toString('utf8');
    const desktop = text.trim();
    return desktop && fs.existsSync(desktop) ? desktop : path.join(os.homedir(), 'Desktop');
  } catch {
    return path.join(os.homedir(), 'Desktop');
  }
}

/** 用 PowerShell + WScript.Shell COM 创建桌面快捷方式（路径经 JSON.stringify 注入，兼容中文/空格）。 */
function createShortcut() {
  const lnk = path.join(getDesktopPath(), SHORTCUT_NAME);
  const ps = [
    '$ws = New-Object -ComObject WScript.Shell',
    `$s = $ws.CreateShortcut(${JSON.stringify(lnk)})`,
    `$s.TargetPath = ${JSON.stringify(EXE)}`,
    `$s.WorkingDirectory = ${JSON.stringify(APP_DIR)}`,
    `$s.IconLocation = ${JSON.stringify(ICO + ',0')}`,
    `$s.Description = 'DeepSeek Harness 桌面端'`,
    '$s.Save()'
  ].join('; ');
  execFileSync('powershell.exe', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command', ps], {
    stdio: 'inherit',
    windowsHide: true
  });
}

/**
 * 部署应用：复制文件 + 创建快捷方式。
 * @returns {boolean} 是否成功
 */
function deploy() {
  if (process.platform !== 'win32') {
    console.log('[dsh-appui] 仅支持 Windows，跳过安装。');
    return false;
  }
  try {
    copyDir(SRC, APP_DIR);
  } catch (err) {
    console.error(`[dsh-appui] 复制应用文件到 ${APP_DIR} 失败：${err.message}`);
    return false;
  }
  if (!fs.existsSync(EXE)) {
    console.error(`[dsh-appui] 应用文件不完整（缺少 ${EXE}），部署失败。`);
    return false;
  }
  console.log(`[dsh-appui] 已安装到 ${APP_DIR}`);
  try {
    createShortcut();
    console.log(`[dsh-appui] 已创建桌面快捷方式「${SHORTCUT_NAME}」`);
  } catch (err) {
    // 快捷方式失败不阻塞部署：应用仍可手动运行
    console.warn(`[dsh-appui] 创建桌面快捷方式失败（不影响使用）：${err.message}`);
    console.warn(`[dsh-appui] 可手动运行 ${EXE} 启动。`);
  }
  return true;
}

// 作为 bin 命令直接运行时：部署并启动应用（npm 12 默认阻止 postinstall，用户可手动运行 dsh-appui）
if (require.main === module) {
  const ok = deploy();
  if (!ok) process.exit(1);
  if (process.argv.includes('--run') || process.argv.includes('-r')) {
    spawn(EXE, [], { detached: true, stdio: 'ignore' }).unref();
  }
}

module.exports = { deploy, APP_DIR, EXE };
