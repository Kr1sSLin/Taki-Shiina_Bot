# P4：输入预算与错误恢复

2026-10-23。阈值已由用户在本次对话确认；实现覆盖 HTTP、WS、礼物附言和所有模型外呼。收集/强化开关保持现状，不启用真实模型评测或生产操作。

## 已启用阈值

| 边界 | 默认上限 | 超限行为 |
|---|---|---|
| 单条聊天 / 礼物附言 | 8,000 Unicode code point、32 KiB UTF-8 | 新请求入库、扣款和外呼前拒绝，`edit_input` |
| 纯文字 HTTP / WS / 互动请求体 | 64 KiB 原始 UTF-8 / JSON 字节 | 新请求拒绝；不依赖 Content-Length；已接受身份先识别 |
| 合并轮次 | 8 个来源、12,000 code point、48 KiB UTF-8 | 新来源完整放入下一轮；不撤销已接受来源；已扣礼物合并也受限 |
| 正式记忆 | 默认 12 条、完整元数据合计 12 KiB JSON | 按权限和原有相关性选择整条记录，不截断事实 |
| 提取候选 | 最多 64 条、完整元数据合计 16 KiB JSON | 权限过滤后按相关性选取；集合不完整则保留来源、标记提取未完成，隔离本轮记忆使用，不生成新正式事实 |
| 文本模型外呼 | 完整 JSON 输入 96 KiB；输出最多 4,096 token、返回文本与 reasoning 合计 16 KiB | 发起前拒绝；返回超长或 finish_reason=length 时受控失败；不删政策或跳过完整检查 |
| 识图 | 文本输入 96 KiB；输出最多 1,024 token、4 KiB UTF-8 | 描述超限或输出未完成则失败，不用伪造描述继续生成 |
| 图片传输 | 沿用每轮 3 张、14,000,000 原始字节及 Gemini 内联 20 MB 边界 | 保留原始图片；WS 沿用既有传输上限；HTTP 解析前总传输保护 20 MB |

字符按 Unicode code point 计数，不按 UTF-16 code unit 或视觉字形计数：Emoji 通常计 1；`e` 加组合重音计 2。Android 使用 codePointCount，Windows 使用 EnumerateRunes，Linux 使用字符串迭代；客户端不截断输入。

文本模型继续使用实际传入的模型名称（当前代码默认 `deepseek-v4-pro`，识图默认 `gemini-2.5-flash`）；未读取本机凭据配置。96 KiB 是保守、可执行的完整请求字节预算，包含政策、人设、当前全部来源、礼物模板、记忆/候选元数据、描述、检查内容及其他请求参数；同时设置输出 token 预留。没有可靠 tokenizer / 经确认的模型窗口映射，因此不声称精确 token 窗口保证。已有较小的日常/主动输出上限保留。

旧请求的 GET 状态/终态读取不受新文本上限影响；正常传输范围内原身份重放先识别已存在操作，再决定是否应用新入口限制。HTTP 超过 20 MB 的旧包体仍受传输保护，应使用 `/api/v1/chat/requests/{request_id}` 读取已保存结果。

## 外呼、提取与恢复

- `services/public_policy.py` 统一覆盖生成、提取、完整检查、重写以及其他现存文本任务。SDK 重试关闭，临时连接错误、408/429/500/502/503/504 最多 3 次，退避 0.25 / 0.5 秒；每次外呼 20 秒。外层持久截止时间继续约束退避、重试和业务阶段。
- 互动的接受快照、总 60 秒 / 单次 20 秒、旧操作 15 秒及整轮接收后 120 秒期限保持；不因预算失败或配置变化重新延长。提取重试也受原接收后 120 秒期限约束。
- 旧流式入口保留可迭代帧接口，先收齐有界结果再回放，超限前不输出半截回复。旧 WS 识图复用共享提供方；Telegram 识图也设置输入、图片、输出及 SDK 重试边界。
- 候选集合不完整时不会把“选中的候选里没有冲突”解释为“没有旧事实冲突”。明确禁用/恢复继续使用现有精确控制路径；纠正可用现有 factId / revision 管理入口。一般提取在无法证明完整性时失败关闭，来源仍在可靠日志中。
- 新 `INPUT_TOO_LONG` 只区分旧 EMPTY_MESSAGE 无法准确表达的类别，HTTP 继续使用既有参数错误码。`HTTPException.detail` 及验证错误的 detail 列表保留；验证错误移除原始输入和上下文，不回显正文或凭据。
- `requestIds` 仍是整批来源身份；WS 新增独立 traceId，不拿诊断标识代替来源身份。

| 动作 | 三端处理 |
|---|---|
| edit_input | 保留原文/原图，回填供编辑，不直接重发 |
| poll_status | 查询/同步原操作，不生成新请求 ID |
| retry_same_request | 复用原身份；Linux 补齐原本空的 IPC 重试实现 |
| resend_new_request | 复用 P1 终态语义，用户明确重发时分配新身份 |
| refresh_then_retry | 沿用余额、配置或鉴权刷新入口 |

## 复用测试与有效证据

未新建测试文件；在现有覆盖内补充缺口：

- `test_controlled_chat.py`：各阶段实际请求预算、完整政策保留、最大 Emoji 来源、超长草稿/检查、候选不完整、实际 SDK 的 MockTransport 限流、旧流式边界。
- `test_reliable_api.py` / `test_reliable_chat_store.py` / `test_reliable_interaction.py`：中文/Emoji/组合字符边界，无业务副作用拒绝，分轮不丢来源，满轮礼物隔离，旧身份重放。
- `test_reliable_image_vision.py` / `test_http_image_worker.py` / `test_application_service.py`：识图描述、原图、独立 worker、旧 Telegram 入口的实际外呼参数。
- `test_unified_api.py`：真实 ASGI 请求体测量、旧 detail 形状及敏感输入不回显。
- 同时复用受影响的记忆、日常、主动、互动补偿、隐私、旧 HTTP / WS 测试。

有效结果：

- 后端受影响组合：305 passed、205 subtests passed（20 个现有测试文件，122.73 秒）。最后补齐的最大输入/满轮礼物/识图检查：7 passed、6 subtests passed。两组有重叠，不相加。
- Windows：受影响装配/帧/互动/搜索 49 passed；REST 信封与适配器 16 passed；装配用例增加实际编辑按钮回填和禁止发送断言后 1 passed。后一次替代前一次同用例证据。初次沙箱 loopback 心跳失败，使用本机权限重跑通过。
- Android：`:feature:chat:testDebugUnitTest --offline`，当前工作区 17 项通过；包括共享新旧错误解析、Unicode/恢复动作、既有图片和历史用例。其中 2 条原有未提交时间戳用例不属于 P4，未入本次提交，提交内对应 15 条。JDK 21；未安装设备。工作区原有时间戳相关改动未混入 P4 提交。
- Linux：类型/i18n 检查通过，真实 Electron 主进程 + 本机 Mock 自检 146 项断言通过，包含实际落库与原 ID 传输桩；未创建 GUI 窗口。
- 暂存隐私检查：50 indexed files，passed；`git diff --cached --check` 通过。

## 规模测量与索引决定

复用 `scripts/check_foundation.py --memory --output design/fix-refactor-p4-benchmark.json`，Windows / Python 3.14.6，合成 1k / 5k / 20k 加密事实，包含禁用数据，单次读路径同时开启 tracemalloc。

| 事实数 | select_memories 耗时 / Python 分配峰值 | extraction_candidates 耗时 / Python 分配峰值 |
|---|---|---|
| 1,000 | 578.948 ms / 45,605 B | 513.090 ms / 73,027 B |
| 5,000 | 2,445.377 ms / 40,791 B | 2,546.357 ms / 58,923 B |
| 20,000 | 9,674.143 ms / 40,817 B | 10,608.914 ms / 58,947 B |

选择结果分别稳定在 12 条 / 4,404 B 和 64 条 / 13,880 B。使用有界堆选择而非累积全量候选；内存不随候选数量线性增长。峰值仅指 Python 分配，不含 SQLite/C 原生内存；耗时含追踪开销，不是线上延迟承诺。20k 下仍需逐行解密扫描，保留为规模风险；现有 owner/domain 索引无法解决密文文本相关性，不新增明文敏感词索引。

## 发布边界与回退

Android 真机 Room/重连/重发与三端完整 GUI 未验证；真实模型与实际配置窗口未验证。离线替身证据不替代这些门禁。安装、部署、真实数据迁移和真实模型调用仍需相应单独授权。

回退时保留新操作的有限预算与旧结果读取；可调整至本表已验证有限值，不能关闭全部限制。发生预算故障优先拒绝受影响的新操作并保留来源、普通聊天及结果查询；不恢复未经检查的回复或伪造故障回复入历史。
