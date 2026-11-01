using System.Globalization;
using Microsoft.Data.Sqlite;

namespace TKSDesktop.Core.Data;

/// <summary>
/// 本地库门面：连接、pragma、读/写串行化（PRD FR-W-DB-7 / FR-W-ARCH-3）。
///
/// <para><b>单写者语义</b>：本类持有**唯一**一条长生命周期连接，并用 <see cref="SemaphoreSlim"/> 串行化
/// 全部访问。写事务一律经 <see cref="WriteAsync{T}"/>，因此天然满足「同一 migration 序列/同一库不得并发写」。</para>
///
/// <para><b>为什么读也串行化</b>：实测（Microsoft.Data.Sqlite 8.0.31 / SQLite 3.53.3）若允许另一连接在
/// 写事务期间读同表，`Cache=Shared` 会以 <c>SQLite Error 6: database table is locked</c> 失败（共享缓存是
/// 表级锁），`Cache=Default`（私有缓存）则正常。WAL 模式与共享缓存同时启用属于 SQLite 明确不建议的组合，
/// 且本地库流量极小，故这里选择「私有缓存 + 单一连接 + 全串行」以获得确定性而非并发度。</para>
///
/// <para><b>启动顺序</b>：建目录 → 打开 → 设 pragma → <c>integrity_check</c>（失败则备份并重建空库）
/// → 顺序迁移（失败则回滚、备份、重建空库继续）。</para>
/// </summary>
public sealed class LocalDb : IAsyncDisposable
{
    /// <summary>写事务串行化等待超时（毫秒）；超时说明存在泄漏的长事务，属缺陷。</summary>
    private const int GateTimeoutMs = 30_000;

    private readonly string _databasePath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly AsyncLocal<int> _gateDepth = new();
    private SqliteConnection? _connection;
    private bool _disposed;

    private LocalDb(string databasePath) => _databasePath = databasePath;

    /// <summary>数据库文件绝对路径。</summary>
    public string DatabasePath => _databasePath;

    /// <summary>是否发生过「备份 + 重建」的恢复（完整性失败或迁移失败），上层据此提示本地图片附件不可恢复（EDGE-W-14）。</summary>
    public bool Recovered { get; private set; }

    /// <summary>本次启动是否因 <c>PRAGMA integrity_check</c> 非 <c>ok</c> 而重建（FR-W-DB-8）。</summary>
    public bool IntegrityFailed { get; private set; }

    /// <summary>本次启动是否因迁移失败而回滚并重建（FR-W-DB-6）。</summary>
    public bool MigrationFailed { get; private set; }

    /// <summary>为受损库保留的备份路径（主库 <c>.bak</c>；WAL/SHM 为同名 <c>.bak-wal</c> / <c>.bak-shm</c>）。</summary>
    public string? LastBackupPath { get; private set; }

    /// <summary>打开后读回的实际日志模式；正常应为 <c>wal</c>。</summary>
    public string? JournalMode { get; private set; }

    /// <summary>当前 <c>user_version</c>（每次查询，用于对账）。</summary>
    public Task<long> GetUserVersionAsync(CancellationToken cancellationToken = default) =>
        ReadAsync(static (conn, ct) => SqliteExecutor.ScalarLongAsync(conn, "PRAGMA user_version;", null, null, ct), cancellationToken);

    /// <summary>
    /// 打开（必要时创建）本地库并完成完整性校验与迁移。
    /// </summary>
    /// <param name="databasePath">`tks.db` 路径（见 <c>Core.Platform.IPaths.DatabaseFile</c>）。</param>
    /// <param name="recoverOnFailure">
    /// 为 <see langword="true"/> 时，完整性失败或迁移失败会自动备份并重建空库继续运行；
    /// 为 <see langword="false"/> 时直接抛错（测试与演练用）。
    /// </param>
    /// <param name="cancellationToken">取消标记。</param>
    public static async Task<LocalDb> OpenAsync(
        string databasePath,
        bool recoverOnFailure = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var db = new LocalDb(databasePath);
        try
        {
            await db.InitializeAsync(recoverOnFailure, cancellationToken).ConfigureAwait(false);
            return db;
        }
        catch
        {
            await db.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /* ------------------------------------------------------------------ */
    /* 读 / 写门面                                                          */
    /* ------------------------------------------------------------------ */

    /// <summary>
    /// 读操作（无显式事务）。仍在写者门内执行，避免与写事务交错导致 <c>SQLITE_LOCKED</c>。
    /// </summary>
    public async Task<T> ReadAsync<T>(
        Func<SqliteConnection, CancellationToken, Task<T>> body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        EnsureUsable();
        EnsureNotInsideGate("ReadAsync");
        await EnterGateAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await body(Connection, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            LeaveGate();
        }
    }

    /// <summary>
    /// 写操作：在串行化门内开启事务，正常返回则提交，异常回滚后原样抛出。
    /// **所有** DML/DDL 必须走本方法（FR-W-ARCH-3 单写者）。
    /// </summary>
    public async Task<T> WriteAsync<T>(
        Func<SqliteConnection, SqliteTransaction, CancellationToken, Task<T>> body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        EnsureUsable();
        EnsureNotInsideGate("WriteAsync");
        await EnterGateAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var connection = Connection;
            var transaction = (SqliteTransaction)await connection
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            try
            {
                var result = await body(connection, transaction, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return result;
            }
            catch
            {
                await TryRollbackAsync(transaction, cancellationToken).ConfigureAwait(false);
                throw;
            }
            finally
            {
                await transaction.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            LeaveGate();
        }
    }

    /// <summary>写操作便捷重载（无返回值语义）。</summary>
    public async Task WriteAsync(
        Func<SqliteConnection, SqliteTransaction, CancellationToken, Task> body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        await WriteAsync(
            async (conn, tx, ct) =>
            {
                await body(conn, tx, ct).ConfigureAwait(false);
                return true;
            },
            cancellationToken).ConfigureAwait(false);
    }

    /* ------------------------------------------------------------------ */
    /* 连接与初始化                                                          */
    /* ------------------------------------------------------------------ */

    private SqliteConnection Connection =>
        _connection ?? throw new InvalidOperationException("本地库尚未初始化（LocalDb.OpenAsync 未完成）。");

    /// <summary>
    /// 连接串（FR-W-DB-7）。<c>Pooling=True</c> 按要求开启；
    /// 缓存模式取 <c>Default</c>（私有缓存）而非 <c>Shared</c>，理由见类注释中的实测结论。
    /// </summary>
    public static string BuildConnectionString(string databasePath) => new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Default,
        Pooling = true,
    }.ToString();

    private async Task InitializeAsync(bool recoverOnFailure, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_databasePath))!);

        await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        // ① 完整性校验（FR-W-DB-8 / EDGE-W-14）
        var integrity = await DbRecovery.CheckIntegrityAsync(Connection, cancellationToken).ConfigureAwait(false);
        if (!integrity.IsOk)
        {
            if (!recoverOnFailure)
            {
                // 异常消息为**诊断文本**（非 UI 文案）：界面文案由调用方经 I18n 渲染。
                throw new InvalidDataException(
                    $"database-integrity-check-failed: {integrity.Detail}");
            }

            IntegrityFailed = true;
            Recovered = true;
            LastBackupPath = await DbRecovery
                .BackupForRecoveryAsync(_databasePath, DbRecovery.IntegrityFailedReason, cancellationToken)
                .ConfigureAwait(false);
            await ReopenFreshAsync(cancellationToken).ConfigureAwait(false);
        }

        // ② journal 相关 pragma（WAL + synchronous=NORMAL）。
        //    ⚠️ 必须放在完整性校验**之后**：对损坏文件设置 journal_mode 会抛 SQLITE_CORRUPT，
        //       若提前执行就永远到不了恢复分支（Windows 端实测确认）。
        await ApplyJournalPragmasAsync(Connection, cancellationToken).ConfigureAwait(false);


        // ③ 顺序迁移（FR-W-DB-3 / FR-W-DB-6）
        try
        {
            await MigrationRunner.RunAsync(Connection, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException)
        {
            if (!recoverOnFailure)
            {
                throw;
            }

            // 迁移失败：备份原库（含 -wal / -shm），然后新建空库继续运行，避免应用起不来。
            MigrationFailed = true;
            Recovered = true;
            LastBackupPath = await DbRecovery
                .BackupForRecoveryAsync(_databasePath, DbRecovery.MigrationFailedReason, cancellationToken)
                .ConfigureAwait(false);
            await ReopenFreshAsync(cancellationToken).ConfigureAwait(false);

            // 空库上的迁移若仍失败，说明是代码/SQL 缺陷而非旧数据问题：必须向上抛，不能静默吞掉。
            await MigrationRunner.RunAsync(Connection, cancellationToken).ConfigureAwait(false);
        }

        JournalMode = await SqliteExecutor
            .ScalarStringAsync(Connection, "PRAGMA journal_mode;", null, null, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(BuildConnectionString(_databasePath));
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        _connection = connection;

        // ⚠️ 先设 busy_timeout 与 foreign_keys（不触盘、不会因损坏而抛），
        //    再设 journal_mode。实测：对已损坏的文件执行 `PRAGMA journal_mode = WAL`
        //    会直接抛 SQLITE_CORRUPT（Error 11），若放在完整性校验之前，
        //    恢复流程根本没机会运行，应用会在启动时崩掉（FR-W-DB-8 失效）。
        await SqliteExecutor
            .ExecuteAsync(connection, "PRAGMA busy_timeout = 5000;", null, null, cancellationToken)
            .ConfigureAwait(false);

        // 外键必须是 ON，否则 chat_attachments 的 ON DELETE CASCADE 不生效（陷阱 8 的另一半）。
        await SqliteExecutor
            .ExecuteAsync(connection, "PRAGMA foreign_keys = ON;", null, null, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// FR-W-DB-7 的 WAL / synchronous。单独拆出，因为它们**需要触碰数据库文件**，
    /// 对损坏文件会抛错；调用方须保证在完整性校验通过（或已重建空库）之后才走到这里。
    /// </summary>
    private static async Task ApplyJournalPragmasAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        // journal_mode 需返回行，用 ExecuteScalar 读取（写入 sqlite 文件头，对后续连接持久生效）。
        _ = await SqliteExecutor
            .ScalarStringAsync(connection, "PRAGMA journal_mode = WAL;", null, null, cancellationToken)
            .ConfigureAwait(false);

        await SqliteExecutor
            .ExecuteAsync(connection, "PRAGMA synchronous = NORMAL;", null, null, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>关闭并删掉主库 + <c>-wal</c> + <c>-shm</c>，再建一个空库。</summary>
    private async Task ReopenFreshAsync(CancellationToken cancellationToken)
    {
        await CloseConnectionAsync().ConfigureAwait(false);
        await DbRecovery.QuarantineAsync(_databasePath, cancellationToken).ConfigureAwait(false);
        await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task CloseConnectionAsync()
    {
        var connection = _connection;
        _connection = null;
        if (connection is null)
        {
            return;
        }

        // Pooling=True 时仅 Dispose 不会释放文件句柄（实测 File.Delete 会报「being used by another process」），
        // 必须先清掉本连接的池，否则后续的备份/删除/重建会失败。
        SqliteConnection.ClearPool(connection);
        await connection.DisposeAsync().ConfigureAwait(false);
    }

    /* ------------------------------------------------------------------ */
    /* 门                                                            */
    /* ------------------------------------------------------------------ */

    private async Task EnterGateAsync(CancellationToken cancellationToken)
    {
        if (!await _gate.WaitAsync(GateTimeoutMs, cancellationToken).ConfigureAwait(false))
        {
            throw new TimeoutException(
                $"local-db-writer-gate-timeout gateTimeoutMs={GateTimeoutMs}");
        }

        _gateDepth.Value += 1;
    }

    private void LeaveGate()
    {
        _gateDepth.Value -= 1;
        _gate.Release();
    }

    /// <summary>
    /// 门是**非重入**的；若在 <see cref="WriteAsync{T}"/> 的 body 内再调 <see cref="ReadAsync{T}"/>
    /// 会自锁死。此处显式失败，把死锁变成可诊断的异常。
    /// </summary>
    private void EnsureNotInsideGate(string operation)
    {
        if (_gateDepth.Value > 0)
        {
            throw new InvalidOperationException(
                $"{operation} was called while the local-db writer gate is held (would self-deadlock); " +
                "complete it inside a single WriteAsync/ReadAsync body.");
        }
    }

    private void EnsureUsable() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static async Task TryRollbackAsync(SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        try
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException)
        {
            // 连接已失效/事务已被 SQLite 自动回滚：无需二次处理，原始异常更重要。
        }
        catch (InvalidOperationException)
        {
            // 事务已完成（例如提交阶段失败后被标记 completed）。
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_connection is { } connection)
            {
                try
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                    command.ExecuteNonQuery();
                }
                catch (SqliteException)
                {
                    // 只读介质/已损坏等场景下 checkpoint 失败不影响退出。
                }
            }

            await CloseConnectionAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}

/// <summary>
/// 最小 SQL 执行辅助（异步、带参数、可挂事务）。放在本文件以免新增未授权文件。
/// </summary>
internal static class SqliteExecutor
{
    /// <summary>构造命名参数；<see langword="null"/> 会以 <c>DBNull</c> 落库。</summary>
    internal static SqliteParameter Param(string name, object? value) =>
        new(name, value ?? DBNull.Value);

    internal static async Task<int> ExecuteAsync(
        SqliteConnection connection,
        string sql,
        IReadOnlyList<SqliteParameter>? parameters,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        AddParameters(command, parameters);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<long> ScalarLongAsync(
        SqliteConnection connection,
        string sql,
        IReadOnlyList<SqliteParameter>? parameters,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        var value = await ScalarAsync(connection, sql, parameters, transaction, cancellationToken)
            .ConfigureAwait(false);
        return value is null ? 0L : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    internal static async Task<int> ScalarIntAsync(
        SqliteConnection connection,
        string sql,
        IReadOnlyList<SqliteParameter>? parameters,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        var value = await ScalarAsync(connection, sql, parameters, transaction, cancellationToken)
            .ConfigureAwait(false);
        return value is null ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    internal static async Task<string?> ScalarStringAsync(
        SqliteConnection connection,
        string sql,
        IReadOnlyList<SqliteParameter>? parameters,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        var value = await ScalarAsync(connection, sql, parameters, transaction, cancellationToken)
            .ConfigureAwait(false);
        return value is null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    internal static async Task<object?> ScalarAsync(
        SqliteConnection connection,
        string sql,
        IReadOnlyList<SqliteParameter>? parameters,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        AddParameters(command, parameters);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is DBNull ? null : value;
    }

    internal static async Task<IReadOnlyList<T>> QueryAsync<T>(
        SqliteConnection connection,
        string sql,
        IReadOnlyList<SqliteParameter>? parameters,
        SqliteTransaction? transaction,
        Func<SqliteDataReader, T> map,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(map);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        AddParameters(command, parameters);

        var rows = new List<T>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(map(reader));
        }

        return rows;
    }

    internal static async Task<T?> QuerySingleOrDefaultAsync<T>(
        SqliteConnection connection,
        string sql,
        IReadOnlyList<SqliteParameter>? parameters,
        SqliteTransaction? transaction,
        Func<SqliteDataReader, T> map,
        CancellationToken cancellationToken)
        where T : class
    {
        var rows = await QueryAsync(connection, sql, parameters, transaction, map, cancellationToken)
            .ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    /// <summary>流式读取可空字符串（NULL → <see langword="null"/>；不做 <c>GetString</c> 以避免 NULL 抛错）。</summary>
    internal static string? NullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    /// <summary>流式读取可空 64 位整数。</summary>
    internal static long? NullableInt64(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    /// <summary>流式读取可空 32 位整数。</summary>
    internal static int? NullableInt32(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    /// <summary>SQLite 无布尔类型：以 0/1 存储。</summary>
    internal static bool AsBool(long value) => value != 0;

    private static void AddParameters(SqliteCommand command, IReadOnlyList<SqliteParameter>? parameters)
    {
        if (parameters is null || parameters.Count == 0)
        {
            return;
        }

        for (var i = 0; i < parameters.Count; i++)
        {
            command.Parameters.Add(parameters[i]);
        }
    }
}
