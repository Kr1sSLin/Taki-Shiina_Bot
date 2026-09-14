# TKS Desktop for Linux（内部代号 `tks-desktop`）

椎名立希（Taki Shiina）AI 陪伴应用的 **Linux 桌面客户端**，对标 Android 端并叠加桌面端增强能力。

- **技术栈**：Electron 32 + TypeScript 5 + React 18 + Vite（`electron-vite` 统一构建主/预加载/渲染三层）
- **后端**：**零改造**，直接对接生产 `https://takishiinabot.top`（REST `/api/v1/` + WS `/ws/chat`）
- **交付依据**：`TKS_Linux桌面客户端_PRD_v1.md`（v1.2）

```
Linux_Desktop_Client/
├── src/
│   ├── shared/            # 主/渲染共用：协议类型、IPC 契约、i18n、等级视觉
│   ├── main/              # 主进程：WS 长连接、SQLite、Token 安全存储、通知、托盘
│   │   ├── app/           #   XDG 目录、结构化日志、设置持久化、事件总线、组合根
│   │   ├── core/          #   network / auth / database / chat / push / gamification
│   │   ├── desktop/       #   tray / shortcuts / autolaunch / window / platform / protocol
│   │   └── ipc/           #   白名单 IPC 处理器（统一结果信封）
│   ├── preload/           # contextBridge 沙箱桥接层（window.tks）
│   └── renderer/          # React 渲染进程：auth / chat / history / profile / settings
├── mock-server/           # 契约一致的本地 Mock 后端（联调用）
├── scripts/               # 图标生成、协议自检、校验和
├── docs/RENDERER_CONTRACT.md
└── electron-builder.yml
```

---

## 快速开始

```bash
npm install                 # 自动为 Electron ABI 重建 better-sqlite3

# ① 连生产后端
npm run dev

# ② 或先连本地 Mock 后端（推荐首次体验，无需账号）
node mock-server/server.mjs                 # 终端 A
npm run dev                                 # 终端 B，然后在设置页把地址改为本地
```

Mock 后端的默认账号：**`kris` / `taki`**，地址：

| 设置项 | 值 |
| --- | --- |
| REST 服务地址 | `http://127.0.0.1:8787/api/v1/` |
| WebSocket 服务地址 | `ws://127.0.0.1:8787` |

> 两者都是回环地址，因此不会触发 FR-CFG-2 的「明文传输」二次确认。

### 可用命令

| 命令 | 说明 |
| --- | --- |
| `npm run dev` | 开发模式（含主进程热重启） |
| `npm run typecheck` | 主进程 + 渲染进程全量类型检查 + i18n 一致性 |
| `npm run check:i18n` | 单独跑 i18n 一致性检查（缺失 / 重复 / 未使用的 key） |
| `npm run build` | 类型检查 + 构建到 `out/` |
| `npm run selftest` | 无界面集成自检（88 项断言，真实 Electron 主进程） |
| `npm run verify` | 协议一致性自检（66 项断言，需先起 Mock） |
| `npm run mock` | 启动契约一致的本地 Mock 后端 |
| `npm run build:linux` | 构建并产出 AppImage / deb / rpm |
| `npm run build:appimage` | 仅 AppImage |
| `npm run build:deb` | 仅 .deb |
| `npm run pack:dir` | 免打包，产出可执行目录（排查打包问题用） |
| `npm run release:checksums` | 生成 `dist/SHA256SUMS`（FR-PKG-6） |
| `npm run icons` | 重新生成占位图标资源 |
| `npm run fix-sandbox` | 一次性恢复完整 Chromium 沙箱（需 sudo，可选，[见下](#-chromium-沙箱曾导致-appimage--npm-run-dev--解包版全都起不来)） |

---

## 关于本地缓存与沙箱

`npm install` 与打包会把下载缓存放在**工程内**（`.npm-cache/`、`.cache/`），
而不是 `~/.npm` / `~/.cache`：

- 在只读 `HOME` 或 bwrap 沙箱下，写全局缓存会直接 `EROFS` 失败；
- 同时也让工程可整体搬移、离线复用。

`.npmrc` 里另外把 Electron 二进制与 electron-builder 辅助二进制指向了 npmmirror 镜像
（本工作区直连 GitHub Releases 会超时）。**若你的网络可直连 GitHub，删掉那两行即可。**

受限环境下建议这样运行构建与自检（把 `HOME` 也指向工程内）：

```bash
export HOME="$PWD/.home" XDG_CACHE_HOME="$PWD/.cache"
npm run selftest
```

---

## 验收自检

本项目提供**两套互补的自检**，都已在此工作区内实际跑通。

### ① 协议一致性自检（裸 WS/HTTP，66 项断言）

```bash
# 终端 A
DEBOUNCE_MS=4000 MERGE_WAIT_MS=300 node mock-server/server.mjs
# 终端 B
node scripts/verify-contract.mjs
```

用与客户端**完全相同**的握手与帧格式逐条核对 PRD 里最容易踩坑的约定
（`pong` 无 payload 包装、`chat.message` 的顶层 `requestId`、`detail.code` 错误形状、
`snake_case` 字段契约、`mergedRequestIds` 时序、4001 关闭码、未知帧忽略等）。

> `DEBOUNCE_MS` 必须**大于** `MERGE_WAIT_MS`，否则防抖 worker 会先消费缓冲，
> 无法复现 §7.1a 的「摘走缓冲」路径（生产环境是 8s 防抖 / 25s 合并等待，天然满足）。

也可以对真实后端跑（需要账号，会占用一个设备位）：

```bash
BASE=https://takishiinabot.top USERNAME_FOR_LOGIN=你的用户名 PASSWORD_FOR_LOGIN=你的密码 \
  node scripts/verify-contract.mjs
```

### ② 集成自检（真实 Electron 主进程，95 项断言）

```bash
npm run selftest          # 自动起 Mock 后端 + 在真实 Electron 里跑
```

`selftest` 以**真实 Electron 主进程**运行，但**不创建窗口、不加载渲染进程**，
因此可以在无显示器、无 GPU、甚至 Chromium 渲染子进程无法启动的受限环境（CI 容器、
bwrap 沙箱）里验证风险最高的那一层：

`safeStorage 凭据往返 → REST 401 自动续签（含并发互斥）→ WS 长连接 →`
`防抖流式回复 → 多气泡拆分落库 → 重复投递去重 → 断线清理 → 提醒排程 →`
`五类通知渠道触发与分类开关抑制 → 积分/等级/互动/补签卡全链路 →`
`数据迁移 → 日志脱敏`

指向真实后端：

```bash
TKS_SELFTEST_API=https://takishiinabot.top/api/v1/ \
TKS_SELFTEST_WS=wss://takishiinabot.top \
TKS_SELFTEST_USER=你的用户名 TKS_SELFTEST_PASS=你的密码 \
npm run selftest
```

### ③ 图形界面端到端自检（需要可用的图形会话）

```bash
npm run build && node scripts/e2e.mjs
```

启动**真实客户端**并经 CDP 驱动渲染进程，走
`主进程 → preload（window.tks）→ 渲染进程` 完整链路。

> ⚠️ **本工作区的沙箱环境无法运行它**：bwrap 的只读 `/` 与受限 `/dev`
> 导致 Chromium 的 GPU 与渲染子进程无法启动（`getProcessId()` 返回占位值、
> `loadFile` 报 `ERR_FAILED (-2)`）。脚本本身是完整可用的，请在带图形会话的机器上运行。

### Mock 后端的测试辅助接口

```bash
# 触发一次升级庆祝（FR-LV-4）
curl -X POST http://127.0.0.1:8787/__test__/trigger -H 'content-type: application/json' \
     -H "Authorization: Bearer $TOKEN" -d '{"kind":"level_upgrade"}'
```

`kind` 可选：`level_upgrade` / `streak_warning` / `makeup_grant` / `bot_error` /
`points_snapshot` / `greeting` / `unknown_frame` / `seed_history`。
另有 `GET /__test__/state` 查看 Mock 内部状态。

### 已验证结论（本工作区实测）

| 项目 | 结果 |
| --- | --- |
| `npm run typecheck`（主进程 + 渲染进程 + i18n 一致性） | 0 error / 0 缺失 / 0 重复 / 0 未使用 |
| `npm run build` | 通过（main 23KB / preload 10KB / renderer 500KB + CSS 47KB） |
| 协议一致性自检 | **66 / 66 通过** |
| 集成自检 | **95 / 95 通过** |
| 原生模块（Electron 32.3.3 / Node 20.18.1 ABI） | better-sqlite3 可用；WAL / 外键 / FTS5 / trigram / integrity_check 全部正常 |
| `safeStorage` | 可用（libsecret 后端） |
| 托盘 / 通知 / 全局快捷键 | 在真实 Electron 进程中创建成功 |
| `electron-builder --linux AppImage deb` | 通过；AppImage ≤ 120MB（NFR-3） |
| deb 打包内容 | `.desktop` 字段正确、7 档 hicolor 图标、AppStream 元数据随包分发 |
| 图形界面端到端（`scripts/e2e.mjs`） | ⚠️ 受沙箱限制未能在本机运行（见上） |
| 真实启动（Ubuntu 24.04 / X11）：`npm run dev`、`dist/linux-unpacked/tks-desktop`、AppImage | 均正常启窗；沙箱模式自动落到 `none`（本机既无 root:4755 helper，内核也禁止非特权 userns），日志 `"sandbox":"none"` |

`INSERT OR REPLACE` 之外，还实测确认了一个关键事实：在外键 `ON DELETE CASCADE` 下
`INSERT OR REPLACE` 会**真的**把附件子行删掉（自检里 `attachmentsAfterReplace = 0`），
这正是 FR-DB-1 要求全部改用 `UPDATE` 的原因。

---

## 架构要点

### 进程模型（PRD §3.2）

WS 长连接、SQLite、Token 存取**全部在主进程**（FR-ARCH-1），对标 Android 的前台服务
`WebSocketService`。窗口隐藏到托盘时主进程连接**不断开**，这是「常驻可达」（G2）的实现基础。

```
主进程 ── WsConnectionManager / Database / AuthStore / NotificationService /
          ReminderScheduler / TrayManager / ShortcutManager / AutoLaunchManager
   │  contextBridge（类型化 IPC，双向）
preload ── window.tks = { auth, connection, chat, images, sync, history,
                          reminders, settings, gamification, desktop }
   │
渲染进程 ── React + Zustand（Auth / Chat / History / Profile / Settings / Interaction）
```

**安全基线（FR-ARCH-2 / NFR-5）**：`contextIsolation: true`、`nodeIntegration: false`、
`sandbox: true`，CSP 禁止 `unsafe-eval`，渲染进程无法访问 `fs` / `net` / `child_process`。

### 目录规范（FR-DSK-11 / FR-DSK-12）

| 用途 | 路径 |
| --- | --- |
| 配置 | `~/.config/tks-desktop/`（`settings.json`、加密凭据） |
| 数据 | `~/.local/share/tks-desktop/`（`tks.db`、`attachments/`） |
| 日志 | `~/.local/state/tks-desktop/logs/`（JSON 行，滚动保留 7 天） |

三者均可用 `TKS_CONFIG_DIR` / `TKS_DATA_DIR` / `TKS_STATE_DIR` 覆盖（便携运行、沙箱环境与测试用）。

### 命令行参数（FR-DSK-9）

| 参数 | 说明 |
| --- | --- |
| `--hidden` | 静默启动到托盘 |
| `--version` | 打印版本号后退出 |
| `--reset-config` | 恢复默认配置 |
| `--force-device-scale-factor=<n>` | HiDPI / 分数缩放覆盖（EDGE-L20） |

---

## 实现中特别处理的后端契约陷阱

这些都是后端源码实测得出的结论，PRD §5 / §7.0 有说明，客户端已逐条适配：

| 陷阱 | 处理位置 |
| --- | --- |
| `chat.message` 的 `requestId` 读**顶层**字段（不是 `payload`） | `core/chat/chat-service.ts::send` |
| `pong` 是顶层 `{type,timestamp}`，**没有** `payload` 包装 | `core/network/ws-client.ts::handleFrame` |
| `http_api` 鉴权失败是 `{"detail":{"code":40101}}`，`code` 不在顶层 | `shared/errors.ts::extractApiErrorCode` |
| 积分路由业务失败是 **HTTP 200 + `code != 0`**，不能只看 `response.ok` | `core/network/rest-client.ts::unwrap` |
| `/level/config`、`/points/history`、`/points/makeup-card*` 是 **snake_case** | `shared/protocol.ts` 逐字段手写 DTO（FR-PROTO-1） |
| WS 无效 token 先 `accept` 再发 `auth.expired`，随后以 **4001** 关闭 | `core/network/ws-client.ts::isAuthFailure` |
| 互动接口最长约 40s，nginx `proxy_read_timeout=60s` → 客户端超时须 **>60s** | `PROTOCOL.INTERACTION_TIMEOUT_MS = 90s` |
| `INSERT OR REPLACE` 会因外键 CASCADE **删掉附件行** | `message-repository.ts` 全部改用 `UPDATE`（FR-DB-1） |
| 互动回复三路到达（WS 推送 / HTTP `reply` / 历史补拉）需按 `messageId` 去重 | `delivered_bot_messages` 表（FR-INT-12 / EDGE-L21） |
| 增量同步 `since` 必须取 `max(本地最大时间戳, 游标)`——只取前者会让「清空会话」后历史立刻回灌 | `chat-service.ts::doSync`（FR-SYNC-3 + FR-CHAT-12） |
| 非法全局快捷键必须报错，不得静默回落默认值 | `shortcuts.ts::update`（FR-SET-7） |
| 「先发文字、防抖未到就点礼物」→ 服务端摘走缓冲，须用 `mergedRequestIds` 置为已送达 | `chat-service.ts::applyMergedRequestIds`（EDGE-L22） |
| `iconUrl` 恒为空串，必须回退 emoji 再回退占位图 | 互动菜单（FR-INT-9 / EDGE-L23） |
| `levelCode=NONE` 时 `levelName` 为空串，须给中性兜底文案 | `lib/level.ts::resolveLevel`（FR-LV-3） |
| `40302` 是死代码；设备超限时后端**静默踢掉最旧设备** | `auth-service.ts::classifyExpiry`（EDGE-L3） |

---

## ⚠️ 打包路径陷阱（曾导致「启动后无画面」）

主进程**不能**直接用 `join(__dirname, '../preload/index.js')` 这类相对路径定位打包资源。

原因：`__dirname` 取决于**代码所在文件**的位置。Rollup 一旦把被共享的模块抽成独立 chunk
（本项目在给主进程加了第二个入口 `selftest.ts` 之后就发生了），
chunk 若落在 `out/main/chunks/`，那些模块里的 `__dirname` 就变成 `out/main/chunks`，
于是 `../preload`、`../renderer` 全部指错 —— 后果是：

- 预加载脚本加载失败 → `window.tks` 不存在 → 渲染端 `window.tks.auth` 报错 → 白屏
- 渲染页面 404
- **而主进程默认完全静默**，表现为「进程在跑、托盘在、但没有任何画面」

两道防线（需同时保留）：

1. `electron.vite.config.ts` 把 `chunkFileNames` 固定为与入口**同层**，
   使 `__dirname` 对入口与 chunk 一致；
2. `window-manager.ts::resolveBundleAsset()` 在运行时按
   `../` → `./` → 逐层向上 的顺序探测，**命中才返回**，找不到时明确报错并弹诊断页。

同时窗口显示不再只依赖 `ready-to-show`（渲染进程起不来时该事件永不触发）：
另有 `did-finish-load` 与 6 秒超时兜底，并监听 `preload-error` / `did-fail-load`，
失败时在窗口内渲染一张诊断页（含日志目录与排查步骤），而不是留一片空白。

> 排查此类问题：看日志里 `scope:"window"` 的行，
> 特别是「加载渲染页面」（应打印正确的 `indexHtml` 路径）与「找不到打包资源」。

---

## ⚠️ Chromium 沙箱（曾导致 AppImage / `npm run dev` / 解包版**全都**起不来）

两条独立的 FATAL，都发生在**主进程 JS 被求值之前**：

```
FATAL:setuid_sandbox_host.cc(163)] The SUID sandbox helper binary was found,
but is not configured correctly. ... is owned by root and has mode 4755.

FATAL:zygote_host_impl_linux.cc(126)] No usable sandbox! Update your kernel ...
```

| 触发条件 | 命中场景 |
| --- | --- |
| ① `chrome-sandbox` 存在但不是 root:4755 | AppImage（nosuid 挂载，**永远**满足不了）、`npm run dev`（npm 装下来是 0755）、`dist/linux-unpacked` |
| ② 加了 `--disable-setuid-sandbox` 但内核禁止非特权用户命名空间 | Ubuntu 24.04 起 `kernel.apparmor_restrict_unprivileged_userns=1`，没有 AppArmor 配置的程序一律被拒 → **必然**命中 |

也就是说，「SUID 不行就退命名空间沙箱」这条路在 Ubuntu 24.04 上是死路——两种模式都用不了，
只剩 `--no-sandbox`。而且**打包时无从判断**目标机器属于哪种情况。

### 修复：在 exec 之前按环境探测

关键在于崩溃早于 JS，主进程里再怎么 `app.commandLine.appendSwitch` 都来不及。
因此沙箱模式统一由**启动包装器**在真正 exec 之前决定，按「从强到弱、取第一个可用」排序：

1. **SUID 沙箱** —— `chrome-sandbox` 为 root:4755（deb/rpm 安装版由 postinst 修好）
2. **命名空间沙箱** —— `--disable-setuid-sandbox`，前提是内核允许非特权 userns
   （探测方式：跑一次 `unshare --user --map-root-user true`）
3. **无沙箱** —— `--no-sandbox`，最后兜底，同时在终端与日志里打告警

落地在三处：

| 位置 | 作用 |
| --- | --- |
| `build/sandbox-launcher.sh` + `scripts/after-pack.mjs` | afterPack 把真身改名 `tks-desktop.bin`，包装器装成 `tks-desktop`。AppImage 的 AppRun、deb/rpm 的 `.desktop`、命令行直跑——所有入口都先过它 |
| `scripts/launch.mjs` | `npm run dev` / `npm start` 的入口，同样的探测逻辑，结果经 `electron-vite … -- <args>` 透传 |
| `build/after-install.tpl` | deb/rpm 安装时把 `chrome-sandbox` 修成 root:4755，让安装版拿到**完整** SUID 沙箱 |

包装器还会**剔除**调用方传入的 `--no-sandbox` / `--disable-setuid-sandbox`——
electron-builder 会把这类开关硬编码进 AppImage 内嵌的 `.desktop`，
前者白白丢掉防护，后者在 Ubuntu 24.04 上直接让程序起不来。

手工覆盖：`TKS_SANDBOX=auto|suid|namespace|none`（默认 `auto`）。
启动日志里的 `"sandbox"` 字段会记录本次实际使用的模式。

> **想拿回完整沙箱**：`npm run fix-sandbox`（需要 sudo，一次即可）。
> 它做两件事——把开发期与 `linux-unpacked` 的 `chrome-sandbox` 改成 root:4755；
> 在启用了 userns 限制的系统上装一份只授予 `userns` 的 AppArmor 配置
> （写法对齐 Ubuntu 官方给 Chrome / VS Code 的 `/etc/apparmor.d/chrome`、`/etc/apparmor.d/code`）。
> 不跑它客户端也能启动，只是会降级到 `--no-sandbox`。

### 为什么自检脚本没发现

`scripts/selftest.mjs` 与 `scripts/e2e.mjs` 都是直接 `spawn(electronBin, ['--no-sandbox', …])`，
恰好绕开了这两条 FATAL，所以全绿的同时真实启动路径是坏的。

---

## ⚠️ AppImage 在 Ubuntu 22.10+ 需要 libfuse2

```
dlopen(): error loading libfuse.so.2
AppImages require FUSE to run.
```

这不是客户端的问题，是 AppImage **运行时**本身的依赖：type-2 runtime 需要 libfuse **2**，
而 Ubuntu 22.10 起只预装 fuse3。三选一：

```bash
# ① 安装 libfuse2（之后双击即可正常启动）
sudo apt install libfuse2t64          # Ubuntu 24.04；22.04/22.10 为 libfuse2

# ② 不装任何东西，自解压运行
"./TKS Desktop-1.0.0-x86_64.AppImage" --appimage-extract-and-run

# ③ 直接装 deb（推荐：顺带拿到完整 SUID 沙箱与桌面集成）
sudo apt install "./dist/TKS Desktop-1.0.0-amd64.deb"
```

---

## ⚠️ 错误码不能放在 Error 自定义属性上（曾导致业务分支失效）

`contextBridge` 在隔离世界之间传递 `Error` 时**只保留 `message` / `stack` / `name`**，
自定义属性会被丢弃。也就是说 preload 里写的：

```ts
err.code = 40201          // ← 到渲染进程就变成 undefined
err.i18nKey = 'error.api.40201'
```

会让渲染端 `errorCodeOf(err)` 恒为 `null`，于是 **40201 / 40204 / 40206 等业务分支
全部失效**（积分不足、已退款、重复补签都走不到对应提示）。

因此错误载荷统一由 `encodeIpcErrorPayload()` **编码进 message**（唯一保证能穿过桥的字段），
渲染端用 `readIpcErrorPayload()` 还原 —— 见 `shared/errors.ts`。
`describeError()` / `errorCodeOf()` 都已改为走这条解码路径。

> 新增 IPC 错误分支时**不要**再假设 `err.code` 存在，一律通过
> `errorCodeOf(err)` / `describeError(err)` 读取。

---

## 与 PRD 的已知差异 / 待补事项

| 项 | 状态 |
| --- | --- |
| **美术资源（FR-UI-8、FR-INT-9、FR-LV-2）** | `视觉资产/` 仅有 `熊猫图像.txt`。应用图标、7 个等级徽章、5 个物品图标均由 `scripts/generate-icons.mjs` **程序化生成占位图**（熊猫意象 + 按 `熊猫图像.txt` 配色）。正式资源到位后**直接替换同名 PNG** 即可，无需改代码 |
| **AppStream 元数据安装** | `.deb` / `.rpm` 通过 `build/after-install.tpl` 安装到 `/usr/share/metainfo/`；AppImage 无法修改宿主，元数据随包放在 `resources/linux/` 供发行版打包者取用 |
| **Wayland 全局快捷键（EDGE-L8）** | Electron 尚未暴露 XDG Portal 的 `GlobalShortcuts` API，因此**不静默失效**：设置页会明确提示并把开关置灰，引导改用托盘图标唤起 |
| **Flatpak（FR-PKG-4）** | 已在 `electron-builder.yml` 声明 portal 权限（网络/通知/文件/autostart），但本地环境无 `flatpak-builder`，**未实测构建** |
| **应用内更新（FR-PKG-7 / OQ-6）** | 仅提示不下载（原始决定），当前「检查更新」按钮展示该说明文案 |
| **互动 AI 失败（40204）** | 客户端展示 `fallbackText` 并提示积分已退回，随后刷新余额；**不做本地补偿**（EDGE-L18，退款由服务端保证） |

---

## 开发者文档

- `docs/RENDERER_CONTRACT.md` —— 渲染进程共享层契约（stores / components / i18n / lib / 路由 / 逐条 FR 落点）
- `Taki_Shiina_Bot/docs/互动积分等级体系_接口契约.md` —— 后端权威契约（字段名契约、错误码、WS 事件）
- `Taki_Shiina_Bot/docs/部署_nginx与多进程路由.md` —— 反向代理分流与排障
