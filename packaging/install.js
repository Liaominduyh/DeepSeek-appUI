// dsh-appui postinstall 入口：npm install 时自动部署（复制到 ~/.dsh + 创建桌面快捷方式）。
// 注意：npm 12 起默认阻止依赖的 install 脚本，若脚本未执行，可手动运行 `dsh-appui` 完成部署。
const { deploy } = require('./deploy.js');

if (!deploy()) process.exit(1);
