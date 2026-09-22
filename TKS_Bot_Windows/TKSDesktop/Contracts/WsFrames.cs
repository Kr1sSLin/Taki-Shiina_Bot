using System.Text.Json;
using System.Text.Json.Serialization;

namespace TKSDesktop.Contracts;

/* ==========================================================================
 * WebSocket 帧契约（PRD §5.3 / §5.5 C-7）
 *
 * ⚠️ 三条必须继承的陷阱（§5.6 陷阱 1 / 2 / 6）：
 *   ① `chat.message` 的 `requestId` 在**帧的顶层**（与 `type` 同级），**不在 payload 内**；
 *   ② `pong` 是**顶层** `{type, timestamp}`，**没有 `payload` 包装**；
 *   ③ WS 无效 token 会先 `accept` 再下发 `auth.expired`，随后以 **4001** 关闭 —— 必须能区分
 *      「Token 失效」与「网络层失败」，**不得**把 4001 当作可重试的网络错误。
 *
 * ⚠️ FR-W-NET-2 / NFR-W-12：解析时必须**忽略未知字段与未知 `type`**，不得抛错、不得中断连接。
 *    实现策略：先只解析 `type` 字符串，再按 type 决定是否反序列化 `payload`；
 *    未识别的 type 记录 debug 日志后丢弃。
 * ========================================================================== */

/* -------------------------------------------------------------------------- */
/* 客户端 → 服务端（§5.3.1）                                                     */
/* -------------------------------------------------------------------------- */

/// <summary>图片载荷。服务端**只读取** `mimeType` / `dataBase64`（`localUri` 为本地字段）。</summary>
public sealed class ChatImagePayloadDto
{
    [JsonPropertyName("mimeType")]
    public string MimeType { get; set; } = string.Empty;

    [JsonPropertyName("dataBase64")]
    public string DataBase64 { get; set; } = string.Empty;

    /// <summary>仅客户端本地字段，服务端忽略。</summary>
    [JsonPropertyName("localUri")]
    public string? LocalUri { get; set; }
}

/// <summary>`chat.message` 的 payload。服务端只读 `content` 与 `images[]`。</summary>
public sealed class ChatMessagePayloadDto
{
    /// <summary>客户端本地字段，服务端忽略；照常发送以保持语义完整。</summary>
    [JsonPropertyName("messageType")]
    public string MessageType { get; set; } = "text";

    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("images")]
    public List<ChatImagePayloadDto>? Images { get; set; }

    /// <summary>客户端本地字段，服务端忽略。</summary>
    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; }
}

/// <summary>`ping` 心跳帧（每 25 秒一次）。</summary>
public sealed class WsPingFrameDto
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "ping";

    [JsonPropertyName("payload")]
    public WsPingPayloadDto Payload { get; set; } = new();
}

public sealed class WsPingPayloadDto
{
    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; }
}

/// <summary>
/// `chat.message` 发送帧。
/// ⚠️ 陷阱 1：`requestId` **必须在顶层**（后端用 `message.get("requestId")` 读取）；
///    放进 payload 会让幂等与多设备回声全线失效。
/// </summary>
public sealed class WsChatMessageFrameDto
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "chat.message";

    [JsonPropertyName("requestId")]
    public string RequestId { get; set; } = string.Empty;

    [JsonPropertyName("payload")]
    public ChatMessagePayloadDto Payload { get; set; } = new();
}

/* -------------------------------------------------------------------------- */
/* 服务端 → 客户端（§5.3.2）                                                     */
/* -------------------------------------------------------------------------- */

/// <summary>`chat.typing` 的 payload；`stage` 决定顶部状态条文案（FR-W-CHAT-14）。</summary>
public sealed class ChatTypingPayloadDto
{
    [JsonPropertyName("typing")]
    public bool Typing { get; set; }

    /// <summary>`vision` / `generating` / `interaction` / `interaction_merge`；无值时用通用文案。</summary>
    [JsonPropertyName("stage")]
    public string? Stage { get; set; }
}

/// <summary>`chat.reply.stream` 的 payload（流式主通道）。</summary>
public sealed class ChatReplyStreamPayloadDto
{
    [JsonPropertyName("delta")]
    public string? Delta { get; set; }

    [JsonPropertyName("done")]
    public bool Done { get; set; }

    /// <summary>仅 `done=true` 时存在。</summary>
    [JsonPropertyName("messageId")]
    public string? MessageId { get; set; }

    /// <summary>仅 `done=true` 时存在；已由服务端清洗、`[[TIMER:...]]` 已剥离。</summary>
    [JsonPropertyName("finalContent")]
    public string? FinalContent { get; set; }

    /// <summary>仅 `done=true` 时存在。</summary>
    [JsonPropertyName("timestamp")]
    public long? Timestamp { get; set; }

    [JsonPropertyName("contentType")]
    public string? ContentType { get; set; }

    [JsonPropertyName("modelProvider")]
    public string? ModelProvider { get; set; }

    /// <summary>服务端已解析完成的提醒指令（FR-W-REM-1）。</summary>
    [JsonPropertyName("timerInstruction")]
    public Dtos.TimerInstructionDto? TimerInstruction { get; set; }

    /// <summary>被防抖合并的**所有**用户消息 id，`done` 时批量标记送达（FR-W-CHAT-7）。</summary>
    [JsonPropertyName("requestIds")]
    public List<string>? RequestIds { get; set; }

    /// <summary>`greeting` / `interaction` / `interaction_failed`；普通聊天无该字段。</summary>
    [JsonPropertyName("messageKind")]
    public string? MessageKind { get; set; }

    [JsonPropertyName("greetingScenario")]
    public string? GreetingScenario { get; set; }

    [JsonPropertyName("interactionItemId")]
    public string? InteractionItemId { get; set; }

    [JsonPropertyName("interactionItemName")]
    public string? InteractionItemName { get; set; }

    [JsonPropertyName("interactionItemIcon")]
    public string? InteractionItemIcon { get; set; }

    [JsonPropertyName("interactionFailed")]
    public bool? InteractionFailed { get; set; }
}

/// <summary>`chat.message.echo` 的 payload（多设备回声）。</summary>
public sealed class ChatEchoPayloadDto
{
    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("imageCount")]
    public int ImageCount { get; set; }

    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; }

    [JsonPropertyName("originDeviceId")]
    public string? OriginDeviceId { get; set; }
}

/// <summary>`chat.queued` 的 payload；`debounceWindowSec` **必须用下发值**，不得硬编码 8/20（EDGE-W-17）。</summary>
public sealed class ChatQueuedPayloadDto
{
    [JsonPropertyName("debounceWindowSec")]
    public double DebounceWindowSec { get; set; }
}

/// <summary>`bot.error` 的 payload。</summary>
public sealed class BotErrorPayloadDto
{
    [JsonPropertyName("errorCode")]
    public string? ErrorCode { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("requestIds")]
    public List<string>? RequestIds { get; set; }

    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; }
}

/// <summary>`auth.expired` 的 payload。</summary>
public sealed class AuthExpiredPayloadDto
{
    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; }
}

/// <summary>`points.changed` 的 payload（积分变动实时同步）。</summary>
public sealed class PointsChangedPayloadDto
{
    [JsonPropertyName("ledgerId")]
    public long LedgerId { get; set; }

    [JsonPropertyName("reasonCode")]
    public string? ReasonCode { get; set; }

    [JsonPropertyName("changeAmount")]
    public int ChangeAmount { get; set; }

    [JsonPropertyName("balanceAfter")]
    public int BalanceAfter { get; set; }

    [JsonPropertyName("balance")]
    public int Balance { get; set; }

    [JsonPropertyName("relatedItemId")]
    public string? RelatedItemId { get; set; }

    [JsonPropertyName("businessDate")]
    public string? BusinessDate { get; set; }

    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; }
}

/// <summary>`level.changed` 的 payload；`changeType` 三态决定反馈强度（FR-W-LV-4 / 5 / 5a）。</summary>
public sealed class LevelChangedPayloadDto
{
    [JsonPropertyName("levelCode")]
    public string? LevelCode { get; set; }

    [JsonPropertyName("levelName")]
    public string? LevelName { get; set; }

    [JsonPropertyName("prevLevelCode")]
    public string? PrevLevelCode { get; set; }

    [JsonPropertyName("continuousDays")]
    public int ContinuousDays { get; set; }

    [JsonPropertyName("changeType")]
    public string? ChangeType { get; set; }

    [JsonPropertyName("changeSource")]
    public string? ChangeSource { get; set; }

    [JsonPropertyName("highestLevelCode")]
    public string? HighestLevelCode { get; set; }

    [JsonPropertyName("gapDays")]
    public int GapDays { get; set; }

    [JsonPropertyName("breakDeadlineDate")]
    public string? BreakDeadlineDate { get; set; }

    [JsonPropertyName("nextLevelCode")]
    public string? NextLevelCode { get; set; }

    [JsonPropertyName("nextLevelName")]
    public string? NextLevelName { get; set; }

    [JsonPropertyName("daysToNextLevel")]
    public int? DaysToNextLevel { get; set; }

    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; }
}

/// <summary>`streak.warning` 的 payload（断签提前提醒）。</summary>
public sealed class StreakWarningPayloadDto
{
    [JsonPropertyName("levelCode")]
    public string? LevelCode { get; set; }

    [JsonPropertyName("levelName")]
    public string? LevelName { get; set; }

    [JsonPropertyName("continuousDays")]
    public int ContinuousDays { get; set; }

    [JsonPropertyName("gapDays")]
    public int GapDays { get; set; }

    [JsonPropertyName("remainingDays")]
    public int RemainingDays { get; set; }

    [JsonPropertyName("deadlineDate")]
    public string? DeadlineDate { get; set; }

    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; }
}

/// <summary>`makeup_card.changed` 的 payload；`reason` ∈ `MONTHLY_GRANT` / `USED`。</summary>
public sealed class MakeupCardChangedPayloadDto
{
    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("available")]
    public int Available { get; set; }

    [JsonPropertyName("used")]
    public int Used { get; set; }

    [JsonPropertyName("totalGranted")]
    public int TotalGranted { get; set; }

    [JsonPropertyName("maxAvailable")]
    public int MaxAvailable { get; set; }

    [JsonPropertyName("lastGrantedMonth")]
    public string? LastGrantedMonth { get; set; }

    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; }
}

/* -------------------------------------------------------------------------- */
/* 强类型帧模型（解析结果）                                                       */
/* -------------------------------------------------------------------------- */

/// <summary>服务端帧的基类。未识别的 type 会被丢弃（不产生实例）。</summary>
public abstract record WsServerFrame(long Timestamp);

/// <summary>心跳应答。⚠️ 顶层 `{type, timestamp}`，无 payload 包装（陷阱 2）。</summary>
public sealed record WsPongFrame(long Timestamp) : WsServerFrame(Timestamp);

/// <summary>多设备回声；按 `RequestId` 幂等，自己的回声必须跳过（EDGE-W-4）。</summary>
public sealed record WsEchoFrame(string RequestId, ChatEchoPayloadDto Payload)
    : WsServerFrame(Payload.Timestamp);

/// <summary>消息进入防抖队列。</summary>
public sealed record WsQueuedFrame(string? RequestId, ChatQueuedPayloadDto Payload)
    : WsServerFrame(0);

/// <summary>输入/思考状态（含识图两段式）。</summary>
public sealed record WsTypingFrame(ChatTypingPayloadDto Payload) : WsServerFrame(0);

/// <summary>流式回复（顶层 `requestId` + payload）。</summary>
public sealed record WsReplyStreamFrame(string? RequestId, ChatReplyStreamPayloadDto Payload)
    : WsServerFrame(Payload.Timestamp ?? 0);

/// <summary>服务端错误帧。</summary>
public sealed record WsBotErrorFrame(string? RequestId, BotErrorPayloadDto Payload)
    : WsServerFrame(Payload.Timestamp);

/// <summary>用户事实创建（实时写入 user_facts 并推进游标）。</summary>
public sealed record WsMemoryFactFrame(Dtos.UserFactDto Payload) : WsServerFrame(Payload.Timestamp);

/// <summary>Token 失效（先 accept 再下发，随后以 4001 关闭）。</summary>
public sealed record WsAuthExpiredFrame(AuthExpiredPayloadDto Payload) : WsServerFrame(Payload.Timestamp);

public sealed record WsPointsChangedFrame(PointsChangedPayloadDto Payload) : WsServerFrame(Payload.Timestamp);

public sealed record WsLevelChangedFrame(LevelChangedPayloadDto Payload) : WsServerFrame(Payload.Timestamp);

public sealed record WsStreakWarningFrame(StreakWarningPayloadDto Payload) : WsServerFrame(Payload.Timestamp);

public sealed record WsMakeupCardChangedFrame(MakeupCardChangedPayloadDto Payload) : WsServerFrame(Payload.Timestamp);

/// <summary>
/// 已知但**不可依赖**的帧（`points.snapshot` 在后端仅有定义、**无任何调用点**）。
/// 保留类型定义以便解析时不报错，但业务**不得**等待它 —— 重连对齐一律走 `GET /points/overview`
/// （FR-W-PROG-6）。
/// </summary>
public sealed record WsPointsSnapshotFrame(long Timestamp) : WsServerFrame(Timestamp);
