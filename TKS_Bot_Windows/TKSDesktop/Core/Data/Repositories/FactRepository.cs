using System.Text;
using Microsoft.Data.Sqlite;
using TKSDesktop.Core.Data.Records;
using TKSDesktop.Contracts.Dtos;

namespace TKSDesktop.Core.Data.Repositories;

public sealed record MemoryPermissionOperation(string OperationId, string UserId, string FactId,
    bool Enabled, long ExpectedRevision, string State);

/// <summary>
/// <c>user_facts</c> 仓储（PRD §9.4，记忆事实；服务端 <c>memory.fact.created</c> 与 <c>GET /memory/facts</c> 落库）。
///
/// <para>写入用 <c>ON CONFLICT (fact_id) DO UPDATE SET fact = excluded.fact WHERE user_facts.fact &lt;&gt; excluded.fact</c>：
/// 同 ID 但正文未变时 <c>changes = 0</c>，既幂等又能让调用方区分「真的更新了」（FR-W-DB-1 的三态精神）。</para>
/// </summary>
public sealed class FactRepository
{
    private const string Columns = "fact_id, user_id, fact, timestamp, revision, revision_id, permission_version, enabled, stage, truth_status, score, source_message_id, temporal_validity";

    /// <summary>列表默认条数上限。</summary>
    public const int DefaultLimit = 300;

    private readonly LocalDb _db;

    /// <summary>构造。</summary>
    public FactRepository(LocalDb db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    /// <summary>幂等写入一条事实。返回 <see langword="true"/> 表示确实发生了变更（新增或正文更新）。</summary>
    public async Task<bool> UpsertAsync(FactRecord fact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fact);

        var affected = await _db.WriteAsync(
            (connection, transaction, ct) => SqliteExecutor.ExecuteAsync(
                connection,
                """
                INSERT INTO user_facts (fact_id, user_id, fact, timestamp)
                VALUES ($id, $user, $fact, $ts)
                ON CONFLICT (fact_id) DO UPDATE SET fact = excluded.fact
                WHERE user_facts.revision = 0 AND user_facts.fact <> excluded.fact;
                """,
                [
                    SqliteExecutor.Param("$id", fact.FactId),
                    SqliteExecutor.Param("$user", fact.UserId),
                    SqliteExecutor.Param("$fact", fact.Fact),
                    SqliteExecutor.Param("$ts", fact.Timestamp),
                ],
                transaction,
                ct),
            cancellationToken).ConfigureAwait(false);

        return affected > 0;
    }

    /// <summary>
    /// 批量写入（历史同步）。**不覆盖**已存在的行（<c>DO NOTHING</c>）：
    /// 全量拉取时以首次落库的正文为准，避免与本地已更新的内容互相打架。返回实际插入行数。
    /// </summary>
    public Task<int> BulkInsertAsync(IReadOnlyList<FactRecord> facts, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (facts.Count == 0)
        {
            return Task.FromResult(0);
        }

        return _db.WriteAsync(
            async (connection, transaction, ct) =>
            {
                var inserted = 0;
                foreach (var fact in facts)
                {
                    inserted += await SqliteExecutor
                        .ExecuteAsync(
                            connection,
                            """
                            INSERT INTO user_facts (fact_id, user_id, fact, timestamp)
                            VALUES ($id, $user, $fact, $ts)
                            ON CONFLICT (fact_id) DO NOTHING;
                            """,
                            [
                                SqliteExecutor.Param("$id", fact.FactId),
                                SqliteExecutor.Param("$user", fact.UserId),
                                SqliteExecutor.Param("$fact", fact.Fact),
                                SqliteExecutor.Param("$ts", fact.Timestamp),
                            ],
                            transaction,
                            ct)
                        .ConfigureAwait(false);
                }

                return inserted;
            },
            cancellationToken);
    }

    /// <summary>Apply one authoritative event page and its cursor in the same SQLite transaction.</summary>
    public Task<int> ApplyEventPageAsync(MemoryFactsDataDto page, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        return _db.WriteAsync(async (connection, transaction, ct) =>
        {
            var cursor = await SqliteExecutor.ScalarLongAsync(connection,
                "SELECT COALESCE((SELECT value FROM sync_cursors WHERE key='memory'), 0);",
                null, transaction, ct).ConfigureAwait(false);
            if (page.NextCursor < cursor) return 0;
            var written = 0;
            foreach (var fact in page.Items)
            {
                if (string.IsNullOrWhiteSpace(fact.EventId) || string.IsNullOrWhiteSpace(fact.FactId)
                    || string.IsNullOrWhiteSpace(fact.UserId))
                    throw new InvalidDataException("Malformed memory event");
                var inserted = await SqliteExecutor.ExecuteAsync(connection,
                    "INSERT OR IGNORE INTO memory_seen_events(event_id,user_id) VALUES($event,$user);",
                    [SqliteExecutor.Param("$event", fact.EventId), SqliteExecutor.Param("$user", fact.UserId)],
                    transaction, ct).ConfigureAwait(false);
                if (inserted == 0) continue;
                written += await SqliteExecutor.ExecuteAsync(connection,
                    """
                    INSERT INTO user_facts(fact_id,user_id,fact,timestamp,revision,revision_id,
                        permission_version,enabled,stage,truth_status,score,source_message_id,temporal_validity)
                    VALUES($id,$user,$fact,$ts,$revision,$revisionId,$permissionVersion,$enabled,
                        $stage,$truth,$score,$source,$validity)
                    ON CONFLICT(fact_id) DO UPDATE SET fact=excluded.fact,timestamp=excluded.timestamp,
                        revision=excluded.revision,revision_id=excluded.revision_id,
                        permission_version=excluded.permission_version,enabled=excluded.enabled,
                        stage=excluded.stage,truth_status=excluded.truth_status,score=excluded.score,
                        source_message_id=excluded.source_message_id,temporal_validity=excluded.temporal_validity
                    WHERE user_facts.revision < excluded.revision OR
                        (user_facts.revision = excluded.revision AND user_facts.permission_version < excluded.permission_version);
                    """,
                    [
                        SqliteExecutor.Param("$id", fact.FactId), SqliteExecutor.Param("$user", fact.UserId),
                        SqliteExecutor.Param("$fact", fact.Fact), SqliteExecutor.Param("$ts", fact.Timestamp),
                        SqliteExecutor.Param("$revision", fact.Revision), SqliteExecutor.Param("$revisionId", fact.RevisionId),
                        SqliteExecutor.Param("$permissionVersion", fact.PermissionVersion),
                        SqliteExecutor.Param("$enabled", fact.Enabled ? 1 : 0), SqliteExecutor.Param("$stage", fact.Stage),
                        SqliteExecutor.Param("$truth", fact.TruthStatus), SqliteExecutor.Param("$score", fact.Score),
                        SqliteExecutor.Param("$source", fact.SourceMessageId),
                        SqliteExecutor.Param("$validity", fact.TemporalValidity?.GetRawText()),
                    ], transaction, ct).ConfigureAwait(false);
            }
            await SqliteExecutor.ExecuteAsync(connection,
                "INSERT INTO sync_cursors(key,value) VALUES('memory',$cursor) ON CONFLICT(key) DO UPDATE SET value=excluded.value;",
                [SqliteExecutor.Param("$cursor", page.NextCursor)], transaction, ct).ConfigureAwait(false);
            return written;
        }, cancellationToken);
    }

    public Task<bool> QueuePermissionAsync(MemoryPermissionOperation operation, CancellationToken ct = default) =>
        _db.WriteAsync(async (connection, transaction, token) =>
        {
            var existing = await SqliteExecutor.ScalarLongAsync(connection,
                "SELECT COUNT(*) FROM memory_permission_queue WHERE fact_id=$fact AND state='pending';",
                [SqliteExecutor.Param("$fact", operation.FactId)], transaction, token).ConfigureAwait(false);
            if (existing > 0) return false;
            return await SqliteExecutor.ExecuteAsync(connection,
                "INSERT INTO memory_permission_queue(operation_id,user_id,fact_id,enabled,expected_revision,state) VALUES($id,$user,$fact,$enabled,$revision,'pending');",
                [SqliteExecutor.Param("$id", operation.OperationId), SqliteExecutor.Param("$user", operation.UserId),
                 SqliteExecutor.Param("$fact", operation.FactId), SqliteExecutor.Param("$enabled", operation.Enabled ? 1 : 0),
                 SqliteExecutor.Param("$revision", operation.ExpectedRevision)], transaction, token).ConfigureAwait(false) > 0;
        }, ct);

    public Task<IReadOnlyList<MemoryPermissionOperation>> PendingPermissionsAsync(CancellationToken ct = default) =>
        _db.ReadAsync((connection, token) => SqliteExecutor.QueryAsync(connection,
            "SELECT operation_id,user_id,fact_id,enabled,expected_revision,state FROM memory_permission_queue WHERE state='pending' ORDER BY rowid;",
            null, null, reader => new MemoryPermissionOperation(reader.GetString(0), reader.GetString(1),
                reader.GetString(2), reader.GetInt64(3) != 0, reader.GetInt64(4), reader.GetString(5)), token), ct);

    public Task<string?> LatestPermissionStateAsync(string factId, CancellationToken ct = default) =>
        _db.ReadAsync(async (connection, token) =>
        {
            var rows = await SqliteExecutor.QueryAsync(connection,
                "SELECT state FROM memory_permission_queue WHERE fact_id=$fact ORDER BY rowid DESC LIMIT 1;",
                [SqliteExecutor.Param("$fact", factId)], null, reader => reader.GetString(0), token).ConfigureAwait(false);
            return rows.FirstOrDefault();
        }, ct);

    public Task MarkPermissionAsync(string operationId, string state, CancellationToken ct = default) =>
        _db.WriteAsync(async (connection, transaction, token) =>
        {
            _ = await SqliteExecutor.ExecuteAsync(connection,
                "UPDATE memory_permission_queue SET state=$state WHERE operation_id=$id;",
                [SqliteExecutor.Param("$state", state), SqliteExecutor.Param("$id", operationId)],
                transaction, token).ConfigureAwait(false);
            return 0;
        }, ct);

    /// <summary>按时间倒序列出事实。</summary>
    public Task<IReadOnlyList<FactRecord>> ListAsync(
        int limit = DefaultLimit,
        CancellationToken cancellationToken = default)
    {
        var safeLimit = Math.Clamp(limit, 1, 2000);
        return _db.ReadAsync(
            (connection, ct) => SqliteExecutor.QueryAsync(
                connection,
                $"SELECT {Columns} FROM user_facts ORDER BY timestamp DESC LIMIT $limit;",
                [SqliteExecutor.Param("$limit", safeLimit)],
                null,
                Map,
                ct),
            cancellationToken);
    }

    /// <summary>
    /// 按关键字搜索（FR-W-HIS-3）。空关键字等价于 <see cref="ListAsync"/>。
    /// <c>LIKE</c> 的 <c>\</c> <c>%</c> <c>_</c> 已转义并配 <c>ESCAPE '\'</c>，用户输入 <c>%</c> 不会变成通配符。
    /// </summary>
    public Task<IReadOnlyList<FactRecord>> SearchAsync(
        string? term,
        int limit = DefaultLimit,
        CancellationToken cancellationToken = default)
    {
        var keyword = (term ?? string.Empty).Trim();
        if (keyword.Length == 0)
        {
            return ListAsync(limit, cancellationToken);
        }

        var safeLimit = Math.Clamp(limit, 1, 2000);
        return _db.ReadAsync(
            (connection, ct) => SqliteExecutor.QueryAsync(
                connection,
                $"""
                 SELECT {Columns} FROM user_facts
                  WHERE fact LIKE $pattern ESCAPE '\'
                  ORDER BY timestamp DESC
                  LIMIT $limit;
                 """,
                [
                    SqliteExecutor.Param("$pattern", $"%{EscapeLike(keyword)}%"),
                    SqliteExecutor.Param("$limit", safeLimit),
                ],
                null,
                Map,
                ct),
            cancellationToken);
    }

    /// <summary>本地最大 <c>timestamp</c>（记忆增量同步游标基准）。无数据时返回 0。</summary>
    public Task<long> ReadMaxTimestampAsync(CancellationToken cancellationToken = default) =>
        _db.ReadAsync(
            (connection, ct) => SqliteExecutor.ScalarLongAsync(
                connection,
                "SELECT COALESCE(MAX(timestamp), 0) FROM user_facts;",
                null,
                null,
                ct),
            cancellationToken);

    /// <summary>事实条数。</summary>
    public Task<int> CountAsync(CancellationToken cancellationToken = default) =>
        _db.ReadAsync(
            (connection, ct) => SqliteExecutor.ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM user_facts;",
                null,
                null,
                ct),
            cancellationToken);

    /// <summary>LIKE 转义（<c>\</c> <c>%</c> <c>_</c> 前置反斜杠）。</summary>
    internal static string EscapeLike(string keyword)
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

    private static FactRecord Map(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetInt64(3))
    {
        Revision = reader.GetInt64(4),
        RevisionId = reader.IsDBNull(5) ? null : reader.GetString(5),
        PermissionVersion = reader.GetInt64(6),
        Enabled = reader.GetInt64(7) != 0,
        Stage = reader.GetString(8),
        TruthStatus = reader.GetString(9),
        Score = reader.GetDouble(10),
        SourceMessageId = reader.IsDBNull(11) ? null : reader.GetString(11),
        TemporalValidity = reader.IsDBNull(12) ? null : reader.GetString(12),
    };
}
