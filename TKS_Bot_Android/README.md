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
- 默认值为占位地址，请改成你自己的后端域名再运行。
