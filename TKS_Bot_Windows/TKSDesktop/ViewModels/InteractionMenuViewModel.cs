using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TKSDesktop.App;
using TKSDesktop.Contracts.Dtos;
using TKSDesktop.Core.Platform;
using TKSDesktop.Core.Services;

namespace TKSDesktop.ViewModels;

/// <summary>
/// 互动菜单中的一件物品（FR-W-INT-1..9）。
///
/// ⚠️ 置灰依据**必须**用服务端 <c>affordable</c>，**不得**用本地余额自算（本地缓存过期会误判 —— FR-W-INT-4）。
/// </summary>
public sealed class InteractionItemViewModel : ObservableObject
{
    /// <summary>由服务端 DTO 投影。</summary>
    public InteractionItemViewModel(InteractionItemDto dto, int balance)
    {
        ArgumentNullException.ThrowIfNull(dto);

        ItemId = dto.Id;
        Name = dto.Name;
        CostPoints = dto.CostPoints;
        SortOrder = dto.SortOrder;

        // 图标三级回退：iconUrl → icon（emoji）→ 程序化占位（FR-W-INT-9 / EDGE-W-23）。
        IconUrl = string.IsNullOrWhiteSpace(dto.IconUrl) ? null : dto.IconUrl;
        Emoji = string.IsNullOrWhiteSpace(dto.Icon) ? null : dto.Icon;
        UsePlaceholder = IconUrl is null && Emoji is null;

        // ⚠️ 置灰一律用服务端 affordable（FR-W-INT-4）。
        Affordable = dto.Affordable;

        Balance = balance;
    }

    /// <summary>物品 ID。</summary>
    public string ItemId { get; }

    /// <summary>物品名。</summary>
    public string Name { get; }

    /// <summary>价格（积分）。</summary>
    public int CostPoints { get; }

    /// <summary>服务端排序（FR-W-INT-2：按 <c>sort_order</c>）。</summary>
    public int SortOrder { get; }

    /// <summary>图标地址（当前恒为空 —— EDGE-W-23）。</summary>
    public string? IconUrl { get; }

    /// <summary>emoji 图标（第二级回退）。</summary>
    public string? Emoji { get; }

    /// <summary>是否使用程序化占位图（第三级回退）。</summary>
    public bool UsePlaceholder { get; }

    /// <summary>服务端判定的可购买性（**唯一置灰依据**）。</summary>
    public bool Affordable { get; }

    /// <summary>当前余额（仅用于计算差额提示，不用于置灰判定）。</summary>
    public int Balance { get; }

    /// <summary>是否可点击（置灰时禁用）。</summary>
    public bool CanSend => Affordable;

    /// <summary>不足时展示的差额文案（<c>interaction.insufficient</c>，参数为差额 —— FR-W-INT-5）。</summary>
    public string InsufficientText => Affordable
        ? string.Empty
        : I18n.T("interaction.insufficient", Math.Max(0, CostPoints - Balance));

    /// <summary>价格文案。</summary>
    public string CostText => CostPoints.ToString(CultureInfo.CurrentCulture);

    /// <summary>emoji 或占位符（第三级回退用 <c>🐾</c> 之外的中性符号，由 XAML 走 <c>PandaBadge</c>）。</summary>
    public string DisplayGlyph => Emoji ?? "\u25CB";
}

/// <summary>
/// 互动菜单视图模型（FR-W-INT-1..9 / FR-W-MC-5 / FR-W-PROG-4）。
///
/// <list type="bullet">
///   <item>按服务端 <c>sort_order</c> 排列（FR-W-INT-2）；</item>
///   <item>置灰一律用服务端 <c>affordable</c>（FR-W-INT-4），不足时显示差额（FR-W-INT-5）；</item>
///   <item>送出带幂等 <c>requestId</c>（重试复用同一值 —— FR-W-INT-11）；</item>
///   <item>离线缓存态整体禁用（FR-W-PROG-4）；</item>
///   <item>等级变化按三态反馈（RESTORE 克制、UPGRADE 才庆祝 —— FR-W-MC-5 同源要求）。</item>
/// </list>
/// </summary>
public sealed partial class InteractionMenuViewModel : ObservableObject, IDisposable
{
    private readonly IGamificationService _gamification;
    private readonly IUiDispatcher _dispatcher;

    private bool _disposed;

    /// <summary>构造。</summary>
    public InteractionMenuViewModel(IGamificationService gamification, IUiDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(gamification);
        ArgumentNullException.ThrowIfNull(dispatcher);

        _gamification = gamification;
        _dispatcher = dispatcher;
        _gamification.ProgressChanged += OnProgressChanged;
    }

    /// <summary>请求关闭浮层（送出成功 / 用户点击遮罩）。</summary>
    public event EventHandler? CloseRequested;

    /// <summary>可购买物品（按服务端 <c>sort_order</c>）。</summary>
    public ObservableCollection<InteractionItemViewModel> Items { get; } = [];

    /// <summary>是否正在加载。</summary>
    [ObservableProperty]
    private bool _isLoading;

    /// <summary>当前余额（展示用）。</summary>
    [ObservableProperty]
    private int _balance;

    /// <summary>状态文案（送出中 / 超时 / 已退回 / 失败）。</summary>
    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <summary>是否显示状态条。</summary>
    [ObservableProperty]
    private bool _isStatusVisible;

    /// <summary>离线缓���态：整体禁用（FR-W-PROG-4）。</summary>
    [ObservableProperty]
    private bool _isOfflineCache;

    /// <summary>离线提示。</summary>
    [ObservableProperty]
    private string _offlineNoticeText = string.Empty;

    /// <summary>可选附言（§7.1a：输入框有文字时随送礼一并提交）。</summary>
    [ObservableProperty]
    private string _messageText = string.Empty;

    /// <summary>是否为空（无可用物品 —— <c>interaction.empty</c>）。</summary>
    public bool IsEmpty => Items.Count == 0;

    /// <summary>是否可送出（在线、有物品、非加载中）。</summary>
    public bool CanSend => !IsOfflineCache && !IsLoading && Items.Count > 0;

    /* ---- 静态 UI 文案（XAML 只绑定 —— V-W-S8） ---- */

    /// <summary>浮层标题。</summary>
    public string TitleText => I18n.T("interaction.title");

    /// <summary>送出按钮文案。</summary>
    public string SendText => I18n.T("interaction.send");

    /// <summary>空列表文案。</summary>
    public string EmptyText => I18n.T("interaction.empty");

    /// <summary>等待文案。</summary>
    public string WaitingText => I18n.T("interaction.waiting");

    /// <summary>超时文案。</summary>
    public string TimeoutText => I18n.T("interaction.timeout");

    /// <summary>退回文案。</summary>
    public string RefundedText => I18n.T("interaction.refunded");

    /// <summary>加载中文案。</summary>
    public string LoadingText => I18n.T("common.loading");

    /// <summary>关闭文案。</summary>
    public string CloseText => I18n.T("common.close");

    /// <summary>积分标签。</summary>
    public string PointsLabel => I18n.T("profile.points");

    /// <summary>
    /// 打开浮层：拉取互动菜单（<c>GET /interaction/items</c>）。
    /// </summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (IsOfflineCache)
        {
            // 离线缓存态不请求、不展示可点项（FR-W-PROG-4）。
            await _dispatcher.InvokeAsync(() =>
            {
                Items.Clear();
                OnPropertyChanged(nameof(IsEmpty));
                OnPropertyChanged(nameof(CanSend));
            }).ConfigureAwait(true);
            return;
        }

        IsLoading = true;
        try
        {
            var data = await _gamification.GetInteractionItemsAsync(ct).ConfigureAwait(true);

            var rows = data.Items
                .Select(dto => new InteractionItemViewModel(dto, data.Balance))
                .OrderBy(static item => item.SortOrder)
                .ToList();

            await _dispatcher.InvokeAsync(() =>
            {
                Items.Clear();
                foreach (var row in rows)
                {
                    Items.Add(row);
                }

                Balance = data.Balance;
                OnPropertyChanged(nameof(IsEmpty));
                OnPropertyChanged(nameof(CanSend));
            }).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await _dispatcher.InvokeAsync(() =>
            {
                ShowStatus(I18n.T(Contracts.ErrorCatalog.UnknownI18nKey));
            }).ConfigureAwait(true);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// 送出一件物品（幂等 <c>requestId</c>；超时 90s 由服务层按 <c>ProtocolConstants.InteractionTimeoutMs</c> 控制 —— FR-W-INT-8）。
    /// </summary>
    [RelayCommand]
    private async Task SendAsync(InteractionItemViewModel? item)
    {
        if (item is null || !item.CanSend || IsOfflineCache)
        {
            return;
        }

        var requestId = Guid.NewGuid().ToString("D");
        ShowStatus(I18n.T("interaction.waiting"));

        try
        {
            var outcome = await _gamification
                .SendInteractionAsync(item.ItemId, requestId, string.IsNullOrWhiteSpace(MessageText) ? null : MessageText)
                .ConfigureAwait(true);

            if (outcome.Success)
            {
                ShowStatus(string.Empty);
                CloseRequested?.Invoke(this, EventArgs.Empty);
                await LoadAsync().ConfigureAwait(true);
                return;
            }

            // 失败：区分「已退回」「不足」「超时」等（错误码 → i18n，未知回中性兜底）。
            var key = outcome.Refunded
                ? "interaction.refunded"
                : outcome.I18nKey ?? Contracts.ErrorCatalog.I18nKeyOf(outcome.ErrorCode);

            ShowStatus(I18n.T(key));
            await LoadAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // 超时：文案必须区别于普通失败（FR-W-INT-8）。
            ShowStatus(I18n.T("interaction.timeout"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ShowStatus(I18n.T(Contracts.ErrorCatalog.UnknownI18nKey));
        }
    }

    /// <summary>取消 / 关闭浮层。</summary>
    [RelayCommand]
    private void Close()
    {
        StatusText = string.Empty;
        IsStatusVisible = false;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gamification.ProgressChanged -= OnProgressChanged;
        GC.SuppressFinalize(this);
    }

    private void OnProgressChanged(object? sender, ProgressSnapshot snapshot)
        => _dispatcher.Invoke(() =>
        {
            Balance = snapshot.Balance;
            IsOfflineCache = snapshot.IsOfflineCache;
            OfflineNoticeText = snapshot.IsOfflineCache
                ? I18n.T("common.offlineData", FormatSyncTime(snapshot.SyncedAt))
                : string.Empty;

            OnPropertyChanged(nameof(CanSend));
        });

    private void ShowStatus(string text)
    {
        StatusText = text;
        IsStatusVisible = !string.IsNullOrEmpty(text);
    }

    private static string FormatSyncTime(long unixMilliseconds)
        => MessageItemViewModel
            .FromUnixMilliseconds(unixMilliseconds)
            .ToString("MM-dd HH:mm", CultureInfo.CurrentCulture);
}
