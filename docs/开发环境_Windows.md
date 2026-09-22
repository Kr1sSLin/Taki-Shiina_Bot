# Windows 开发环境搭建与已知坑

> 本仓库原先主要在 Linux 上开发，本文档记录**在 Windows 上把三端跑起来**的完整步骤。
> 所有命令与结论都在 Windows 10 (19045) x64 上实测过，实测版本见文末「验证记录」。
>
> 设计原则：**双端并存**。Windows 相关改动都是「新增平台分支」或「新增 .ps1 脚本」，
> 原有的 `scripts/release.sh`、`gradlew`（sh）与 Linux 代码路径全部保留可用。

---

## 0. 一分钟总览

| 组件 | Windows 上能做什么 | 不能做什么 |
|---|---|---|
| `Taki_Shiina_Bot`（Python 后端） | ✅ 开发、跑测试、启动三个进程 | — |
| `TKS_Bot_Android`（Android） | ✅ 构建 debug / release APK | — |
| `TKS_Bot_Linux`（Electron 客户端） | ✅ 类型检查、自检、`npm run dev` 开发运行 | ❌ 打包 Linux deb/AppImage/rpm（需 dpkg/fakeroot，无法交叉构建） |

---

## 1. 前置依赖

| 依赖 | 要求 | 说明 |
|---|---|---|
| **Python** | 3.11+（实测 3.14.6） | `requirements.txt` 在 3.14 上可完整安装 |
| **Node.js** | ≥ 20.19（实测 24.13.0） | 仅 Electron 客户端需要 |
| **JDK** | **17 或 21**（实测 21.0.10） | ⚠️ 见下方「坑 1」，**JDK 26 会导致构建失败** |
| **Android SDK** | `platforms;android-34`、任一 `build-tools` | 另需 `platform-tools` 才有 adb |
| **Git for Windows** | 任意较新版本 | 提供 Git Bash；同时带来 `core.autocrlf` 问题（见「坑 2」） |
| （可选）Visual Studio C++ 生成工具 | 仅当需要 `npm install` Electron 原生模块 | 见「坑 5」 |

### Android SDK / JDK 的定位方式

仓库内**不含**任何 SDK/JDK（`.jdk-home/`、`.gradle-home/`、`.android-home/`、`local.properties`
都被 gitignore）。Windows 上按以下顺序定位，无需手工配置也能跑：

- **JDK**：`JAVA_HOME` → Android Studio 自带 JBR → 常见 JDK 安装目录里的 17/21
- **Android SDK**：`ANDROID_HOME` / `ANDROID_SDK_ROOT` → `%LOCALAPPDATA%\Android\Sdk`

> 最省事的组合：装好 **Android Studio**（自带 JBR 21 与 Android SDK），
> 就不用单独装 JDK，`scripts\release.ps1` 会自动探测到两者。

---

## 2. 后端（Taki_Shiina_Bot）

```powershell
cd Taki_Shiina_Bot

# 1) 建虚拟环境（.venv 已被 gitignore）
python -m venv .venv

# 2) 装依赖：运行时 + 开发（pytest）
.\.venv\Scripts\python.exe -m pip install -r requirements.txt
.\.venv\Scripts\python.exe -m pip install -r requirements-dev.txt

# 3) 配置 .env
Copy-Item .env.example .env
#    至少填：DATA_ENC_KEY、AUTH_JWT_SECRET、AUTH_USERNAME、AUTH_PASSWORD
#    本地开发生成一个 DATA_ENC_KEY：
#    .\.venv\Scripts\python.exe -c "import base64,os;print(base64.urlsafe_b64encode(os.urandom(32)).decode())"

# 4) 环境体检（缺什么会直接告诉你怎么补）
.\scripts\run_dev.ps1 check

# 5) 启动
.\scripts\run_dev.ps1 auth     # 认证服务      :8002
.\scripts\run_dev.ps1 http     # HTTP API      :8000
.\scripts\run_dev.ps1 ws       # WebSocket API :8001
.\scripts\run_dev.ps1 all      # 三个各开一个新窗口
.\scripts\run_dev.ps1 check -DryRun   # 只看命令不执行
```

### 跑测试

```powershell
cd Taki_Shiina_Bot
.\.venv\Scripts\python.exe -m pytest tests/ -q --ignore=tests/test_text_utils.py
```

> **为什么必须 `--ignore=tests/test_text_utils.py`**：该用例引用的 `split_into_bubbles`
> 已在早前重构中移除，属**改动前就存在**的历史遗留失败，与平台无关。README 亦有说明。

### 两个容易踩的启动前提

- `main.py` 用 `from Taki_Shiina_Bot.core...` **绝对导入**，所以必须在**仓库根**
  （`Taki_Shiina_Bot` 的上一级）启动 `uvicorn main:app`。`run_dev.ps1` 已自动设好
  `PYTHONPATH` 并把工作目录切到仓库根，手工敲命令时要注意这一点。
- `DATA_ENC_KEY` 缺失或格式非法会在 **import 期**直接抛异常（不是启动后才报错），
  没有可用的降级路径。

---

## 3. Android（TKS_Bot_Android）

日常构建用 `gradlew.bat`（`gradlew` 是 sh 版，Windows 上不能用）：

```powershell
cd TKS_Bot_Android
$env:JAVA_HOME    = 'C:\Program Files\Android\Android Studio\jbr'
$env:ANDROID_HOME = "$env:LOCALAPPDATA\Android\Sdk"

.\gradlew.bat :app:assembleDebug        # 调试包
.\gradlew.bat :app:assembleRelease      # 发布包
```

### 一键发布（推荐）

```powershell
.\scripts\release.ps1            # 构建 release APK，自动递增版本号
.\scripts\release.ps1 --no-bump  # 按当前版本号重新构建
.\scripts\release.ps1 deb        # Linux 产物：会明确告知 Windows 上不可构建
.\scripts\release.ps1 -Help      # 完整帮助
```

`release.ps1` 与 `scripts/release.sh` 是**对等实现**，共用同一套版本号规则与产物命名
（`TKS-Android-<versionName>-<versionCode>.apk`，输出到 `~/Desktop/Release`，可用
`TKS_RELEASE_DIR` 覆盖）。差异只有一处：两端的产物目标不同，见下表。

| 目标 | `release.sh`（Linux） | `release.ps1`（Windows） |
|---|---|---|
| `apk` | ✅ | ✅ |
| `deb` | ✅ | ❌ 明确跳过并给出替代路径（WSL / Linux 主机） |

### 签名

密钥不进仓库，由环境变量注入（见 `app/build.gradle.kts` 顶部）：

```powershell
$env:TKS_ANDROID_KEYSTORE      = 'C:\path\to\1.jks'
$env:TKS_ANDROID_KEYSTORE_PASS = '...'
$env:TKS_ANDROID_KEY_ALIAS     = '1'
```

缺密钥时**不会中断构建**，而是产出 `app-release-unsigned.apk`
（`release.ps1` 会把签名相关变量清空，让构建正确地走到 unsigned 分支）。
但未签名 APK 无法覆盖安装已装的 release 版，正式发布务必显式设置上面三个变量。

---

## 4. Electron 客户端（TKS_Bot_Linux）

```powershell
cd TKS_Bot_Linux
npm install              # ⚠️ 见「坑 5」，无 MSVC 时会失败
npm run typecheck        # 类型检查 + i18n 校验
npm run selftest         # 无界面集成自检
npm run dev              # 开发运行
npm run fix-sandbox      # Windows 上会提示"无需修复"并正常退出
```

`npm run e2e` 在 Windows 上**预期不会全绿**：其中数条断言是 Linux 专属语义
（XDG autostart `.desktop`、托盘/AppIndicator），另有个别与平台无关的既有断言问题。
详见文末「未完成事项」。

---

## 5. 已知坑（都已在脚本里处理或规避，但值得知道）

### 坑 1：JDK 版本不能用 26

Gradle 8.13 只支持在 Java 8–23 上运行，AGP 8.13.2 需要 JDK 17+。装了 JDK 26 会报
`Unsupported class file major version`。**用 Android Studio 自带的 JBR（21）即可**，
`release.ps1` 会自动探测；手敲 `gradlew.bat` 时记得设 `JAVA_HOME`。

### 坑 2：换行符（已通过 `.gitattributes` 修复）

Git for Windows 默认 `core.autocrlf=true`，会把检出内容转成 CRLF。在加 `.gitattributes`
之前，`gradlew` 的首行会变成 `#!/bin/sh\r`，在 Git Bash / WSL 下执行直接报：

```
bash: ./gradlew: /bin/sh^M: bad interpreter: No such file or directory
```

仓库现已加 `.gitattributes`：`*.sh` / `gradlew` 强制 LF，`*.bat` / `*.ps1` 强制 CRLF。
**如果你在加这个文件之前就克隆了仓库**，工作区里可能仍是旧的行尾，执行一次即可修正：

```powershell
git add --renormalize .
# 或对个别文件：删掉后重新检出
Remove-Item TKS_Bot_Android\gradlew
git checkout -- TKS_Bot_Android/gradlew
```

### 坑 3：PowerShell 5.1 下 `.ps1` 必须带 UTF-8 BOM

Windows PowerShell 5.1 会把**不带 BOM** 的 `.ps1` 当 ANSI(cp936) 读取，脚本里的中文
会变乱码并直接导致语法错误（`字符串缺少终止符`）。仓库里的 `*.ps1` 都带 BOM。
如果你新增或编辑 `.ps1` 后发现中文乱码，用这个方式写回：

```powershell
$t = [System.IO.File]::ReadAllText($path, (New-Object System.Text.UTF8Encoding($false)))
[System.IO.File]::WriteAllText($path, $t, (New-Object System.Text.UTF8Encoding($true)))
```

（注意：`Set-Content -Encoding UTF8` 在 5.1 下会加 BOM，但读写 .NET API 更可控。）

### 坑 4：`$ErrorActionPreference='Stop'` + 原生命令写 stderr = 假崩溃

PowerShell 5.1 的经典陷阱：当 `$ErrorActionPreference = 'Stop'` 时，**原生命令写到
stderr 的任何输出都会被当成终止性错误抛出**。表现为「Gradle/uvicorn 刚开始输出就没了」。

仓库里的 `release.ps1`、`run_dev.ps1` 都已在调用原生命令处局部降级为 `Continue`，
并改用 `$LASTEXITCODE` 判定成败。两个相关细节：

- 原生命令的 stdout 会进入 PowerShell 输出流，必须 `| Out-Host` 拦住，
  否则会被函数当返回值一起捕获。
- 传给原生命令的参数若含 `=`，**必须加引号**。例如 `-Dkotlin...=in-process` 不加引号会被
  拆成两段，Gradle 报 `Task '.compiler...' not found`。

### 坑 5：`npm install` 在 Windows 上可能失败（Electron 客户端的真实阻塞）

`TKS_Bot_Linux` 依赖 `better-sqlite3`（原生模块）。`postinstall` 会执行
`electron-builder install-app-deps` 按 Electron ABI 重编译它。如果：

- 本机没有 **Visual Studio C++ 生成工具**，且
- 该 Node/Electron 组合没有 prebuilt 二进制

则 `npm install` 会以 `gyp ERR! find VS ...` 失败并回滚 `node_modules`。

**这是 Windows 上 Electron 客户端唯一的硬阻塞。** 两条出路：

1. **正规做法（推荐）**：安装 Visual Studio Build Tools，勾选「使用 C++ 的桌面开发」工作负载，
   然后正常 `npm install`。
2. **在不装 MSVC 的机器上先跑起来**（实测可行，仅影响本机 `node_modules`，不改仓库文件）：

```powershell
cd TKS_Bot_Linux
npm install --ignore-scripts --no-audit --no-fund

# 手动下载 Electron 二进制（国内网络建议走镜像）
$env:ELECTRON_MIRROR = 'https://registry.npmmirror.com/-/binary/electron/'
node node_modules/electron/install.js

# 手动取 better-sqlite3 的 Electron ABI 预编译包（这一步等价于 postinstall 想做的事）
cd node_modules\better-sqlite3
node ..\prebuild-install\bin.js --runtime=electron --target=<electron版本> --arch=x64
```

`<electron版本>` 取 `TKS_Bot_Linux/package.json` 里 `electron` 的版本（实测 32.3.3）。

---

## 6. 验证记录（本机实测输出摘要）

| 验证项 | 命令 | 结果 |
|---|---|---|
| 后端依赖安装 | `pip install -r requirements.txt -r requirements-dev.txt` | exit 0（Python 3.14.6） |
| 后端测试 | `pytest tests/ -q --ignore=tests/test_text_utils.py` | **136 passed** |
| Windows 文件锁 | `pytest tests/test_progress_store_file_lock.py` | **7 passed** |
| 文件锁跨进程互斥 | 独立脚本：父进程持锁、子进程抢锁 | 子进程阻塞 2.0s，释放后 1.89s 获取 → **互斥真实生效** |
| 认证服务启动 | `.\scripts\run_dev.ps1 auth` + `GET /api/v1/health` | **HTTP 200** `{"code":0,"message":"ok","data":{"status":"up"}}` |
| Gradle / JDK | `gradlew.bat --version` | Gradle 8.13 on JBR 21.0.10 |
| Android 发布构建 | `.\scripts\release.ps1 --no-bump` | **BUILD SUCCESSFUL**，产物 `TKS-Android-1.2.2-4.apk` (13 MB) |
| APK 哈希 | 同上的 SHA256 段 | `98a95f3d…25367e` |
| shell 脚本语法 | `bash -n scripts/release.sh` | exit 0 |
| PowerShell 语法 | `[Parser]::ParseFile` 对 `release.ps1` / `run_dev.ps1` | 无错误 |
| Electron 类型检查 | `npm run typecheck` | exit 0（399 key，i18n 无缺失/重复） |
| Electron 自检 | `npm run selftest` | **127 项断言全通过** |
| Electron 开发运行 | `npm run dev` | 主窗口显示，SQLite 迁移 0→5，托盘可用 |

---

## 7. 未完成事项 / 需要你决定的事项

1. **Linux 侧回归未在真实 Linux 主机验证**：本机是 Windows，无法执行 `release.sh deb`
   与 Linux 的 Electron 流程。所有改动都做了「新增平台分支、不动 Linux 逻辑」的约束，
   但**建议在 Linux 上复跑一次发布**再正式出包。
2. **Electron `npm run e2e` 在 Windows 上非全绿**（61/66）。失败项分三类：
   - Linux 专属语义（XDG `.desktop` 自启断言）——Windows 已改为注册表 Run 项，断言天然不成立；
   - `FR-SET-9` 版本号断言硬编码 `'1.0.0'`，而 `package.json` 已是 `1.0.1`——**与平台无关的既有不一致**；
   - `FR-IMG-8` 剪贴板为空时的错误码跨 IPC 被 `src/shared/errors.ts:toIpcFailure()`
     统一降级成 `error.unknown`，丢失了原始 `i18nKey`——**与平台无关的既有缺陷**。
     后两类建议单独授权修复（会动到 Linux 行为，需回归）。
3. **Windows 开机自启未做真实写入验证**：为避免在你的机器 HKCU 留下自启项，只做了代码路径
   与读回逻辑核对，未实测「重启后确实自启」。
4. **`TKS_Bot_Linux/build/` 目录缺失**（既有问题，与 Windows 无关但会阻断 Linux 打包）：
   `electron-builder.yml` 引用的 `build/sandbox-launcher.sh`、`build/after-install.tpl`、
   `build/after-remove.tpl` 在**全 git 历史中都不存在**，且无任何脚本能生成。
   根 `.gitignore` 已修正（`build/` 改为根锚定 `/build/`），这三个文件现在**可以入库**了，
   但内容需要从原先的 Linux 开发机取回或重写。
5. **Linux 端是否也修**：`release.sh` 里发现了一个确定性缺陷——密钥文件不存在时它只打警告，
   但 `app/build.gradle.kts` 判定 `hasReleaseSigning` 只看环境变量是否有值，
   于是 Gradle 会在 `:app:validateSigningRelease` 硬失败，与脚本自己的提示矛盾。
   已一并修好（改为清空变量走 unsigned 分支），行为与 `release.ps1` 一致。
