using Microsoft.Data.Sqlite;
using TKSDesktop.Core.Data.Records;

namespace TKSDesktop.Core.Data.Repositories;

/// <summary>
/// <c>reminders</c> 仓储（PRD §9.5，FR-W-REM-4）。
///
/// <para>Windows 端没有 WorkManager 等价能力，提醒必须自行落库并在重启后重新装载。</para>
///
/// <para><b>KEEP 语义（FR-W-REM-3）</b>：<see cref="UpsertReminderAsync"/> 对已存在的
/// <c>reminder_id</c> **保留原计划不覆盖**（对齐 Android <c>ExistingWorkPolicy.KEEP</c>）。
/// 尤其不能把已 <c>fired</c> / <c>cancelled</c> 的提醒复活成 <c>pending</c>——
/// 否则用户取消过的提醒会在下次连上服务端时被重新触发。</para>
/// </summary>
public sealed class ReminderRepository
{
    private const string Columns = "reminder_id, target_time, fire_at, text, status, created_at";

    /// <summary><c>pending</c> 状态值。</summary>
    public const string StatusPending = "pending";

    /// <summary><c>fired</c> 状态值（已触发）。</summary>
    public const string StatusFired = "fired";

    /// <summary><c>cancelled</c> 状态值。</summary>
    public const string StatusCancelled = "cancelled";

    /// <summary>终态提醒保留天数，超期清理。</summary>
    private const int TerminalRetentionDays = 30;

    /// <summary>设置页列表默认条数上限。</summary>
    public const int DefaultLimit = 200;

    private readonly LocalDb _db;

    /// <summary>构造。</summary>
    public ReminderRepository(LocalDb db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    /// <summary>
    /// 写入提醒，**KEEP 语义**：同 <c>reminder_id</c> 已存在则原样返回已有行，不做任何覆盖。
    /// </summary>
    public Task<ReminderUpsertResult> UpsertReminderAsync(
        string reminderId,
        string targetTime,
        long fireAt,
        string text,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reminderId);

        return _db.WriteAsync(async (connection, transaction, ct) =>
        {
            var existing = await SqliteExecutor
                .QuerySingleOrDefaultAsync(
                    connection,
                    $"SELECT {Columns} FROM reminders WHERE reminder_id = $id;",
                    [SqliteExecutor.Param("$id", reminderId)],
                    transaction,
                    Map,
                    ct)
                .ConfigureAwait(false);

            if (existing is not null)
            {
                // KEEP：保留原计划（含 fired / cancelled 的终态），不复活、不改时间。
                return new ReminderUpsertResult(false, existing);
            }

            var createdAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _ = await SqliteExecutor
                .ExecuteAsync(
                    connection,
                    """
                    INSERT INTO reminders (reminder_id, target_time, fire_at, text, status, created_at)
                    VALUES ($id, $target, $fire, $text, 'pending', $created);
                    """,
                    [
                        SqliteExecutor.Param("$id", reminderId),
                        SqliteExecutor.Param("$target", targetTime),
                        SqliteExecutor.Param("$fire", fireAt),
                        SqliteExecutor.Param("$text", text),
                        SqliteExecutor.Param("$created", createdAt),
                    ],
                    transaction,
                    ct)
                .ConfigureAwait(false);

            return new ReminderUpsertResult(
                true,
                new ReminderRecord(reminderId, targetTime, fireAt, text, StatusPending, createdAt));
        }, cancellationToken);
    }

    /// <summary>全部待触发提醒，按触发时刻升序（启动时重新排程）。</summary>
    public Task<IReadOnlyList<ReminderRecord>> ListPendingAsync(CancellationToken cancellationToken = default) =>
        _db.ReadAsync(
            (connection, ct) => SqliteExecutor.QueryAsync(
                connection,
                $"SELECT {Columns} FROM reminders WHERE status = 'pending' ORDER BY fire_at ASC;",
                null,
                null,
                Map,
                ct),
            cancellationToken);

    /// <summary>设置页列表：按触发时刻倒序取前 <paramref name="limit"/> 条（含终态）。</summary>
    public Task<IReadOnlyList<ReminderRecord>> ListPendingForSettingsAsync(
        int limit = DefaultLimit,
        CancellationToken cancellationToken = default)
    {
        var safeLimit = Math.Clamp(limit, 1, 1000);
        return _db.ReadAsync(
            (connection, ct) => SqliteExecutor.QueryAsync(
                connection,
                $"SELECT {Columns} FROM reminders ORDER BY fire_at DESC LIMIT $limit;",
                [SqliteExecutor.Param("$limit", safeLimit)],
                null,
                Map,
                ct),
            cancellationToken);
    }

    /// <summary>按主键取单条。</summary>
    public Task<ReminderRecord?> GetAsync(string reminderId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reminderId);
        return _db.ReadAsync(
            (connection, ct) => SqliteExecutor.QuerySingleOrDefaultAsync(
                connection,
                $"SELECT {Columns} FROM reminders WHERE reminder_id = $id;",
                [SqliteExecutor.Param("$id", reminderId)],
                null,
                Map,
                ct),
            cancellationToken);
    }

    /// <summary>标记已触发（仅当当前为 <c>pending</c>，避免重复触发覆盖终态）。返回是否生效。</summary>
    public Task<bool> MarkFiredAsync(string reminderId, CancellationToken cancellationToken = default) =>
        SetTerminalStatusAsync(reminderId, StatusFired, cancellationToken);

    /// <summary>取消提醒（仅当当前为 <c>pending</c>）。返回是否生效。</summary>
    public Task<bool> CancelAsync(string reminderId, CancellationToken cancellationToken = default) =>
        SetTerminalStatusAsync(reminderId, StatusCancelled, cancellationToken);

    /// <summary>清理已结束（非 <c>pending</c>）且 30 天前的提醒，返回被删行数。</summary>
    public Task<int> PruneFinishedAsync(CancellationToken cancellationToken = default)
    {
        var cutoff = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (TerminalRetentionDays * 24L * 3600 * 1000);
        return _db.WriteAsync(
            (connection, transaction, ct) => SqliteExecutor.ExecuteAsync(
                connection,
                "DELETE FROM reminders WHERE status <> 'pending' AND created_at < $cutoff;",
                [SqliteExecutor.Param("$cutoff", cutoff)],
                transaction,
                ct),
            cancellationToken);
    }

    /// <summary>待触发提醒条数。</summary>
    public Task<int> PendingCountAsync(CancellationToken cancellationToken = default) =>
        _db.ReadAsync(
            (connection, ct) => SqliteExecutor.ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM reminders WHERE status = 'pending';",
                null,
                null,
                ct),
            cancellationToken);

    private Task<bool> SetTerminalStatusAsync(
        string reminderId,
        string status,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reminderId);

        return _db.WriteAsync(
            async (connection, transaction, ct) =>
            {
                var affected = await SqliteExecutor
                    .ExecuteAsync(
                        connection,
                        """
                        UPDATE reminders
                           SET status = $status
                         WHERE reminder_id = $id AND status = 'pending';
                        """,
                        [
                            SqliteExecutor.Param("$status", status),
                            SqliteExecutor.Param("$id", reminderId),
                        ],
                        transaction,
                        ct)
                    .ConfigureAwait(false);

                return affected > 0;
            },
            cancellationToken);
    }

    private static ReminderRecord Map(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetInt64(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetInt64(5));
}
