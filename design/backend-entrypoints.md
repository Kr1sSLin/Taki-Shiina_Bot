# 启动、协议兼容与升级顺序

2026-10-23，按 P1–P6 当前代码核对。固定合成输入见[共享协议样例](foundation-protocol-samples.json)，开发验证见 [P6 记录](fix-refactor-p6.md)。

## 推荐只启动一个统一进程

在后端目录配置环境后运行：

```bash
cd Taki_Shiina_Bot
python unified_api.py
```

默认监听 `127.0.0.1:8000`，提供认证、HTTP 聊天/历史/记忆/设置、`/ws/chat`、养成与 `/healthz`。仓库根也可用 `python -m Taki_Shiina_Bot.unified_api`。启动脚本直接把已装配的 app 交给 Uvicorn，包导入与项目目录导入共用入口模块；不要对同一数据目录启动多个统一进程或多个 Uvicorn worker。

HTTP 处理器复用 WS 的状态、仓储、提供方及可靠运行时。可靠聊天/提取/投递、日常和主动调度仍由 `ChatRuntime` 管理；积分调度只启动一次。旧问候、场景和历史分隔函数保持兼容，但不会因 P6 清理被重新启用。关闭时先停积分调度、排空已接收的 WS 缓冲，再停止可靠运行时。

需要 `AUTH_JWT_SECRET`、登录账号及密码/哈希配置、原有 `DATA_ENC_KEY`。调用文本模型另需提供方配置，图片需 `GEMINI_API_KEY`。`BOT_HTTP_TOKEN/BOT_WS_TOKEN` 是保留的兼容鉴权，不替代 JWT 配置。沿用原数据密钥、`FOUNDATION_KEY_ID` 和数据目录；不要为升级生成新密钥。

## 三进程入口继续支持

| 进程 | 命令与执行目录 | 路由 |
|---|---|---|
| 认证 | 仓库根：`python -m uvicorn Taki_Shiina_Bot.main:app --host 127.0.0.1 --port 8002` | `/api/v1/auth/*`、`/api/v1/health` |
| HTTP | 后端目录：`python http_api.py` | 聊天、历史、原图、记忆、设置；默认 8000 |
| WS / 养成 | 后端目录：`python ws_api.py` | `/ws/chat`、互动/积分/等级/管理；默认 8001 |

HTTP/WS 也支持仓库根的 `python -m Taki_Shiina_Bot.http_api` / `python -m Taki_Shiina_Bot.ws_api`。Windows 开发脚本继续支持 `unified` 和 `auth/http/ws/all`。独立 HTTP 不接管 WS 的积分调度或账户连接。

反向代理按实际拓扑转发，保留 URI（`proxy_pass` 后不加路径）、`Authorization`、`Sec-WebSocket-Protocol` 与 WS Upgrade 头。公网必须拒绝 `/internal`、`/internal/**`、`/api/v1/admin`、`/api/v1/admin/**`；管理仅本机直连并携带普通鉴权及 `X-Admin-Token`。统一拓扑示例见 [Linux 部署指南](../scripts/README_backend_linux.md)。兼容拓扑中认证到 8002、养成与 WS 到 8001，其余 API 到 8000。

## 心跳、失败和超时

- 三端新 ping 为 `{"type":"ping","payload":{"timestamp":123}}`。服务端继续接受旧顶层 `timestamp`；两处冲突或非法类型返回当前连接的 `INVALID_JSON`，后续合法帧继续处理。
- pong 保留顶层服务端 `timestamp`，可选顶层 `pingTimestamp` 回显。只回复发起连接。有回显时匹配当前连接的在途 ping；无回显旧 pong 仍兼容。重连清空心跳，旧连接回调不能影响新连接。
- WS 静默窗口通常 8 秒，回复后 40 秒内为 20 秒。新互动生成/检查/至多一次重写共用持久总预算 60 秒，单次 20 秒；旧无快照操作保留 15 秒规则。接收后整轮截止 120 秒，外层等待 125 秒；重启/热更新不延长截止时间。
- Linux/Windows 互动 HTTP 等待当前为 90 秒；客户端超时可能早于外层完成，不能据此宣布未扣款或新建身份。通过原请求状态/历史恢复。代理互动读超时建议 150 秒，WS 长连接保留 3600 秒。Android 的超时策略以网络模块为准，不用本指南替换工作区中的独立改动。
- `poll_status` 查询原操作；`retry_same_request` 保留原身份和原文/原图；`resend_new_request` 只在用户明确重发终态失败批次时分配新身份；`edit_input` 回填编辑；`refresh_then_retry` 刷新对应配置/余额/鉴权。缺失动作的旧错误继续使用各端旧映射。
- `requestIds` 是整批来源身份，`traceId` 是诊断身份。已扣礼物失败仍沿用整批失败/退款，不能拆出文字自动补答。HTTP 原 `detail` 与旧错误码保留，不做全局 snake_case/camelCase 转换。

输入及外呼上限见 [P4 预算表](fix-refactor-p4.md)。SDK 重试、生成和检查都受原持久截止时间约束。

## 图片与历史检查点

WS 图片逐字段使用 `mimeType/dataBase64`。每轮最多 3 张、原图总量 14,000,000 字节；本地 20 MB/张限制不代表服务端接受 3×20 MB。保留原始字节和 EXIF，历史行用 `requestId/imageCount/historySource` 恢复原图；认证下载 `GET /api/v1/chat/images/{request_id}/{index}`，缺失或越权 404，未核验恢复权限 503。

内置 Python WS 启动器设置帧大小；独立 Uvicorn 加载 app 时必须显式设置 `--ws-max-size 18928812 --ws-max-queue 4`（对应默认图片预算）。HTTP 总传输保护为 20 MB；上游代理不能把合法 JSON/Base64 图片截在更小的传输上限。

新客户端从 `GET /api/v1/chat/history?cursor=start&limit=300` 开始。`historySyncVersion=1` 时，整页消息、失败批次和不透明 `nextCursor` 在本地事务提交后一起推进；`hasMore=false` 也保留检查点供下次增量。`datasetGeneration` 绑定数据代际，不靠展示时间或解码游标判断进度。`blocked=true` 按 `pollAfterMs` 等待，不能跳过未投递结果。

游标失效/过期/代际变化返回 `40910`，从 `start` 重建扫描而不清除本地消息。无 `historySyncVersion` 的旧服务端仅有限 `since` 回退，必须提示受限且不覆盖新检查点。旧 JSON 不自动导入 SQL，也不因分页而承诺永久保留；只能恢复服务端现存记录。

## 先后端，再逐端升级

1. 按原运维流程备份数据、配置及可恢复密钥，记录当前版本；不在本次开发中执行迁移或生产备份。
2. 停止旧拓扑，再启动一个新后端进程，检查健康、鉴权、当前连接 ping、原身份状态与现存历史分页。统一/兼容模式二选一，不双启动账本所有者。
3. 逐端更新，继续兼容旧 ping、旧 pong、旧错误和有限历史。没有旧版本覆盖证据，不移除解析分支。
4. 回退只回退受影响代码/拓扑，保留数据库、请求身份、账本、修订和游标代际规则。后端无分页时客户端提示有限同步；故障时暂停受影响的新操作，保留结果读取与补偿。不恢复广播 pong、未扣款取消文字或静默默认换价。
