using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TKSDesktop.App;
using TKSDesktop.Contracts;
using TKSDesktop.Core.Platform;

namespace TKSDesktop.ViewModels;

/// <summary>历史页的一条 Bot 通知行（FR-W-HIS-1/2）。</summary>
public sealed partial class NotificationItemViewModel : ObservableObject
{
    /// <summary>由本地投影构建。</summary>
    public NotificationItemViewModel(LocalNotificationItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        NotificationId = item.NotificationId;
        ErrorCode = item.ErrorCode;
        Message = Truncate(item.Message);
        IsRead = item.IsRead;
        TimeText = MessageItemViewModel
            .FromUnixMilliseconds(item.Timestamp)
            .ToString("MM-dd HH:mm", CultureInfo.CurrentCulture);
    }

    /// <summary>主键。</summary>
    public string NotificationId { get; }

    /// <summary>服务端错误码。</summary>
    public string ErrorCode { get; }

    /// <summary>正文（按 <c>NotificationBodyMaxChars</c> 截断）。</summary>
    public string Message { get; }

    /// <summary>创建时间文案。</summary>
    public string TimeText { get; }

    /// <summary>是否已读。</summary>
    [ObservableProperty]
    private bool _isRead;

    /// <summary>未读标记（未读时显示红点）。</summary>
    public bool IsUnread => !IsRead;

    partial void OnIsReadChanged(bool value) => OnPropertyChanged(nameof(IsUnread));

    private static string Truncate(string? message)
    {
        var text = message ?? string.Empty;
        return text.Length <= ProtocolConstants.NotificationBodyMaxChars
            ? text
            : text[..ProtocolConstants.NotificationBodyMaxChars];
    }
}

/// <summary>记忆档案一行（FR-W-HIS-3/5；**只读**）。</summary>
public sealed class FactItemViewModel
{
    /// <summary>由本地投影构建。</summary>
    public FactItemViewModel(LocalFactItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        FactId = item.FactId;
        Fact = item.Fact;
        TimeText = MessageItemViewModel
            .FromUnixMilliseconds(item.Timestamp)
            .ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
    }

    /// <summary>主键。</summary>
    public string FactId { get; }

    /// <summary>事实正文。</summary>
    public string Fact { get; }

    /// <summary>时间文案。</summary>
    public string TimeText { get; }
}

/// <summary>
/// 历史与记忆页视图模型（FR-W-HIS-1..5）。
///
/// <list type="bullet">
///   <item>Bot 通知列表：未读数 + 单条标记已读 + 全部已读（FR-W-HIS-1/2）；</item>
///   <item>事实列表：**倒序**、**只读**、可搜索（FR-W-HIS-3/5）；</item>
///   <item>⚠️ 本页**必须**有明确可达入口（聊天页工具栏按钮），不得「已实现但 UI 不可达」。</item>
/// </list>
/// </summary>
public sealed partial class HistoryViewModel : ObservableObject
{
    private readonly ILocalHistoryReader _history;
    private readonly IUiDispatcher _dispatcher;

    /// <summary>构造。</summary>
    public HistoryViewModel(ILocalHistoryReader history, IUiDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(dispatcher);
        _history = history;
        _dispatcher = dispatcher;
    }

    /// <summary>Bot 通知（倒序）。</summary>
    public ObservableCollection<NotificationItemViewModel> Notifications { get; } = [];

    /// <summary>记忆事实（倒序）。</summary>
    public ObservableCollection<FactItemViewModel> Facts { get; } = [];

    /// <summary>未读数。</summary>
    [ObservableProperty]
    private int _unreadCount;

    /// <summary>是否存在未读（决定「全部已读」是否可用）。</summary>
    public bool HasUnread => UnreadCount > 0;

    /// <summary>记忆搜索词。</summary>
    [ObservableProperty]
    private string _factSearchTerm = string.Empty;

    /// <summary>通知列表是否为空。</summary>
    public bool IsNotificationEmpty => Notifications.Count == 0;

    /// <summary>事实列表是否为空。</summary>
    public bool IsFactEmpty => Facts.Count == 0;

    /* ---- 静态 UI 文案（XAML 只绑定 —— V-W-S8） ---- */

    /// <summary>页面标题。</summary>
    public string TitleText => I18n.T("history.title");

    /// <summary>通知分栏标题。</summary>
    public string NotificationsTitle => I18n.T("history.notifications");

    /// <summary>记忆分栏标题。</summary>
    public string FactsTitle => I18n.T("history.facts");

    /// <summary>未读数文案。</summary>
    public string UnreadText => I18n.T("history.unread", UnreadCount);

    /// <summary>单条「标记已读」按钮文案。</summary>
    public string MarkReadText => I18n.T("history.markRead");

    /// <summary>「全部已读」按钮文案。</summary>
    public string MarkAllReadText => I18n.T("history.markAllRead");

    /// <summary>记忆只读说明（FR-W-HIS-5）。</summary>
    public string FactsReadonlyText => I18n.T("history.facts.readonly");

    /// <summary>记忆搜索占位文案。</summary>
    public string FactSearchPlaceholder => I18n.T("history.search.placeholder");

    /// <summary>空态文案。</summary>
    public string EmptyText => I18n.T("history.empty");

    /// <summary>刷新按钮文案。</summary>
    public string RefreshText => I18n.T("common.refresh");

    /// <summary>关闭按钮文案。</summary>
    public string CloseText => I18n.T("common.close");

    /// <summary>加载页面数据（首次显示 / 刷新）。</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        var notifications = await _history.ListNotificationsAsync(200, ct).ConfigureAwait(true);
        var unread = await _history.CountUnreadNotificationsAsync(ct).ConfigureAwait(true);
        var facts = await _history.ListFactsAsync(FactSearchTerm, 300, ct).ConfigureAwait(true);

        await _dispatcher.InvokeAsync(() =>
        {
            Notifications.Clear();
            foreach (var item in notifications)
            {
                Notifications.Add(new NotificationItemViewModel(item));
            }

            UnreadCount = unread;
            ApplyFacts(facts);
            RaiseCounters();
        }).ConfigureAwait(true);
    }

    /// <summary>标记一条通知为已读（FR-W-HIS-2）。</summary>
    [RelayCommand]
    private async Task MarkReadAsync(NotificationItemViewModel? item)
    {
        if (item is null || item.IsRead)
        {
            return;
        }

        _ = await _history.MarkNotificationsReadAsync([item.NotificationId]).ConfigureAwait(true);

        await _dispatcher.InvokeAsync(() =>
        {
            item.IsRead = true;
            UnreadCount = Math.Max(0, UnreadCount - 1);
            RaiseCounters();
        }).ConfigureAwait(true);
    }

    /// <summary>全部标记为已读（FR-W-HIS-2）。</summary>
    [RelayCommand]
    private async Task MarkAllReadAsync()
    {
        if (UnreadCount == 0)
        {
            return;
        }

        _ = await _history.MarkAllNotificationsReadAsync().ConfigureAwait(true);

        await _dispatcher.InvokeAsync(() =>
        {
            foreach (var item in Notifications)
            {
                item.IsRead = true;
            }

            UnreadCount = 0;
            RaiseCounters();
        }).ConfigureAwait(true);
    }

    /// <summary>按关键字搜索记忆（FR-W-HIS-3）。</summary>
    [RelayCommand]
    private async Task SearchFactsAsync()
    {
        var facts = await _history.ListFactsAsync(FactSearchTerm, 300).ConfigureAwait(true);
        await _dispatcher.InvokeAsync(() => ApplyFacts(facts)).ConfigureAwait(true);
    }

    private void ApplyFacts(IReadOnlyList<LocalFactItem> facts)
    {
        Facts.Clear();
        foreach (var fact in facts)
        {
            Facts.Add(new FactItemViewModel(fact));
        }

        OnPropertyChanged(nameof(IsFactEmpty));
    }

    private void RaiseCounters()
    {
        OnPropertyChanged(nameof(HasUnread));
        OnPropertyChanged(nameof(UnreadText));
        OnPropertyChanged(nameof(IsNotificationEmpty));
    }
}
