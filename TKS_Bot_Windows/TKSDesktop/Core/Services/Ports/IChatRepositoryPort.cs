namespace TKSDesktop.Core.Services.Ports;

/// <summary>
/// 消息写入结果：区分「新建」「就地更新」「无变化」三态。
/// **必须**区分 <c>Updated</c> 与 <c>Unchanged</c>：历史同步把一条 <c>error</c> 消息修复成 <c>sent</c>
/// 时走的是 UPDATE 分支，若统一返回「非新建」，调用方无法得知需要刷新 UI。
/// </summary>
public enum MessageWriteOutcome
{
    /// <summary>新插入。</summary>
    Inserted,

    /// <summary>就地更新了已有行。</summary>
    Updated,

    /// <summary>已存在且给定字段无变化。</summary>
    Unchanged,
}

/// <summary>
/// 可持久化的一条聊天消息（PRD §9.1 的列，一一对应）。
///
/// 刻意**不复用**数据层实体：数据层由并行实现方提供、签名可能变动，因此这里只声明对话核心
/// 真正需要的字段。适配器负责双向映射（见 <c>Ports/ExternalAdapters.cs</c>）。
///
/// ⚠️ 不含 <c>messageKind</c> / <c>interactionItemName</c> / <c>interactionItemIcon</c> /
///    <c>interactionFailed</c>：PRD §9.1 的 <c>chat_messages</c> **没有这些列**。
///    它们只随帧 / 历史行在「渲染瞬间」存在，因此走 <see cref="MessageProjection"/> 的瞬态通道，
///    不参与落库（重启后互动气泡靠 <c>content_type = 'interaction'</c> 识别）。
/// </summary>
public sealed record ChatMessageRecord(
    string MessageId,
    string Role,
    string ContentType,
    string Content,
    string Status,
    long Timestamp,
    string? ErrorCode = null,
    string? ModelProvider = null,
    string? MessageType = null)
{
    /// <summary>
    /// 附件绝对路径集合（默认空集合，调用方无需 null 检查）。
    /// 仅在本端自己产生的消息（乐观发送、流式落库）由调用方直接填充，
    /// 避免为列表查询引入 N+1 次附件查询。
    /// </summary>
    public IReadOnlyList<string> AttachmentPaths { get; init; } = [];

    /// <summary>是否为用户消息。</summary>
    public bool IsUser => string.Equals(Role, "user", StringComparison.Ordinal);
}

/// <summary>
/// 渲染瞬态元数据（**不落库**）。
/// </summary>
/// <param name="Message">消息本体。</param>
/// <param name="Interaction">是否按互动气泡渲染（帧的 <c>messageKind</c> ∈ interaction / interaction_failed，
/// 或历史行的 <c>contentType == "interaction"</c>）。</param>
/// <param name="InteractionItemName">物品名（仅当次帧/行有值时存在，UI 缺失时按 EDGE-W-23 回退）。</param>
/// <param name="InteractionItemIcon">物品 emoji。</param>
/// <param name="InteractionFailed">互动失败标记。</param>
public sealed record MessageProjection(
    ChatMessageRecord Message,
    bool Interaction = false,
    string? InteractionItemName = null,
    string? InteractionItemIcon = null,
    bool InteractionFailed = false);

/// <summary>
/// 启动期孤儿流式消息规整结果（FR-W-CHAT-18 / EDGE-W-7）。
/// </summary>
/// <param name="DeletedEmptyStreaming">被删除的「<c>streaming</c> 且正文为空」孤儿占位条数。</param>
/// <param name="PromotedStreaming">被从 <c>streaming</c> 提升为 <c>received</c> 的条数。</param>
/// <param name="DeletedBlankBot">被删除的「<c>bot</c> 且正文为空」存量空气泡条数。</param>
public sealed record StartupNormalizationResult(
    int DeletedEmptyStreaming,
    int PromotedStreaming,
    int DeletedBlankBot)
{
    /// <summary>是否实际做了任何改动。</summary>
    public bool HasChanges => DeletedEmptyStreaming > 0 || PromotedStreaming > 0 || DeletedBlankBot > 0;
}

/// <summary>
/// 聊天落库端口（窄接口）。
///
/// 由主控 DI 用真实 <c>Core.Data.Repositories.*</c> 适配；本层只依赖该端口，
/// 因此并行实现方改签名不会波及对话核心。
///
/// ⚠️ 实现约束（PRD §9.2 FR-W-DB-1 / 陷阱 8）：更新已有消息行**必须**用 <c>UPDATE</c>，
///    绝不可 <c>INSERT OR REPLACE</c>（外键 <c>ON DELETE CASCADE</c> 会连带删掉附件子行）。
/// </summary>
public interface IChatRepositoryPort
{
    /// <summary>幂等写入一条消息（已存在则就地 <c>UPDATE</c>）。</summary>
    Task<MessageWriteOutcome> UpsertMessageAsync(
        ChatMessageRecord message,
        CancellationToken cancellationToken = default);

    /// <summary>按主键读取；不存在返回 <c>null</c>。实现方**应**同时填充 <see cref="ChatMessageRecord.AttachmentPaths"/>。</summary>
    Task<ChatMessageRecord?> GetMessageAsync(string messageId, CancellationToken cancellationToken = default);

    /// <summary>更新消息状态与错误码（<paramref name="errorCode"/> 为 <c>null</c> 时清空错误码）。</summary>
    Task<bool> UpdateMessageStatusAsync(
        string messageId,
        string status,
        string? errorCode,
        CancellationToken cancellationToken = default);

    /// <summary>删除一条本地消息。</summary>
    Task<bool> DeleteMessageAsync(string messageId, CancellationToken cancellationToken = default);

    /// <summary>按时间升序取最近 <paramref name="limit"/> 条（不含附件路径）。</summary>
    Task<IReadOnlyList<ChatMessageRecord>> ListMessagesAsync(
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>本地最大时间戳；无消息时为 0（FR-W-SYNC-3 的 <c>since</c> 输入之一）。</summary>
    Task<long> MaxTimestampAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// **启动期**孤儿流式消息规整（FR-W-CHAT-18 / EDGE-W-7）：**同一事务内**
    /// ① 删除 <c>streaming</c> 且正文为空的占位；② 其余 <c>streaming</c> 提升为 <c>received</c>；
    /// ③ 删除正文为空的 <c>bot</c> 空气泡。
    /// </summary>
    Task<StartupNormalizationResult> NormalizeStreamingOnStartupAsync(
        CancellationToken cancellationToken = default);

    /// <summary>全部处于 <c>sending</c> 的用户消息（断线兜底标记用，FR-W-CONN-8）。</summary>
    Task<IReadOnlyList<ChatMessageRecord>> ListSendingMessagesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 本地全文搜索（FR-W-CHAT-17）。实现方负责分词器路由：
    /// ≥3 字符走 FTS trigram，1–2 字符（或 FTS 空结果）回退 <c>LIKE</c>，保证「永不返回假空」。
    /// </summary>
    Task<IReadOnlyList<ChatMessageRecord>> SearchMessagesAsync(
        string term,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>清空本地会话（消息 + 附件 + 去重表）；<paramref name="advanceCursor"/> 为真时把 <c>chat</c> 游标推到 now。</summary>
    Task<int> ClearConversationAsync(bool advanceCursor, CancellationToken cancellationToken = default);

    /* --- 同步游标（FR-W-SYNC-3 / FR-W-SYNC-7：聊天与记忆必须独立） --- */

    /// <summary>读取游标；不存在返回 0。</summary>
    Task<long> GetCursorAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>写入 / 覆盖游标。</summary>
    Task SetCursorAsync(string key, long value, CancellationToken cancellationToken = default);

    /* --- 三路去重表（FR-W-INT-12 / EDGE-W-21） --- */

    /// <summary>该拆分后 <c>messageId</c> 是否已投递过。</summary>
    Task<bool> IsDeliveredAsync(string messageId, CancellationToken cancellationToken = default);

    /// <summary>批量记入去重表（<c>INSERT OR IGNORE</c> 语义）。</summary>
    Task MarkDeliveredAsync(IReadOnlyList<string> messageIds, CancellationToken cancellationToken = default);

    /* --- 通知中心 / 记忆 --- */

    /// <summary>写入一条 <c>bot_notifications</c>（幂等：同 ID 不重复）。</summary>
    Task InsertBotNotificationAsync(
        string errorCode,
        string message,
        long timestamp,
        CancellationToken cancellationToken = default);

    /// <summary>幂等写入用户事实（FR-W-SYNC-7）。</summary>
    Task UpsertFactAsync(
        string factId,
        string userId,
        string fact,
        long timestamp,
        CancellationToken cancellationToken = default);

    /// <summary>本地事实最大时间戳；无记录时为 0。</summary>
    Task<long> MaxFactTimestampAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// 提醒持久化端口（PRD §9.6）。
/// </summary>
public interface IReminderPort
{
    /// <summary>
    /// 幂等插入：同主键已存在时**不覆盖**并返回 <c>false</c>（FR-W-REM-3 的 KEEP 语义）。
    /// 尤其**不得**把已 <c>fired</c> / <c>cancelled</c> 的提醒复活为 <c>pending</c>。
    /// </summary>
    Task<bool> InsertKeepAsync(ReminderInfo reminder, CancellationToken cancellationToken = default);

    /// <summary>全部 <c>pending</c> 提醒，按 <c>fire_at</c> 升序。</summary>
    Task<IReadOnlyList<ReminderInfo>> ListPendingAsync(CancellationToken cancellationToken = default);

    /// <summary>按主键读取；不存在返回 <c>null</c>。</summary>
    Task<ReminderInfo?> GetAsync(string reminderId, CancellationToken cancellationToken = default);

    /// <summary>置为已触发（仅当当前为 <c>pending</c>）。</summary>
    Task<bool> MarkFiredAsync(string reminderId, CancellationToken cancellationToken = default);

    /// <summary>置为已取消（仅当当前为 <c>pending</c>）。</summary>
    Task<bool> CancelAsync(string reminderId, CancellationToken cancellationToken = default);

    /// <summary>清理已结束且超过保留期的提醒，返回删除条数。</summary>
    Task<int> PruneFinishedAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// 附件持久化端口（PRD §9.2：<c>message_id</c> 允许 NULL，表示尚未发送的草稿附件）。
/// </summary>
public interface IAttachmentPort
{
    /// <summary>登记一张草稿附件（<c>message_id</c> 为 NULL），返回带主键的草稿。</summary>
    Task<AttachmentDraft> InsertDraftAsync(
        string mimeType,
        string localPath,
        long fileSize,
        int? width,
        int? height,
        CancellationToken cancellationToken = default);

    /// <summary>当前全部草稿附件（<c>message_id IS NULL</c>），按时间升序。</summary>
    Task<IReadOnlyList<AttachmentDraft>> ListDraftsAsync(CancellationToken cancellationToken = default);

    /// <summary>删除一条附件记录（**不**删磁盘文件；磁盘清理由调用方按返回值处理）。</summary>
    Task<bool> DeleteAsync(string attachmentId, CancellationToken cancellationToken = default);

    /// <summary>把草稿附件挂到真实消息上（FR-W-IMG-6）。</summary>
    Task BindAsync(
        IReadOnlyList<string> attachmentIds,
        string messageId,
        CancellationToken cancellationToken = default);

    /// <summary>某条消息的附件（按时间升序）。</summary>
    Task<IReadOnlyList<AttachmentDraft>> ListByMessageAsync(
        string messageId,
        CancellationToken cancellationToken = default);

    /// <summary>删除 <paramref name="days"/> 天前的附件记录，返回被删条数与对应本地路径（供磁盘清理）。</summary>
    Task<(int DeletedCount, IReadOnlyList<string> Paths)> DeleteOlderThanAsync(
        int days,
        CancellationToken cancellationToken = default);

    /// <summary>附件占用字节数（FR-W-SET-11）。</summary>
    Task<long> TotalSizeAsync(CancellationToken cancellationToken = default);
}
