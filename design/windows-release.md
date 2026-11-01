# Windows M6 发布与人工验收

M6 是 Windows 客户端旧分期的发布阶段，和 Fix-Refactor P6 不同。本指南保留本地原 `TKS_Bot_Windows/docs/M6_发布与验收.md` 的发布边界；版本号由 `TKSDesktop.csproj` 读取，不能用历史 v1.0.0 包证明当前源码。

在 Windows 客户端目录运行 `powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1`。构建前先自行退出托盘中的客户端；脚本不应强杀用户正在使用的客户端。每次产物位于独立 `dist/<build-id>/`，包括 NSIS 安装包、portable、校验和与发布记录。不要从旧 dist 根目录或 artifacts/portable 取包。

正常发布使用已有五层 verify、精确发布二进制 selftest 和启动冒烟。缺少 NSIS 默认失败；`-SkipInstaller/-SkipTests` 只能交付部分候选。发布要求自包含 .NET，不启用未经验证的 WPF 裁剪。包内 `build-info.json` 和同目录 `release-record.json` 标识实际候选。

未签名安装包可能触发 SmartScreen。确认来源和发布记录后再安装，不关闭系统安全保护。构建脚本的发布校验和用于分发完整性；日常改代码不需要反复计算文件摘要。

## 安装前由用户或运维确认

- 对当前拓扑确认健康接口、TLS、鉴权、WS 代理及图片传输大小；兼容三进程才需要养成路由分流至 8001。当前合同见[后端指南](backend-entrypoints.md)。
- 使用可还原测试机和专用账号，安装/卸载由用户操作。检查中文/空格路径、AUMID 通知中心、点击定位、覆盖升级保留数据、自启的明确选择和跨目录单实例。
- 卸载只移除产品清单中的文件；未知文件与 APPDATA 用户数据保留。便携数据在包目录 data，不回退写 APPDATA；更换 Windows 账号无法解密原 DPAPI 凭据属于预期。
- 运行旧 PRD 的人工性能与行为验收，逐项记录通过/失败/未测。自动化自检不替代 GUI、系统通知、真实升级和安装包验收。旧 PRD §16 与 M5 手动清单不在当前入库规格中，完整发布需原负责人补回权威版本，不能按编号推定已通过。

所有必需人工项及运维条件完成前，候选保持 `CANDIDATE_MANUAL_ACCEPTANCE_PENDING`。原生 Linux 包、Android 真机、真实模型、生产部署和数据恢复分别验收，不合并为 Windows M6 的通过结论。
