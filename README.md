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
| `core:network` | REST API（Auth/Chat/**Gamification**）、`TokenManager`/`TokenAuthenticator` 自动续签、WebSocket 客户端 |
| `core:database` | Room 数据库：Entity / DAO / Repository（新增 `user_progress` 积分等级缓存表） |
| `core:push` | 本地通知（含升级庆祝 / 断签提醒）、`ReminderWorker` 提醒调度 |
| `core:ui` | 主题与通用 UI 组件 |
| `feature:auth` | 登录认证 |
| `feature:chat` | 聊天界面（ViewModel + Compose 路由，含互动入口与升级弹窗） |
| `feature:interaction` | 互动菜单（右下角“+”悬浮按钮 + 平铺物品浮层） |
| `feature:profile` | 积分/等级主页、积分流水、补签卡 |
| `feature:history` | 历史记录 |
| `feature:settings` | 设置（含主题偏好存储） |

### 当前状态
核心链路已实现：认证登录、REST 对话、WebSocket 实时消息、Room 本地持久化、提醒与通知、主题设置、
以及「互动礼物 → 积分 → 等级」陪伴养成闭环（互动菜单、积分流水、熊猫等级、补签卡）。

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
- 🎁 **互动礼物 · 积分 · 等级**（新增，详见 `TKS_互动积分等级体系_PRD_v2.md` 与 `docs/互动积分等级体系_接口契约.md`）:
  咖啡/泡面/白龙/手柄/能量棒五种互动物品 → 扣积分并生成拟人回复；
  每日首次 +1、连续 3 天 +3（循环）、纪念日 +100；熊猫成长等级（初生熊猫 → 传奇熊猫）；
  断签清零与补签卡保护
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
| `api/v1/` | FastAPI 版本化路由（auth、health、**interaction、points、level、admin**） |
| `core/` | 配置与统一响应 |
| `handlers/` | 消息处理器（对话、图片、记忆查询、城市设置、**互动发送**） |
| `services/` | 业务服务层（记忆、历史、提示词、天气、认证存储、**积分/等级/补签卡/互动配置**） |
| `jobs/` | 定时任务调度（**断签扫描与提醒、月度补签卡发放、纪念日奖励**） |
| `config/` | 后台可配置项（`gamification_config.json`，首次启动自动生成） |
| `docs/` | 接口契约与对接说明 |
| `tests/` | 单元测试与 API 集成测试 |
| `secure_storage.py` | 落盘数据加密存储 |
| `alert_sender.py` | 飞书告警模块（详见 `README_alert_sender.md`） |

### 互动积分 · 等级体系（新增）

数据模型与规则见 PRD 第五节；运行时数据落在以下加密文件中（与聊天记录同等保护级别）：

| 文件 | 内容 |
|---|---|
| `points_account.json` | 积分余额 + 积分流水（`points_account` / `points_ledger`） |
| `progress_data.json` | 等级与连续天数 + 每日有效对话流水 + 补签卡库存 |
| `config/gamification_config.json` | 互动物品、Prompt 模板、等级阈值、积分规则（**明文，可直接编辑**） |

> ⚠️ **这些 REST 接口由 `ws_api.py` 提供**（同一进程持有 WebSocket 连接与对话状态）。
> 定时任务（断签扫描/补签卡发放/纪念日）也在 `ws_api.py` 启动时拉起。
> 反向代理需按路径分流，完整 nginx 配置与自检命令见
> **[`Taki_Shiina_Bot/docs/部署_nginx与多进程路由.md`](Taki_Shiina_Bot/docs/部署_nginx与多进程路由.md)**，最少需要新增：

```nginx
# 注意：proxy_pass 后面不要带路径，否则会剥掉 /api/v1/ 前缀导致 404
location /api/v1/interaction/ { proxy_pass http://127.0.0.1:8001; proxy_read_timeout 60s; }
location /api/v1/points/      { proxy_pass http://127.0.0.1:8001; proxy_read_timeout 60s; }
location /api/v1/level/       { proxy_pass http://127.0.0.1:8001; proxy_read_timeout 60s; }
location /api/v1/admin/       { proxy_pass http://127.0.0.1:8001; proxy_read_timeout 60s; }
```

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

# 互动积分/等级：自然日判定时区（PRD FR-10，默认 8 即北京时间 UTC+8）
BIZ_TZ_OFFSET_HOURS=8
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
> 说明：仓库中 `tests/test_text_utils.py` 为历史遗留用例，引用的 `split_into_bubbles`
> 已在早前重构中移除，属**改动前就存在**的失败用例；本次新增的积分/等级/互动用例可用
> `python -m pytest tests/ --ignore=tests/test_text_utils.py` 全量跑通。

---

## 📦 发布打包

一键发布脚本 `scripts/release.sh` 负责两端构建、版本号递增与产物归档。

```bash
scripts/release.sh            # deb + apk 全量发布
scripts/release.sh deb        # 只发 deb
scripts/release.sh apk        # 只发 apk
scripts/release.sh --no-bump  # 按当前版本号重新构建，不递增
```

### 产物输出路径

统一输出到 **`~/Desktop/Release/`**（可用 `TKS_RELEASE_DIR` 覆盖）：

| 产物 | 文件名 |
|---|---|
| Linux 安装包 | `TKS-Desktop-<version>-linux-amd64.deb` |
| Android 安装包 | `TKS-Android-<versionName>-<versionCode>.apk` |

- Linux 侧路径由 `TKS_Bot_Linux/electron-builder.yml` 的 `directories.output` 指定；
  `TKS_Bot_Linux/scripts/checksums.mjs`（`npm run release:checksums`）跟随同一目录。
- electron-builder 的 `linux-unpacked/` 与 `builder-*.yml` 中间文件由发布脚本自动清理，
  目录内只留可分发产物。

### 版本号规则

**每次发布前自动递增**，两端独立计数（补丁位 +1）：

| 端 | 版本来源 | 本次 |
|---|---|---|
| Linux | `TKS_Bot_Linux/package.json` → `version` | 1.0.0 → **1.0.1** |
| Android | `TKS_Bot_Android/app/build.gradle.kts` → `versionName` + `versionCode` | 1.2.0(2) → **1.2.1(3)** |

### Android 签名

release 变体的签名密钥**不进仓库**，由环境变量注入
（`TKS_Bot_Android/app/build.gradle.kts` 顶部的 `signingConfigs`）：

```bash
TKS_ANDROID_KEYSTORE=~/1.jks       # 密钥库路径
TKS_ANDROID_KEYSTORE_PASS=...      # storePassword 与 keyPassword
TKS_ANDROID_KEY_ALIAS=1            # 别名，默认 "1"
```

发布脚本内置上述默认值。缺任一变量时不创建 signingConfig，
release 产物退化为 `app-release-unsigned.apk`，构建依然可跑通。
**沿用同一密钥（CN=KrisSLin）才能覆盖安装已装的旧版本。**

### 构建环境

| 依赖 | 位置 |
|---|---|
| Android SDK | `/home/administrator/Android/Sdk`（`platforms;android-34` + `build-tools;34.0.0`），可用 `ANDROID_HOME` 覆盖 |
| JDK 17 | 仓库内 `.jdk-home/jdk-17.0.20.1+1`（`TKS_Bot_Android/local.properties` 指向该 SDK） |
| Gradle 目录 | 仓库内 `.gradle-home` / `.android-home` / `.xdg-home`（由发布脚本导出） |

> 首次在缺少 SDK 的机器上构建，需先装 SDK：
> `sdkmanager "platform-tools" "platforms;android-34" "build-tools;34.0.0"`

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
