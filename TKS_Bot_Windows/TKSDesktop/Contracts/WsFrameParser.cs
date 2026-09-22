using System.Text.Json;

namespace TKSDesktop.Contracts;

/// <summary>帧解析结果类别。</summary>
public enum WsFrameParseKind
{
    /// <summary>成功解析为已知帧。</summary>
    Known,

    /// <summary>合法 JSON，但 `type` 未知 —— **必须忽略且不中断连接**（NFR-W-12 / FR-W-NET-2）。</summary>
    UnknownType,

    /// <summary>非法 JSON —— 丢弃并记 debug 日志，不得中断连接。</summary>
    InvalidJson,
}

/// <summary>帧解析结果。</summary>
public sealed record WsFrameParseResult(
    WsFrameParseKind Kind,
    WsServerFrame? Frame,
    string? RawType,
    string? Error);

/// <summary>
/// WebSocket 帧解析器（PRD §5.3.2 / §5.5 C-7 / §5.6 陷阱 1、2）。
///
/// 实现要点（与 PRD 的实现建议逐条对应）：
/// ① **先只解析 `type` 字符串**，再按 type 决定是否反序列化 `payload` —— 未知 type 永不进入
///    payload 反序列化，因此未知结构不会抛错；
/// ② `pong` 从**顶层**读取 `timestamp`（**没有** `payload` 包装 —— 陷阱 2）；
/// ③ `chat.message.echo` / `chat.queued` / `chat.reply.stream` / `bot.error` 的 `requestId`
///    在**顶层**（陷阱 1）；
/// ④ 全程**不使用**全局命名策略，字段名一律由 DTO 的 `[JsonPropertyName]` 决定（C-7 / V-W-S2）。
/// </summary>
public static class WsFrameParser
{
    /// <summary>
    /// 解析器选项：**不设** <see cref="JsonSerializerOptions.PropertyNamingPolicy"/>，
    /// 完全依赖 DTO 上的显式 <c>[JsonPropertyName]</c>；未知字段静默忽略（这是 System.Text.Json 的默认行为，
    /// 但显式写明以免后续被误改）。
    /// </summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = null,
        DictionaryKeyPolicy = null,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>已知的服务端帧 type 全集（供机械校验与排障）。</summary>
    public static readonly IReadOnlyList<string> KnownServerFrameTypes =
    [
        "pong",
        "chat.message.echo",
        "chat.queued",
        "chat.typing",
        "chat.reply.stream",
        "bot.error",
        "memory.fact.created",
        "auth.expired",
        "points.changed",
        "level.changed",
        "streak.warning",
        "makeup_card.changed",
        // 后端仅有定义、无调用点；保留以便解析时不报错，业务不得依赖（FR-W-PROG-6）。
        "points.snapshot",
    ];

    /// <summary>
    /// 解析一条服务端文本帧。
    /// **永不抛错**：任何异常都被收敛为 <see cref="WsFrameParseKind.InvalidJson"/> 结果。
    /// </summary>
    public static WsFrameParseResult Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new WsFrameParseResult(WsFrameParseKind.InvalidJson, null, null, "empty frame");
        }

        string? type;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new WsFrameParseResult(WsFrameParseKind.InvalidJson, null, null, "root is not an object");
            }

            type = doc.RootElement.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString()
                : null;
        }
        catch (JsonException ex)
        {
            return new WsFrameParseResult(WsFrameParseKind.InvalidJson, null, null, ex.Message);
        }

        if (string.IsNullOrEmpty(type))
        {
            // 合法 JSON 但缺少 type：按未知处理（忽略，不抛错）。
            return new WsFrameParseResult(WsFrameParseKind.UnknownType, null, null, "missing type");
        }

        try
        {
            var frame = ParseKnown(type, raw);
            return frame is null
                ? new WsFrameParseResult(WsFrameParseKind.UnknownType, null, type, null)
                : new WsFrameParseResult(WsFrameParseKind.Known, frame, type, null);
        }
        catch (JsonException ex)
        {
            // 字段类型与预期不符（服务端契约漂移）：丢弃该帧但不中断连接。
            return new WsFrameParseResult(WsFrameParseKind.UnknownType, null, type, ex.Message);
        }
    }

    /// <summary>
    /// 按 type 分派解析。返回 <c>null</c> 表示未知 type。
    /// </summary>
    private static WsServerFrame? ParseKnown(string type, string raw)
    {
        switch (type)
        {
            // ⚠️ 陷阱 2：顶层 {type, timestamp}，无 payload。
            case "pong":
                {
                    var dto = Deserialize<WsPongTopLevelDto>(raw);
                    return new WsPongFrame(dto?.Timestamp ?? 0);
                }

            case "chat.message.echo":
                {
                    var dto = Deserialize<WsEnvelopeDto<ChatEchoPayloadDto>>(raw);
                    if (dto?.Payload is null)
                    {
                        return null;
                    }

                    return new WsEchoFrame(dto.RequestId ?? string.Empty, dto.Payload);
                }

            case "chat.queued":
                {
                    var dto = Deserialize<WsEnvelopeDto<ChatQueuedPayloadDto>>(raw);
                    return dto?.Payload is null ? null : new WsQueuedFrame(dto.RequestId, dto.Payload);
                }

            case "chat.typing":
                {
                    var dto = Deserialize<WsEnvelopeDto<ChatTypingPayloadDto>>(raw);
                    return dto?.Payload is null ? null : new WsTypingFrame(dto.Payload);
                }

            case "chat.reply.stream":
                {
                    var dto = Deserialize<WsEnvelopeDto<ChatReplyStreamPayloadDto>>(raw);
                    return dto?.Payload is null ? null : new WsReplyStreamFrame(dto.RequestId, dto.Payload);
                }

            case "bot.error":
                {
                    var dto = Deserialize<WsEnvelopeDto<BotErrorPayloadDto>>(raw);
                    return dto?.Payload is null ? null : new WsBotErrorFrame(dto.RequestId, dto.Payload);
                }

            case "memory.fact.created":
                {
                    var dto = Deserialize<WsEnvelopeDto<Dtos.UserFactDto>>(raw);
                    return dto?.Payload is null ? null : new WsMemoryFactFrame(dto.Payload);
                }

            case "auth.expired":
                {
                    var dto = Deserialize<WsEnvelopeDto<AuthExpiredPayloadDto>>(raw);
                    return dto?.Payload is null ? null : new WsAuthExpiredFrame(dto.Payload);
                }

            case "points.changed":
                {
                    var dto = Deserialize<WsEnvelopeDto<PointsChangedPayloadDto>>(raw);
                    return dto?.Payload is null ? null : new WsPointsChangedFrame(dto.Payload);
                }

            case "level.changed":
                {
                    var dto = Deserialize<WsEnvelopeDto<LevelChangedPayloadDto>>(raw);
                    return dto?.Payload is null ? null : new WsLevelChangedFrame(dto.Payload);
                }

            case "streak.warning":
                {
                    var dto = Deserialize<WsEnvelopeDto<StreakWarningPayloadDto>>(raw);
                    return dto?.Payload is null ? null : new WsStreakWarningFrame(dto.Payload);
                }

            case "makeup_card.changed":
                {
                    var dto = Deserialize<WsEnvelopeDto<MakeupCardChangedPayloadDto>>(raw);
                    return dto?.Payload is null ? null : new WsMakeupCardChangedFrame(dto.Payload);
                }

            case "points.snapshot":
                {
                    var dto = Deserialize<WsPongTopLevelDto>(raw);
                    return new WsPointsSnapshotFrame(dto?.Timestamp ?? 0);
                }

            default:
                return null;
        }
    }

    private static T? Deserialize<T>(string raw)
        where T : class
        => JsonSerializer.Deserialize<T>(raw, Options);

    /// <summary>
    /// 判断一个 WS 关闭码是否为「Token 失效」（陷阱 6）。
    /// ⚠️ `WebSocketCloseStatus` 枚举**无法表达 4001**，必须读取 `ClientWebSocket.CloseStatus`
    /// 的原始数值再比较。
    /// </summary>
    public static bool IsAuthFailureClose(int? rawCloseCode)
        => rawCloseCode == ProtocolConstants.WsCloseInvalidToken;
}

/// <summary>顶层 `{type, timestamp}` 形态（`pong`，以及 `points.snapshot` 的宽松读取）。</summary>
internal sealed class WsPongTopLevelDto
{
    [System.Text.Json.Serialization.JsonPropertyName("type")]
    public string? Type { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("timestamp")]
    public long Timestamp { get; set; }
}

/// <summary>通用 `{type, requestId?, payload}` 包装（服务端下行帧的多数形态）。</summary>
internal sealed class WsEnvelopeDto<TPayload>
    where TPayload : class
{
    [System.Text.Json.Serialization.JsonPropertyName("type")]
    public string? Type { get; set; }

    /// <summary>⚠️ 陷阱 1：在**顶层**，不在 payload 内。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("requestId")]
    public string? RequestId { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("payload")]
    public TPayload? Payload { get; set; }
}
