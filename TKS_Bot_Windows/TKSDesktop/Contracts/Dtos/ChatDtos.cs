using System.Text.Json.Serialization;

namespace TKSDesktop.Contracts.Dtos;

/* -------------------------------------------------------------------------- */
/* 历史 / 记忆 / 城市 / 降级聊天（§5.2）                                          */
/* -------------------------------------------------------------------------- */

/// <summary>服务端 timeline 记录（`GET /chat/history` 的 `items[]`）。</summary>
public sealed class TimelineItemDto
{
    [JsonPropertyName("messageId")]
    public string MessageId { get; set; } = string.Empty;

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
    [JsonPropertyName("items")]
    public List<TimelineItemDto> Items { get; set; } = [];
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
}

/// <summary>`GET /memory/facts` 响应 `data`。</summary>
public sealed class MemoryFactsDataDto
{
    [JsonPropertyName("items")]
    public List<UserFactDto> Items { get; set; } = [];
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
