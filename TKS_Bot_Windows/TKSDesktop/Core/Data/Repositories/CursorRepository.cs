using Microsoft.Data.Sqlite;
using TKSDesktop.Contracts;

namespace TKSDesktop.Core.Data.Repositories;

/// <summary>
/// <c>sync_cursors</c> 与 <c>delivered_bot_messages</c> 仓储（PRD §9.1 的两张小表）。
///
/// <para><b>两个独立游标（FR-W-SYNC-3 / FR-W-SYNC-7）</b>：聊天历史与记忆事实各用一条游标，
/// key 分别为 <see cref="ProtocolConstants.CursorKeyChat"/> 与 <see cref="ProtocolConstants.CursorKeyMemory"/>。
/// 共用一个游标会导致先同步完的一侧把另一侧的 since 推到未来，从而**永久漏拉**数据。</para>
///
/// <para><b>已投递去重（FR-W-INT-12 / EDGE-W-21）</b>：互动与合并回复可能让同一条 Bot 消息重复到达，
/// 用 <c>INSERT OR IGNORE</c> 记录已处理过的 messageId。</para>
/// </summary>
public sealed class CursorRepository
{
    /// <summary>去重记录保留天数。</summary>
    public const int DeliveredRetentionDays = 30;

    private readonly LocalDb _db;

    /// <summary>构造。</summary>
    public CursorRepository(LocalDb db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    public Task<string?> GetHistoryStateAsync(string key, CancellationToken ct = default)
        => _db.ReadAsync(async (connection, token) =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM history_sync_state WHERE key=$key";
            command.Parameters.AddWithValue("$key", key);
            return (await command.ExecuteScalarAsync(token).ConfigureAwait(false)) as string;
        }, ct);

    public Task SetHistoryStateAsync(string key, string value, CancellationToken ct = default)
        => _db.WriteAsync((connection, transaction, token) => SqliteExecutor.ExecuteAsync(connection,
            "INSERT INTO history_sync_state VALUES ($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value",
            [SqliteExecutor.Param("$key", key), SqliteExecutor.Param("$value", value)], transaction, token), ct);

    /// <summary>读游标；不存在返回 0。key 用 <c>ProtocolConstants.CursorKey*</c>。</summary>
    public Task<long> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return _db.ReadAsync(
            (connection, ct) => SqliteExecutor.ScalarLongAsync(
                connection,
                "SELECT COALESCE((SELECT value FROM sync_cursors WHERE key = $key), 0);",
                [SqliteExecutor.Param("$key", key)],
                null,
                ct),
            cancellationToken);
    }

    /// <summary>写游标（<c>ON CONFLICT (key) DO UPDATE SET value = excluded.value</c>）。</summary>
    public Task SetAsync(string key, long value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return _db.WriteAsync(
            (connection, transaction, ct) => SqliteExecutor.ExecuteAsync(
                connection,
                """
                INSERT INTO sync_cursors (key, value) VALUES ($key, $value)
                ON CONFLICT (key) DO UPDATE SET value = excluded.value;
                """,
                [
                    SqliteExecutor.Param("$key", key),
                    SqliteExecutor.Param("$value", value),
                ],
                transaction,
                ct),
            cancellationToken);
    }

    /// <summary>聊天历史游标（<c>chat</c>）。</summary>
    public Task<long> GetChatCursorAsync(CancellationToken cancellationToken = default) =>
        GetAsync(ProtocolConstants.CursorKeyChat, cancellationToken);

    /// <summary>写聊天历史游标。</summary>
    public Task SetChatCursorAsync(long value, CancellationToken cancellationToken = default) =>
        SetAsync(ProtocolConstants.CursorKeyChat, value, cancellationToken);

    /// <summary>记忆事实游标（<c>memory</c>），与聊天游标**互相独立**。</summary>
    public Task<long> GetMemoryCursorAsync(CancellationToken cancellationToken = default) =>
        GetAsync(ProtocolConstants.CursorKeyMemory, cancellationToken);

    /// <summary>写记忆事实游标。</summary>
    public Task SetMemoryCursorAsync(long value, CancellationToken cancellationToken = default) =>
        SetAsync(ProtocolConstants.CursorKeyMemory, value, cancellationToken);

    /// <summary>
    /// 增量同步起点：<c>max(本地最大 timestamp, 游标)</c>（FR-W-SYNC-3 的 <c>since</c>）。
    /// 两者取大值，既覆盖游标领先（清空后推进过）也覆盖本地已有更新数据的情况。
    /// </summary>
    public async Task<long> ResolveSinceAsync(
        string cursorKey,
        long localMaxTimestamp,
        CancellationToken cancellationToken = default)
    {
        var cursor = await GetAsync(cursorKey, cancellationToken).ConfigureAwait(false);
        return Math.Max(cursor, localMaxTimestamp);
    }

    /// <summary>标记已投递（<c>INSERT OR IGNORE</c>：重复标记不报错，也不覆盖原时间戳）。</summary>
    public Task MarkDeliveredAsync(string messageId, long timestamp, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        return _db.WriteAsync(
            (connection, transaction, ct) => SqliteExecutor.ExecuteAsync(
                connection,
                "INSERT OR IGNORE INTO delivered_bot_messages (message_id, timestamp) VALUES ($id, $ts);",
                [
                    SqliteExecutor.Param("$id", messageId),
                    SqliteExecutor.Param("$ts", timestamp),
                ],
                transaction,
                ct),
            cancellationToken);
    }

    /// <summary>批量标记已投递（同事务，用于一次回复的多段消息）。</summary>
    public Task<int> MarkDeliveredAsync(
        IReadOnlyList<string> messageIds,
        long timestamp,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messageIds);
        if (messageIds.Count == 0)
        {
            return Task.FromResult(0);
        }

        return _db.WriteAsync(
            async (connection, transaction, ct) =>
            {
                var inserted = 0;
                foreach (var messageId in messageIds)
                {
                    inserted += await SqliteExecutor
                        .ExecuteAsync(
                            connection,
                            "INSERT OR IGNORE INTO delivered_bot_messages (message_id, timestamp) VALUES ($id, $ts);",
                            [
                                SqliteExecutor.Param("$id", messageId),
                                SqliteExecutor.Param("$ts", timestamp),
                            ],
                            transaction,
                            ct)
                        .ConfigureAwait(false);
                }

                return inserted;
            },
            cancellationToken);
    }

    /// <summary>是否已投递（去重判定：为真则**不得**再渲染/通知）。</summary>
    public Task<bool> IsDeliveredAsync(string messageId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        return _db.ReadAsync(
            async (connection, ct) =>
            {
                var count = await SqliteExecutor
                    .ScalarIntAsync(
                        connection,
                        "SELECT COUNT(*) FROM delivered_bot_messages WHERE message_id = $id;",
                        [SqliteExecutor.Param("$id", messageId)],
                        null,
                        ct)
                    .ConfigureAwait(false);
                return count > 0;
            },
            cancellationToken);
    }

    /// <summary>清理 30 天前的去重记录，避免表无限增长。返回被删行数。</summary>
    public Task<int> PruneDeliveredAsync(
        int retentionDays = DeliveredRetentionDays,
        CancellationToken cancellationToken = default)
    {
        var safeDays = Math.Max(1, retentionDays);
        var cutoff = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (safeDays * 24L * 3600 * 1000);
        return _db.WriteAsync(
            (connection, transaction, ct) => SqliteExecutor.ExecuteAsync(
                connection,
                "DELETE FROM delivered_bot_messages WHERE timestamp < $cutoff;",
                [SqliteExecutor.Param("$cutoff", cutoff)],
                transaction,
                ct),
            cancellationToken);
    }

    /// <summary>去重记录条数（自检/对账用）。</summary>
    public Task<int> DeliveredCountAsync(CancellationToken cancellationToken = default) =>
        _db.ReadAsync(
            (connection, ct) => SqliteExecutor.ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM delivered_bot_messages;",
                null,
                null,
                ct),
            cancellationToken);
}
