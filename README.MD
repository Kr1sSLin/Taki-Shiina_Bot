# TKS (Taki Shiina)

TKS 项目仓库，包含 Android 客户端与 Python AI 后端两个子项目：Android 应用通过 REST / WebSocket 接入后端，获取智能对话、记忆与实时推送能力。

## 项目结构

```
Taki-Shiina_Bot/
├── Android_AI_Assistant/    # Android AI 对话助手（Kotlin / Compose，多模块）
└── Taki_Shiina_Bot/         # Python 后端（FastAPI / WebSocket AI 服务）
```

## 架构概览

```
┌──────────────────────┐   REST (8000) / WebSocket (8001)
│ Android_AI_Assistant │ ──────────────────────────────▶
└──────────────────────┘
                                                       ▼
┌──────────────────────────────────┐   ┌─────────────────────┐
│      Taki_Shiina_Bot (Python)    │──▶│ DeepSeek / Gemini   │
│  main.py / http_api.py / ws_api  │   │ OpenWeather 等外部服务│
│    （对话 / 记忆 / 定时任务）        │   └─────────────────────┘
└──────────────────────────────────┘
```

---

## 📱 Android_AI_Assistant

Android AI 对话助手客户端，多模块架构。

### 技术栈
- **语言 / UI**: Kotlin, Jetpack Compose
- **依赖注入**: Hilt
- **本地存储**: Room（聊天消息、附件、用户事实、Bot 通知四张表）
- **网络**: Retrofit/OkHttp（REST + Token 自动刷新）、OkHttp WebSocket 长连接
- **后台任务**: WorkManager（提醒调度）、系统通知

### 模块结构
| 模块 | 职责 |
|---|---|
| `app` | 主应用入口、`MainActivity`、`WebSocketService` 长连接服务 |
| `core:common` | 公共组件（协程调度器封装等） |
| `core:network` | REST API（Auth/Chat）、`TokenManager`/`TokenAuthenticator` 自动续签、WebSocket 客户端 |
| `core:database` | Room 数据库：Entity / DAO / Repository |
| `core:push` | 本地通知、`ReminderWorker` 提醒调度 |
| `core:ui` | 主题与通用 UI 组件 |
| `feature:auth` | 登录认证 |
| `feature:chat` | 聊天界面（ViewModel + Compose 路由） |
| `feature:history` | 历史记录 |
| `feature:settings` | 设置（含主题偏好存储） |

### 当前状态
核心链路已实现：认证登录、REST 对话、WebSocket 实时消息、Room 本地持久化、提醒与通知、主题设置。

### 网络地址配置
位于 `core:network/build.gradle.kts`：
- `API_BASE_URL` — REST API 地址
- `WS_BASE_URL` — WebSocket 地址

⚠️ 默认值为占位地址，请修改为实际后端域名后再运行。

### 构建
```bash
cd Android_AI_Assistant
./gradlew assembleDebug
```

---

## 🤖 Taki_Shiina_Bot

Python AI 后端服务，为 Android 客户端提供 REST / WebSocket 对话、认证与实时推送 API。

### 技术栈
- **语言**: Python 3
- **Web 框架**: FastAPI + Uvicorn（REST / WebSocket）
- **AI 服务**: DeepSeek（OpenAI 兼容接口）、Google Gemini
- **存储安全**: cryptography 加密存储（聊天记录 / 记忆数据落盘加密）

### 主要功能
- 🗣️ **智能对话**: DeepSeek / Gemini 双模型支持，情感化回复与表情注入
- 💭 **记忆管理**: 用户记忆存储、检索与查询命令
- 📸 **图片处理**: 图片识别与分析
- 🌤️ **天气服务**: OpenWeather 实时天气，支持城市设置
- ⏰ **定时任务**: 定时场景更新与提醒调度
- 🔐 **设备认证**: JWT 登录/刷新、设备白名单（最多 4 台）、密码 PBKDF2-SHA256 哈希
- 🚨 **飞书告警**: `alert_sender.py` 在服务异常时向飞书群发送告警（60 秒限流）
- 📡 **实时推送**: WebSocket 防抖合并、断线继续处理、在线实时推送

### 入口文件
| 入口 | 说明 |
|---|---|
| `uvicorn` 加载 `main:app` | FastAPI 认证服务（`/api/v1` 下 health / auth 路由） |
| `python http_api.py` | HTTP 对话 API（默认 `127.0.0.1:8000`，需 `BOT_HTTP_TOKEN`） |
| `python ws_api.py` | WebSocket 对话 API（默认 `127.0.0.1:8001`，需 `BOT_WS_TOKEN`） |

生产环境建议反向代理终止 TLS，服务仅绑定 `127.0.0.1`，并透传 `Authorization` 与 `Sec-WebSocket-Protocol` 头。

### 目录结构
| 路径 | 职责 |
|---|---|
| `api/v1/` | FastAPI 版本化路由（auth、health） |
| `core/` | 配置与统一响应 |
| `handlers/` | 消息处理器（对话、图片、记忆查询、城市设置） |
| `services/` | 业务服务层（记忆、历史、提示词、天气、认证存储） |
| `jobs/` | 定时任务调度 |
| `tests/` | 单元测试 |
| `secure_storage.py` | 落盘数据加密存储 |
| `alert_sender.py` | 飞书告警模块（详见 `README_alert_sender.md`） |

### 环境配置
复制 `.env.example` 为 `.env` 并填写。关键变量：

```env
# AI 服务
DEEPSEEK_API_KEY=your_deepseek_api_key
GOOGLE_API_KEY=your_google_api_key

# 天气
OPENWEATHER_API_KEY=your_openweather_api_key
MY_LAT=0.0
MY_LON=0.0

# HTTP / WebSocket API（必填，否则对应服务无法启动）
BOT_HTTP_TOKEN=your_http_api_token
BOT_HTTP_HOST=127.0.0.1
BOT_HTTP_PORT=8000
BOT_WS_TOKEN=your_ws_api_token
BOT_WS_HOST=127.0.0.1
BOT_WS_PORT=8001

# 认证（JWT，密码推荐 PBKDF2-SHA256 哈希）
AUTH_USERNAME=your_admin_username
AUTH_PASSWORD_HASH=your_password_hash
AUTH_PASSWORD_SALT=your_password_salt
AUTH_JWT_SECRET=your_jwt_secret

# 数据加密（32 字节 key，base64 或 hex）
DATA_ENC_KEY=your_base64_or_hex_key
```

完整变量（含飞书告警、设备白名单、数据迁移开关等）请见 `.env.example`。

### 依赖安装
```bash
cd Taki_Shiina_Bot
pip install -r requirements.txt
```

主要依赖：`fastapi`、`uvicorn[standard]`、`websockets`、`openai`、`google-genai`、`httpx`、`cryptography`、`Pillow`、`reportlab`。

### 测试
```bash
cd Taki_Shiina_Bot
python -m pytest tests/
```

---

## 🚀 快速开始

### 前置要求
- **Android 端**: Android Studio, JDK 11+
- **后端**: Python 3.8+, pip；配置好 `.env`

### 启动顺序
1. 启动后端：`uvicorn main:app`（认证服务），并按需启动 `http_api.py` / `ws_api.py`（对话 API）
2. 修改 Android 端 `API_BASE_URL` / `WS_BASE_URL` 指向后端
3. `cd Android_AI_Assistant && ./gradlew assembleDebug` 构建安装

---

## 📝 开发规范

- 遵循各子项目的代码风格规范
- 提交前进行代码检查和测试
- 使用有意义的 commit message
- 重要变更需更新相应文档

## 📄 许可证

请查看各子项目的具体许可证信息。

---

**最后更新**: 2026-08-03
