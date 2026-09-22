using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TKSDesktop.App;
using TKSDesktop.Core.Platform;
using TKSDesktop.Core.Services;

namespace TKSDesktop.ViewModels;

/// <summary>已排程提醒行（FR-W-REM-6 / §9.6）。</summary>
public sealed class ReminderItemViewModel
{
    /// <summary>由服务层快照构建。</summary>
    public ReminderItemViewModel(ReminderInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        ReminderId = info.ReminderId;
        TargetTime = info.TargetTime;
        Text = info.Text;
        FireAtText = MessageItemViewModel
            .FromUnixMilliseconds(info.FireAt)
            .ToString("MM-dd HH:mm", CultureInfo.CurrentCulture);
    }

    /// <summary>主键（去重键 —— FR-W-REM-3）。</summary>
    public string ReminderId { get; }

    /// <summary>提醒时刻 `HH:mm`（服务端下发，展示用）。</summary>
    public string TargetTime { get; }

    /// <summary>提醒文本。</summary>
    public string Text { get; }

    /// <summary>触发时刻文案（本地时间）。</summary>
    public string FireAtText { get; }
}

/// <summary>
/// 已排程提醒视图模型（FR-W-REM-6）。
///
/// <list type="bullet">
///   <item>只列出 <c>pending</c> 提醒（按 <c>fire_at</c> 升序，由服务层保证）；</item>
///   <item>取消需二次确认，取消后刷新列表；</item>
///   <item>空闲 / 失败均给出明确文案，**不静默**（NFR-W-8）。</item>
/// </list>
/// </summary>
public sealed partial class RemindersViewModel : ObservableObject
{
    private readonly IReminderScheduler _scheduler;
    private readonly IUserPrompt _prompt;
    private readonly IUiDispatcher _dispatcher;

    /// <summary>构造。</summary>
    public RemindersViewModel(IReminderScheduler scheduler, IUserPrompt prompt, IUiDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(dispatcher);

        _scheduler = scheduler;
        _prompt = prompt;
        _dispatcher = dispatcher;
    }

    /// <summary>已排程提醒。</summary>
    public ObservableCollection<ReminderItemViewModel> Reminders { get; } = [];

    /// <summary>是否为空。</summary>
    [ObservableProperty]
    private bool _isEmpty = true;

    /// <summary>操作结果文案（已取消 / 失败）。</summary>
    [ObservableProperty]
    private string _statusText = string.Empty;

    /* ---- 静态 UI 文案（XAML 只绑定 —— V-W-S8） ---- */

    /// <summary>页面标题（<c>reminder.title</c>）。</summary>
    public string TitleText => I18n.T("reminder.title");

    /// <summary>空态文案（<c>reminder.empty</c>）。</summary>
    public string EmptyText => I18n.T("reminder.empty");

    /// <summary>「取消提醒」按钮文案。</summary>
    public string CancelText => I18n.T("reminder.cancel");

    /// <summary>「提醒时间」列标题。</summary>
    public string TimeColumnText => I18n.T("reminder.column.time");

    /// <summary>「内容」列标题。</summary>
    public string TextColumnText => I18n.T("reminder.column.text");

    /// <summary>刷新按钮文案。</summary>
    public string RefreshText => I18n.T("common.refresh");

    /// <summary>关闭按钮文案。</summary>
    public string CloseText => I18n.T("common.close");

    /// <summary>加载列表（进入页面 / 刷新）。</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        IReadOnlyList<ReminderInfo> rows;
        try
        {
            rows = await _scheduler.ListPendingAsync(ct).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 读取失败不得崩溃，也不得谎报「没有提醒」——给出中性提示。
            await _dispatcher.InvokeAsync(() =>
            {
                StatusText = I18n.T("error.unknown");
            }).ConfigureAwait(true);
            return;
        }

        await _dispatcher.InvokeAsync(() =>
        {
            Reminders.Clear();
            foreach (var info in rows)
            {
                Reminders.Add(new ReminderItemViewModel(info));
            }

            IsEmpty = Reminders.Count == 0;
        }).ConfigureAwait(true);
    }

    /// <summary>取消一条提醒（二次确认后调用 —— FR-W-REM-6）。</summary>
    [RelayCommand]
    private async Task CancelAsync(ReminderItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        if (!_prompt.Confirm(I18n.T("reminder.cancel")))
        {
            return;
        }

        try
        {
            await _scheduler.CancelAsync(item.ReminderId).ConfigureAwait(true);
            StatusText = I18n.T("reminder.cancelled");
            await LoadAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StatusText = I18n.T("error.unknown");
            await LoadAsync().ConfigureAwait(true);
        }
    }
}
