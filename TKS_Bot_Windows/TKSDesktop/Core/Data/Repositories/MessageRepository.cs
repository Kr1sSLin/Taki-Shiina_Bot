using System.Text;
using Microsoft.Data.Sqlite;
using TKSDesktop.Contracts;
using TKSDesktop.Core.Data.Records;

namespace TKSDesktop.Core.Data.Repositories;

/// <summary>Bot 回复按 <c>\n</c> 拆分后的一段（FR-W-CHAT-6 / FR-W-SYNC-5）。</summary>
/// <param name="MessageId">主键 <c>{finalMessageId}_{index}</c>。</param>
/// <param name="Content">该段正文（已 trim、非空）。</param>
/// <param name="Timestamp"><c>{finalTimestamp} + index</c>。</param>
public sealed record BotMessageSegment(string MessageId, string Content, long Timestamp);

/// <summary>
/// 消息 / 附件 仓储（PRD §9.1 / §9.2）。同步游标与已投递去重在 <see cref="CursorRepository"/>。
///
/// <list type="bullet">
///   <item><b>FR-W-DB-1 / 陷阱 8</b>：更新消息行**禁止** <c>INSERT OR REPLACE</c> —— REPLACE 会先 DELETE 再 INSERT，
///         外键 <c>ON DELETE CASCADE</c> 会连带删掉附件子行。必须 SELECT 后 <c>UPDATE</c>。
///         Windows 端实测：<c>INSERT OR REPLACE</c> 后子行数 1→0，<c>UPDATE</c> 后保持 1。</item>
///   <item><b>FR-W-SYNC-5</b>：实时流式 <c>done</c> 与历史同步**必须**共用 <see cref="SplitBotContent"/>，
///         否则同一条消息会以两种形态重复入库。</item>
///   <item><b>FR-W-CHAT-17</b>：≥3 字符走 trigram 索引；&lt;3 字符或 FTS 异常/空结果回退 LIKE，保证「永不返回假空」。</item>
/// </list>
/// </summary>
public sealed class MessageRepository
{
    /// <summary>搜索条数上限（FR-W-DB-2）。</summary>
    public const int MaxSearchLimit = 500;

    /// <summary>trigram 索引最少需要的查询字符数。</summary>
    private const int TrigramMinChars = 3;

    /// <summary>片段上下文字符数（命中位置前后各取一部分）。</summary>
    private const int SnippetRadius = 24;

    private const string MessageColumns =
        "message_id, session_id, role, message_type, content_type, model_provider, content, status, timestamp, error_code";

    /// <summary>同一组列带 <c>m.</c> 前缀（FTS 联结查询用，避免靠字符串替换生成列清单）。</summary>
    private const string MessageColumnsAliased =
        "m.message_id, m.session_id, m.role, m.message_type, m.content_type, m.model_provider, m.content, m.status, m.timestamp, m.error_code";

    private const string AttachmentColumns =
        "attachment_id, message_id, session_id, mime_type, local_path, file_size, width, height, upload_state, timestamp";

    private readonly LocalDb _db;

    /// <summary>构造。</summary>
    public MessageRepository(LocalDb db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    /* ------------------------------------------------------------------ */
    /* 主键约定                                                            */
    /* ------------------------------------------------------------------ */

    /// <summary>流式占位消息 ID = <c>pending_{requestId}</c>。</summary>
    public static string PendingMessageId(string requestId) => $"pending_{requestId}";

    /// <summary>
    /// Bot 最终内容按 <c>\n</c> 拆分：trim、过滤空行，每段独立一条消息，
    /// 主键 <c>{finalMessageId}_{index}</c>，<c>timestamp = finalTimestamp + index</c>（FR-W-CHAT-6 / FR-W-SYNC-5）。
    /// 全部分段为空时返回空列表（调用方不得落空内容气泡）。
    /// </summary>
    public static IReadOnlyList<BotMessageSegment> SplitBotContent(
        string finalMessageId,
        string? content,
        long finalTimestamp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(finalMessageId);

        var lines = (content ?? string.Empty)
            .Split('\n')
            .Select(static line => line.Trim())
            .Where(static line => line.Length > 0)
            .ToList();

        if (lines.Count == 0)
        {
            return [];
        }

        var segments = new List<BotMessageSegment>(lines.Count);
        for (var index = 0; index < lines.Count; index++)
        {
            segments.Add(new BotMessageSegment($"{finalMessageId}_{index}", lines[index], finalTimestamp + index));
        }

        return segments;
    }

    /* ------------------------------------------------------------------ */
    /* 写入：消息                                                          */
    /* ------------------------------------------------------------------ */

    /// <summary>
    /// 幂等写入：已存在且 <c>status</c> + <c>content</c> 均无变化 → <see cref="UpsertOutcome.Unchanged"/>（不写库）；
    /// 否则仅 <c>UPDATE</c> 指定列；不存在则 <c>INSERT</c>。**永不 REPLACE**（FR-W-DB-1）。
    /// </summary>
    public Task<UpsertOutcome> UpsertAsync(MessageUpsert message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        return _db.WriteAsync(async (connection, transaction, ct) =>
        {
            var existing = await SqliteExecutor
                .QuerySingleOrDefaultAsync(
                    connection,
                    $"SELECT {MessageColumns} FROM chat_messages WHERE message_id = $id;",
                    [SqliteExecutor.Param("$id", message.MessageId)],
                    transaction,
                    MapMessage,
                    ct)
                .ConfigureAwait(false);

            if (existing is not null)
            {
                if (string.Equals(existing.Status, message.Status, StringComparison.Ordinal)
                    && string.Equals(existing.Content, message.Content, StringComparison.Ordinal)
                    && (!message.Authoritative || (existing.Timestamp == message.Timestamp && existing.ContentType == message.ContentType
                        && existing.MessageType == message.MessageType && existing.ModelProvider == message.ModelProvider && existing.ErrorCode == message.ErrorCode)))
                {
                    return UpsertOutcome.Unchanged;
                }

                await SqliteExecutor
                    .ExecuteAsync(
                        connection,
                        """
                        UPDATE chat_messages
                           SET status = $status,
                               content = $content,
                               error_code = $error,
                               model_provider = $model,
                               content_type = $ctype,
                               message_type = $mtype,
                               timestamp = $ts
                         WHERE message_id = $id;
                        """,
                        [
                            SqliteExecutor.Param("$status", message.Status),
                            SqliteExecutor.Param("$content", message.Content),
                            SqliteExecutor.Param("$error", message.ErrorCode),
                            SqliteExecutor.Param("$model", message.ModelProvider),
                            SqliteExecutor.Param("$ctype", message.ContentType),
                            SqliteExecutor.Param("$mtype", message.MessageType),
                            SqliteExecutor.Param("$ts", message.Authoritative ? message.Timestamp : existing.Timestamp),
                            SqliteExecutor.Param("$id", message.MessageId),
                        ],
                        transaction,
                        ct)
                    .ConfigureAwait(false);

                return UpsertOutcome.Updated;
            }

            await SqliteExecutor
                .ExecuteAsync(
                    connection,
                    """
                    INSERT INTO chat_messages
                      (message_id, session_id, role, message_type, content_type, model_provider, content, status, timestamp, error_code)
                    VALUES
                      ($id, $session, $role, $mtype, $ctype, $model, $content, $status, $ts, $error);
                    """,
                    [
                        SqliteExecutor.Param("$id", message.MessageId),
                        SqliteExecutor.Param("$session", message.SessionId),
                        SqliteExecutor.Param("$role", message.Role),
                        SqliteExecutor.Param("$mtype", message.MessageType),
                        SqliteExecutor.Param("$ctype", message.ContentType),
                        SqliteExecutor.Param("$model", message.ModelProvider),
                        SqliteExecutor.Param("$content", message.Content),
                        SqliteExecutor.Param("$status", message.Status),
                        SqliteExecutor.Param("$ts", message.Timestamp),
                        SqliteExecutor.Param("$error", message.ErrorCode),
                    ],
                    transaction,
                    ct)
                .ConfigureAwait(false);

            return UpsertOutcome.Inserted;
        }, cancellationToken);
    }

    /// <summary>
    /// 落库一轮 Bot 最终回复：按 <see cref="SplitBotContent"/> 拆分并逐段 upsert。
    /// 返回各段的主键（供 <c>delivered_bot_messages</c> 去重与 UI 定位使用）。
    /// </summary>
    public async Task<IReadOnlyList<BotMessageSegment>> SaveBotReplyAsync(
        string sessionId,
        string finalMessageId,
        string? content,
        long finalTimestamp,
        string contentType = "text",
        string? modelProvider = null,
        CancellationToken cancellationToken = default)
    {
        var segments = SplitBotContent(finalMessageId, content, finalTimestamp);
        if (segments.Count == 0)
        {
            return segments;
        }

        await _db.WriteAsync(async (connection, transaction, ct) =>
        {
            foreach (var segment in segments)
            {
                await UpsertWithinAsync(
                    connection,
                    transaction,
                    new MessageUpsert
                    {
                        MessageId = segment.MessageId,
                        SessionId = sessionId,
                        Role = "bot",
                        Content = segment.Content,
                        Status = ProtocolConstants.StatusReceived,
                        Timestamp = segment.Timestamp,
                        ContentType = contentType,
                        ModelProvider = modelProvider,
                    },
                    ct).ConfigureAwait(false);
            }

            return true;
        }, cancellationToken).ConfigureAwait(false);

        return segments;
    }

    /// <summary>创建流式占位消息（首个非空 delta 时调用），返回占位主键。</summary>
    public async Task<string> CreateStreamingPlaceholderAsync(
        string requestId,
        string sessionId,
        long timestamp,
        string contentType = "text",
        string? modelProvider = null,
        CancellationToken cancellationToken = default)
    {
        var messageId = PendingMessageId(requestId);
        _ = await UpsertAsync(
            new MessageUpsert
            {
                MessageId = messageId,
                SessionId = sessionId,
                Role = "bot",
                Content = string.Empty,
                Status = ProtocolConstants.StatusStreaming,
                Timestamp = timestamp,
                ContentType = contentType,
                ModelProvider = modelProvider,
            },
            cancellationToken).ConfigureAwait(false);

        return messageId;
    }

    /// <summary>增量流式：<c>UPDATE chat_messages SET content = content || ?, status = 'streaming' WHERE message_id = ?</c>。</summary>
    public Task AppendStreamingDeltaAsync(string requestId, string delta, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentNullException.ThrowIfNull(delta);

        return _db.WriteAsync(
            (connection, transaction, ct) => SqliteExecutor.ExecuteAsync(
                connection,
                """
                UPDATE chat_messages
                   SET content = content || $delta,
                       status = 'streaming'
                 WHERE message_id = $id;
                """,
                [
                    SqliteExecutor.Param("$delta", delta),
                    SqliteExecutor.Param("$id", PendingMessageId(requestId)),
                ],
                transaction,
                ct),
            cancellationToken);
    }

    /// <summary>仅更新状态与错误码（不触碰 <c>content</c>，避免无谓的 FTS 触发器开销）。</summary>
    public Task UpdateStatusAsync(
        string messageId,
        string status,
        string? errorCode = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(status);

        return _db.WriteAsync(
            (connection, transaction, ct) => SqliteExecutor.ExecuteAsync(
                connection,
                "UPDATE chat_messages SET status = $status, error_code = $error WHERE message_id = $id;",
                [
                    SqliteExecutor.Param("$status", status),
                    SqliteExecutor.Param("$error", errorCode),
                    SqliteExecutor.Param("$id", messageId),
                ],
                transaction,
                ct),
            cancellationToken);
    }

    /// <summary>删除一条消息（外键 CASCADE 会一并删除其附件行）。</summary>
    public Task<int> DeleteAsync(string messageId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        return _db.WriteAsync(
            (connection, transaction, ct) => SqliteExecutor.ExecuteAsync(
                connection,
                "DELETE FROM chat_messages WHERE message_id = $id;",
                [SqliteExecutor.Param("$id", messageId)],
                transaction,
                ct),
            cancellationToken);
    }

    /* ------------------------------------------------------------------ */
    /* 启动规整（FR-W-CHAT-18 / EDGE-W-7）                                  */
    /* ------------------------------------------------------------------ */

    /// <summary>
    /// 启动时规整孤儿行，**同一事务内**完成三件事并返回各自条数：
    /// <list type="number">
    ///   <item><c>DELETE FROM chat_messages WHERE status='streaming' AND TRIM(content)=''</c>（空占位直接删）。</item>
    ///   <item><c>UPDATE chat_messages SET status='received' WHERE status='streaming'</c>（有正文的残留流提升为已接收，不再无限转圈）。</item>
    ///   <item><c>DELETE FROM chat_messages WHERE role='bot' AND TRIM(content)=''</c>（历史遗留空气泡）。</item>
    /// </list>
    /// </summary>
    public Task<MessageNormalizationResult> NormalizeOnStartupAsync(CancellationToken cancellationToken = default) =>
        _db.WriteAsync(async (connection, transaction, ct) =>
        {
            var deletedEmptyStreaming = await SqliteExecutor
                .ExecuteAsync(
                    connection,
                    "DELETE FROM chat_messages WHERE status = 'streaming' AND TRIM(content) = '';",
                    null,
                    transaction,
                    ct)
                .ConfigureAwait(false);

            var promoted = await SqliteExecutor
                .ExecuteAsync(
                    connection,
                    "UPDATE chat_messages SET status = 'received' WHERE status = 'streaming';",
                    null,
                    transaction,
                    ct)
                .ConfigureAwait(false);

            var deletedBlankBot = await SqliteExecutor
                .ExecuteAsync(
                    connection,
                    "DELETE FROM chat_messages WHERE role = 'bot' AND TRIM(content) = '';",
                    null,
                    transaction,
                    ct)
                .ConfigureAwait(false);

            return new MessageNormalizationResult(deletedEmptyStreaming, promoted, deletedBlankBot);
        }, cancellationToken);

    /* ------------------------------------------------------------------ */
    /* 清空会话（FR-W-CHAT-12）                                            */
    /* ------------------------------------------------------------------ */

    /// <summary>
    /// 清空本地会话：**同一事务内** 删 <c>chat_attachments</c> → <c>chat_messages</c> →
    /// <c>delivered_bot_messages</c>，并把 <c>chat</c> 游标推进到 <c>now</c>（防历史立刻回灌）。
    /// 设置页与聊天页必须共用本方法（Android 曾因两条路径不一致导致清空后被拉回）。
    /// </summary>
    public Task<ClearConversationResult> ClearConversationAsync(CancellationToken cancellationToken = default) =>
        _db.WriteAsync(async (connection, transaction, ct) =>
        {
            // 先删附件：虽然外键 CASCADE 会处理，但草稿附件（message_id IS NULL）不会被级联，必须显式删。
            _ = await SqliteExecutor
                .ExecuteAsync(connection, "DELETE FROM chat_attachments;", null, transaction, ct)
                .ConfigureAwait(false);

            var deleted = await SqliteExecutor
                .ExecuteAsync(connection, "DELETE FROM chat_messages;", null, transaction, ct)
                .ConfigureAwait(false);

            _ = await SqliteExecutor
                .ExecuteAsync(connection, "DELETE FROM delivered_bot_messages;", null, transaction, ct)
                .ConfigureAwait(false);

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _ = await SqliteExecutor
                .ExecuteAsync(
                    connection,
                    """
                    INSERT INTO sync_cursors (key, value) VALUES ($key, $value)
                    ON CONFLICT (key) DO UPDATE SET value = excluded.value;
                    """,
                    [
                        SqliteExecutor.Param("$key", ProtocolConstants.CursorKeyChat),
                        SqliteExecutor.Param("$value", now),
                    ],
                    transaction,
                    ct)
                .ConfigureAwait(false);

            return new ClearConversationResult(deleted, now);
        }, cancellationToken);

    /* ------------------------------------------------------------------ */
    /* 查询                                                              */
    /* ------------------------------------------------------------------ */

    /// <summary>按时间升序取最近 <paramref name="limit"/> 条（首屏本地加载，FR-W-CHAT-16）。</summary>
    public Task<IReadOnlyList<MessageRecord>> ListRecentAsync(
        int limit,
        string? sessionId = null,
        CancellationToken cancellationToken = default,
        int skipFromEnd = 0)
    {
        var safeLimit = Math.Clamp(limit, 1, 1000);
        var session = sessionId ?? ProtocolConstants.DefaultSessionId;

        return _db.ReadAsync(
            async (connection, ct) =>
            {
                var rows = await SqliteExecutor
                    .QueryAsync(
                        connection,
                        $"""
                         SELECT {MessageColumns} FROM chat_messages
                          WHERE session_id = $session
                          ORDER BY timestamp DESC, message_id DESC
                          LIMIT $limit OFFSET $offset;
                         """,
                        [
                            SqliteExecutor.Param("$session", session),
                            SqliteExecutor.Param("$limit", safeLimit),
                            SqliteExecutor.Param("$offset", Math.Max(0, skipFromEnd)),
                        ],
                        null,
                        MapMessage,
                        ct)
                    .ConfigureAwait(false);

                return (IReadOnlyList<MessageRecord>)rows.Reverse().ToList();
            },
            cancellationToken);
    }

    /// <summary>按状态列出消息（断线时规整全部 sending 行，不能受首屏分页上限影响）。</summary>
    public Task<IReadOnlyList<MessageRecord>> ListByStatusAsync(
        string status,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        return _db.ReadAsync(
            (connection, ct) => SqliteExecutor.QueryAsync(
                connection,
                $"SELECT {MessageColumns} FROM chat_messages WHERE status = $status ORDER BY timestamp ASC;",
                [SqliteExecutor.Param("$status", status)],
                null,
                MapMessage,
                ct),
            cancellationToken);
    }

    /// <summary>按主键取单条。</summary>
    public Task<MessageRecord?> GetAsync(string messageId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        return _db.ReadAsync(
            (connection, ct) => SqliteExecutor.QuerySingleOrDefaultAsync(
                connection,
                $"SELECT {MessageColumns} FROM chat_messages WHERE message_id = $id;",
                [SqliteExecutor.Param("$id", messageId)],
                null,
                MapMessage,
                ct),
            cancellationToken);
    }

    /// <summary>
    /// 本地最大 <c>timestamp</c>；增量同步用 <c>since = max(本地最大 timestamp, chat 游标)</c>（FR-W-SYNC-3）。
    /// 无数据时返回 0。
    /// </summary>
    public Task<long> ReadMaxTimestampAsync(
        string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        var session = sessionId ?? ProtocolConstants.DefaultSessionId;
        return _db.ReadAsync(
            (connection, ct) => SqliteExecutor.ScalarLongAsync(
                connection,
                "SELECT COALESCE(MAX(timestamp), 0) FROM chat_messages WHERE session_id = $session;",
                [SqliteExecutor.Param("$session", session)],
                null,
                ct),
            cancellationToken);
    }

    /// <summary>
    /// 本地消息全文搜索（FR-W-DB-2 / FR-W-CHAT-17）。
    ///
    /// <para>路由：查询词按**字符数**（非 UTF-16 长度，emoji/代理对按 1 计）判断 ——
    /// ≥3 → <c>chat_messages_fts_tri</c>（trigram，中文子串可命中）；&lt;3 → <c>LIKE</c>。
    /// FTS 抛错或命中为空时回退 <c>LIKE</c>（子串匹配，保证不出现假空）。
    /// FTS 查询词整体加引号并把内部 <c>"</c> 翻倍，避免用户输入被当作 FTS 运算符。</para>
    /// </summary>
    public Task<IReadOnlyList<MessageSearchHit>> SearchAsync(
        string? term,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        var keyword = (term ?? string.Empty).Trim();
        if (keyword.Length == 0)
        {
            return Task.FromResult<IReadOnlyList<MessageSearchHit>>([]);
        }

        var safeLimit = Math.Clamp(limit, 1, MaxSearchLimit);
        var useTrigram = CountChars(keyword) >= TrigramMinChars;
        var ftsQuery = BuildFtsPhrase(keyword);

        return _db.ReadAsync(
            async (connection, ct) =>
            {
                IReadOnlyList<MessageRecord> rows = [];
                if (useTrigram)
                {
                    try
                    {
                        rows = await SqliteExecutor
                            .QueryAsync(
                                connection,
                                $"""
                                 SELECT {MessageColumnsAliased}
                                   FROM chat_messages_fts_tri f
                                   JOIN chat_messages m ON m.message_id = f.message_id
                                  WHERE chat_messages_fts_tri MATCH $q
                                  ORDER BY m.timestamp DESC
                                  LIMIT $limit;
                                 """,
                                [
                                    SqliteExecutor.Param("$q", ftsQuery),
                                    SqliteExecutor.Param("$limit", safeLimit),
                                ],
                                null,
                                MapMessage,
                                ct)
                            .ConfigureAwait(false);
                    }
                    catch (SqliteException)
                    {
                        // FTS 语法/索引异常一律降级到 LIKE：搜索是辅助功能，绝不能因查询语法中断界面。
                        rows = [];
                    }
                }

                if (rows.Count == 0)
                {
                    rows = await SqliteExecutor
                        .QueryAsync(
                            connection,
                            $"""
                             SELECT {MessageColumns} FROM chat_messages
                              WHERE content LIKE $pattern ESCAPE '\'
                              ORDER BY timestamp DESC, message_id DESC
                              LIMIT $limit;
                             """,
                            [
                                SqliteExecutor.Param("$pattern", $"%{EscapeLike(keyword)}%"),
                                SqliteExecutor.Param("$limit", safeLimit),
                            ],
                            null,
                            MapMessage,
                            ct)
                        .ConfigureAwait(false);
                }

                return (IReadOnlyList<MessageSearchHit>)rows
                    .Select(row => new MessageSearchHit(row, BuildSnippet(row.Content, keyword)))
                    .ToList();
            },
            cancellationToken);
    }

    /* ------------------------------------------------------------------ */
    /* 附件（§9.2）                                                        */
    /* ------------------------------------------------------------------ */

    /// <summary>
    /// 新增**草稿附件**（<c>message_id</c> 为 NULL）：图片在选中时就已复制进私有目录，
    /// 因此必须持久化，否则发送前退出会丢附件（FR-W-IMG-3 / FR-W-IMG-5）。
    /// </summary>
    public Task<AttachmentRecord> InsertDraftAttachmentAsync(
        string sessionId,
        string mimeType,
        string localPath,
        long fileSize,
        int? width = null,
        int? height = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(mimeType);
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);

        var attachmentId = Guid.NewGuid().ToString("D");
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        return _db.WriteAsync(
            async (connection, transaction, ct) =>
            {
                _ = await SqliteExecutor
                    .ExecuteAsync(
                        connection,
                        """
                        INSERT INTO chat_attachments
                          (attachment_id, message_id, session_id, mime_type, local_path,
                           file_size, width, height, upload_state, timestamp)
                        VALUES
                          ($id, NULL, $session, $mime, $path, $size, $width, $height, 'local', $ts);
                        """,
                        [
                            SqliteExecutor.Param("$id", attachmentId),
                            SqliteExecutor.Param("$session", sessionId),
                            SqliteExecutor.Param("$mime", mimeType),
                            SqliteExecutor.Param("$path", localPath),
                            SqliteExecutor.Param("$size", fileSize),
                            SqliteExecutor.Param("$width", width),
                            SqliteExecutor.Param("$height", height),
                            SqliteExecutor.Param("$ts", timestamp),
                        ],
                        transaction,
                        ct)
                    .ConfigureAwait(false);

                return new AttachmentRecord(
                    attachmentId,
                    null,
                    sessionId,
                    mimeType,
                    localPath,
                    fileSize,
                    width,
                    height,
                    "local",
                    timestamp);
            },
            cancellationToken);
    }

    /// <summary>把草稿附件挂到真实消息上（发送成功时调用）。返回受影响行数。</summary>
    public Task<int> AttachToMessageAsync(
        IReadOnlyList<string> attachmentIds,
        string messageId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attachmentIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);

        if (attachmentIds.Count == 0)
        {
            return Task.FromResult(0);
        }

        // 逐条参数化 UPDATE（不使用 IN (...) 字符串拼接，避免任何注入面）。
        return _db.WriteAsync(
            async (connection, transaction, ct) =>
            {
                var affected = 0;
                foreach (var attachmentId in attachmentIds)
                {
                    affected += await SqliteExecutor
                        .ExecuteAsync(
                            connection,
                            "UPDATE chat_attachments SET message_id = $message WHERE attachment_id = $id;",
                            [
                                SqliteExecutor.Param("$message", messageId),
                                SqliteExecutor.Param("$id", attachmentId),
                            ],
                            transaction,
                            ct)
                        .ConfigureAwait(false);
                }

                return affected;
            },
            cancellationToken);
    }

    /// <summary>某条消息的附件（按 <c>timestamp</c> 升序，即用户选择顺序）。</summary>
    public Task<IReadOnlyList<AttachmentRecord>> ListByMessageAsync(
        string messageId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        return _db.ReadAsync(
            (connection, ct) => SqliteExecutor.QueryAsync(
                connection,
                $"""
                 SELECT {AttachmentColumns} FROM chat_attachments
                  WHERE message_id = $id
                  ORDER BY timestamp ASC;
                 """,
                [SqliteExecutor.Param("$id", messageId)],
                null,
                MapAttachment,
                ct),
            cancellationToken);
    }

    /// <summary>当前草稿附件（<c>message_id IS NULL</c>），启动时恢复待发送图片用。</summary>
    public Task<IReadOnlyList<AttachmentRecord>> ListDraftsAsync(CancellationToken cancellationToken = default) =>
        _db.ReadAsync(
            (connection, ct) => SqliteExecutor.QueryAsync(
                connection,
                $"""
                 SELECT {AttachmentColumns} FROM chat_attachments
                  WHERE message_id IS NULL
                  ORDER BY timestamp ASC;
                 """,
                null,
                null,
                MapAttachment,
                ct),
            cancellationToken);

    /// <summary>删除指定附件行（磁盘文件由调用方按 <see cref="AttachmentRecord.LocalPath"/> 清理）。</summary>
    public Task<int> DeleteAttachmentAsync(string attachmentId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(attachmentId);
        return _db.WriteAsync(
            (connection, transaction, ct) => SqliteExecutor.ExecuteAsync(
                connection,
                "DELETE FROM chat_attachments WHERE attachment_id = $id;",
                [SqliteExecutor.Param("$id", attachmentId)],
                transaction,
                ct),
            cancellationToken);
    }

    /// <summary>附件占用统计（行数 / 字节 / 草稿数）。</summary>
    public async Task<AttachmentStats> StatsAsync(CancellationToken cancellationToken = default)
    {
        var rows = await _db.ReadAsync(
            (connection, ct) => SqliteExecutor.QueryAsync(
                connection,
                """
                SELECT COUNT(*),
                       COALESCE(SUM(file_size), 0),
                       COALESCE(SUM(CASE WHEN message_id IS NULL THEN 1 ELSE 0 END), 0)
                  FROM chat_attachments;
                """,
                null,
                null,
                static reader => new AttachmentStats(reader.GetInt32(0), reader.GetInt64(1), reader.GetInt32(2)),
                ct),
            cancellationToken).ConfigureAwait(false);

        return rows.Count == 0 ? new AttachmentStats(0, 0, 0) : rows[0];
    }

    /// <summary>
    /// 删除 <paramref name="days"/> 天前的附件行（EDGE-W-15 磁盘清理入口），返回被删行数与路径清单。
    /// 磁盘文件删除由调用方完成（本层不碰文件系统）。
    /// </summary>
    public Task<(int DeletedCount, IReadOnlyList<string> Paths)> DeleteOlderThanAsync(
        int days,
        CancellationToken cancellationToken = default)
    {
        var safeDays = Math.Max(1, days);
        var cutoff = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (safeDays * 24L * 3600 * 1000);

        return _db.WriteAsync(
            async (connection, transaction, ct) =>
            {
                var paths = await SqliteExecutor
                    .QueryAsync(
                        connection,
                        "SELECT local_path FROM chat_attachments WHERE timestamp < $cutoff;",
                        [SqliteExecutor.Param("$cutoff", cutoff)],
                        transaction,
                        static reader => reader.GetString(0),
                        ct)
                    .ConfigureAwait(false);

                var deleted = await SqliteExecutor
                    .ExecuteAsync(
                        connection,
                        "DELETE FROM chat_attachments WHERE timestamp < $cutoff;",
                        [SqliteExecutor.Param("$cutoff", cutoff)],
                        transaction,
                        ct)
                    .ConfigureAwait(false);

                return (deleted, (IReadOnlyList<string>)paths);
            },
            cancellationToken);
    }

    /// <summary>清理指向不存在消息的孤儿附件（正常流程不应出现，作为兜底）。草稿（NULL）不动。</summary>
    public Task<int> PruneOrphanAttachmentsAsync(CancellationToken cancellationToken = default) =>
        _db.WriteAsync(
            (connection, transaction, ct) => SqliteExecutor.ExecuteAsync(
                connection,
                """
                DELETE FROM chat_attachments
                 WHERE message_id IS NOT NULL
                   AND message_id NOT IN (SELECT message_id FROM chat_messages);
                """,
                null,
                transaction,
                ct),
            cancellationToken);

    /* ------------------------------------------------------------------ */
    /* 内部辅助                                                            */
    /* ------------------------------------------------------------------ */

    /// <summary>在已持有事务时复用的 upsert（避免嵌套 <c>WriteAsync</c> 自锁）。</summary>
    private static async Task UpsertWithinAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        MessageUpsert message,
        CancellationToken cancellationToken)
    {
        var existing = await SqliteExecutor
            .QuerySingleOrDefaultAsync(
                connection,
                $"SELECT {MessageColumns} FROM chat_messages WHERE message_id = $id;",
                [SqliteExecutor.Param("$id", message.MessageId)],
                transaction,
                MapMessage,
                cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            if (string.Equals(existing.Status, message.Status, StringComparison.Ordinal)
                && string.Equals(existing.Content, message.Content, StringComparison.Ordinal))
            {
                return;
            }

            _ = await SqliteExecutor
                .ExecuteAsync(
                    connection,
                    """
                    UPDATE chat_messages
                       SET status = $status,
                           content = $content,
                           error_code = $error,
                           model_provider = $model,
                           content_type = $ctype,
                           message_type = $mtype
                     WHERE message_id = $id;
                    """,
                    [
                        SqliteExecutor.Param("$status", message.Status),
                        SqliteExecutor.Param("$content", message.Content),
                        SqliteExecutor.Param("$error", message.ErrorCode),
                        SqliteExecutor.Param("$model", message.ModelProvider),
                        SqliteExecutor.Param("$ctype", message.ContentType),
                        SqliteExecutor.Param("$mtype", message.MessageType),
                        SqliteExecutor.Param("$id", message.MessageId),
                    ],
                    transaction,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        _ = await SqliteExecutor
            .ExecuteAsync(
                connection,
                """
                INSERT INTO chat_messages
                  (message_id, session_id, role, message_type, content_type, model_provider, content, status, timestamp, error_code)
                VALUES
                  ($id, $session, $role, $mtype, $ctype, $model, $content, $status, $ts, $error);
                """,
                [
                    SqliteExecutor.Param("$id", message.MessageId),
                    SqliteExecutor.Param("$session", message.SessionId),
                    SqliteExecutor.Param("$role", message.Role),
                    SqliteExecutor.Param("$mtype", message.MessageType),
                    SqliteExecutor.Param("$ctype", message.ContentType),
                    SqliteExecutor.Param("$model", message.ModelProvider),
                    SqliteExecutor.Param("$content", message.Content),
                    SqliteExecutor.Param("$status", message.Status),
                    SqliteExecutor.Param("$ts", message.Timestamp),
                    SqliteExecutor.Param("$error", message.ErrorCode),
                ],
                transaction,
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal static MessageRecord MapMessage(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        SqliteExecutor.NullableString(reader, 5),
        reader.GetString(6),
        reader.GetString(7),
        reader.GetInt64(8),
        SqliteExecutor.NullableString(reader, 9));

    internal static AttachmentRecord MapAttachment(SqliteDataReader reader) => new(
        reader.GetString(0),
        SqliteExecutor.NullableString(reader, 1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetInt64(5),
        SqliteExecutor.NullableInt32(reader, 6),
        SqliteExecutor.NullableInt32(reader, 7),
        reader.GetString(8),
        reader.GetInt64(9));

    /// <summary>按 Unicode 码点计数（emoji / 代理对按 1 个字符计），用于 trigram 的 ≥3 判定。</summary>
    private static int CountChars(string value)
    {
        var count = 0;
        foreach (var _ in value.EnumerateRunes())
        {
            count++;
        }

        return count;
    }

    /// <summary>FTS5 短语查询：整体加双引号，内部 <c>"</c> 翻倍。</summary>
    private static string BuildFtsPhrase(string keyword) =>
        string.Concat("\"", keyword.Replace("\"", "\"\"", StringComparison.Ordinal), "\"");

    /// <summary>LIKE 转义：<c>\</c> <c>%</c> <c>_</c> 前置反斜杠（配合 <c>ESCAPE '\'</c>）。</summary>
    private static string EscapeLike(string keyword)
    {
        var builder = new StringBuilder(keyword.Length + 8);
        foreach (var ch in keyword)
        {
            if (ch is '\\' or '%' or '_')
            {
                _ = builder.Append('\\');
            }

            _ = builder.Append(ch);
        }

        return builder.ToString();
    }

    /// <summary>围绕首个命中位置取上下文片段；换行压成空格，便于列表单行显示。未命中时取头部。</summary>
    private static string BuildSnippet(string content, string keyword)
    {
        var flattened = content.Replace('\r', ' ').Replace('\n', ' ');
        var index = flattened.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            index = 0;
        }

        var start = Math.Max(0, index - SnippetRadius);
        var length = Math.Min(flattened.Length - start, (SnippetRadius * 2) + keyword.Length);
        var snippet = flattened.Substring(start, length);

        if (start > 0)
        {
            snippet = "…" + snippet;
        }

        if (start + length < flattened.Length)
        {
            snippet += "…";
        }

        return snippet;
    }
}
