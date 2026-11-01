using Microsoft.Data.Sqlite;
using TKSDesktop.Contracts.Dtos;
using TKSDesktop.Core.Data.Records;

namespace TKSDesktop.Core.Data.Repositories;

/// <summary>
/// 积分 / 等级 / 补签卡 离线缓存仓储（PRD §9.6）。
///
/// <para>⚠️ <b>FR-W-PROG-1</b>：这五张表**仅为缓存**，任何一次写操作都**不是**业务事实来源。
/// 余额与等级只能由服务端下发值覆盖，禁止本地推算后当作真相展示。</para>
///
/// <para>⚠️ <b>FR-W-DB-4</b>：列名风格与 §7.0 字段名契约一致，**不做统一驼峰转换**：
/// <c>user_progress_cache</c> / <c>makeup_candidates_cache</c> 为 camelCase 语义；
/// <c>points_ledger_cache</c> / <c>makeup_cards_cache</c> 为 snake_case（直接映射服务端 DTO）。</para>
/// </summary>
public sealed class ProgressCacheRepository
{
    private const string ProgressColumns =
        "user_id, balance, balance_updated_at, level_code, level_name, prev_level_code, highest_level_code, " +
        "continuous_days, next_level_code, next_level_name, next_level_threshold_days, days_to_next_level, " +
        "last_valid_date, gap_days, break_deadline_date, is_default_level, level_updated_at, " +
        "available_makeup_cards, synced_at";

    private const string LedgerColumns =
        "id, user_id, change_amount, reason_code, balance_after, related_item_id, idempotency_key, created_at, business_date";

    private const string CardColumns =
        "id, user_id, granted_month, status, used_for_date, used_at, created_at";

    /// <summary>流水缓存默认条数上限。</summary>
    public const int DefaultLedgerLimit = 300;

    /// <summary>补签卡缓存默认条数上限。</summary>
    public const int DefaultCardLimit = 200;

    private readonly LocalDb _db;

    /// <summary>构造。</summary>
    public ProgressCacheRepository(LocalDb db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    /* ------------------------------------------------------------------ */
    /* user_progress_cache                                                 */
    /* ------------------------------------------------------------------ */

    /// <summary>
    /// 按 <c>GET /points/overview</c> 语义**整体覆盖**写入（登录 / 重连 / 收到事件时调用，FR-W-PT-5）。
    /// 未下发的可选字段写为 NULL 或该列的默认值，不做「保留旧值」的猜测。
    /// </summary>
    public Task SaveProgressAsync(
        string userId,
        PointsOverviewDataDto overview,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentNullException.ThrowIfNull(overview);

        var level = overview.Level;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var balanceUpdatedAt = overview.BalanceUpdatedAt != 0 ? overview.BalanceUpdatedAt : now;
        var availableCards = level?.AvailableMakeupCards ?? overview.MakeupCard?.Available ?? 0;

        return _db.WriteAsync(
            (connection, transaction, ct) => SqliteExecutor.ExecuteAsync(
                connection,
                """
                INSERT INTO user_progress_cache (
                    user_id, balance, balance_updated_at, level_code, level_name, prev_level_code,
                    highest_level_code, continuous_days, next_level_code, next_level_name,
                    next_level_threshold_days, days_to_next_level, last_valid_date, gap_days,
                    break_deadline_date, is_default_level, level_updated_at, available_makeup_cards, synced_at)
                VALUES (
                    $user, $balance, $balanceAt, $levelCode, $levelName, $prev,
                    $highest, $days, $nextCode, $nextName,
                    $nextThreshold, $toNext, $lastValid, $gap,
                    $breakDeadline, $isDefault, $levelAt, $cards, $syncedAt)
                ON CONFLICT (user_id) DO UPDATE SET
                    balance = excluded.balance,
                    balance_updated_at = excluded.balance_updated_at,
                    level_code = excluded.level_code,
                    level_name = excluded.level_name,
                    prev_level_code = excluded.prev_level_code,
                    highest_level_code = excluded.highest_level_code,
                    continuous_days = excluded.continuous_days,
                    next_level_code = excluded.next_level_code,
                    next_level_name = excluded.next_level_name,
                    next_level_threshold_days = excluded.next_level_threshold_days,
                    days_to_next_level = excluded.days_to_next_level,
                    last_valid_date = excluded.last_valid_date,
                    gap_days = excluded.gap_days,
                    break_deadline_date = excluded.break_deadline_date,
                    is_default_level = excluded.is_default_level,
                    level_updated_at = excluded.level_updated_at,
                    available_makeup_cards = excluded.available_makeup_cards,
                    synced_at = excluded.synced_at;
                """,
                [
                    SqliteExecutor.Param("$user", userId),
                    SqliteExecutor.Param("$balance", overview.Balance),
                    SqliteExecutor.Param("$balanceAt", balanceUpdatedAt),
                    SqliteExecutor.Param("$levelCode", level?.LevelCode ?? string.Empty),
                    SqliteExecutor.Param("$levelName", level?.LevelName ?? string.Empty),
                    SqliteExecutor.Param("$prev", level?.PrevLevelCode),
                    SqliteExecutor.Param("$highest", level?.HighestLevelCode),
                    SqliteExecutor.Param("$days", level?.ContinuousDays ?? 0),
                    SqliteExecutor.Param("$nextCode", level?.NextLevelCode),
                    SqliteExecutor.Param("$nextName", level?.NextLevelName),
                    SqliteExecutor.Param("$nextThreshold", level?.NextLevelThresholdDays),
                    SqliteExecutor.Param("$toNext", level?.DaysToNextLevel),
                    SqliteExecutor.Param("$lastValid", level?.LastValidDate),
                    SqliteExecutor.Param("$gap", level?.GapDays ?? 0),
                    SqliteExecutor.Param("$breakDeadline", level?.BreakDeadlineDate),
                    SqliteExecutor.Param("$isDefault", level?.IsDefaultLevel == true ? 1 : 0),
                    SqliteExecutor.Param("$levelAt", level?.LevelUpdatedAt),
                    SqliteExecutor.Param("$cards", availableCards),
                    SqliteExecutor.Param("$syncedAt", now),
                ],
                transaction,
                ct),
            cancellationToken);
    }

    /// <summary>读进度缓存；无记录返回 <see langword="null"/>（调用方须回退到服务端拉取）。</summary>
    public Task<ProgressCacheRecord?> GetProgressAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        return _db.ReadAsync(
            (connection, ct) => SqliteExecutor.QuerySingleOrDefaultAsync(
                connection,
                $"SELECT {ProgressColumns} FROM user_progress_cache WHERE user_id = $user;",
                [SqliteExecutor.Param("$user", userId)],
                null,
                MapProgress,
                ct),
            cancellationToken);
    }

    /// <summary>事件驱动的最小覆盖：仅余额（<c>points.changed</c> / <c>points.snapshot</c>）。</summary>
    public Task PatchBalanceAsync(
        string userId,
        int balance,
        long updatedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        return _db.WriteAsync(
            (connection, transaction, ct) => SqliteExecutor.ExecuteAsync(
                connection,
                """
                INSERT INTO user_progress_cache (user_id, balance, balance_updated_at, synced_at)
                VALUES ($user, $balance, $at, $syncedAt)
                ON CONFLICT (user_id) DO UPDATE SET
                    balance = excluded.balance,
                    balance_updated_at = excluded.balance_updated_at,
                    synced_at = excluded.synced_at;
                """,
                [
                    SqliteExecutor.Param("$user", userId),
                    SqliteExecutor.Param("$balance", balance),
                    SqliteExecutor.Param("$at", updatedAt),
                    SqliteExecutor.Param("$syncedAt", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
                ],
                transaction,
                ct),
            cancellationToken);
    }

    /// <summary>事件驱动的最小覆盖：仅可用补签卡数（<c>makeup_card.changed</c>，FR-W-MC-8）。</summary>
    public Task PatchMakeupAvailableAsync(
        string userId,
        int available,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        return _db.WriteAsync(
            (connection, transaction, ct) => SqliteExecutor.ExecuteAsync(
                connection,
                """
                INSERT INTO user_progress_cache (user_id, available_makeup_cards, synced_at)
                VALUES ($user, $available, $syncedAt)
                ON CONFLICT (user_id) DO UPDATE SET
                    available_makeup_cards = excluded.available_makeup_cards,
                    synced_at = excluded.synced_at;
                """,
                [
                    SqliteExecutor.Param("$user", userId),
                    SqliteExecutor.Param("$available", available),
                    SqliteExecutor.Param("$syncedAt", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
                ],
                transaction,
                ct),
            cancellationToken);
    }

    /* ------------------------------------------------------------------ */
    /* points_ledger_cache（snake_case 列，FR-W-DB-4）                      */
    /* ------------------------------------------------------------------ */

    /// <summary>
    /// 缓存一页流水。主键是**服务端给的 <c>id</c>**（不可自增替换，否则分页会错位）。
    /// 返回实际写入行数（含更新）。
    /// </summary>
    public Task<int> SaveLedgerAsync(
        IReadOnlyList<LedgerCacheRecord> entries,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
        {
            return Task.FromResult(0);
        }

        return _db.WriteAsync(
            async (connection, transaction, ct) =>
            {
                var affected = 0;
                foreach (var entry in entries)
                {
                    affected += await SqliteExecutor
                        .ExecuteAsync(
                            connection,
                            """
                            INSERT INTO points_ledger_cache
                              (id, user_id, change_amount, reason_code, balance_after,
                               related_item_id, idempotency_key, created_at, business_date)
                            VALUES
                              ($id, $user, $amount, $reason, $after, $related, $key, $created, $date)
                            ON CONFLICT (id) DO UPDATE SET
                              change_amount = excluded.change_amount,
                              reason_code = excluded.reason_code,
                              balance_after = excluded.balance_after,
                              related_item_id = excluded.related_item_id,
                              idempotency_key = excluded.idempotency_key,
                              created_at = excluded.created_at,
                              business_date = excluded.business_date;
                            """,
                            [
                                SqliteExecutor.Param("$id", entry.Id),
                                SqliteExecutor.Param("$user", entry.UserId),
                                SqliteExecutor.Param("$amount", entry.ChangeAmount),
                                SqliteExecutor.Param("$reason", entry.ReasonCode),
                                SqliteExecutor.Param("$after", entry.BalanceAfter),
                                SqliteExecutor.Param("$related", entry.RelatedItemId),
                                SqliteExecutor.Param("$key", entry.IdempotencyKey),
                                SqliteExecutor.Param("$created", entry.CreatedAt),
                                SqliteExecutor.Param("$date", entry.BusinessDate),
                            ],
                            transaction,
                            ct)
                        .ConfigureAwait(false);
                }

                return affected;
            },
            cancellationToken);
    }

    /// <summary>读流水缓存（按 <c>created_at</c> 倒序）。</summary>
    public Task<IReadOnlyList<LedgerCacheRecord>> GetLedgerAsync(
        int limit = DefaultLedgerLimit,
        CancellationToken cancellationToken = default)
    {
        var safeLimit = Math.Clamp(limit, 1, 2000);
        return _db.ReadAsync(
            (connection, ct) => SqliteExecutor.QueryAsync(
                connection,
                $"SELECT {LedgerColumns} FROM points_ledger_cache ORDER BY created_at DESC LIMIT $limit;",
                [SqliteExecutor.Param("$limit", safeLimit)],
                null,
                MapLedger,
                ct),
            cancellationToken);
    }

    /* ------------------------------------------------------------------ */
    /* makeup_cards_cache（snake_case 列，FR-W-DB-4）                       */
    /* ------------------------------------------------------------------ */

    /// <summary>缓存补签卡记录；已存在则只更新状态相关列（发卡月份与创建时刻是服务端事实，不覆盖）。</summary>
    public Task<int> SaveMakeupCardsAsync(
        IReadOnlyList<MakeupCardCacheRecord> cards,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cards);
        if (cards.Count == 0)
        {
            return Task.FromResult(0);
        }

        return _db.WriteAsync(
            async (connection, transaction, ct) =>
            {
                var affected = 0;
                foreach (var card in cards)
                {
                    affected += await SqliteExecutor
                        .ExecuteAsync(
                            connection,
                            """
                            INSERT INTO makeup_cards_cache
                              (id, user_id, granted_month, status, used_for_date, used_at, created_at)
                            VALUES
                              ($id, $user, $month, $status, $usedFor, $usedAt, $created)
                            ON CONFLICT (id) DO UPDATE SET
                              status = excluded.status,
                              used_for_date = excluded.used_for_date,
                              used_at = excluded.used_at;
                            """,
                            [
                                SqliteExecutor.Param("$id", card.Id),
                                SqliteExecutor.Param("$user", card.UserId),
                                SqliteExecutor.Param("$month", card.GrantedMonth),
                                SqliteExecutor.Param("$status", card.Status),
                                SqliteExecutor.Param("$usedFor", card.UsedForDate),
                                SqliteExecutor.Param("$usedAt", card.UsedAt),
                                SqliteExecutor.Param("$created", card.CreatedAt),
                            ],
                            transaction,
                            ct)
                        .ConfigureAwait(false);
                }

                return affected;
            },
            cancellationToken);
    }

    /// <summary>读补签卡缓存（按 <c>created_at</c> 倒序）。</summary>
    public Task<IReadOnlyList<MakeupCardCacheRecord>> GetMakeupCardsAsync(
        int limit = DefaultCardLimit,
        CancellationToken cancellationToken = default)
    {
        var safeLimit = Math.Clamp(limit, 1, 2000);
        return _db.ReadAsync(
            (connection, ct) => SqliteExecutor.QueryAsync(
                connection,
                $"SELECT {CardColumns} FROM makeup_cards_cache ORDER BY created_at DESC LIMIT $limit;",
                [SqliteExecutor.Param("$limit", safeLimit)],
                null,
                MapCard,
                ct),
            cancellationToken);
    }

    /* ------------------------------------------------------------------ */
    /* makeup_candidates_cache                                             */
    /* ------------------------------------------------------------------ */

    /// <summary>
    /// 整体替换补签候选：先清空再插入（候选是「当前可补签的日期集合」，
    /// 增量合并会留下已被补掉的陈旧日期，导致日历显示错误）。同一事务内完成。
    /// </summary>
    public Task ReplaceCandidatesAsync(
        IReadOnlyList<MakeupCandidateDto> candidates,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        return _db.WriteAsync(
            async (connection, transaction, ct) =>
            {
                _ = await SqliteExecutor
                    .ExecuteAsync(connection, "DELETE FROM makeup_candidates_cache;", null, transaction, ct)
                    .ConfigureAwait(false);

                foreach (var candidate in candidates)
                {
                    if (string.IsNullOrWhiteSpace(candidate.Date))
                    {
                        // 日期是主键；空值入库会产生无法查询的幽灵行。
                        continue;
                    }

                    _ = await SqliteExecutor
                        .ExecuteAsync(
                            connection,
                            "INSERT INTO makeup_candidates_cache (date, days_ago, synced_at) VALUES ($date, $days, $at);",
                            [
                                SqliteExecutor.Param("$date", candidate.Date),
                                SqliteExecutor.Param("$days", candidate.DaysAgo),
                                SqliteExecutor.Param("$at", now),
                            ],
                            transaction,
                            ct)
                        .ConfigureAwait(false);
                }

                return true;
            },
            cancellationToken);
    }

    /// <summary>读补签候选（按日期倒序）。</summary>
    public Task<IReadOnlyList<MakeupCandidateCacheRecord>> GetCandidatesAsync(
        CancellationToken cancellationToken = default) =>
        _db.ReadAsync(
            (connection, ct) => SqliteExecutor.QueryAsync(
                connection,
                "SELECT date, days_ago, synced_at FROM makeup_candidates_cache ORDER BY date DESC;",
                null,
                null,
                static reader => new MakeupCandidateCacheRecord(
                    reader.GetString(0),
                    reader.GetInt32(1),
                    reader.GetInt64(2)),
                ct),
            cancellationToken);

    /* ------------------------------------------------------------------ */
    /* level_config_cache（snake_case 字段，EDGE-W-24 防御）                 */
    /* ------------------------------------------------------------------ */

    /// <summary>
    /// 整体替换等级配置（响应结构完整，增量更新会残留旧阈值）。
    /// <c>level_code</c> 为空的条目**直接丢弃**，绝不写入空主键（EDGE-W-24）。
    /// </summary>
    public Task ReplaceLevelConfigAsync(
        IReadOnlyList<LevelConfigEntryDto> levels,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(levels);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        return _db.WriteAsync(
            async (connection, transaction, ct) =>
            {
                _ = await SqliteExecutor
                    .ExecuteAsync(connection, "DELETE FROM level_config_cache;", null, transaction, ct)
                    .ConfigureAwait(false);

                foreach (var level in levels)
                {
                    if (string.IsNullOrWhiteSpace(level.LevelCode))
                    {
                        continue;
                    }

                    _ = await SqliteExecutor
                        .ExecuteAsync(
                            connection,
                            """
                            INSERT INTO level_config_cache (level_code, level_name, threshold_days, sort_order, synced_at)
                            VALUES ($code, $name, $threshold, $sort, $at);
                            """,
                            [
                                SqliteExecutor.Param("$code", level.LevelCode),
                                SqliteExecutor.Param("$name", level.LevelName ?? string.Empty),
                                SqliteExecutor.Param("$threshold", level.ThresholdDays),
                                SqliteExecutor.Param("$sort", level.SortOrder),
                                SqliteExecutor.Param("$at", now),
                            ],
                            transaction,
                            ct)
                        .ConfigureAwait(false);
                }

                return true;
            },
            cancellationToken);
    }

    /// <summary>读等级配置（按 <c>sort_order</c> 升序）；无缓存返回空列表。</summary>
    public Task<IReadOnlyList<LevelConfigCacheRecord>> GetLevelConfigAsync(
        CancellationToken cancellationToken = default) =>
        _db.ReadAsync(
            (connection, ct) => SqliteExecutor.QueryAsync(
                connection,
                "SELECT level_code, level_name, threshold_days, sort_order, synced_at FROM level_config_cache ORDER BY sort_order ASC;",
                null,
                null,
                static reader => new LevelConfigCacheRecord(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetInt32(2),
                    reader.GetInt32(3),
                    reader.GetInt64(4)),
                ct),
            cancellationToken);

    /* ------------------------------------------------------------------ */
    /* 清理                                                              */
    /* ------------------------------------------------------------------ */

    /// <summary>
    /// 清空缓存。给 <paramref name="userId"/> 时只清该用户的三张按用户划分的表；
    /// 候选与等级配置是全局的，**总是**被清（切换账号时必须重拉，否则会看到上一个账号的等级表）。
    /// </summary>
    public Task ClearAsync(string? userId = null, CancellationToken cancellationToken = default) =>
        _db.WriteAsync(
            async (connection, transaction, ct) =>
            {
                if (string.IsNullOrWhiteSpace(userId))
                {
                    _ = await SqliteExecutor
                        .ExecuteAsync(connection, "DELETE FROM user_progress_cache;", null, transaction, ct)
                        .ConfigureAwait(false);
                    _ = await SqliteExecutor
                        .ExecuteAsync(connection, "DELETE FROM points_ledger_cache;", null, transaction, ct)
                        .ConfigureAwait(false);
                    _ = await SqliteExecutor
                        .ExecuteAsync(connection, "DELETE FROM makeup_cards_cache;", null, transaction, ct)
                        .ConfigureAwait(false);
                }
                else
                {
                    var parameter = SqliteExecutor.Param("$user", userId);
                    _ = await SqliteExecutor
                        .ExecuteAsync(
                            connection,
                            "DELETE FROM user_progress_cache WHERE user_id = $user;",
                            [parameter],
                            transaction,
                            ct)
                        .ConfigureAwait(false);
                    _ = await SqliteExecutor
                        .ExecuteAsync(
                            connection,
                            "DELETE FROM points_ledger_cache WHERE user_id = $user;",
                            [parameter],
                            transaction,
                            ct)
                        .ConfigureAwait(false);
                    _ = await SqliteExecutor
                        .ExecuteAsync(
                            connection,
                            "DELETE FROM makeup_cards_cache WHERE user_id = $user;",
                            [parameter],
                            transaction,
                            ct)
                        .ConfigureAwait(false);
                }

                _ = await SqliteExecutor
                    .ExecuteAsync(connection, "DELETE FROM makeup_candidates_cache;", null, transaction, ct)
                    .ConfigureAwait(false);

                return true;
            },
            cancellationToken);

    /* ------------------------------------------------------------------ */
    /* 行映射                                                              */
    /* ------------------------------------------------------------------ */

    internal static ProgressCacheRecord MapProgress(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetInt32(1),
        reader.GetInt64(2),
        reader.GetString(3),
        reader.GetString(4),
        SqliteExecutor.NullableString(reader, 5),
        SqliteExecutor.NullableString(reader, 6),
        reader.GetInt32(7),
        SqliteExecutor.NullableString(reader, 8),
        SqliteExecutor.NullableString(reader, 9),
        SqliteExecutor.NullableInt32(reader, 10),
        SqliteExecutor.NullableInt32(reader, 11),
        SqliteExecutor.NullableString(reader, 12),
        reader.GetInt32(13),
        SqliteExecutor.NullableString(reader, 14),
        SqliteExecutor.AsBool(reader.GetInt64(15)),
        SqliteExecutor.NullableInt64(reader, 16),
        reader.GetInt32(17),
        reader.GetInt64(18));

    internal static LedgerCacheRecord MapLedger(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetString(1),
        reader.GetInt32(2),
        reader.GetString(3),
        reader.GetInt32(4),
        SqliteExecutor.NullableString(reader, 5),
        SqliteExecutor.NullableString(reader, 6),
        reader.GetInt64(7),
        reader.GetString(8));

    internal static MakeupCardCacheRecord MapCard(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        SqliteExecutor.NullableString(reader, 4),
        SqliteExecutor.NullableInt64(reader, 5),
        reader.GetInt64(6));
}
