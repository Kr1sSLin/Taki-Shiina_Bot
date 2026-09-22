using TKSDesktop.Core.Platform;
using System.Globalization;
using Microsoft.Extensions.Logging;
using TKSDesktop.App;
using TKSDesktop.Contracts;
using TKSDesktop.Contracts.Dtos;
using TKSDesktop.Core.Services.Ports;

namespace TKSDesktop.Core.Services.Reminders;

/// <summary>
/// 提醒调度（PRD FR-W-REM-1..7）。
///
/// <list type="bullet">
///   <item><b>FR-W-REM-1</b>：**只消费**服务端已解析的 <see cref="TimerInstructionDto"/>。
///         客户端**不得**实现 <c>[[TIMER:HH:MM|text]]</c> 正则 —— 解析在服务端，
///         客户端做一遍只会引入两套不一致的解析结果。</item>
///   <item><b>FR-W-REM-2</b>：目标时刻取**今日**该时刻，已过则顺延**次日**（本地时区语义：
///         提醒是用户对自己说的「两点叫我睡觉」，语义就是用户本地的两点；
///         积分/等级的「自然日」才必须用服务端日期，两者不冲突）。</item>
///   <item><b>FR-W-REM-3</b>：去重键 <c>reminder_{requestIds.join("_")}_{target}_{hash(text)}</c>；
///         同键已存在 → **保留原计划**（KEEP 语义，对齐 Android <c>ExistingWorkPolicy.KEEP</c>）。
///         用 <c>requestIds</c> 拼接是为了兼容服务端防抖合并（一次回复对应多条用户消息）。</item>
///   <item><b>FR-W-REM-4</b>：持久化到 <c>reminders</c> 表；启动时重新装载未触发提醒；
///         已过期未触发者 —— 超过 <see cref="ProtocolConstants.ReminderStaleMs"/>（30 分钟）丢弃，
///         否则**立即触发**。</item>
///   <item><b>FR-W-REM-5</b>：触发时走高优先级通知：标题 <c>notification.reminder.title</c>、
///         正文 <c>notification.reminder.body</c>（带格式化参数），并且**必须写入通知中心**
///         —— 因此经 <see cref="IUserNotificationService.Notify"/> 走
///         <see cref="NotificationCategory.Reminder"/>（语义 ID <c>Reminder</c> = 1005）。</item>
///   <item><b>FR-W-REM-7</b>：<see cref="ResendOverdueAsync"/> 供系统唤醒后补发。</item>
/// </list>
///
/// <para><b>可注入时钟</b>：所有时间判定走 <see cref="TimeProvider"/>，计时器也由
/// <see cref="TimeProvider.CreateTimer"/> 创建，因此注入假时钟即可断言
/// 「今日已过 → 次日」「过期 31 分钟丢弃 / 29 分钟立即触发」而不真实等待（V-W-B22）。</para>
/// </summary>
public sealed class ReminderScheduler : IReminderScheduler, IDisposable
{
    /// <summary>
    /// 单个计时器允许的最大时长。
    /// <c>System.Threading.Timer</c>（<see cref="TimeProvider.System"/> 的底层实现）对
    /// <c>dueTime</c> 有 <c>uint.MaxValue</c> 毫秒（≈49.7 天）上限，超出会抛
    /// <see cref="ArgumentOutOfRangeException"/>。因此超长延迟拆成多段（到期后重算剩余）。
    /// 按 FR-W-REM-2 目标时刻最多顺延一日，此处仅作防御。
    /// </summary>
    private static readonly TimeSpan MaxTimerDelay = TimeSpan.FromMilliseconds(2_147_483_000);

    private readonly IReminderPort _repository;
    private readonly IUserNotificationService _notifications;
    private readonly IUiDispatcher _dispatcher;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ReminderScheduler> _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, ITimer> _timers = new(StringComparer.Ordinal);

    private bool _disposed;

    /// <summary>构造。</summary>
    /// <param name="repository">提醒持久化端口。</param>
    /// <param name="notifications">通知服务（通知分类 / 勿扰 / 截断均由实现方负责）。</param>
    /// <param name="dispatcher">UI 线程调度（FR-W-ARCH-3：通知与列表刷新必须在 UI 线程投递）。</param>
    /// <param name="timeProvider">时钟（测试注入假时钟）。</param>
    /// <param name="logger">日志。</param>
    public ReminderScheduler(
        IReminderPort repository,
        IUserNotificationService notifications,
        IUiDispatcher dispatcher,
        TimeProvider timeProvider,
        ILogger<ReminderScheduler> logger)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(notifications);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _repository = repository;
        _notifications = notifications;
        _dispatcher = dispatcher;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /* ===================================================================== */
    /* FR-W-REM-2 / FR-W-REM-3：纯逻辑（可直接单测）                          */
    /* ===================================================================== */

    /// <summary>
    /// FR-W-REM-2：计算目标触发时刻（Unix 毫秒）。
    /// 取**本地今日**该时刻；若 <c>&lt;= from</c> 则顺延至**次日**。
    /// </summary>
    /// <param name="target"><c>HH:MM</c>（或 <c>H:MM</c>，服务端可能下发不补零的小时）。</param>
    /// <param name="from">计算基准（通常为现在的 Unix 毫秒）。**必须**显式传入以便单测。</param>
    /// <returns>触发时刻（Unix 毫秒）；<paramref name="target"/> 非法时回退为 <paramref name="from"/> + 1 小时。</returns>
    public static long ComputeFireAt(string? target, long from)
    {
        if (!TryParseTarget(target, out var timeOfDay))
        {
            // 格式非法：与 Linux 端一致回退为 1 小时后（**不抛错**：提醒是尽力而为的增强功能）。
            return from + 3_600_000L;
        }

        var baseTime = DateTimeOffset.FromUnixTimeMilliseconds(from).ToLocalTime();
        var candidate = new DateTimeOffset(
            baseTime.Year,
            baseTime.Month,
            baseTime.Day,
            timeOfDay.Hour,
            timeOfDay.Minute,
            0,
            baseTime.Offset);

        if (candidate.ToUnixTimeMilliseconds() <= from)
        {
            // 今日该时刻已过（或正好现在）→ 顺延至次日。
            candidate = candidate.AddDays(1);
        }

        return candidate.ToUnixTimeMilliseconds();
    }

    /// <summary>
    /// FR-W-REM-3：去重键 <c>reminder_{requestIds.join("_")}_{target}_{hash(text)}</c>。
    /// </summary>
    /// <param name="requestIds">被防抖合并的**全部**用户消息 id（顺序即服务端下发顺序，参与拼接）。</param>
    /// <param name="target"><c>HH:MM</c>。</param>
    /// <param name="text">提醒文案。</param>
    public static string BuildReminderId(IReadOnlyList<string> requestIds, string? target, string? text)
    {
        ArgumentNullException.ThrowIfNull(requestIds);

        var joined = requestIds.Count == 0
            ? string.Empty
            : string.Join('_', requestIds);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"reminder_{joined}_{target}_{SimpleHash(text)}");
    }

    /// <summary>
    /// 跨端一致的稳定短哈希：等价于 Linux 端 / Android 端的 Java <c>String.hashCode()</c> 形态
    /// （<c>hash = (hash &lt;&lt; 5) - hash + codeUnit</c>，int32 环绕，取绝对值后转 36 进制小写）。
    /// **不得**改用 <c>string.GetHashCode()</c>：它带随机化种子，跨进程不稳定，去重键会失效。
    /// </summary>
    public static string SimpleHash(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "0";
        }

        var hash = 0;
        foreach (var codeUnit in text)
        {
            unchecked
            {
                hash = (hash << 5) - hash + codeUnit;
            }
        }

        // int.MinValue 的绝对值超出 int 范围 → 先用 long 承接。
        var magnitude = Math.Abs((long)hash);
        return ToBase36(magnitude);
    }

    private static string ToBase36(long value)
    {
        if (value == 0)
        {
            return "0";
        }

        const string Digits = "0123456789abcdefghijklmnopqrstuvwxyz";
        Span<char> buffer = stackalloc char[16];
        var index = buffer.Length;
        var remaining = value;
        while (remaining > 0)
        {
            index -= 1;
            buffer[index] = Digits[(int)(remaining % 36)];
            remaining /= 36;
        }

        return new string(buffer[index..]);
    }

    private static bool TryParseTarget(string? target, out TimeOnly timeOfDay)
    {
        var trimmed = (target ?? string.Empty).Trim();
        return TimeOnly.TryParseExact(
                   trimmed,
                   ["HH:mm", "H:mm"],
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.None,
                   out timeOfDay)
               || TimeOnly.TryParseExact(
                   trimmed,
                   "HH:mm",
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.None,
                   out timeOfDay);
    }

    /* ===================================================================== */
    /* IReminderScheduler                                                    */
    /* ===================================================================== */

    /// <inheritdoc />
    public async Task ScheduleAsync(
        IReadOnlyList<string> requestIds,
        TimerInstructionDto instruction,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(requestIds);
        ArgumentNullException.ThrowIfNull(instruction);

        if (string.IsNullOrWhiteSpace(instruction.Target))
        {
            // 服务端只在下发 timerInstruction 时才带 target；缺失说明帧不完整，**不得**猜一个时间。
            _logger.LogWarning("timerInstruction 缺少 target，已忽略排程");
            return;
        }

        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var target = instruction.Target.Trim();
        var text = instruction.Text ?? string.Empty;
        var fireAt = ComputeFireAt(target, now);
        var reminderId = BuildReminderId(requestIds, target, text);

        var reminder = new ReminderInfo(reminderId, target, fireAt, text, "pending", now);

        bool created;
        try
        {
            created = await _repository.InsertKeepAsync(reminder, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "提醒落库失败 reminderId={ReminderId}", reminderId);
            return;
        }

        if (!created)
        {
            // FR-W-REM-3 KEEP：保留原计划，不重复排程、不改时间、不复活终态。
            _logger.LogInformation("提醒已存在，保留原计划（KEEP 语义）reminderId={ReminderId}", reminderId);
            return;
        }

        _logger.LogInformation(
            "已排程提醒 reminderId={ReminderId} target={Target} fireAt={FireAt}",
            reminderId,
            target,
            fireAt);
        Arm(reminder);
    }

    /// <inheritdoc />
    public async Task RestoreAsync(CancellationToken ct = default)
    {

        IReadOnlyList<ReminderInfo> pending;
        try
        {
            pending = await _repository.ListPendingAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "装载已排程提醒失败");
            return;
        }

        _logger.LogInformation("装载已排程提醒 count={Count}", pending.Count);

        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var fired = 0;
        var discarded = 0;

        foreach (var reminder in pending)
        {
            if (reminder.FireAt > now)
            {
                Arm(reminder);
                continue;
            }

            // FR-W-REM-4：已过期未触发 —— 超过 30 分钟丢弃，否则立即触发。
            if (await DiscardOrFireAsync(reminder, now, ct).ConfigureAwait(false))
            {
                fired += 1;
            }
            else
            {
                discarded += 1;
            }
        }

        if (fired > 0 || discarded > 0)
        {
            _logger.LogInformation("启动提醒规整完成 fired={Fired} discarded={Discarded}", fired, discarded);
        }

        try
        {
            _ = await _repository.PruneFinishedAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 清理失败不影响功能（只是终态行残留）。
            _logger.LogDebug(ex, "清理终态提醒失败");
        }
    }

    /// <inheritdoc />
    public async Task ResendOverdueAsync(CancellationToken ct = default)
    {
        IReadOnlyList<ReminderInfo> pending;
        try
        {
            pending = await _repository.ListPendingAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "唤醒后校验提醒失败");
            return;
        }

        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var fired = 0;

        foreach (var reminder in pending)
        {
            if (reminder.FireAt <= now)
            {
                if (await DiscardOrFireAsync(reminder, now, ct).ConfigureAwait(false))
                {
                    fired += 1;
                }

                continue;
            }

            if (!IsArmed(reminder.ReminderId))
            {
                // 退避计时器可能因休眠 / 系统时钟被修改而失效 → 重新装载（FR-W-REM-7 / EDGE-W-13）。
                Arm(reminder);
            }
        }

        if (fired > 0)
        {
            _logger.LogInformation("唤醒后补发提醒 count={Count}", fired);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ReminderInfo>> ListPendingAsync(CancellationToken ct = default)
    {
        try
        {
            return await _repository.ListPendingAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "读取已排程提醒失败");
            return [];
        }
    }

    /// <inheritdoc />
    public async Task CancelAsync(string reminderId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reminderId);

        Disarm(reminderId);

        bool cancelled;
        try
        {
            cancelled = await _repository.CancelAsync(reminderId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "取消提醒失败 reminderId={ReminderId}", reminderId);
            return;
        }

        if (cancelled)
        {
            _logger.LogInformation("提醒已取消 reminderId={ReminderId}", reminderId);
        }
    }

    /// <summary>释放全部计时器。</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var timer in _timers.Values)
            {
                timer.Dispose();
            }

            _timers.Clear();
        }
    }

    /* ===================================================================== */
    /* 内部                                                                  */
    /* ===================================================================== */

    /// <summary>
    /// 过期提醒的两种归宿（FR-W-REM-4）。
    /// </summary>
    /// <returns><see langword="true"/> = 立即触发；<see langword="false"/> = 丢弃。</returns>
    private async Task<bool> DiscardOrFireAsync(ReminderInfo reminder, long now, CancellationToken ct)
    {
        var lateBy = now - reminder.FireAt;
        if (lateBy > ProtocolConstants.ReminderStaleMs)
        {
            _logger.LogInformation(
                "丢弃过期提醒（超过 {StaleMs} 分钟）reminderId={ReminderId} lateMinutes={LateMinutes}",
                ProtocolConstants.ReminderStaleMs / 60_000,
                reminder.ReminderId,
                lateBy / 60_000);
            await SafeCancelAsync(reminder.ReminderId, ct).ConfigureAwait(false);
            return false;
        }

        await FireAsync(reminder, ct).ConfigureAwait(false);
        return true;
    }

    private void Arm(ReminderInfo reminder)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (_timers.TryGetValue(reminder.ReminderId, out var existing))
            {
                existing.Dispose();
                _timers.Remove(reminder.ReminderId);
            }

            var delay = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds() - reminder.FireAt;
            if (delay >= 0)
            {
                // 已到期：交由调用方的过期分支处理，避免在锁内触发 IO。
                return;
            }

            var effective = TimeSpan.FromMilliseconds(-delay);
            var clamped = effective > MaxTimerDelay ? MaxTimerDelay : effective;

            var timer = _timeProvider.CreateTimer(
                static state =>
                {
                    var (scheduler, id) = ((ReminderScheduler Scheduler, string ReminderId))state!;
                    scheduler.OnTimer(id);
                },
                (this, reminder.ReminderId),
                clamped,
                Timeout.InfiniteTimeSpan);

            _timers[reminder.ReminderId] = timer;
        }
    }

    private bool IsArmed(string reminderId)
    {
        lock (_gate)
        {
            return _timers.ContainsKey(reminderId);
        }
    }

    private void Disarm(string reminderId)
    {
        lock (_gate)
        {
            if (_timers.Remove(reminderId, out var timer))
            {
                timer.Dispose();
            }
        }
    }

    private void OnTimer(string reminderId)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (_timers.Remove(reminderId, out var timer))
            {
                timer.Dispose();
            }
        }

        // 计时器回调在 ThreadPool 线程上：先回读库中现状（可能已被取消 / 已触发），再决定是否通知。
        _ = Task.Run(async () =>
        {
            try
            {
                var reminder = await _repository.GetAsync(reminderId).ConfigureAwait(false);
                if (reminder is null || !string.Equals(reminder.Status, "pending", StringComparison.Ordinal))
                {
                    return;
                }

                var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
                if (reminder.FireAt > now)
                {
                    // 系统时钟被回拨（EDGE-W-13）：重新装载，不误触发。
                    Arm(reminder);
                    return;
                }

                if (now - reminder.FireAt > ProtocolConstants.ReminderStaleMs)
                {
                    await SafeCancelAsync(reminder.ReminderId, CancellationToken.None).ConfigureAwait(false);
                    return;
                }

                await FireAsync(reminder, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "提醒触发流程失败 reminderId={ReminderId}", reminderId);
            }
        });
    }

    /// <summary>
    /// FR-W-REM-5：置 <c>fired</c> 并推送高优先级通知。
    /// 通知**必须**经 <see cref="IUiDispatcher"/> 投递（FR-W-ARCH-3），
    /// 且**必须**写进通知中心 —— 因此走 <see cref="NotificationCategory.Reminder"/>（语义 ID 1005）。
    /// </summary>
    private async Task FireAsync(ReminderInfo reminder, CancellationToken ct)
    {
        // 先置终态：置状态失败（例如已被并发取消）时不得弹通知，否则会出现「已取消却又提醒」。
        bool marked;
        try
        {
            marked = await _repository.MarkFiredAsync(reminder.ReminderId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "标记提醒已触发失败 reminderId={ReminderId}", reminder.ReminderId);
            return;
        }

        if (!marked)
        {
            _logger.LogDebug("提醒已非 pending，跳过触发 reminderId={ReminderId}", reminder.ReminderId);
            return;
        }

        var title = I18n.T("notification.reminder.title");
        var body = I18n.T("notification.reminder.body", reminder.TargetTime, reminder.Text);

        await _dispatcher.InvokeAsync(() =>
        {
            _notifications.Notify(NotificationCategory.Reminder, title, body);
        }).ConfigureAwait(false);

        _logger.LogInformation(
            "提醒已触发 reminderId={ReminderId} target={Target}",
            reminder.ReminderId,
            reminder.TargetTime);
    }

    private async Task SafeCancelAsync(string reminderId, CancellationToken ct)
    {
        try
        {
            _ = await _repository.CancelAsync(reminderId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "丢弃过期提醒时置状态失败 reminderId={ReminderId}", reminderId);
        }
    }
}
