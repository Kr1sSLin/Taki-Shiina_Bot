using System.Globalization;
using Microsoft.Data.Sqlite;

namespace TKSDesktop.Core.Data;

/// <summary><c>PRAGMA integrity_check</c> 的结果。</summary>
/// <param name="IsOk">是否等于 <c>ok</c>。</param>
/// <param name="Detail">非 <c>ok</c> 时的首行诊断文本（已脱敏：只保留 SQLite 自身输出，不含用户内容）。</param>
public sealed record IntegrityCheckResult(bool IsOk, string Detail)
{
    /// <summary>通过。</summary>
    public static IntegrityCheckResult Ok { get; } = new(true, "ok");
}

/// <summary>
/// 数据库损坏检测与恢复（PRD FR-W-DB-8 / EDGE-W-14）。
///
/// <para>损坏时的处理顺序（**必须**按此顺序，否则会丢证据）：</para>
/// <list type="number">
///   <item>备份**主库 + <c>-wal</c> + <c>-shm</c>**（只备份主库会丢掉 WAL 中未 checkpoint 的最新数据）。</item>
///   <item>把三个文件移出原位（隔离），确保新库从零开始而不是复用损坏页。</item>
///   <item>由 <see cref="LocalDb"/> 重建空库并跑完整迁移链。</item>
/// </list>
///
/// <para>备份命名：主库 <c>tks.db.integrity-failed-{yyyyMMddHHmmss}.bak</c>，
/// WAL/SHM 追加 <c>-wal</c> / <c>-shm</c> 后缀（同名同戳，便于配对）。</para>
///
/// <para><b>注意</b>：本地图片附件**不随库备份恢复**（附件是文件系统对象，且路径已在库中丢失）——
/// 这正是 <see cref="LocalDb.Recovered"/> 为真时上层必须提示用户的原因。</para>
/// </summary>
public static class DbRecovery
{
    /// <summary>完整性失败时的备份原因段。</summary>
    public const string IntegrityFailedReason = "integrity-failed";

    /// <summary>迁移失败时的备份原因段。</summary>
    public const string MigrationFailedReason = "migration-failed";

    private static readonly string[] SidecarSuffixes = ["-wal", "-shm"];

    /// <summary>
    /// 执行 <c>PRAGMA integrity_check</c>（全量，非 <c>quick_check</c>）。
    /// SQLite 在严重损坏时会直接抛 <see cref="SqliteException"/>，一并转换为失败结果而不是让启动崩掉。
    /// </summary>
    public static async Task<IntegrityCheckResult> CheckIntegrityAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        try
        {
            var rows = await SqliteExecutor
                .QueryAsync(
                    connection,
                    "PRAGMA integrity_check;",
                    null,
                    null,
                    static reader => reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                    cancellationToken)
                .ConfigureAwait(false);

            if (rows.Count == 0)
            {
                // 诊断文本（非 UI 文案）：只进日志与异常消息，界面文案由调用方经 I18n 渲染。
                return new IntegrityCheckResult(false, "integrity-check-returned-no-rows");
            }

            // 成功时只返回一行 'ok'；失败时返回多行问题描述，取首行做诊断即可。
            var first = rows[0];
            return string.Equals(first, "ok", StringComparison.OrdinalIgnoreCase)
                ? IntegrityCheckResult.Ok
                : new IntegrityCheckResult(false, first);
        }
        catch (SqliteException ex)
        {
            return new IntegrityCheckResult(
                false,
                $"integrity-check-failed sqliteErrorCode={ex.SqliteErrorCode} message={ex.Message}");
        }
    }

    /// <summary>
    /// 备份主库与 WAL/SHM。返回主库备份路径；备份失败（如磁盘满）返回 <see langword="null"/> 且**不抛**
    /// ——恢复优先于留证，不能因为备份失败就让应用起不来。
    /// </summary>
    public static async Task<string?> BackupForRecoveryAsync(
        string databasePath,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        var stamp = DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var backupBase = $"{databasePath}.{reason}-{stamp}.bak";

        try
        {
            if (File.Exists(databasePath))
            {
                await CopyAsync(databasePath, backupBase, cancellationToken).ConfigureAwait(false);
            }

            foreach (var suffix in SidecarSuffixes)
            {
                var source = $"{databasePath}{suffix}";
                if (File.Exists(source))
                {
                    // 与主库同名同戳：tks.db.{reason}-{stamp}.bak-wal / -shm
                    await CopyAsync(source, $"{backupBase}{suffix}", cancellationToken).ConfigureAwait(false);
                }
            }

            return backupBase;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// 把主库与 WAL/SHM 移出原位（先尝试 Move，跨卷/被占用时退化为删除）。
    /// 调用前必须已关闭并清理连接池，否则文件仍被占用。
    /// </summary>
    public static Task QuarantineAsync(string databasePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        cancellationToken.ThrowIfCancellationRequested();

        var failedPath = $"{databasePath}.failed-{DateTime.Now:yyyyMMddHHmmss}";

        foreach (var path in EnumerateDatabaseFiles(databasePath))
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // 仍被占用：退一步改为改名，避免新库复用到损坏页。
                TryMove(path, $"{failedPath}{Path.GetExtension(path)}");
            }
            catch (UnauthorizedAccessException)
            {
                TryMove(path, $"{failedPath}{Path.GetExtension(path)}");
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>库文件 + <c>-wal</c> + <c>-shm</c>。</summary>
    public static IEnumerable<string> EnumerateDatabaseFiles(string databasePath)
    {
        yield return databasePath;
        foreach (var suffix in SidecarSuffixes)
        {
            yield return $"{databasePath}{suffix}";
        }
    }

    private static async Task CopyAsync(string source, string destination, CancellationToken cancellationToken)
    {
        // FileShare.ReadWrite：WAL/SHM 可能仍被本进程或其他进程持有句柄。
        await using var input = new FileStream(
            source,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            useAsync: true);
        await using var output = new FileStream(
            destination,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            useAsync: true);
        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void TryMove(string source, string destination)
    {
        try
        {
            File.Move(source, destination, overwrite: true);
        }
        catch (IOException)
        {
            // 移动也不行：保留原文件，后续打开会再次触发完整性失败并被上层记录。
        }
        catch (UnauthorizedAccessException)
        {
            // 同上。
        }
    }
}
