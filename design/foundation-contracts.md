# 阶段 A 实际契约与合成样例

冻结日期：2026-10-16。范围为 PRD-00 的共用约束及 PRD-01 的阶段 A，并记录用户随后明确要求开放图片的例外。本文以当前本地代码为准，描述已经存在的聊天、事实仓储与可靠任务能力；不代表已经执行生产迁移、服务器部署或后续阶段验收。

## 1. 契约层次与启用条件

| 层次 | 当前实现 | 边界 |
|---|---|---|
| 外部协议 | `POST /api/v1/chat`、`GET /api/v1/chat/history`、`GET /api/v1/chat/images/{request_id}/{index}`、`GET /api/v1/memory/facts`、`/ws/chat`、互动菜单与发送 HTTP 路由 | 图片下载路径用源请求 ID 与从 0 开始的序号；响应是原图片二进制和 MIME，其余外部 JSON 字段沿用 camelCase |
| 应用编排 | `ChatRuntime.accept/submit/wait/request/ready/cancel` | HTTP、WS、互动共用可靠接收和回复结果；适配器保留各自响应格式 |
| 持久仓储 | `ReliableChatStore` 继承 `FoundationStore` | 短事务、加密来源与正文、CAS、任务、变更、outbox；不向客户端直接开放数据库 |
| 声明类型 | `ChatCommand`、`LogicalTurn`、`FactRef`、`GenerationContext`、`Domain` | 声明类型不等于全部字段已经进入当前运行时；实际生成快照见第 4 节 |

使用 `FOUNDATION_ENABLED=1` 首次启用当前数据目录的 `foundation.sqlite3`。没有数据库且未启用时，保留旧 JSON 兼容路径。数据库存在时，即使把该变量设回 `0`，仍必须使用 SQL 权限与可靠性边界；不能借开关绕回旧写入。当前进程已装配可靠运行时后，数据库消失会明确报错。已有空库、外来库、错误密钥或损坏内容不能被当作新空状态初始化。

推荐 `unified_api`：HTTP 与 WS 共用 WS 装配的状态、仓储、模型和运行时。`configure_interactions` 在启动 worker 前绑定互动补偿回调；仅支持 HTTP 的装配不抢走未注册的互动恢复任务。启用 SQL 不会自动导入或删除旧 JSON；历史展示仍可按认证用户合并旧记录和 SQL 记录，模型读取另行受权限检查。

WS 文字与图片进入同一可靠链路。客户端可在 `chat.message.payload.images` 传入最多 `VISION_MAX_IMAGE_COUNT` 张 `{mimeType,dataBase64}`；默认仅接收 JPEG/PNG、配置的单张 20 MB 上限、单轮最多 3 张。Gemini inline 请求还有整轮原图总量 14,000,000 字节的有效上限，超过张数或总量的后续消息进入新轮，单条消息超限则接收前拒绝。未配置 `GEMINI_API_KEY` 时图片也在接收前明确拒绝。服务端验证图片格式，保存原始字节的规范 base64 与 SHA-256 到加密源消息；恢复和重试读取同一份原图，再交给 Gemini。历史图片行只给 `requestId/imageCount/contentType`，不内联原图；登录设备可用 Bearer 令牌下载单张原图，跨用户、缺图或越界统一 404，恢复权限门闩未核验时 503，响应禁止缓存。用户后来明确允许图片 EXIF 和画面坐标进入服务器及识图模型，这与原 PRD-01 FND-10 构成限定例外；文字输入、识图后的文字描述和文字回复仍清理详细坐标。HTTP 聊天输入接口仍只定义文本 `message`，新增的图片 HTTP 接口仅供历史原图读取。SQL 模式不恢复旧主动问候生成链路；后续主动消息资格、配额及其它后续业务属于后续阶段。

## 2. ChatCommand：认证身份与不可变请求配对

内部字段如下：

| 字段 | 含义与当前行为 |
|---|---|
| `owner` | 服务端由认证上下文取得；忽略请求自行声明的用户身份，不能接受模型提供的 owner |
| `request_id` | 调用者应重试时保留的请求 ID；缺省时适配器生成，因此缺省请求不能获得跨次重试的稳定身份 |
| `client_message_id` | 客户端消息 ID；HTTP/WS 缺省为 `request_id`；互动固定为 `interaction_user_` 加请求 ID |
| `device_id` | 认证设备或服务端装配的互动设备标识；允许另一设备重试原身份配对 |
| `text` | 清理详细坐标后的当前输入；保存到认证加密的源消息域 |
| `context_revision` | 内部上下文版本，当前 HTTP/WS 适配器使用默认 `0`，没有新增外部上下文修订协议 |
| `channel` | `http` 或 `ws`，记录首次接收的上下文策略；互动使用 `ws` |
| `intent` | `chat` 或 `interaction`；由服务端适配器装配 |
| `metadata` | 可选内部字典；互动保存物品信息、附言、`render_prompt(item)` 模板快照和 `business_date`；WS 图片保存原图的 `mimeType/dataBase64/sha256`，不保存旧历史、画像或完整 prompt |

owner、两个请求身份、设备和正文必须为非空字符串，四个身份字段长度分别不超过 200。存储同时约束 `(owner, request_id)` 与 `(owner, client_message_id)` 唯一。首次接收绑定两个 ID：同一认证用户只改其中一个 ID、改变正文或上下文版本会冲突；相同配对跨 HTTP/WS、跨设备重试返回原来源与逻辑轮次。设备和 channel 不会覆盖首次保存的来源。

通用 `accept` 的重放比较字段为两个身份、正文、`context_revision` 及图片元数据；不要把它描述成任意 metadata 的通用深度比较器。相同请求 ID 换图会冲突，完全相同的图片重试返回原轮次。互动处理器另行核对原物品 ID 与附言，并读取已经持久化的模板/物品快照，配置变化或下架不改写已接收互动。HTTP 的 `userId`、`meta` 或 `conversationId` 不会直接变成内部 owner、intent 或权限依据。

`accept` 同一事务保存源消息、去重映射、轮次引用及必要提取任务。返回值实际为：

```json
{"message_id":"11111111111111111111111111111111","turn_id":"22222222222222222222222222222222","duplicate":false}
```

上例 ID 是合成占位值；实际新实体使用 UUID 十六进制字符串。源消息 `message_id`、用户时间线 `client_message_id`、助手回复 `messageId` 是三种身份，不能混用。基础仓储 `FoundationStore.receive` 是保留的单来源闭环接口，额外返回提取 `job_id`；生产文本入口使用 `ReliableChatStore.accept`。

## 3. LogicalTurn 与互动支付恢复

SQL 轮次的实际列为 `id/owner/status/generation/created/deadline/body`。加密 body 保存来源 ID 列表、聊天 job ID、是否允许合并、持久结果，以及互动支付状态。新轮次代次为 `1`，初始截止时间为接收时刻后 120 秒；互动首次进入生成时进一步收紧为首次生成起不超过 15 秒，见第 4 节。UTC Unix 秒用于内部时间，回复/同步对外时间戳使用 UTC Unix 毫秒。业务日期沿用 `time_utils`，默认 UTC+8，可由已有 `BIZ_TZ_OFFSET_HOURS` 配置覆盖。

WS 在接收文字或图片时就持久接收，再等待防抖窗口；8/20 是秒数，角色回复后的 40 秒内使用 20 秒，否则 8 秒。`merge_pending=True` 仅合并尚未开始的 pending 轮次，且合并后图片总数及原图总字节数不能超过上限；来源增加时推进代次，超限的新消息开新轮。running/completed 来源不重新回答。`ready` 可提前释放已接收任务的延迟，不重建来源身份。

每个逻辑轮次最多一个礼物：同批多个互动被拒绝；新礼物不并入已有礼物的 pending 轮次，后续 WS 文字仍可并入该轮。该限制使支付与整批取消只有一个礼物退款边界。

互动的可靠顺序如下：

1. 先持久接收 gift 来源、支付 `pending` 状态及 `interaction_reconcile` 任务；恢复任务最初在 15 秒后可运行。无附言时可延迟聊天 20 秒以合并尚未开始的 WS 文字。
2. `charge_interaction` 在短 SQL 事务内验证支付门禁，再同步调用原积分账本扣款；不执行模型或网络。原扣分仍使用 `item_send:{owner}:{request_id}`，金额及活动/首卡规则保持既有实现。
3. 门禁确认后为 `confirmed`，聊天 worker 才能领取。已取消或正在补偿的门禁在执行扣款回调之前拒绝，阻止迟到扣款。若 JSON 已扣而 SQL 确认失败，先前持久化的意图仍可供恢复核账。
4. 确认支付不会结束恢复监测。聊天还在正常 pending/running/retry_wait 且未过截止时间时，恢复 job 不被领取、不消耗尝试预算；成功完成后可无副作用结束。聊天最终失败、已取消或截止后，恢复事务先取消/隔离整个轮次和旧租约，再核账退款。
5. `reconcile_reliable` 核对原 owner、request、物品及真实负扣款；无扣款不退款，已退款不再加钱，实际退款继续使用原 `item_refund:{owner}:{request_id}`。退款后或取消后进程中断，恢复任务仍可继续核对。

这是一组持久补偿流程，不能称为 JSON 积分账本与 SQL 的分布式原子事务。SQL 接收失败时根本不扣款，也不凭空退款。取消 HTTP 等候不自动退款；在线失败需要先原子取消轮次，若取消检查发现回复已经提交，则返回原成功结果，禁止再退款。

用户已确认失败策略：礼物退款后，合并的整批文字明确失败并保留来源，由用户重新发送；不自动拆分礼物、重排原文字或新建回复。原 requestId 仍指向原已取消轮次；新发送使用新的请求身份。

## 4. 当前实际 GenerationContext 与最终回复

`begin_turn(job)` 返回的运行时快照是字典，当前字段为：

```json
{
  "turn_id":"22222222222222222222222222222222",
  "generation":1,
  "deadline":1792110120.0,
  "owner":"synthetic-owner",
  "sources":[{"owner":"synthetic-owner","request_id":"request-synthetic-1","client_message_id":"client-synthetic-1","device_id":"device-synthetic","text":"今天吃了青椒","context_revision":0,"channel":"http","intent":"chat","metadata":null}],
  "permission_epoch":0,
  "legacy_allowed":true
}
```

`permission_epoch` 为该 owner 最近一次 `disabled/restored/updated` 变更的事件序号；新建事实和回复完成不无故失效正在生成的回复。`legacy_allowed` 在任一事实禁用时关闭无法精确映射来源的旧上下文，恢复状态未核验时整体拒绝业务。应用服务还检查旧权限门禁，模型历史必须与这两个允许状态取交集；关闭后只允许公开人设与当前输入，不能从旧画像、摘要、场景缓存或历史旁路取回禁用内容。

每次模型调用和重试前执行 `assert_snapshot`；聊天续租、最终回复提交再次检查权限、代次、截止时间、owner 和租约。模型与网络处于事务外。`GenerationContext` 声明类型中的 `source_message_ids/eligibility/prompt_version` 是后续契约预留，当前文本运行时没有把逐事实资格列表或持久 prompt 版本完整接上，不能声称已经实现该精度。

`commit_reply` 同一事务保存每条用户来源的时间线投影、助手回复、轮次结果、完成事件/outbox 与聊天 job 成功状态。不等客户端 ACK 才保存。返回结果含 `reply/messageId/timestamp/requestIds/eventId/deliveryId`，另有模型 usage、可选计时指令和互动信息；互动结果同时保留物品信息与生成尝试次数。提交失败不发送成功结果，`CommittedJob` 告诉 worker 该业务与任务终态已经在仓储处理，不能二次 finish。

互动生成使用持久累计上限：第一次 `begin_turn` 将 SQL `deadline` 和加密 body 中的 `generation_deadline` 固定为 `min(原截止时间, 首次开始生成时刻 + 15秒)`。防抖等待不计入这 15 秒；失败重试、租约恢复及新进程再次开始生成都沿用原值，不能重新获得 15 秒。模型调用使用剩余时间；超过截止时间后，开始生成和提交旧结果均被拒绝，恢复任务核对真实扣款后退款。在线请求等候和单次生成内部重试也各自受不超过 15 秒限制。

## 5. Fact、修订、时间与权限

| 契约 | 当前实际能力 |
|---|---|
| 身份 | `fact_id` 固定绑定 owner 与 domain；不能借同 ID 转移用户或领域 |
| 修订 | 当前行存整数 `revision`；新增需 `expected_revision=0`，修订/权限操作使用 CAS 并递增；没有独立 UUID revisionId 或完整历史版本表 |
| 内容写入 | `put_fact` 支持 `user_fact/character_daily/official_lore`，显式要求布尔 `enabled`；内容修订不能顺便更改已禁用状态 |
| 权限变更 | `set_permission` 专门禁用/恢复；同状态重复操作不新建修订/事件；业务提交、变更事件与所需 outbox 同事务 |
| 使用资格 | 模型读取检查 owner、enabled、修订依赖和恢复门禁；`FactRef` 实际字段为 `fact_id/revision` |
| 当前提取 | 只从当前用户自述提取字符串列表，最多处理 20 条；按来源 ID 与列表位置生成稳定事实身份；gift 只提取附言，不从物品模板推断偏好 |

当前提取写入的加密 body 例子：

```json
{"text":"用户今天吃了青椒","enabled":true,"score":0.3,"source":"11111111111111111111111111111111","truthfulness":"user_asserted","subject":"user","valid_from":null,"valid_until":null}
```

`user_asserted` 表示用户自述，不等于已经核实为真。当前 `valid_from/valid_until` 为 null，尚未实现事实时间窗推断、到期处理、候选证据评分/强化、三域内容生产、人格/日常演进等后续阶段功能。基础仓储允许额外 body 字段，不把示例字段伪装成已完整校验的业务 schema。`evidence` 等域枚举已声明，加密器可分域保护，但没有据此声称证据生产与加分事务已经落地。

## 6. ChangeEvent 与事实同步分页

事件身份为随机 `event_id`，顺序为同一数据库全局递增 `seq`；owner 查询可出现序号间隔。加密事件 body 实际包含 `event_id/entity_id/revision/action/created_at`，当前动作包括 `created/updated/disabled/restored/reply_completed`。

`fact_page(owner, cursor=0, since=0, limit=200)` 是仓储接口：cursor 为排他事件序号，since 为毫秒时间过滤，返回 `items/nextCursor/hasMore`，limit 收敛到 1～500。扫描到回复完成等不产生事实项目的事件也推进游标；空页不等于已到末尾，应使用 `hasMore` 和 `nextCursor`。同毫秒事件通过 seq 分页，不以时间戳充当唯一游标。

```json
{
  "items":[{"factId":"fact-synthetic-1","userId":"synthetic-owner","revision":2,"eventRevision":1,"eventId":"33333333333333333333333333333333","sequence":7,"action":"created","enabled":false,"fact":"","timestamp":1792110000000}],
  "nextCursor":7,
  "hasMore":true
}
```

这是一份按历史事件序列读取的**当前安全投影**：`revision/enabled/fact` 取事实当前版本与权限；`eventRevision/action` 描述该事件当时的动作。禁用后旧 created 事件也不泄露原正文。它不是历史修订快照或按事件回放原文的 API。

外部已实现 `GET /api/v1/memory/facts?cursor=0&limit=200`，响应 envelope 的 data 带上述分页字段。显式传 cursor 时只读取 SQL 事件页；不传 cursor 的旧兼容请求可合并旧 JSON 项，不能用该混合列表冒充完整事件页。该端点 `userId` 参数不改变认证 owner。当前没有新增事实写入/禁用/恢复 HTTP 路由；管理能力是仓储接口，后续阶段再定义客户端交互。

## 7. DurableJob、租约与 outbox

任务行实际包含随机 job ID、HMAC 业务去重键、owner 索引、状态、尝试次数、预算、可运行时间、租约截止/token，以及加密 payload。当前运行时处理 `chat/extract/deliver`，绑定互动处理器后再注册 `interaction_reconcile`；未知类型不被 worker 领取。

```json
{"job_id":"44444444444444444444444444444444","lease_token":"55555555555555555555555555555555","attempt":2,"payload":{"kind":"extract","message_id":"11111111111111111111111111111111","turn_id":"22222222222222222222222222222222","dependencies":[]}}
```

状态流为 pending → running → succeeded，失败进入 retry_wait 或 failed；失效/取消为 cancelled。默认预算 3 次，通用 enqueue 可指定 1～10 次。默认租约 30 秒，允许范围大于 0 且不超过 300 秒；worker 默认每租约三分之一续租，finish/renew 都以当前 token 和未过期租约为条件。过期后仍复用原 jobId，不能由旧工作者提交新工作者的结果。

当前运行时有两个 worker；同 owner 同时最多领取一个 chat 任务，其他用户和其他类型任务可以继续。模型/提取在事务外，SQLite 写锁等待默认 0.5 秒、配置范围为大于 0 且最多 5 秒。恢复权限未核验时不领取任务。续租失败取消本地 handler，取消不标记成功；`stop(timeout=5)` 有界等候，未及时结束的线程操作不能被强杀，未完成租约交由恢复处理。

`ChangeEvent + OutboxRecord + deliver job` 与业务提交原子创建。outbox 保存稳定 `delivery_id/event_id/job_id` 和加密目标信息；重试不重新生成业务结果、eventId、deliveryId 或回复 messageId。投递状态与重试预算由关联 deliver job 承载，不另行虚构一个有 ACK 状态机的 outbox API。

WS 实时通知没有客户端 ACK 确认协议；无在线接收者会让投递失败并按预算重试，稳定结果仍可从历史/事实同步读取。接收方按 messageId/eventId/deliveryId 去重。网络投递是至少一次尝试，不能承诺网络 exactly-once 或无限期后台推送。

## 8. 外部 HTTP/WS 兼容样例与差异

以下均为合成内容，owner 和 device 由认证上下文决定。

HTTP 请求：

```json
{"requestId":"request-synthetic-1","clientMessageId":"client-synthetic-1","message":"今天吃了青椒","stream":false}
```

对应的跨设备 WS 重试：

```json
{"type":"chat.message","requestId":"request-synthetic-1","payload":{"clientMessageId":"client-synthetic-1","content":"今天吃了青椒"}}
```

两者保持相同认证 owner 和 ID 配对时，返回原逻辑来源与回复，不重新生成。HTTP 返回现有 `code/message/data/traceId` envelope，data 内有 `conversationId/reply/messageId/debounceWindowSec` 及持久结果字段。WS 最终事件保持既有格式，并补稳定事件/投递 ID：

```json
{"type":"chat.reply.stream","requestId":"request-synthetic-1","payload":{"delta":"","done":true,"messageId":"66666666666666666666666666666666","finalContent":"味道怎么样？","timestamp":1792110000000,"requestIds":["request-synthetic-1"],"contentType":"text","modelProvider":"deepseek","eventId":"33333333333333333333333333333333","deliveryId":"77777777777777777777777777777777"}}
```

| 差异 | HTTP | WS / 互动 |
|---|---|---|
| 调度 | 单条立即提交；`stream` 字段兼容保留，当前可靠分支返回最终 JSON | 接收时持久化，再按 8/20 秒防抖合并；互动可合并未开始文字 |
| 模型上下文 | 最近 20 条历史 | 当日分隔后最近 10 条；互动附加已持久化模板 |
| 回复关联 | data 中的持久结果及原 conversationId | `chat.reply.stream` 的 requestIds 覆盖本轮所有来源；礼物附加既有 interactionItemId/Name/Icon 字段 |
| 冲突/失败 | 身份冲突 409/40901；仓储不可用 503/50301；未完成 503/50302；其他错误 500 | `bot.error` 使用 REQUEST_CONFLICT、SERVICE_UNAVAILABLE、CHAT_RETRY 等既有错误帧 |
| 历史展示 | `GET /api/v1/chat/history` 按 owner 合并可用旧记录与 SQL，since 为排他毫秒过滤，响应上限 500 | 重连复用同一历史接口；用户来源逐条保存，不能只保存整批最后一个 requestId |

互动外部接口仍为 `POST /api/v1/interaction/send`，字段 `itemId/requestId`，兼容 `idempotencyKey`，可选 `text` 附言。成功返回原 `InteractionSendResult.to_http_data()` 结构，推送由可靠 outbox 承担。内部支付门禁、补偿任务、代次和密钥信息不进入用户操作流程。

## 9. 加密、恢复与严格失败

源消息、事实、证据域、角色日常、官方来源、派生内容、任务、事件和投递内容通过独立 HKDF 域密钥及 AES-GCM 信封保护；信封含格式版本、key ID、随机 nonce，AAD 绑定域、owner 索引与实体/修订身份。owner/请求/客户端/业务幂等索引使用独立 HMAC 索引密钥，不建立明文敏感词检索索引。文字中的详细坐标在输入、模型消息、加密写入及读取投影处清理；依照用户后续决定，图片原字节（含 EXIF 与画面坐标）是此规则的唯一例外，进入加密源消息和 Gemini 图片输入，不写入明文历史、事件、任务或日志。

随机 ID、域、事件序号、修订、任务状态/次数/运行时间仍是明文运行元数据，需要限制数据库与备份访问。检索每批解密 100 行，在内存做子串匹配，结果最多 1000；它不是语义召回或后续正式记忆资格引擎。

只有缺失的旧文件允许默认空值；损坏、错误密钥、读写失败必须明确报错且不覆盖原数据。认证失败不能在密码学上可靠区分错误密钥与密文被篡改，应保留这两种诊断可能。SQL 锁冲突为 `StorageBusy`，身份/版本/租约变化为 `VersionConflict`，权限或恢复门禁为 `PermissionDenied`。

备份通过 SQLite 一致快照生成，未完成副本是 `.partial`，不会发布成可用库，也不覆盖已有备份。备份打开后 `permissions_ready=false`；必须用来自恢复集之外的可信最新事件水位执行 `verify_restored_permissions`。缺少最新禁用记录时保持关闭，不能从同一旧备份读取旧水位自证“最新”。密钥恢复目标、正式迁移、完整客户端副本与后续记忆业务仍按后续 PRD 验收。

本文字段核对来源：`services/contracts.py`、`services/foundation_store.py`、`services/reliable_chat_store.py`、`services/chat_runtime.py`、`services/durable_worker.py`、`services/text_generation.py`、`handlers/interaction_handler.py`、`http_api.py`、`ws_api.py` 与 `unified_api.py`。验证结果与部署状态应查独立实施记录；不能用本文的逻辑契约代替生产演练证据。
