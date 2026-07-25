# TKS-Front

TKS (Taki Shiina) 项目前端集合仓库，包含多个子项目。

## 📁 项目结构

```
TKS-Front/
├── Android_AI_Assistant/    # Android AI 对话助手前端应用
├── Taki_Shiina_Bot/         # Telegram 智能对话机器人
└── openspec/                # 规范配置目录
```

---

## 📱 Android_AI_Assistant

Android AI 对话助手前端基础骨架工程（多模块架构）。

### 技术栈
- **开发语言**: Kotlin
- **UI 框架**: Jetpack Compose
- **依赖注入**: Hilt
- **构建工具**: Gradle (Kotlin DSL)

### 模块结构
- `app` - 主应用模块
- `core:common` - 公共组件
- `core:network` - 网络层
- `core:database` - 数据库层
- `core:push` - 推送服务
- `core:ui` - UI 组件库
- `feature:auth` - 用户认证功能
- `feature:chat` - 聊天功能
- `feature:history` - 历史记录
- `feature:settings` - 设置功能

### 当前状态
已完成基础工程脚手架、Hilt 入口、Compose 主界面、核心数据模型与网络/数据库占位实现。

### 配置说明
网络地址配置位于 `core:network/build.gradle.kts`：
- `API_BASE_URL` - REST API 地址
- `WS_BASE_URL` - WebSocket 地址

⚠️ **注意**: 默认值为占位地址，请修改为实际后端域名后再运行。

### 快速开始
```bash
cd Android_AI_Assistant
./gradlew build
```

---

## 🤖 Taki_Shiina_Bot

基于 Telegram 的智能对话机器人，具有情感化对话、记忆管理、定时任务等功能。

### 技术栈
- **开发语言**: Python 3.x
- **核心框架**: python-telegram-bot
- **AI 服务**: OpenAI API, Google Gemini API
- **API 框架**: FastAPI
- **HTTP 客户端**: httpx

### 主要功能
- 🗣️ **智能对话**: 基于 AI 模型的自然语言对话
- 💭 **记忆管理**: 用户记忆存储与检索
- 📸 **图片处理**: 支持图片识别与分析
- 🌤️ **天气服务**: 实时天气查询
- ⏰ **定时任务**: 主动问候、提醒等定时功能
- 📝 **聊天历史**: 对话历史记录与管理

### 核心模块
- `api/` - FastAPI 接口
- `core/` - 核心配置与逻辑
- `handlers/` - 消息处理器
- `services/` - 业务服务层
- `jobs/` - 定时任务
- `tests/` - 测试代码

### 环境配置
创建 `.env` 文件（参考 `.env.example`）：
```env
TELEGRAM_TOKEN=your_telegram_bot_token
OWNER_ID=your_telegram_user_id
DEEPSEEK_API_KEY=your_deepseek_api_key
DEEPSEEK_MODEL=deepseek-v4-pro  # 可选，默认 deepseek-v4-pro，可改为 deepseek-v4-flash
GOOGLE_API_KEY=your_google_api_key
OPENWEATHER_API_KEY=your_openweather_api_key
MY_LAT=your_latitude
MY_LON=your_longitude
MONITOR_BOT_TOKEN=your_monitor_bot_token
DATA_ENC_KEY=your_base64_or_hex_key
```

生产建议使用反向代理终止 TLS，服务仅绑定 `127.0.0.1`，并透传 `Authorization` 与 `Sec-WebSocket-Protocol` 头。

### 依赖安装
```bash
cd Taki_Shiina_Bot
pip install -r requirements.txt
```

### 运行方式

**启动 Telegram Bot:**
```bash
python bot.py
```

**启动 FastAPI 服务:**
```bash
python main.py
```

**启动 HTTP API:**
```bash
python http_api.py
```

### 主要依赖
- `python-telegram-bot` - Telegram Bot API
- `openai` - OpenAI API 客户端
- `google-genai` / `google-generativeai` - Google Gemini API
- `httpx` - HTTP 客户端
- `python-dotenv` - 环境变量管理
- `Pillow` - 图片处理
- `reportlab` - PDF 生成

---

## 📋 openspec

规范配置目录，用于项目开发规范和 AI 辅助配置。

### 结构
- `config.yaml` - 主配置文件
- `specs/` - 规范文档
- `changes/` - 变更记录

### 配置说明
配置文件支持：
- 项目上下文定义
- 技术栈说明
- 开发规范
- AI 辅助规则

---

## 🚀 快速开始

### 前置要求
- **Android 开发**: Android Studio, JDK 11+
- **Python 开发**: Python 3.8+, pip
- **环境变量**: 配置相应的 API Keys

### 克隆仓库
```bash
git clone <repository-url>
cd TKS-Front
```

### 分别启动各项目

**Android 应用:**
```bash
cd Android_AI_Assistant
./gradlew assembleDebug
```

**Telegram Bot:**
```bash
cd Taki_Shiina_Bot
pip install -r requirements.txt
python bot.py
```

---

## 📝 开发规范

- 遵循各项目的代码风格规范
- 提交前进行代码检查和测试
- 使用有意义的 commit message
- 重要变更需更新相应文档

---

## 🔧 维护与贡献

### 问题反馈
如遇到问题，请在 Issue 中详细描述：
- 问题描述
- 复现步骤
- 运行环境
- 错误日志

### 贡献指南
1. Fork 本仓库
2. 创建特性分支
3. 提交变更
4. 推送到分支
5. 创建 Pull Request

---

## 📄 许可证

请查看各子项目的具体许可证信息。

---

## 👥 联系方式

如有疑问或建议，欢迎联系项目维护者。

---

**最后更新**: 2026-03-31
