# Windows 开发：启动与定向验证

在仓库根运行后端体检，再启动推荐的统一进程：

```powershell
.\Taki_Shiina_Bot\scripts\run_dev.ps1 check
.\Taki_Shiina_Bot\scripts\run_dev.ps1 unified
```

首次需要后端虚拟环境、安装锁定依赖并自行填写 `Taki_Shiina_Bot/.env`。不要覆盖已有配置、密钥或数据目录。手动启动与兼容三进程方式见[启动指南](backend-entrypoints.md)。

## 只验证受影响部分

```powershell
# 后端示例：在仓库根运行现有受影响测试；不再排除 test_text_utils.py
.\Taki_Shiina_Bot\.venv\Scripts\python.exe -m pytest Taki_Shiina_Bot/tests/test_unified_api.py -q -p no:cacheprovider

# Android：使用已有 JBR 21 和 SDK，离线运行网络/聊天 JVM 测试，不安装设备
cd TKS_Bot_Android
$env:JAVA_HOME = 'C:\Program Files\Android\Android Studio\jbr'
$env:ANDROID_HOME = "$env:LOCALAPPDATA\Android\Sdk"
.\gradlew.bat :core:network:testDebugUnitTest :feature:chat:testDebugUnitTest --offline --no-daemon
```

Linux 客户端在其目录运行 `npm run typecheck` / `npm run selftest`；后者未指定真实地址时自动使用本机 Mock，在真实 Electron 主进程中验证 SQLite、重连、分页和重发，不创建 GUI 窗口。不要把真实账号地址传给合成自检。

Windows 客户端在其目录运行 `dotnet test Tests/TKSDesktop.Tests/TKSDesktop.Tests.csproj --no-restore`；完整发布继续用原 `scripts/verify.ps1` 和 `build.ps1`，见[发布指南](windows-release.md)。

## 环境失败与验收层次

- 后端 `cryptography/Pillow` 原生 DLL 或临时目录访问失败时，先区分沙箱权限与代码错误；使用获准的本机隔离测试，不删除断言或更改生产文件。
- Gradle 使用 JDK 17/21，推荐 Android Studio JBR 21；不要用 JDK 26。离线缓存缺失时报告依赖缺口，不私自联网安装。
- Electron 原生依赖按其 README 安装；Windows 能运行开发客户端，不代表已验证原生 Linux 安装包。
- PowerShell 5.1 脚本需要 UTF-8 BOM；原生命令最终以退出码判断，不能仅因 stderr 有日志就报失败。

本指南取代根 README 对本地未入库 `docs/开发环境_Windows.md` 的依赖；原文的历史结果不作为当前候选证明。当前结果与未测设备/GUI/发布项写在 [P6 记录](fix-refactor-p6.md)，旧阶段来源见[分期索引](phase-index.md)。
