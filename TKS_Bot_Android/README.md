# Android_AI_Assistant

Android AI 对话助手前端基础骨架工程（多模块）。

## 模块结构
- `app`
- `core:common`
- `core:network`
- `core:database`
- `core:push`
- `core:ui`
- `feature:auth`
- `feature:chat`
- `feature:history`
- `feature:settings`

## 当前状态
已完成基础工程脚手架、Hilt 入口、Compose 主界面、核心数据模型与网络/数据库占位实现。

## 网络地址配置
- Android 客户端网络地址在 `core:network/build.gradle.kts` 中配置：
  - `API_BASE_URL`（REST）
  - `WS_BASE_URL`（WebSocket）
- `release` 连接正式域名 `takishiinabot.top`；`debug` 连接测试域名 `staging.takishiinabot.top`，均使用 HTTPS / WSS。

## DEV 安装包

`debug` 构建显示为 **Taki DEV**，包名为 `com.krisslin.androidaiassistant.dev`，版本带 `-dev` 后缀。
可与正式版同时安装，登录凭据、数据库和应用文件由 Android 按包名隔离。
该包使用本机调试签名，适合内部测试；后续覆盖安装应继续使用同一调试密钥。

```powershell
# 在 TKS_Bot_Android 目录执行，要求 JDK 17/21 和 Android SDK
.\gradlew.bat :app:assembleDebug
```

Linux 对应命令为 `./gradlew :app:assembleDebug`。
产物为 `app/build/outputs/apk/debug/app-debug.apk`。
测试后端需为 staging 域名配置有效 HTTPS 证书，并把 `/api/v1/` 与 `/ws/chat` 代理至测试后端。
正式 `release` 的包名、版本号和签名配置不受此区分影响。
