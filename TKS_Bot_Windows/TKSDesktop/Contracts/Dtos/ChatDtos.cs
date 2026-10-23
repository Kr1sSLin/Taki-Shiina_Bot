using System.Text.Json;
using System.Text.Json.Serialization;

namespace TKSDesktop.Contracts.Dtos;

/* -------------------------------------------------------------------------- */
/* 历史 / 记忆 / 城市 / 降级聊天（§5.2）                                          */
/* -------------------------------------------------------------------------- */

/// <summary>服务端 timeline 记录（`GET /chat/history` 的 `items[]`）。</summary>
public sealed class TimelineItemDto
{
    [JsonPropertyName("historySource")] public string? HistorySource { get; set; }
    [JsonPropertyName("messageId")]
    public string MessageId { get; set; } = string.Empty;

    [JsonPropertyName("requestId")]
    public string? RequestId { get; set; }

    [JsonPropertyName("imageCount")]
    public int ImageCount { get; set; }

    [JsonPropertyName("userId")]
    public string UserId { get; set; } = string.Empty;

    /// <summary>`user` / `bot`；未知值由调用方按忽略处理（NFR-W-12）。</summary>
    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; }

    /* ---- FR-W-SYNC-11：历史行可能是互动行，必须按互动气泡渲染，不得当普通文本 ---- */

    /// <summary>互动行标记：`text` / `image` / `mixed` / `interaction`。</summary>
    [JsonPropertyName("contentType")]
    public string? ContentType { get; set; }

    [JsonPropertyName("itemId")]
    public string? ItemId { get; set; }

    [JsonPropertyName("itemName")]
    public string? ItemName { get; set; }

    [JsonPropertyName("itemIcon")]
    public string? ItemIcon { get; set; }

    [JsonPropertyName("costPoints")]
    public int? CostPoints { get; set; }

    /// <summary>互动失败标记（`messageKind = interaction_failed` 的历史形态）。</summary>
    [JsonPropertyName("interactionFailed")]
    public bool? InteractionFailed { get; set; }

    /// <summary>只增不改字段，客户端必须容忍缺失（W-P4）。</summary>
    [JsonPropertyName("messageKind")]
    public string? MessageKind { get; set; }

    [JsonPropertyName("greetingScenario")]
    public string? GreetingScenario { get; set; }

    [JsonPropertyName("modelProvider")]
    public string? ModelProvider { get; set; }
}

/// <summary>`GET /chat/history` 响应 `data`。</summary>
public sealed class ChatHistoryDataDto
{
    [JsonPropertyName("historySyncVersion")] public int? HistorySyncVersion { get; set; }
    [JsonPropertyName("datasetGeneration")] public string? DatasetGeneration { get; set; }
    [JsonPropertyName("nextCursor")] public string? NextCursor { get; set; }
    [JsonPropertyName("hasMore")] public bool HasMore { get; set; }
    [JsonPropertyName("blocked")] public bool Blocked { get; set; }
    [JsonPropertyName("pollAfterMs")] public int PollAfterMs { get; set; }
    [JsonPropertyName("items")]
    public List<TimelineItemDto> Items { get; set; } = [];

    [JsonPropertyName("operations")]
    public List<ChatOperationDto> Operations { get; set; } = [];
}

public sealed class ChatOperationDto
{
    [JsonPropertyName("sources")] public List<TimelineItemDto> Sources { get; set; } = [];
    [JsonPropertyName("status")] public string Status { get; set; } = string.Empty;
    [JsonPropertyName("requestIds")] public List<string> RequestIds { get; set; } = [];
    [JsonPropertyName("failureCategory")] public string? FailureCategory { get; set; }
    [JsonPropertyName("refundStatus")] public string? RefundStatus { get; set; }
    [JsonPropertyName("retryable")] public bool? Retryable { get; set; }
    [JsonPropertyName("recoveryAction")] public string? RecoveryAction { get; set; }
}

/// <summary>用户事实（`GET /memory/facts` 的 `items[]`）。</summary>
public sealed class UserFactDto
{
    [JsonPropertyName("factId")]
    public string FactId { get; set; } = string.Empty;

    [JsonPropertyName("userId")]
    public string UserId { get; set; } = string.Empty;

    [JsonPropertyName("fact")]
    public string Fact { get; set; } = string.Empty;

    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; }

    [JsonPropertyName("eventId")]
    public string? EventId { get; set; }

    [JsonPropertyName("revision")]
    public long Revision { get; set; }

    [JsonPropertyName("revisionId")]
    public string? RevisionId { get; set; }

    [JsonPropertyName("permissionVersion")]
    public long PermissionVersion { get; set; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("stage")]
    public string Stage { get; set; } = "candidate";

    [JsonPropertyName("truthStatus")]
    public string TruthStatus { get; set; } = "pending";

    [JsonPropertyName("score")]
    public double Score { get; set; }

    [JsonPropertyName("sourceMessageId")]
    public string? SourceMessageId { get; set; }

    [JsonPropertyName("temporalValidity")]
    public JsonElement? TemporalValidity { get; set; }
}

/// <summary>`GET /memory/facts` 响应 `data`。</summary>
public sealed class MemoryFactsDataDto
{
    [JsonPropertyName("items")]
    public List<UserFactDto> Items { get; set; } = [];

    [JsonPropertyName("nextCursor")]
    public long NextCursor { get; set; }

    [JsonPropertyName("hasMore")]
    public bool HasMore { get; set; }
}

public sealed class MemoryFactDetailDto
{
    [JsonPropertyName("factId")] public string FactId { get; set; } = string.Empty;
    [JsonPropertyName("domain")] public string Domain { get; set; } = string.Empty;
    [JsonPropertyName("userId")] public string UserId { get; set; } = string.Empty;
    [JsonPropertyName("content")] public string Content { get; set; } = string.Empty;
    [JsonPropertyName("revision")] public long Revision { get; set; }
    [JsonPropertyName("revisionId")] public string? RevisionId { get; set; }
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("stage")] public string Stage { get; set; } = "candidate";
    [JsonPropertyName("truthStatus")] public string TruthStatus { get; set; } = "pending";
    [JsonPropertyName("score")] public double Score { get; set; }
    [JsonPropertyName("temporalValidity")] public JsonElement? TemporalValidity { get; set; }
    [JsonPropertyName("sources")] public List<MemoryFactSourceDto> Sources { get; set; } = [];
}

public sealed class MemoryFactSourceDto
{
    [JsonPropertyName("sourceMessageId")] public string? SourceMessageId { get; set; }
}

public sealed class ProactiveSettingsDto
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("version")] public long Version { get; set; }
}

/// <summary>`GET` / `PUT /settings/city` 响应 `data`；未设置时服务端返回 `auto_ip`。</summary>
public sealed class CityDataDto
{
    [JsonPropertyName("city")]
    public string? City { get; set; }
}

/// <summary>`PUT /settings/city` 请求体。</summary>
public sealed class CityUpdateRequestDto
{
    [JsonPropertyName("city")]
    public string City { get; set; } = string.Empty;
}

/* -------------------------------------------------------------------------- */
/* REST 降级通道（FR-W-CHAT-9）                                                  */
/* -------------------------------------------------------------------------- */

/// <summary>`POST /chat` 请求体（**降级通道**，正常链路走 WS；**不参与积分结算**）。</summary>
public sealed class RestChatRequestDto
{
    [JsonPropertyName("requestId")]
    public string? RequestId { get; set; }

    [JsonPropertyName("conversationId")]
    public string? ConversationId { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("stream")]
    public bool? Stream { get; set; }

    [JsonPropertyName("traceId")]
    public string? TraceId { get; set; }
}

/// <summary>`POST /chat` 响应 `data`。</summary>
public sealed class RestChatDataDto
{
    [JsonPropertyName("conversationId")]
    public string? ConversationId { get; set; }

    [JsonPropertyName("reply")]
    public string? Reply { get; set; }

    [JsonPropertyName("messageId")]
    public string? MessageId { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("debounceWindowSec")]
    public double? DebounceWindowSec { get; set; }

    [JsonPropertyName("timerInstruction")]
    public TimerInstructionDto? TimerInstruction { get; set; }
}

/// <summary>
/// 服务端已解析完成的提醒指令（FR-W-REM-1）。
/// ⚠️ 客户端**只消费该字段**，**不得**自行解析原始 `[[TIMER:HH:MM|text]]` 标记——解析在服务端。
/// </summary>
public sealed class TimerInstructionDto
{
    /// <summary>`HH:MM`。</summary>
    [JsonPropertyName("target")]
    public string? Target { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }
}
