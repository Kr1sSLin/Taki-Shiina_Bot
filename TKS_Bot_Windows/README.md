# TKS Desktop for Windows

> **TKS Desktop for Windows**（内部代号 `tks-win`）—— 原生 Windows 桌面客户端，与 Linux 端
> **功能完全对等**，作为 Android / Linux 之外的**第三台对等设备**接入同一后端。
>
> 技术栈：**WPF + .NET 8**（`net8.0-windows`）｜仅 **x64**｜目标 **Windows 10 1809+ / Windows 11**
> 后端：**零改造**，直连 `https://takishiinabot.top`（REST `/api/v1/` + WS `/ws/chat`）

---

## M6 发布候选

使用 `build.ps1` 生成本次独立目录 `dist/<build-id>/` 下的安装包、便携包及 SHA256SUMS.txt；旧 dist 根目录或 artifacts/portable 中的包不代表当前源码。
包内 build-info.json 记录程序集哈希与构建身份。缺少 NSIS 默认失败；使用 -SkipInstaller/-SkipTests 会明确留下未完成项，不算完整验收。发布强制自包含，体积超限即失败。
当前完整发布/手动验收步骤及未核实的运维前置项见 [M6 发布与验收](docs/M6_发布与验收.md)。安装包只在用户手动运行时安装，本轮构建不修改已安装客户端。

## 1. 快速开始

### 1.1 前置条件

| 依赖 | 用途 | 安装 |
|---|---|---|
| **.NET 8 SDK** | 构建（必需） | `winget install Microsoft.DotNet.SDK.8` |
| **NSIS 3.x** | 产出安装包（可选，但**通知中心集成依赖它**） | `winget install NSIS.NSIS` |
| Node.js 18+ | 跑 Mock 后端做本地自检（可选） | `winget install OpenJS.NodeJS.LTS` |

> `dotnet` 可能不在 `PATH`（本机实测位于 `C:\Program Files\dotnet\dotnet.exe`）；
> 本文档示例使用绝对路径，如已在 `PATH` 可直接用 `dotnet`。

### 1.2 构建与运行

```powershell
cd TKS_Bot_Windows

# 调试构建（含「警告即错误」门禁）
& "C:\Program Files\dotnet\dotnet.exe" build TKSDesktop\TKSDesktop.csproj

# 运行
.\TKSDesktop\bin\Debug\net8.0-windows\TKSDesktop.exe
```

### 1.3 一键校验（推荐）

```powershell
# 串联：构建 → 单测+机械门禁 → i18n 扫描 → 集成自检（自动拉起 Mock 后端）
.\scripts\verify.ps1
```

**任一环节失败即非 0 退出**（FR-W-PKG-11 / V-W-C7）。五层依次为：
① 构建（`-warnaserror`）→ ② 单测 + 机械门禁 → ③ i18n 静态扫描 → ④ `--selftest` 集成自检
（自动拉起 Mock 后端并轮询其就绪）→ ⑤ `dotnet format --verify-no-changes`（产品 + 测试工程）。

也可单独运行：

```powershell
& "C:\Program Files\dotnet\dotnet.exe" test Tests\TKSDesktop.Tests\TKSDesktop.Tests.csproj
.\TKSDesktop\bin\Debug\net8.0-windows\TKSDesktop.exe --selftest   # 无图形界面，失败退出码非 0
.\scripts\check-i18n.ps1                                          # 禁止硬编码中文
```

> **当前实测状态（M6）**：`verify.ps1` **五层全绿、退出码 0**（213 项 xUnit 全通过；`--selftest`
> 285 项断言 ALL PASS —— 含 DPAPI 往返、迁移链 0→5、trigram 中文子串检索、陷阱 8 复现）。
> `dotnet format` 曾因 `.cs` / `.ps1` 行尾不确定而**不可复现**，已由本目录新增的
> **`.editorconfig`** 固定（源码 LF、Windows 脚本 CRLF）。

### 1.4 打包

```powershell
.\build.ps1                      # 产出 dist\<build-id>\ 下的便携版 + NSIS + 校验和 + 发布记录
.\build.ps1 -SkipInstaller       # 未装 NSIS 时跳过安装包
```

产物：

| 文件 | 说明 |
|---|---|
| `dist\TKS-Desktop-<version>-win-x64-setup.exe` | NSIS 安装包（中文界面、开始菜单 AUMID 快捷方式、卸载清理自启项） |
| `dist\TKS-Desktop-<version>-win-x64-portable.zip` | 便携版（数据与凭据全部写在程序目录下） |
| `dist\SHA256SUMS.txt` | SHA256 校验和 |

版本号**单一来源**：`TKSDesktop/TKSDesktop.csproj` 的 `<Version>`（脚本只读取，不另存）。

---

## 2. 命令行参数

| 参数 | 行为 |
|---|---|
| `--hidden` | **有持久化凭据时**静默启动到托盘并自动建连；无凭据时忽略该参数并显示登录页 |
| `--version` | 打印版本（读程序集元数据）后退出 |
| `--reset-config` | 重置设置为默认值 |
| `--selftest` | **无图形界面**自检；输出逐项 PASS/FAIL，失败退出码非 0 |
| `--force-device-scale-factor=<n>` | DPI 缩放覆盖（如 `1.25`） |

---

## 3. 数据与凭据位置

**安装版**（`%APPDATA%\TKS Desktop\`）：

```
credentials.bin      登录凭据 —— DPAPI 加密密文（仅当前 Windows 用户可解密）
settings.json        应用设置
device.json          deviceId（仅 DPAPI 不可用时的降级载体）
data\tks.db          本地聊天记录（SQLite，WAL + FTS5 双索引）
data\attachments\    聊天图片附件（应用私有目录）
state\logs\          运行日志（JSON 行，滚动保留 7 天）
```

**便携版**：以上全部位于 `<解压目录>\data\` 下，**不写入 `%APPDATA%`**。
若程序目录不可写（如放在 `C:\Program Files\`），会**明确报错并提示**，不会静默回退。

> ⚠️ **凭据始终为 DPAPI 密文，任何情况下不会以明文写入磁盘**（V-W-S4）。
> DPAPI 不可用时（域策略 / 用户配置损坏）**拒绝持久化并告知用户**，不降级为明文。

三个路径可用环境变量覆盖（优先级高于默认值，用于便携运行 / 自动化测试 / 沙箱）：
`TKS_CONFIG_DIR`、`TKS_DATA_DIR`、`TKS_STATE_DIR`。

---

## 4. 通知与托盘

### 4.1 通知中心集成的前置条件（§14.3，**易漏**）

Windows Toast 需要 **AUMID + 开始菜单快捷方式**，两者由**安装包**创建：

1. 进程启动时声明 `SetCurrentProcessExplicitAppUserModelID("TKSDesktop")`；
2. 安装包在开始菜单创建 `TKS Desktop.lnk`，并把其 `AppUserModelID` 属性设为 `TKSDesktop`。

**便携版不创建该快捷方式** → 通知自动**降级为托盘气泡**，设置页会如实展示当前通知能力，
并提示「便携版建议运行一次安装包以获得通知中心集成」。**若需要通知中心集成，请运行一次安装包。**

### 4.2 托盘菜单

显示/隐藏窗口、连接状态、个人中心、已排程提醒、重新连接、设置、退出。
双击托盘切换窗口显隐；关闭窗口默认**最小化到托盘**（可在设置中改为直接退出）。

> 若托盘注册失败（极少见），应用会**临时强制「关闭即退出」**并在设置页提示，
> 避免用户点了关闭后应用凭空消失且无法唤起。

---

## 5. 已知限制（如实登记）

| # | 限制 | 说明 |
|---|---|---|
| 1 | **未做代码签名** | 全新 Windows 上首次运行会有 SmartScreen「不常见的应用」提示，选「更多信息 → 仍要运行」（FR-W-PKG-4） |
| 2 | **检查更新仅提示不下载** | 后端**不提供**更新源（OQ-W-6），客户端**不发起任何更新网络请求**；可选的本地 `version.json` 清单机制见设置页 |
| 3 | 服务端只保留 300 条聊天记录 | 本地库**不做 300 条截断**，是更长期的留存载体，支持导出 JSON / 纯文本 |
| 4 | **离线时禁止发送消息** | 不做离线队列：服务端防抖窗口基于服务端时间，积压补发会被合并成语义混乱的长消息（EDGE-W-6） |
| 5 | 单账号、无注册/改密/找回 | 后端为单账号设计（`AUTH_USERNAME` 比对），客户端无法实现 |
| 6 | 单会话 | `session_id` 固定 `default_session`，与后端单一历史列表一致 |
| 7 | 便携版无通知中心集成 | 见 §4.1；自动降级为托盘气泡 |
| 8 | 正式美术资源未交付 | 应用图标 / 等级徽章 / 物品图标当前为**程序化生成的占位图**（OQ-W-8「等后续」）。替换规程见 `TKSDesktop/Resources/Assets/asset-manifest.json`：覆盖同名文件并把 `status` 改为 `final`，**无需改代码或 XAML** |
| 9 | 仅 x64 | 不需要 arm64（OQ-W-9） |
| 10 | 发布前置检查（运维侧） | 见 §6 |

---

## 6. 发布前置检查（运维侧，FR-W-PKG-12 / V-W-S13）

正式发布前需确认下列三项并记入发布记录；未完成时客户端仍可用，但**必须在发布说明中登记为已知限制**：

| # | 检查项 | 未完成时的影响 |
|---|---|---|
| 1 | nginx 补 `/healthz` 与 `/api/v1/health` 的 `location` | 连通性测试返回 404 → 显示「服务可达，但健康检查路径不可用」（**中间态**，非「地址错误」） |
| 2 | `client_max_body_size` 调大（建议 `128m`） | 大图发送可能 413；客户端已有 **24MB 总重前置拦截**（FR-W-IMG-11）做防御 |
| 3 | 积分四组路由（`/interaction/` `/points/` `/level/` `/admin/`）已分流至 `:8001` | 积分 / 等级 / 互动 / 补签卡不可用（这些路由与 WS **同进程**） |

---

## 7. 工程结构

```
TKS_Bot_Windows/
├── TKSDesktop/                     # WPF 应用（单进程，Generic Host）
│   ├── Contracts/                  # ⚠️ 契约冻结层：常量、等级视觉、错误码、DTO、WS 帧与解析器
│   ├── Core/                       # 业务层（**不得**引用 System.Windows.*）
│   │   ├── Auth/                   # 凭据（DPAPI）、deviceId、Token 生命周期与 401 互斥续签
│   │   ├── Data/                   # SQLite：迁移链 0→5、双 FTS 索引、仓储、损坏恢复
│   │   ├── Network/                # REST（超时分级/双形状错误码）、WS（心跳/退避/4001）
│   │   ├── Services/               # 对话/同步/提醒/媒体/养成/通知 + 窄端口（Ports）
│   │   └── Platform/               # 10 个平台能力**接口**（W-P5：唯一允许平台差异处）
│   ├── Platform/Windows/           # 上述接口的 Windows 实现（托盘/Toast/自启/快捷键/剪贴板/窗口/电源/DPAPI）
│   ├── Views/ + ViewModels/        # MVVM（CommunityToolkit.Mvvm）
│   ├── Resources/Themes/           # 设计令牌、液态玻璃、涂鸦背景、控件样式
│   ├── Diagnostics/SelfTestHost.cs # `--selftest` 无图形界面自检
│   ├── App/                        # 路径、设置、日志（JSON 行 + 脱敏）、i18n、组合根、启动协调器
│   └── Program.cs                  # 入口（AUMID 声明、CLI 分派）
├── Tests/TKSDesktop.Tests/         # xUnit 单测 + 机械门禁（V-W-S1..S13 / V-W-C1..C7）
├── installer/                      # NSIS 脚本（含 AUMID 快捷方式）+ LICENSE
├── scripts/                        # verify.ps1（一键门禁）、check-i18n.ps1
├── docs/                           # M0 实测报告、跨端契约登记
└── build.ps1                       # 打包（NSIS + 便携版 + SHA256）
```

**依赖方向（强制）**：`Views → ViewModels → Core → Platform/Data`。
`Core` **不得**引用 `System.Windows.*`（有架构测试断言）；`Platform` **不得**被 `Core` 反向依赖（经接口注入）。

---

## 8. 关键契约（不要改）

本端作为**第四端**加入既有跨端契约体系，常量与落点登记见 **`docs/跨端契约.md`**。要点：

- **不要改** `Contracts/ProtocolConstants.cs` 的数值（心跳 25s、退避 `2^n`/上限 60s/15 次、4001、
  超时分级 30s/90s/150s/10s、图片 3×20MB、总量 24MB、提醒 30min、通知截断 160 字符…）；
  门禁会机械断言（V-W-C5 反向验证：**故意改一个常量必须让门禁失败**）。
- **通知语义 ID 必须按 Android 基准**：`1002 CHAT` / `1003 GREETING` / `1004 ERROR` / `1005 REMINDER`
  / `1006 LEVEL` / `1007 STREAK` / `1008 PROGRESS`。**不要照抄 Linux 端现值**（其 1004–1006 语义与 Android 互换）。
- **DTO 必须逐字段手写 `[JsonPropertyName]`**，禁止全局命名策略转换。三处 `snake_case` 例外：
  `/level/config.levels[]`、`/points/history.items[]`、`/points/makeup-card*`。
- **更新消息必须用 `UPDATE`**，禁止 `INSERT OR REPLACE`（外键 CASCADE 会真的删掉附件子行 —— 已实测复现）。
- **不得依赖 `points.snapshot`**（后端死事件，无调用点）；重连全量对齐走 `GET /points/overview`。

M0 三项硬性实测的结论与证据见 **`docs/M0_实测报告.md`**（含一条重要发现：
`H.NotifyIcon.Wpf` **必须锁 2.3.2**，2.4.1 无 `net8.0` 资产会触发 NU1701 而与「零警告」门禁冲突）。

---

## 9. 开发约定

- **界面文案不得硬编码中文**：一律 `App.I18n.T("key")`；`scripts/check-i18n.ps1` 会静态扫描并失败。
  XAML 中请把文案暴露为 ViewModel 字符串属性再 `{Binding}`，或用 `ObjectDataProvider` 调 `I18n.T`。
- **`Core` 层禁止** `.Result` / `.Wait()`；一律 `async/await`。
- **`ObservableCollection` 变更必须经 `IUiDispatcher`** 调度到 UI 线程。
- **每次网络/凭据操作都要考虑「能力不可用」的降级路径**（托盘、Toast、快捷键、系统模糊、DPAPI），
  且降级必须**在界面上如实可见**，不得静默失效。
- 新增平台能力：先在 `Core/Platform` 定义接口 + 假实现（使 `Core` 单测无需真实 Windows 会话），
  再在 `Platform/Windows` 实现。

---

## 10. 排障

| 现象 | 排查 |
|---|---|
| 启动即要求重新登录，且每次都要登录 | 检查 `%APPDATA%\TKS Desktop\credentials.bin` 是否存在；设置页「关于」会显示**凭据存储状态**（「已加密保存（DPAPI）」/「未保存——系统无法安全存储」）。若显示未保存，说明 DPAPI 在当前账户下不可用（域策略 / 用户配置损坏） |
| 换 Windows 账户后提示「登录状态已失效」 | **预期行为**：DPAPI 密文绑定原账户，换账户无法解密 → 视为凭据损坏（**不会**静默删除文件） |
| 超过 7 天未打开，被要求重新登录 | **预期行为**：客户端本地强制的免登录空闲上限（服务端 Refresh TTL 为 30 天，客户端主动收紧到 7 天） |
| 通知弹不出来 | 便携版或未创建开始菜单快捷方式（§4.1）；设置页会显示当前通知能力并自动降级为托盘气泡。系统「专注助手 / 勿扰」开启时也会被抑制（但**仍正常收消息**） |
| 连通性测试说「服务可达，但健康检查路径不可用」 | nginx 未配置 `/healthz` location（§6 检查项 1）；**服务本身是通的**，该文案就是为此区分 |
| 提示「本地图片附件不可恢复」 | 数据库 `integrity_check` 失败并已备份重建（损坏库备份为 `tks.db.integrity-failed-*.bak`）。服务端数据可通过全量同步恢复，但**附件只在本地** |
| 想看详细日志 | 设置页「打开日志目录」（`%APPDATA%\TKS Desktop\state\logs\`，JSON 行，保留 7 天）。日志已脱敏，**不会**包含 Token / 密码 / 图片 base64 |
| 托盘图标不见了 / 点关闭应用就退出 | Explorer 重启会触发托盘重新注册；若托盘始终不可用，应用会临时强制「关闭即退出」并在设置页提示 |

---

## 11. 文档索引

| 文档 | 内容 |
|---|---|
| `docs/跨端契约.md` | **本端作为第四端的契约登记**：C-1 ~ C-8 常量与落点、机械校验测试索引、已知漂移与不照抄项、发布前置检查 |
| `docs/M0_实测报告.md` | **M0 三项硬性实测**（托盘库兼容性 / DPAPI 行为 / 资源清单机制）的证据与结论，含 SQLite trigram 可用性与陷阱 8 复现 |
| `TKSDesktop/Resources/Assets/asset-manifest.json` | 美术资源**单一清单**（逻辑名 → 文件名 → 期望尺寸 → `placeholder`/`final`），正式资源替换规程 |
| `../PRD/TKS_Windows桌面客户端_PRD_v1.md` | 本端完整开发 PRD（v1.1） |
