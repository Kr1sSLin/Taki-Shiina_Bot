using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using TKSDesktop.Core.Data.Records;

namespace TKSDesktop.Core.Data.Repositories;

/// <summary>
/// <c>bot_notifications</c> 仓储（PRD §9.3，服务端 <c>bot.error</c> 落库与设置页展示）。
///
/// <para>幂等键由 <c>错误码 + 时间戳 + 正文</c> 派生（SHA-1 前 20 位十六进制），
/// 并用 <c>ON CONFLICT (notification_id) DO NOTHING</c>：同一次错误重复到达时**不覆盖**
/// 已有的已读状态（否则用户刚读过的通知会重新变成未读）。</para>
/// </summary>
public sealed class NotificationRepository
{
    private const string Columns = "notification_id, error_code, message, is_read, timestamp";

    /// <summary>列表默认条数上限。</summary>
    public const int DefaultLimit = 200;

    private readonly LocalDb _db;

    /// <summary>构造。</summary>
    public NotificationRepository(LocalDb db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    /// <summary>
    /// 落库一条 Bot 错误通知。返回通知 ID。
    /// <paramref name="notificationId"/> 为 <see langword="null"/> 时按
    /// <c>err_{sha1(errorCode|timestamp|message)[0..20]}</c> 派生。
    /// </summary>
    public async Task<string> InsertAsync(
        string errorCode,
        string message,
        long timestamp,
        string? notificationId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        ArgumentNullException.ThrowIfNull(message);

        var id = string.IsNullOrWhiteSpace(notificationId)
            ? DeriveId(errorCode, timestamp, message)
            : notificationId;

        await _db.WriteAsync(
            (connection, transaction, ct) => SqliteExecutor.ExecuteAsync(
                connection,
                """
                INSERT INTO bot_notifications (notification_id, error_code, message, is_read, timestamp)
                VALUES ($id, $code, $message, 0, $ts)
                ON CONFLICT (notification_id) DO NOTHING;
                """,
                [
                    SqliteExecutor.Param("$id", id),
                    SqliteExecutor.Param("$code", errorCode),
                    SqliteExecutor.Param("$message", message),
                    SqliteExecutor.Param("$ts", timestamp),
                ],
                transaction,
                ct),
            cancellationToken).ConfigureAwait(false);

        return id;
    }

    /// <summary>按时间倒序列出通知。</summary>
    public Task<IReadOnlyList<NotificationRecord>> ListAsync(
        int limit = DefaultLimit,
        CancellationToken cancellationToken = default)
    {
        var safeLimit = Math.Clamp(limit, 1, 1000);
        return _db.ReadAsync(
            (connection, ct) => SqliteExecutor.QueryAsync(
                connection,
                $"SELECT {Columns} FROM bot_notifications ORDER BY timestamp DESC LIMIT $limit;",
                [SqliteExecutor.Param("$limit", safeLimit)],
                null,
                Map,
                ct),
            cancellationToken);
    }

    /// <summary>未读数（设置页红点）。</summary>
    public Task<int> UnreadCountAsync(CancellationToken cancellationToken = default) =>
        _db.ReadAsync(
            (connection, ct) => SqliteExecutor.ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM bot_notifications WHERE is_read = 0;",
                null,
                null,
                ct),
            cancellationToken);

    /// <summary>把给定 ID 标为已读。返回受影响行数。</summary>
    public Task<int> MarkReadAsync(
        IReadOnlyList<string> notificationIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notificationIds);
        if (notificationIds.Count == 0)
        {
            return Task.FromResult(0);
        }

        // 逐条参数化 UPDATE（不拼接 IN 列表）。
        return _db.WriteAsync(
            async (connection, transaction, ct) =>
            {
                var affected = 0;
                foreach (var id in notificationIds)
                {
                    affected += await SqliteExecutor
                        .ExecuteAsync(
                            connection,
                            "UPDATE bot_notifications SET is_read = 1 WHERE notification_id = $id;",
                            [SqliteExecutor.Param("$id", id)],
                            transaction,
                            ct)
                        .ConfigureAwait(false);
                }

                return affected;
            },
            cancellationToken);
    }

    /// <summary>全部标为已读。返回受影响行数（仅统计原本未读的）。</summary>
    public Task<int> MarkAllReadAsync(CancellationToken cancellationToken = default) =>
        _db.WriteAsync(
            (connection, transaction, ct) => SqliteExecutor.ExecuteAsync(
                connection,
                "UPDATE bot_notifications SET is_read = 1 WHERE is_read = 0;",
                null,
                transaction,
                ct),
            cancellationToken);

    /// <summary>通知 ID 派生（与服务端错误码 + 时间戳 + 正文一一对应，天然幂等）。</summary>
    internal static string DeriveId(string errorCode, long timestamp, string message)
    {
        var payload = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{errorCode}|{timestamp}|{message}");
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(payload));
        return $"err_{Convert.ToHexString(hash)[..20].ToLowerInvariant()}";
    }

    private static NotificationRecord Map(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        SqliteExecutor.AsBool(reader.GetInt64(3)),
        reader.GetInt64(4));
}
