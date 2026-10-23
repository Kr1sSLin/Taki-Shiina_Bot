namespace TKSDesktop.Core.Data.Records;

/// <summary>
/// 写操作结果三态（PRD FR-W-DB-1）。
///
/// ⚠️ **必须**区分 <see cref="Updated"/> 与 <see cref="Unchanged"/>：历史同步把一条 <c>error</c> 消息
/// 修回 <c>sent</c> 时走的是 UPDATE 分支，若统一返回「非新建」，调用方无法得知需要刷新 UI，
/// 于是库里已修好而界面一直显示「发送失败」。
/// </summary>
public enum UpsertOutcome
{
    /// <summary>新建行。</summary>
    Inserted,

    /// <summary>就地更新（已存在且 <c>status</c> 或 <c>content</c> 确有变化）。</summary>
    Updated,

    /// <summary>已存在且 <c>status</c> + <c>content</c> 均无变化 → 未写库。</summary>
    Unchanged,
}

/// <summary><c>chat_messages</c> 行（§9.1）。字段名与列名一一对应（列名直译为 snake_case）。</summary>
/// <param name="MessageId">主键。用户消息 = <c>requestId</c>；流式占位 = <c>pending_{requestId}</c>；Bot 分段 = <c>{messageId}_{index}</c>。</param>
/// <param name="SessionId">会话 ID（单会话后端为固定值）。</param>
/// <param name="Role"><c>user</c> / <c>bot</c>。</param>
/// <param name="MessageType"><c>text</c> / <c>image</c>。</param>
/// <param name="ContentType"><c>text</c> / <c>image</c> / <c>mixed</c> / <c>interaction</c>。</param>
/// <param name="ModelProvider">模型标识。</param>
/// <param name="Content">正文。</param>
/// <param name="Status">见 <c>ProtocolConstants.Status*</c>。</param>
/// <param name="Timestamp">Unix 毫秒（本地按服务端下发值存储，不做时区换算）。</param>
/// <param name="ErrorCode">失败码（客户端自有码或服务端码）。</param>
public sealed record MessageRecord(
    string MessageId,
    string SessionId,
    string Role,
    string MessageType,
    string ContentType,
    string? ModelProvider,
    string Content,
    string Status,
    long Timestamp,
    string? ErrorCode);

/// <summary>
/// upsert 输入。用 <c>required</c> + <c>init</c> 而非位置记录，避免调用方把
/// <c>ContentType</c> / <c>ModelProvider</c> 顺序写错（写错不报错，只会静默写错列）。
/// </summary>
public sealed record MessageUpsert
{
    public bool Authoritative { get; init; }
    /// <summary>主键（见 <see cref="MessageRecord.MessageId"/>）。</summary>
    public required string MessageId { get; init; }

    /// <summary>会话 ID。</summary>
    public required string SessionId { get; init; }

    /// <summary><c>user</c> / <c>bot</c>。</summary>
    public required string Role { get; init; }

    /// <summary>正文。</summary>
    public required string Content { get; init; }

    /// <summary>状态（<c>ProtocolConstants.Status*</c>）。</summary>
    public required string Status { get; init; }

    /// <summary>Unix 毫秒。</summary>
    public required long Timestamp { get; init; }

    /// <summary>消息形态，默认 <c>text</c>。</summary>
    public string MessageType { get; init; } = "text";

    /// <summary>内容类型，默认 <c>text</c>。</summary>
    public string ContentType { get; init; } = "text";

    /// <summary>模型标识，可空。</summary>
    public string? ModelProvider { get; init; }

    /// <summary>失败码，可空。</summary>
    public string? ErrorCode { get; init; }
}

/// <summary>搜索命中：消息行 + 关键片段（FR-W-DB-2 / FR-W-CHAT-17）。</summary>
/// <param name="Message">命中的消息行。</param>
/// <param name="Snippet">围绕首个命中位置截取的上下文片段（已把换行压成空格，便于列表单行显示）。</param>
public sealed record MessageSearchHit(MessageRecord Message, string Snippet);

/// <summary>
/// 启动规整结果（FR-W-CHAT-18 / EDGE-W-7）。
/// </summary>
/// <param name="DeletedEmptyStreaming">被删除的「<c>streaming</c> 且正文为空」的孤儿占位。</param>
/// <param name="PromotedStreaming">被从 <c>streaming</c> 提升为 <c>received</c> 的残留消息（有正文的孤儿流）。</param>
/// <param name="DeletedBlankBot">被删除的「<c>bot</c> 且正文为空」的存量空气泡。</param>
public sealed record MessageNormalizationResult(
    int DeletedEmptyStreaming,
    int PromotedStreaming,
    int DeletedBlankBot);

/// <summary>清空会话结果（FR-W-CHAT-12）。</summary>
/// <param name="DeletedMessages">被删除的 <c>chat_messages</c> 行数。</param>
/// <param name="AdvancedCursor">推进后的 <c>chat</c> 游标值（用于防历史回灌）。</param>
public sealed record ClearConversationResult(int DeletedMessages, long AdvancedCursor);
