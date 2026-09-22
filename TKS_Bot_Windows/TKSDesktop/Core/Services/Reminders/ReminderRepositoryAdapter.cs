using TKSDesktop.Core.Data.Repositories;
using TKSDesktop.Core.Services;

namespace TKSDesktop.Core.Services.Reminders;

/// <summary>
/// 提醒持久化端口的适配器：把窄端口 <see cref="Ports.IReminderPort"/> 接到真实仓储
/// <see cref="ReminderRepository"/>（PRD §3.3 依赖方向：Core → Data）。
///
/// ⚠️ KEEP 语义（FR-W-REM-3）：`InsertKeepAsync` 在同主键已存在时**不得覆盖**，
///    尤其**不得**把已 `fired` / `cancelled` 的提醒复活为 `pending` —— 由仓储的
///    `UpsertReminderAsync` 承担（其结果为三态）。
/// </summary>
public sealed class ReminderRepositoryAdapter : Ports.IReminderPort
{
    private readonly ReminderRepository _repository;

    public ReminderRepositoryAdapter(ReminderRepository repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        _repository = repository;
    }

    /// <inheritdoc />
    public async Task<bool> InsertKeepAsync(ReminderInfo reminder, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reminder);

        var result = await _repository.UpsertReminderAsync(
            reminder.ReminderId,
            reminder.TargetTime,
            reminder.FireAt,
            reminder.Text,
            cancellationToken).ConfigureAwait(false);

        // KEEP 语义：仅「新建」算真正排程成功；已存在（无论内容是否变化）返回 false，
        // 调用方据此保留原计划、不重复排程。
        return result.Created;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ReminderInfo>> ListPendingAsync(CancellationToken cancellationToken = default)
    {
        var rows = await _repository.ListPendingAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(ToInfo).ToList();
    }

    /// <inheritdoc />
    public async Task<ReminderInfo?> GetAsync(string reminderId, CancellationToken cancellationToken = default)
    {
        var row = await _repository.GetAsync(reminderId, cancellationToken).ConfigureAwait(false);
        return row is null ? null : ToInfo(row);
    }

    /// <inheritdoc />
    public Task<bool> MarkFiredAsync(string reminderId, CancellationToken cancellationToken = default)
        => _repository.MarkFiredAsync(reminderId, cancellationToken);

    /// <inheritdoc />
    public Task<bool> CancelAsync(string reminderId, CancellationToken cancellationToken = default)
        => _repository.CancelAsync(reminderId, cancellationToken);

    /// <inheritdoc />
    public Task<int> PruneFinishedAsync(CancellationToken cancellationToken = default)
        => _repository.PruneFinishedAsync(cancellationToken);

    private static ReminderInfo ToInfo(Data.Records.ReminderRecord row)
        => new(row.ReminderId, row.TargetTime, row.FireAt, row.Text, row.Status, row.CreatedAt);
}
