# P6：协议收敛与结构清理

2026-10-23。基于 `Refactor` / `6e9d66b`，按用户指定的 P6 v0.2 与总则实施。P1–P4 已分别提交，P5 的离线结果在下文汇总；本期不改数据库、账本格式、请求身份、模型政策或采集/强化开关。

## 已交付范围

| 需求 | 结果 | 复用验证 |
|---|---|---|
| P6-1 心跳 | Android 改为 `payload.timestamp`，与 Linux/Windows 一致；旧顶层 ping、旧 pong、当前连接回显匹配和旧回调隔离保留 | 原 Android `OkHttpBotWebSocketClientTest`、Windows `NetworkFrameContractTests`、Electron selftest、后端 `test_ws_lock_error` |
| P6-2 共享协议 | 原样例扩展新旧心跳、带图分页、旧历史、blocked 与终态整批；保留各端逐字段解析。补齐 Windows 冻结表中 P4 已有 `RestChatDataDto.recoveryAction/requestIds`，仍严格检查完整字段集合 | 原 Android 图片历史测试改读共享样例；Windows 帧/Contract；Electron 共享分页实际同步入 SQLite、恢复两张图片、保留整批失败状态 |
| P6-3 重复模块/入口 | 删除无消费者的四个模板文件；入口短名称与包名称共用模块。统一启动直接传已装配 app，不再为启动二次导入；独立 HTTP/WS 和认证入口保留 | 六个独立子进程检查项目目录/包入口，导入顺序、模块/状态同一性、原路由唯一性 |
| P6-4 小步解耦 | 帧/图片边界校验移到 `services/ws_transport.py`；会话映射、防抖 worker 与尾批任务归入现有 `services/debounce_merge.py`。ws_api 保留旧访问别名和薄适配，不复制状态。互动抽取失败恢复、已提交结果收尾两个阶段；SQL/JSON 与补偿仍由原服务持有 | 原可靠互动、存储故障、旧 HTTP/WS 时间线、原图及双来源游标测试；新增缺口在原装配/防抖测试文件内扩展 |
| P6-5 文档 | README 改指可随仓库分发的启动/Windows 开发/发布指南，校正统一拓扑、超时、图片、游标和升级顺序；旧分期和缺失规格明确登记 | [当前协议与入口](backend-entrypoints.md)、[分期索引](phase-index.md)；本地链接逐项检查 |

删除的候选是 `services/api/v1/auth.py`（mock token）、`services/api/v1/health.py`、`services/core/config.py`、`services/core/response.py`。删除前检查入库代码、测试、脚本及本地 docs/design 引用：没有运行消费者；本地 2026-09-28 评估仅将其列作清理候选。真实认证仍在 `api/v1/auth.py`，配置/响应仍在顶层 `core/`。没有删除受支持的 HTTP/WS 入口。

迁移保留 WS 原 `_validate_frame/_parse_ws_images` 入口；图片数量、单图与总量限制仍由 WS 配置传入。PIL 故障注入随职责搬到实际校验模块，原错误码、整批拒绝、不入库和不广播断言都保留。

## 生命周期修复

原积分调度由裸 `create_task` 启动且 shutdown 不持有任务句柄。现在重复启动复用同一任务，关闭等待其取消完成，之后可重新启动。防抖每用户仍只有一个 worker；关闭唤醒并排空已接收缓冲，再结束尾批和可靠任务，不能把旧入口文字随取消直接丢弃。连接断开仍不取消业务处理。旧问候/场景/分隔函数没有因文档中的旧“四类调度”描述而重新启用。

只为未覆盖的重复启动/关闭缺陷和入口别名补充既有测试文件，不新建测试框架或同义用例文件。纯搬迁沿用现有业务断言。

## 有效测试证据

全部使用隔离数据与模型替身。首次沙箱被原生 DLL/临时目录权限阻断，获准使用本机隔离环境后运行。

- 核心十文件组合：`test_ws_lock_error`、`test_debounce_merge`、`test_unified_api`、`test_reliable_interaction`、`test_api_endpoints`、`test_reliable_api`、`test_reliable_image_history`、`test_merged_timeline_rows`、`test_interaction_handler`、`test_storage_failures`。149 passed、53 subtests passed；另两项图片校验搬迁失败在下一组修正并通过。互动的请求 ID 收尾缺口也在此组合前修正，原支付/退款/迟到提交断言均恢复通过。
- 最终图片/旧入口六文件：`test_reliable_api`、`test_http_chat_timeline`、`test_http_image_worker`、`test_timeline_write_safety`、`test_timeline_store`、`test_application_service`，**55 passed、3 subtests passed**。包含上组两个故障：总量配置透传和实际 PIL 校验器注入；没有放宽图片限制。
- 包属性访问补齐后最终 `test_unified_api`：**10 passed、17 subtests passed**，包括六种全新进程入口、同一会话映射、路由与生命周期。上述组有重叠，不累计为单次全量结果；后端保留 12 项既有依赖弃用警告。
- Windows 帧、Contract、CompositionRoot、HistoryImageDownload 组合原 46 项中 45 通过；唯一失败是冻结表缺两个已有字段。补齐精确期望后仅复跑该项，**1 passed**；其余 45 项证据有效。包含真实 loopback 心跳、10,050 行分页续读与落库，不只是 DTO 解析。
- Linux 类型/i18n 检查通过；最终真实 Electron 主进程 + 本机 Mock **147 项断言通过**，包含共享样例同步落库、原图、旧协议、重连/重发与历史恢复。替身限定为 `chat/history`，不污染同步后的积分刷新。
- Android JBR 21，离线网络模块 **4 项通过**（其中心跳 2 项），聊天模块最终 **17 项通过**。这是当前工作区结果，包含原有未提交的网络超时/时间戳用例；本次只提交心跳发送/对应原测试、共享历史样例消费，不提交这些原有改动。
- 文档检查：9 份文档的 41 个本地链接可解析，共享 JSON 有效；暂存隐私检查 **26 个非删除文件通过**，`git diff --cached --check` 通过。

命令沿用原入口：后端 `.venv/Scripts/python.exe -m pytest <上述文件> -q -p no:cacheprovider --tb=short`；Linux `npm run typecheck` / `npm run selftest`；Windows `dotnet test ... --no-restore --filter ...`；Android `gradlew :core:network:testDebugUnitTest :feature:chat:testDebugUnitTest --offline --no-daemon`。最终只对变更使证据失效的部分复跑。

## P5 证据接续

[P5 记录](fix-refactor-p5.md)保留当前“绘画/画画”双向相关性修复：18 个分层样例，正向 9/9 命中、负向 9/9 排除实际上下文，权限/主体/修订/时间/O-03 不变。受影响组合 106 passed、87 subtests；最后改动文件 5 passed、21 subtests，互不累计。P6 未改记忆资格、排序、提示或外呼预算，沿用其有效离线证据。

2026-10-20 的历史 13/13 提取/回放及四项探针仍是历史候选的证据；当前 P4/P5 后的真实模型调用数是 0，不能按模型同名推定语义质量通过。

## 发布与回退

本次开发完成后按用户授权提交并推送当前 `Refactor`；不是生产部署或安装包发布。原有 Android、配置忽略规则、阶段文档与部署脚本工作不混入本期。没有执行生产操作、真实模型调用、数据迁移或设备安装。

P6 未改界面、客户端分页事务或数据 schema，沿用 P1–P4 对相同行为的已有有效证据；本期没有重做 Android 设备/三端 GUI/原生 Linux 安装包/Windows M6 安装与升级验收。生产恢复与缺失旧规格的完整验收仍由对应发布/规格负责人完成，具体来源和未知对应见分期索引。

回退按职责成对恢复服务和消费者：传输函数与 WS 适配一起；防抖服务与会话别名一起；互动阶段与调用者一起。保留单连接 pong、幂等付款/退款、输入预算及本期任务关闭修复，不用整树恢复或旧默认配置覆盖数据。入口模式切换先停原进程，继续保留同一数据目录、密钥和已接收请求的结果读取；旧客户端兼容分支不删。
