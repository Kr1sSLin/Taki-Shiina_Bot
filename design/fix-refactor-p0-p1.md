# Fix-Refactor P0 / P1 执行记录

2026-10-22。基于本轮 v0.2 总则、P0 与 P1；未来阶段仅建立覆盖索引，不提前实现。

## 使用新配置与恢复失败消息

管理配置先 GET `/api/v1/admin/gamification-config`，再把返回的 `revision` 作为 PUT 请求体的 `expectedRevision`，与待修改的配置段一起提交。版本冲突返回 HTTP 409 / 40901，重新读取后再编辑。reset 同样提交 `expectedRevision`，回退会产生新修订。

`version` 是 schema，`revision` 是单调配置修订；模板、价格、每日限次及重试规则在接收意图时取同一份快照。默认每日限次仍为 `null`。管理写入缺少版本返回 400，这是本期为避免覆盖他人配置新增的写入前提；普通聊天和查询不受影响。

配置旁的 `.last-good` 是跨重启恢复副本，`.revision-N` 是不可覆盖的审计版本。备份、恢复时与主文件一同保留。当前文件损坏使用最近有效版本并显示 `degraded`；当前文件与恢复副本均不可用时暂停新送礼。已有账本、进度或可靠聊天库的部署不会因为配置目录消失而自动生成默认价格。全新数据目录初始化才生成默认配置；补偿退款不依赖配置。

失败批次可用原身份 GET `/api/v1/chat/requests/{request_id}` 查询，也随原 `/api/v1/chat/history` 的可选 `operations` 返回。字段包括全部 `requestIds`、`sources`、`failureCategory`、`refundStatus`、`retryable` 和 `recoveryAction`。`sources` 保留正文与图片下载身份，不内联原图、不改写既有历史正文。查询由认证 owner 隔离，统一入口与独立 HTTP 入口均挂载。

`processing` 使用 `poll_status`；`failed` 使用 `resend_new_request`。原 ID 只返回原裁决，重新发送才使用新身份；普通文字和图片重发不再次发送旧礼物。三端在实时错误、HTTP 失败及重连同步时消费终态，保留原消息和附件。Windows 对网络发送失败继续沿用原 ID，仅明确的服务端终态切换新 ID。

## 支付状态与恢复边界

1. `pending`：独立持久意图。普通 accept 不会并入它，提取与生成不能领取它，也不阻挡普通文字调度。
2. JSON 账本事务先命中原幂等键，再判断余额与限次，追加真实 `ITEM_SEND`；SQL 确认到 `paid`。SQL 失败时原意图仍可被补偿任务找到。
3. 付款确认与挂接在一个 SQL 事务完成：只挂接尚未开始、无礼物的 pending 文字轮次；同步请求映射、提取和补偿任务、generation，并取两轮较早截止时间。没有安全目标时独立确认。每轮最多一份礼物。
4. `confirmed` 才能生成。文字已经 running/completed 时不夺取它。挂接前失败只取消礼物；挂接后失败整批终止，先封闭生成，再幂等退款并保存批次结果。
5. `completed` 优先返回已提交结果；投递或等待失败不能退款。断开等待者不撤销已接收操作。`interaction_reconcile` 沿用原扣/退款键恢复，迟到扣款与确认受状态及截止时间约束。
6. 升级前已存在的未支付混合意图，在扣款或补偿前先隔离礼物。隔离先持久化，扣款拒绝回滚不会把礼物重新挂回文字。

额度按同 owner、原扣款业务日计数。只有 owner/request、物品和金额均匹配的真实退款释放对应扣款名额；跨日旧退款也按原身份关联。新退款的业务日取原扣款日，发生时间仍取实际时刻。日期缺失、格式异常或退款无法关联时，仅阻止该账户新的限次扣款，原请求查询、幂等重放和退款保留。公共送礼入口的业务日由服务端生成，不接受客户端指定历史日期。

## P0 错误动作基线

原 HTTP 状态、数字 code、字符串 errorCode、message/data/traceId 和旧 detail 形状保留。新增字段可缺失；不得仅按 50302 判定是否重新发送。`retryable` 仅表示原操作可否重试。

| 原错误/状态 | 恢复动作 | 原操作可重试 | 身份处理 |
|---|---|---|---|
| 40901 / REQUEST_CONFLICT | edit_input | 否 | 核对输入；修改后用新 ID，原结果继续可读 |
| 管理配置 40901 | refresh_then_retry | 否 | 刷新 revision 后重新提交修改 |
| 50301 / SERVICE_UNAVAILABLE，未接收 | retry_same_request | 是 | 保留原 ID |
| 50302 / CHAT_RETRY，processing | poll_status | 是 | 查询原 ID，不重复发送新消息 |
| 50302 / CHAT_RETRY，failed | resend_new_request | 否 | 保留来源，新发使用新 ID |
| INVALID_JSON / EMPTY_MESSAGE | edit_input | 否 | 修正帧或正文后重新发送 |
| 40201 积分不足 / 40203 限次，未持久接收 | refresh_then_retry | 是 | 刷新余额/额度后重试 |
| 已持久保存的未扣款拒绝 | resend_new_request | 否 | 原 ID 返回原拒绝，不重新准入 |
| 50303 配置不可用，未接收 | refresh_then_retry | 是 | 配置恢复后重试，普通聊天继续 |
| 40204 / 40205，终态退款或待补偿 | resend_new_request | 否 | 整批明确失败；pending 退款继续核账，不把待补偿显示成已退款 |
| completed | none | 否 | 读取已有结果，不扣款、不退款 |

本表是 P4 全错误信封落地的基线。本期在互动失败、可靠聊天 50302 和状态查询中落地；其他旧错误仍允许缺失可选字段。

## 验收到现有覆盖的映射

以下路径相对仓库根。后端函数名位于 `Taki_Shiina_Bot/tests/`；同一候选有效结果复用，不新增同义测试文件。

| 验收 ID | 现有入口 / fixture / 样例 | 当前覆盖与剩余缺口 |
|---|---|---|
| P0-A1 | 本表；`gamification_fixture.make_rig`、`controlled_model_fixture.checker_response` | 复用覆盖归属与补充原因已记录 |
| P0-A2 / P0-5 | `design/foundation-protocol-samples.json`；Linux `scripts/verify-contract.mjs`、原 selftest；Windows `NetworkFrameContractTests.SharedSamples_PreserveLegacyAndTerminalRecovery`；Android `BotErrorTimestampTest` | 原样例保留；新旧错误和 processing/terminal 样例共用。Linux Electron 实际消费仍受环境阻断 |
| P0-A3 | P1–P6 各行 | 分页、完整异步 I/O、外呼预算保留给所属阶段 |
| P0-A4 | 下方有效结果与环境记录 | 不删检查、不设置 xfail |
| P1-A1 | `test_reliable_interaction.test_unpaid_rejections_never_cancel_or_move_pending_text`；`test_reliable_chat_store.test_unpaid_intent_does_not_block_text_and_paid_gift_cannot_steal_running_text` | 余额竞争拒绝、限次、核账和配置拒绝；文字不移动、不取消、正常一次回复；待支付不阻挡文字 |
| P1-A2 | `test_points_level.test_atomic_daily_limit_owner_isolation_and_original_retry`；`test_reliable_interaction.test_five_item_matrix_keeps_checks_and_rechecks_noodles_rewrite` | 五线程原子准入恰好两次、另一 owner 独立；五礼物生成成功不退款 |
| P1-A3 | `test_points_level.test_cross_day_refund_limit_changes_and_failed_refund`、`test_ambiguous_legacy_ledger_blocks_only_new_limited_charges`；`test_success_and_restart_replay_use_one_charge_and_sql_result` | 同 ID、启用限额、退款失败不释放、原日退款和旧跨日退款关联；旧记录歧义阻断新限次扣款 |
| P1-A4 | `test_reliable_interaction` 原 `test_crash_*`、`test_cancelled_payment_gate_rejects_a_late_deduction_callback`、`test_sql_failure_after_json_charge_refunds_only_that_real_charge`、`test_timeout_racing_committed_reply_returns_result_without_refund`、`test_confirm_attachment_failure_refunds_gift_without_touching_text`；存储 `test_pre_upgrade_unpaid_mixed_intent_is_split_before_a_failed_charge` | 意图/扣款/确认/挂接/退款/重启/迟到回调复用原故障注入；未进行生产故障注入 |
| P1-A5 | `test_paid_mixed_failure_survives_restart_and_original_id_replay`；`test_reliable_image_history.test_failed_batch_retains_original_image_and_recoverable_source_after_restart`；Windows `CompositionRootTests.ProductionCompositionRoot_ResolvesM0ThroughM3RuntimeGraph`；Linux 原 selftest 扩展；Android 原解析测试及同步入口 | 后端整批终态、原图和新 ID 重发已覆盖；Windows 实际 SQLite 同步/附件克隆/新身份已覆盖。Linux Electron 集成、Android Room/设备与三端 GUI 待验收 |
| P1-A6 | `test_staging_fixes.test_config_recovery_restart_manual_revision_and_snapshot_isolation`、`test_config_rejects_templates_and_parallel_stale_writes`、`test_unrecoverable_config_stops_new_gifts_but_keeps_refunds`、`test_config_dual_write_failure_is_not_reported_as_publication_success`；`test_api_endpoints.test_admin_config_read_and_update` | 热读/冷启动不默认换价、不可变副本、手工修订、模板拒绝、双写错误和冲突检测；多进程底层锁复用已有 file_lock |
| P2-A1 | `test_reliable_api` 的 `new_runtime`、`http_chat`；`test_reliable_chat_store.test_ws_pending_merge_waits_for_last_quiet_window_and_keeps_source_rows` | 可复用真实 SQL/HTTP fixture；当前聊天没有 10,050 条分页验收，P2 补充 |
| P2-A2 | `test_reliable_image_history.test_history_links_request_without_inlining_image`；`test_merged_timeline_rows.py`；`test_reliable_api.test_http_fact_cursor_pages_past_500_equal_timestamps_and_final_disable` | 来源和同毫秒游标 harness 可复用；事实游标不算聊天双来源分页，JSON 变化/裁剪待 P2 |
| P2-A3 | `test_controlled_chat.test_history_recovery_delivery_is_fenced_against_new_input`、`test_pre_upgrade_invalid_outbox_cannot_block_later_checked_reply` | 权限和首次投递已有断言；低 seq 检查点与回滚 reset 待 P2 |
| P2-A4/A5 | `test_reliable_image_history` 原图/恢复权限测试；Windows `HistoryImageDownloadTests`；Android `HistoryImageRestoreTest`；Linux 原 selftest 历史和解段 | 现有原图、幂等和权限路径复用；逐页中断、新旧服务组合与数据库回退待 P2 |
| P3-A1/A2 | `test_reliable_api.FakeWebSocket` / `capture_frame`；Linux verify 的 ping/pong 段；Windows `NetworkFrameContractTests` | 连接与旧帧 harness 已有；逐连接响应、非对象帧和设备归属断言待 P3 |
| P3-A3/A4 | `test_progress_store_file_lock.py`；`test_ws_lock_error.py`；P1 扣退款取消/恢复测试 | 复用真实文件锁与错误入口；持锁 2 秒时 ping <200ms 的组合检查待 P3，未宣称已经异步化全部积分路径 |
| P3-A5 | 三端原连接管理器及现有自检；P2-A4/A5 | 重连与原图测试可用；逐连接迟到回调和完整补漏待 P3/P2 |
| P4-A1/A2 | `test_reliable_api` HTTP/WS 拒绝与图片预算用例；`test_reliable_chat_store` 原图片批次边界、回滚测试 | 可复用入口和副作用计数；聊天单条/合并正文预算待 P4，2000 字不作为既有聊天合同 |
| P4-A3/A4 | `test_controlled_chat.test_all_actual_calls_include_exact_public_constraints_and_version`、`test_every_rewrite_is_checked_and_rejections_are_terminal`、`test_continuous_input_does_not_reset_deadline_or_allow_third_draft`；`test_reliable_interaction.test_generation_budget_is_persisted_across_retry_restart_and_expiry` | 实际替身外呼捕获、持久 deadline、检查/重写和补偿可复用；各类外呼输入上限与规模测量待 P4 |
| P4-A5 | P0 共享样例与三端原契约检查 | 可选字段兼容先固定；统一错误提取及所有 UI 恢复动作待 P4，P1 终态提前接入 |
| P5-1/2/3 | `design/memory-semantic-cases.json` 及旧评测报告；`test_controlled_chat.test_fixed_hard_negative_replays`；`test_memory_extraction.test_candidates_are_snapshotted_before_the_model_await` | 旧报告仅作定位；未重跑旧真实模型结果，候选/模型/预算核对与类别分母由 P5 完成 |
| P5-4/5 | `test_memory_stage_b.test_a09_expired_plan_and_sickness_never_become_current_facts`、`test_normal_stable_preference_change_keeps_past_and_starts_new_at_six`、`test_management_correction_invalidates_selected_draft`；`test_memory_generation.test_model_failure_after_selection_has_no_usage_side_effect` | 到期、过去事实、纠正、权限与使用分层可复用；质量评测待 P5，不把替身当真实质量 |
| P6-1/2 | Linux verify、Windows Contract/NetworkFrame、Android BotError 与 P0 共享样例 | 旧帧保留；协议收敛和 historySyncVersion 等随 P2–P4 完成后再核对 |
| P6-3/4 | `test_unified_api.test_key_routes_appear_exactly_once`；`test_reliable_api`；`test_debounce_merge.py`；Windows `CompositionRootTests` | 原入口和装配回归复用；重复模块清理、职责搬迁与启动/停止验证留给 P6 |
| P6-5 | 既有 README/契约/阶段文档及本索引 | 本套编号不等于旧 PRD02–08 / A–E / M0 的交付状态；链接和完整发布说明由 P6 核对 |

新增用例都扩展原测试文件，复用已有模型替身、临时 SQL/JSON 与客户端测试入口。具体缺口是未支付隔离、限次计数、配置恢复/修订、失败批次持久查询及新旧恢复字段；没有新增测试框架或平行测试文件。原“扣款前并入”断言已改为“付款后安全挂接”；原退款幂等 fixture 补上真实扣款，仍保留只退款一次的断言。

## 有效结果与实际缺口

结果以本节最终记录为准。测试只使用合成数据与离线模型替身，未部署、迁移、安装设备或调用真实模型。

- 实现前后端现有基线：85 passed、34 subtests passed。
- 后端受影响 10 个现有测试文件：189 passed、110 subtests passed。最后补充退款 owner/reason 校验后，积分与互动 3 个受影响文件复跑：72 passed、38 subtests passed；两组有重叠，不累计计数。
- Windows 受影响 Contract、帧、原图、积分、聊天与装配检查：58 passed；包含真实 SQLite 失败同步和新 ID 附件重发。
- Linux `npm run typecheck`：通过。契约脚本本地 Mock：70 项断言通过，防抖 600ms、礼物等待 250ms，与原 selftest 驱动一致；已同步修正 Mock 默认值。旧默认 400/1200ms 会让普通回复先取走缓冲，导致四项合并断言失败。
- Linux `npm run selftest`：构建成功，Electron 创建临时目录时 EPERM，未进入业务断言。最小复现中 Electron 的 mkdir/mkdtemp 都被拒绝，Node 同路径成功。只读排查确认受控文件夹访问关闭、两者用户相同，未发现对应 Defender/AppLocker/CodeIntegrity 拦截事件；未改变 ACL 或系统安全设置。用户已接手在 Codex 外普通 PowerShell 运行同一命令，结果待回传；通过前保留 P0-A2 / P1-A5 此项验收缺口。
- Android `:feature:chat:testDebugUnitTest` 与 `:core:network:testDebugUnitTest` 离线通过：分别 15、2 项，共 17 项，失败与错误均为 0。JVM 不证明设备上的 Room、气泡展示和重连；这部分仍待隔离设备/模拟器验收。三端 GUI 尚未验收，P1-A5 未全部关闭。

回退优先关闭新送礼准入，保留原请求查询、普通聊天、结果读取与补偿。不得回退到未支付取消文字或默认换价；配置回退用新 revision，不覆盖在途快照和审计文件。不要把旧服务端不理解的新 `paid` 门闩直接投入滚动混跑；升级/回退时先停止新送礼并让本版完成在途 paid 意图的确认或补偿，再切换处理进程。
