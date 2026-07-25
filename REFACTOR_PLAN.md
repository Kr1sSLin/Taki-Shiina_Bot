# Taki-Shiina_Bot 后端整理方案

> 制定时间:2026-07-26  
> 基线:deepseek-v4-pro 迁移 + notify_owner 修复已上线并验证正常,git 已提交,服务器与仓库同步。  
> 总量约 3915 行 Python,目标:阶段 1+2 去掉约 1500 行死代码/重复代码,阶段 3 拆分巨型文件。

---

## 阶段 0:前置动作(动代码前完成)

- [ ] 服务器下线 Telegram 渠道:`systemctl disable --now taki-bot`
- [ ] 打回滚保险:`git tag pre-refactor && git push --tags`

## 阶段 1:Telegram 死代码移除(约 -1113 行,低风险)

**直接删除**(用 `git rm`,历史可恢复;均已确认 ws_api / http_api / main.py 对它们零依赖):

| 文件                                 | 行数            |
| ---------------------------------- | ------------- |
| `bot.py`                           | 283           |
| `handlers/chat_handler.py`         | 316           |
| `handlers/basic_handlers.py`       | 97            |
| `jobs/scheduler_jobs.py`           | 250           |
| `alert_sender.py`                  | 167           |
| `tests/test_chat_handler_utils.py` | 随 handlers 处理 |

**迁移保留**(唯一有价值的幸存者):

- `compute_debounce_window`、`extract_timer_instruction` 两个纯函数 → 移入 `text_utils.py`(或新建 `debounce_utils.py`),`ws_api.py` 内联的防抖逻辑改为复用,测试改指新位置。

**先决检查**:确认 `ws_api.py` 内联的 `greeting_scheduler` / `_delayed_greeting` 已覆盖早安/晚安问候推送,再删 `jobs/`。

**文档同步**:README.MD 与 `.env.example` 移除 `TELEGRAM_TOKEN`、`OWNER_ID`、`MONITOR_BOT_TOKEN` 等 TG 配置项。

**验证**:`py_compile` 全量 → `pytest` → 服务器 `git pull && systemctl restart taki-ws taki-http` → 安卓端冒烟(发消息、等整点问候)。

## 阶段 2:重复模块合并(约 -400 行,低风险)

以 `main.py` 实际引用为准——根目录的是活的,`services/` 下的是孤儿双胞胎:

| 保留                               | 删除                                       |
| -------------------------------- | ---------------------------------------- |
| `api/v1/`(auth 146 + health 7)   | `services/api/v1/`(auth 20 + health 7)   |
| `core/`(config 10 + response 6)  | `services/core/`(config 10 + response 6) |
| `services/memory_service.py`(67) | `memory_db.py`(71)                       |

**验证**:同阶段 1。

## 阶段 3:拆分 `ws_api.py`(945 行,中风险,建议单独安排时间)

目标结构(建议):

| 新模块                      | 职责                                                                                                          |
| ------------------------ | ----------------------------------------------------------------------------------------------------------- |
| `core/config.py`(扩展)     | 集中所有 env(DEEPSEEK_API_KEY / DEEPSEEK_MODEL / GEMINI / JWT / BOT_WS_TOKEN…),消除 ws_api / http_api 各自重复 getenv |
| `ws/connection.py`       | ACTIVE_CONNECTIONS、连接建立与鉴权                                                                                  |
| `ws/debounce.py`         | message_buffer、防抖 worker                                                                                    |
| `ws/chat_flow.py`        | process_buffered_messages、extract_user_facts(可供 http_api 复用,消除两端重复聊天逻辑)                                     |
| `schedulers/greeting.py` | greeting_scheduler、history_separator                                                                        |
| `ws_api.py`              | 瘦身为纯装配层(目标 < 150 行)                                                                                         |

**节奏**:拆 2~3 个 commit,每个 commit 后 `py_compile` + 本地起服务 + 安卓端冒烟,再推服务器。

## 通用纪律

1. 每阶段独立 commit,推服务器后立即冒烟验证。
2. 只删已验证无引用的文件,一律 `git rm` 不物理删除。
3. 现有 3 个测试文件保持可运行,重构后 `pytest` 必须全绿。

## 预期成果

- 阶段 1+2:3915 → 约 2400 行,目录职责一目了然。
- 阶段 3:入口文件 < 150 行,ws/http 逻辑单点维护。
