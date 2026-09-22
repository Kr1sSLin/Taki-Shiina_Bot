using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using TKSDesktop.App;
using TKSDesktop.Contracts;
using TKSDesktop.Core.Services;

namespace TKSDesktop.ViewModels;

/// <summary>
/// 一条消息的气泡视图模型（FR-W-CHAT-*、FR-W-SYNC-11、FR-W-INT-6、C-5）。
///
/// ⚠️ 互动气泡**复用同一个气泡模板**（仅由 <see cref="IsInteraction"/> / <see cref="InteractionFailed"/>
///    切换标记与样式），**不新建独立气泡组件**（FR-W-INT-6）。
/// ⚠️ 日期分隔线由 <see cref="IsHistorySeparator"/> 单独渲染为细线，**不得**当普通气泡（C-5）。
/// ⚠️ 所有可写状态都只由 <see cref="ChatViewModel"/> 经 <c>IUiDispatcher</c> 变更（FR-W-ARCH-3）。
/// </summary>
public sealed partial class MessageItemViewModel : ObservableObject
{
    /// <summary>构造。</summary>
    /// <param name="view">服务层下发的消息视图（冻结契约）。</param>
    public MessageItemViewModel(ChatMessageView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        MessageId = view.MessageId;
        IsUser = string.Equals(view.Role, "user", StringComparison.Ordinal);
        Timestamp = view.Timestamp;
        LocalTime = FromUnixMilliseconds(view.Timestamp);
        Attachments = new ObservableCollection<string>(view.AttachmentPaths ?? []);

        Apply(view);
    }

    /// <summary>主键（用户消息 = requestId；重发必须复用该值 —— FR-W-CHAT-11）。</summary>
    public string MessageId { get; }

    /// <summary>是否为用户消息（决定气泡左右对齐）。</summary>
    public bool IsUser { get; }

    /// <summary>是否为 Bot 消息。</summary>
    public bool IsBot => !IsUser;

    /// <summary>Unix 毫秒（排序与去重基准）。</summary>
    public long Timestamp { get; }

    /// <summary>本地时间（时间合并展示用；C-4）。</summary>
    public DateTimeOffset LocalTime { get; }

    /// <summary>正文。</summary>
    [ObservableProperty]
    private string _content = string.Empty;

    /// <summary>消息状态（<c>ProtocolConstants.Status*</c>）。</summary>
    [ObservableProperty]
    private string _status = string.Empty;

    /// <summary>失败错误码（客户端自有码或服务端码）。</summary>
    [ObservableProperty]
    private string? _errorCode;

    /// <summary>C-5：是否为「新的一天」分隔线。</summary>
    [ObservableProperty]
    private bool _isHistorySeparator;

    /// <summary>互动物品名（帧 / 历史行瞬态字段；为空表示非互动气泡）。</summary>
    [ObservableProperty]
    private string? _interactionItemName;

    /// <summary>互动物品 emoji（三级回退的中间一级）。</summary>
    [ObservableProperty]
    private string? _interactionItemIcon;

    /// <summary>互动失败标记（决定失败样式）。</summary>
    [ObservableProperty]
    private bool _interactionFailed;

    /// <summary>该消息已投递（送达确认后不再显示「发送中」）。</summary>
    [ObservableProperty]
    private bool _isDelivered;

    /// <summary>附件（图片）本地绝对路径；空表示纯文本。</summary>
    public ObservableCollection<string> Attachments { get; }

    /// <summary>是否为「互动」气泡（<c>InteractionItemName</c> / <c>Icon</c> 任一非空 —— FR-W-SYNC-11）。</summary>
    public bool IsInteraction =>
        !string.IsNullOrWhiteSpace(InteractionItemName) || !string.IsNullOrWhiteSpace(InteractionItemIcon);

    /// <summary>互动气泡右上角标记文案（<c>interaction.giftBadge</c>）。</summary>
    public string GiftBadgeText => I18n.T("interaction.giftBadge");

    /// <summary>发送中（本地乐观态）。</summary>
    public bool IsSendingState => string.Equals(Status, ProtocolConstants.StatusSending, StringComparison.Ordinal);

    /// <summary>已是最终态（<c>sent</c> / <c>received</c>）。</summary>
    public bool IsSettled =>
        string.Equals(Status, ProtocolConstants.StatusSent, StringComparison.Ordinal)
        || string.Equals(Status, ProtocolConstants.StatusReceived, StringComparison.Ordinal);

    /// <summary>
    /// 时间是否显示。同日内相邻消息间隔 ≤ 1 分钟时合并为一条时间（FR-W-CHAT-13），
    /// 由 <see cref="ChatViewModel"/> 在集合变化后统一重算（经 <c>IUiDispatcher</c>）。
    /// </summary>
    [ObservableProperty]
    private bool _showTimestamp = true;

    /// <summary>
    /// C-5 分隔线文案。**必须**取 i18n（<c>chat.daySeparator</c>），不得渲染服务端常量原文。
    /// </summary>
    public string SeparatorText => I18n.T("chat.daySeparator");

    /// <summary>发送失败（<c>error</c>；「重发」按钮据此显示）。</summary>
    public bool IsFailed =>
        string.Equals(Status, ProtocolConstants.StatusError, StringComparison.Ordinal) || InteractionFailed;

    /// <summary>失败文案：错误码 → i18n（未知码回中性兜底，V-W-S6）。</summary>
    public string ErrorText =>
        string.IsNullOrWhiteSpace(ErrorCode) ? string.Empty : I18n.T(ErrorCatalog.I18nKeyOf(ErrorCode));

    /// <summary>是否有附件（决定附件缩略图条是否占位）。</summary>
    public bool HasAttachments => Attachments.Count > 0;

    /// <summary>可失败重发（用户消息 + 失败 + 非互动合成气泡）。</summary>
    public bool CanRetry => IsUser && IsFailed;

    /// <summary>把服务层快照合并进本对象（流式 delta / 状态变化时调用）。</summary>
    internal void Apply(ChatMessageView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        Content = view.Content ?? string.Empty;
        Status = view.Status ?? string.Empty;
        ErrorCode = view.ErrorCode;
        IsHistorySeparator = view.IsHistorySeparator;
        InteractionItemName = view.InteractionItemName;
        InteractionItemIcon = view.InteractionItemIcon;
        InteractionFailed = view.InteractionFailed;
        SyncAttachments(view.AttachmentPaths);

        // 送达态：收到明确状态即视为已投递（发送中的本地占位不置位）。
        IsDelivered = IsSettled;
        RefreshDerived();
    }

    /// <summary>局部状态变更（离线兜底标记用 —— FR-W-CONN-8）。</summary>
    internal void SetStatus(string status, string? errorCode)
    {
        Status = status ?? string.Empty;
        ErrorCode = errorCode;
        IsDelivered = IsSettled;
        RefreshDerived();
    }

    private void RefreshDerived()
    {
        OnPropertyChanged(nameof(IsInteraction));
        OnPropertyChanged(nameof(GiftBadgeText));
        OnPropertyChanged(nameof(IsSendingState));
        OnPropertyChanged(nameof(IsSettled));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(ErrorText));
        OnPropertyChanged(nameof(HasAttachments));
        OnPropertyChanged(nameof(CanRetry));
    }

    /// <summary>就地同步附件集合并报告行数变化（不重建集合，避免列表闪烁）。</summary>
    private void SyncAttachments(IReadOnlyList<string>? paths)
    {
        var incoming = paths ?? [];
        if (Attachments.Count == incoming.Count)
        {
            var identical = true;
            for (var i = 0; i < incoming.Count; i++)
            {
                if (!string.Equals(Attachments[i], incoming[i], StringComparison.Ordinal))
                {
                    identical = false;
                    break;
                }
            }

            if (identical)
            {
                return;
            }
        }

        Attachments.Clear();
        foreach (var path in incoming)
        {
            Attachments.Add(path);
        }
    }

    /// <summary>Unix 毫秒 → 本地时间（越界值回退为 epoch，NFR-W-12）。</summary>
    internal static DateTimeOffset FromUnixMilliseconds(long milliseconds)
    {
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).ToLocalTime();
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTimeOffset.UnixEpoch.ToLocalTime();
        }
    }
}
