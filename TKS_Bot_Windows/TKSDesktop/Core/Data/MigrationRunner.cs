using System.Globalization;
using Microsoft.Data.Sqlite;

namespace TKSDesktop.Core.Data;

/// <summary>一次迁移执行的结果（供启动自检与对账输出）。</summary>
/// <param name="FromVersion">执行前的 <c>user_version</c>。</param>
/// <param name="ToVersion">执行后的 <c>user_version</c>。</param>
/// <param name="Applied">实际执行的迁移名（升序）；库已是最新时为空数组。</param>
public sealed record MigrationReport(int FromVersion, int ToVersion, IReadOnlyList<string> Applied);

/// <summary>
/// 迁移执行器（PRD FR-W-DB-3 / FR-W-DB-6）。
///
/// <para><b>执行规则</b>：</para>
/// <list type="number">
///   <item>读 <c>PRAGMA user_version</c> 作为唯一进度来源。</item>
///   <item>逐版本执行：**每版一个事务**，事务内跑该版全部脚本并写入 <c>user_version = N</c>；
///         任一步抛错即整版回滚（<c>DDL</c> 与 <c>user_version</c> 一起回滚，实测已验证），
///         已提交的低版本保持不动，异常向上抛给 <see cref="LocalDb"/> 走「备份 + 重建」。</item>
///   <item>幂等：全部 DDL 用 <c>IF NOT EXISTS</c>，回填带 <c>NOT EXISTS</c> 守卫，
///         因此重复打开同一库不会重复建对象，也不会重复回填 FTS 行。</item>
///   <item>不并发：只由 <see cref="LocalDb.InitializeAsync"/> 在启动期单线程调用一次，
///         且不与任何仓储写事务并发（唯一 migration 序列锁在 <see cref="LocalDb"/> 的门上）。</item>
/// </list>
/// </summary>
public static class MigrationRunner
{
    /// <summary>执行全部待应用迁移；已是最新则直接返回（不做任何写入）。</summary>
    public static async Task<MigrationReport> RunAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var from = await SqliteExecutor
            .ScalarIntAsync(connection, "PRAGMA user_version;", null, null, cancellationToken)
            .ConfigureAwait(false);

        var pending = DatabaseSchema.All
            .Where(step => step.Version > from)
            .OrderBy(step => step.Version)
            .ToList();

        if (pending.Count == 0)
        {
            return new MigrationReport(from, from, []);
        }

        var applied = new List<string>(pending.Count);
        foreach (var step in pending)
        {
            // 一版一事务：失败时该版本全部对象与 user_version 一起回滚。
            var transaction = (SqliteTransaction)await connection
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            try
            {
                foreach (var script in step.Scripts)
                {
                    await SqliteExecutor
                        .ExecuteAsync(connection, script, null, transaction, cancellationToken)
                        .ConfigureAwait(false);
                }

                await SqliteExecutor
                    .ExecuteAsync(
                        connection,
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"PRAGMA user_version = {step.Version};"),
                        null,
                        transaction,
                        cancellationToken)
                    .ConfigureAwait(false);

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                applied.Add(step.Name);
            }
            catch
            {
                await RollbackQuietlyAsync(transaction, cancellationToken).ConfigureAwait(false);
                throw;
            }
            finally
            {
                await transaction.DisposeAsync().ConfigureAwait(false);
            }
        }

        var to = await SqliteExecutor
            .ScalarIntAsync(connection, "PRAGMA user_version;", null, null, cancellationToken)
            .ConfigureAwait(false);

        return new MigrationReport(from, to, applied);
    }

    /// <summary>
    /// 逐项核对 schema 是否与 <see cref="DatabaseSchema"/> 声明一致（对账用，不做修改）。
    /// 返回缺失对象清单；空清单表示一致。
    /// </summary>
    public static async Task<IReadOnlyList<string>> VerifyAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var existing = await SqliteExecutor
            .QueryAsync(
                connection,
                "SELECT type, name FROM sqlite_master WHERE name NOT LIKE 'sqlite_%';",
                null,
                null,
                static reader => (Type: reader.GetString(0), Name: reader.GetString(1)),
                cancellationToken)
            .ConfigureAwait(false);

        var byType = existing
            .GroupBy(entry => entry.Type, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(entry => entry.Name).ToHashSet(StringComparer.Ordinal),
                StringComparer.Ordinal);

        var missing = new List<string>();
        Check("table", DatabaseSchema.TableNames, "table");
        Check("table", DatabaseSchema.VirtualTableNames, "fts-virtual-table");
        Check("index", DatabaseSchema.IndexNames, "index");
        Check("trigger", DatabaseSchema.TriggerNames, "trigger");

        return missing;

        void Check(string type, IReadOnlyList<string> expected, string label)
        {
            _ = byType.TryGetValue(type, out var actual);
            foreach (var name in expected)
            {
                if (actual is null || !actual.Contains(name))
                {
                    missing.Add($"{label}:missing:{name}");
                }
            }
        }
    }

    private static async Task RollbackQuietlyAsync(SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        try
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException)
        {
            // 事务可能已被 SQLite 因错误自动回滚。
        }
        catch (InvalidOperationException)
        {
            // 事务已处于完成状态。
        }
    }
}
