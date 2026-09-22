using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TKSDesktop.App;
using TKSDesktop.Contracts;
using TKSDesktop.Contracts.Dtos;
using TKSDesktop.Core.Platform;
using TKSDesktop.Core.Services;

namespace TKSDesktop.ViewModels;

/// <summary>一条积分流水行（FR-W-PT-2/3/7）。</summary>
public sealed class PointsLedgerRow
{
    /// <summary>由服务端 DTO 投影。</summary>
    public PointsLedgerRow(PointsLedgerItemDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        Id = dto.Id;
        ChangeAmount = dto.ChangeAmount;
        BalanceAfter = dto.BalanceAfter;
        ReasonCode = dto.ReasonCode;
        CreatedAt = dto.CreatedAt;
        BusinessDate = dto.BusinessDate;
        TimeText = MessageItemViewModel
            .FromUnixMilliseconds(dto.CreatedAt)
            .ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
    }

    /// <summary>服务端流水 ID（分页稳定键）。</summary>
    public long Id { get; }

    /// <summary>变动值（可负）。</summary>
    public int ChangeAmount { get; }

    /// <summary>变动后余额。</summary>
    public int BalanceAfter { get; }

    /// <summary>事由码（**未知必须回退 <c>points.reason.unknown</c>** —— FR-W-PT-3）。</summary>
    public string? ReasonCode { get; }

    /// <summary>创建时刻（Unix 毫秒）。</summary>
    public long CreatedAt { get; }

    /// <summary>业务日期。</summary>
    public string? BusinessDate { get; }

    /// <summary>本地时间文案（`YYYY-MM-DD HH:mm`）。</summary>
    public string TimeText { get; }

    /// <summary>事由文案：<c>points.reason.{code}</c>，未知码回退中性兜底。</summary>
    public string ReasonText
    {
        get
        {
            var key = string.IsNullOrWhiteSpace(ReasonCode) ? null : $"points.reason.{ReasonCode}";
            return key is not null && I18n.Has(key) ? I18n.T(key) : I18n.T("points.reason.unknown");
        }
    }

    /// <summary>变动文案（`+N` / `-N` 带符号）。</summary>
    public string ChangeText => ChangeAmount >= 0
        ? "+" + ChangeAmount.ToString(CultureInfo.CurrentCulture)
        : ChangeAmount.ToString(CultureInfo.CurrentCulture);

    /// <summary>变动后余额文案。</summary>
    public string BalanceAfterText => BalanceAfter.ToString(CultureInfo.CurrentCulture);

    /// <summary>是否为加分（决定着色）。</summary>
    public bool IsGain => ChangeAmount >= 0;
}

/// <summary>等级说明页一行（FR-W-LV-2；<c>level_code</c> 空必须防御 —— EDGE-W-24）。</summary>
public sealed class LevelConfigRow
{
    /// <summary>由服务端配置项投影（配色 / emoji 用 <see cref="LevelVisuals"/>）。</summary>
    public LevelConfigRow(LevelConfigEntryDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);

        // ⚠️ EDGE-W-24：`level_code` 可能为空串，直接用作 key 会重复崩溃；
        //    LevelVisuals.Resolve 对空值返回中性兜底视觉。
        var visual = LevelVisuals.Resolve(dto.LevelCode);

        LevelCode = visual.Code;
        Emoji = visual.Emoji;
        GradientFrom = visual.GradientFrom;
        GradientTo = visual.GradientTo;
        Accent = visual.Accent;

        DisplayName = string.IsNullOrWhiteSpace(dto.LevelName)
            ? visual.FallbackName
            : dto.LevelName;

        ThresholdDays = dto.ThresholdDays;
        SortOrder = dto.SortOrder;
    }

    /// <summary>等级码（空值已归一为 <c>NONE</c>）。</summary>
    public string LevelCode { get; }

    /// <summary>称号。</summary>
    public string DisplayName { get; }

    /// <summary>所需连续天数（以服务端 <c>threshold_days</c> 为准 —— W-P2）。</summary>
    public int ThresholdDays { get; }

    /// <summary>排序（以服务端 <c>sort_order</c> 为准）。</summary>
    public int SortOrder { get; }

    /// <summary>emoji（C-1 逐值一致）。</summary>
    public string Emoji { get; }

    /// <summary>渐变起始色。</summary>
    public string GradientFrom { get; }

    /// <summary>渐变结束色。</summary>
    public string GradientTo { get; }

    /// <summary>强调色。</summary>
    public string Accent { get; }

    /// <summary>是否七彩动态（PANDA_LV7 —— FR-W-LV-4）。</summary>
    public bool IsAnimated => string.Equals(LevelCode, "PANDA_LV7", StringComparison.Ordinal);

    /// <summary>「已达 N 天」文案（阈值只读展示）。</summary>
    public string ThresholdText => ThresholdDays.ToString(CultureInfo.CurrentCulture);
}

/// <summary>
/// 个人中心视图模型（FR-W-LV-1/2/3/4/5/5a/6/8、FR-W-PT-1/2/3/7、FR-W-MC-1..7、FR-W-PROG-4）。
///
/// <list type="bullet">
///   <item>徽章 + 称号 + 连续天数 + 进度条 + 距下一等级天数（FR-W-LV-1）；</item>
///   <item><c>IsDefaultLevel</c> 或 <c>LevelName</c> 为空 → <c>profile.level.justStarted</c>（FR-W-LV-3）；</item>
///   <item>断签清零时显示 <c>profile.pointsUnaffected</c>（FR-W-LV-8）；</item>
///   <item>补签规则用服务端 <c>MonthlyGrant</c> / <c>MaxAvailable</c>（FR-W-MC-1）；</item>
///   <item>补签日历**唯一数据源**是 <c>GetMakeupCandidatesAsync()</c>（FR-W-MC-2）；</item>
///   <item>补签点击 → **二次确认** → 才调 <c>UseMakeupCardAsync</c>（FR-W-MC-3），失败按 <c>ErrorCode</c> 显示并刷新日历；</item>
///   <item><c>IsOfflineCache</c> → <c>common.offlineData</c> 提示 + 互动菜单整体禁用（FR-W-PROG-4）。</item>
/// </list>
/// </summary>
public sealed partial class ProfileViewModel : ObservableObject, IDisposable
{
    private const int LedgerPageSize = 20;
    private const int CandidatesLimit = 120;

    private readonly IGamificationService _gamification;
    private readonly IUserNotificationService _notifications;
    private readonly IUserPrompt _prompt;
    private readonly IUiDispatcher _dispatcher;

    private LevelChangeFeedback? _lastFeedback;
    private int _ledgerPage = 1;
    private bool _disposed;

    /// <summary>构造。</summary>
    public ProfileViewModel(
        IGamificationService gamification,
        IUserNotificationService notifications,
        IUserPrompt prompt,
        IUiDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(gamification);
        ArgumentNullException.ThrowIfNull(notifications);
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(dispatcher);

        _gamification = gamification;
        _notifications = notifications;
        _prompt = prompt;
        _dispatcher = dispatcher;

        _gamification.ProgressChanged += OnProgressChanged;
        _gamification.LevelChanged += OnLevelChanged;

        // 等级说明页启动即拉配置（FR-W-LV-2）。
        LoadLevelConfigCommand.Execute(null);

        Apply(_gamification.Current);
        ApplyLevelFeedback(null);
    }

    /* ------------------------------------------------------------------ */
    /* 进度快照                                                            */
    /* ------------------------------------------------------------------ */

    /// <summary>连续陪伴天数（<= 0 时隐藏）。</summary>
    [ObservableProperty]
    private int _continuousDays;

    /// <summary>积分余额。</summary>
    [ObservableProperty]
    private int _balance;

    /// <summary>称号（空 / 默认等级时用 <c>profile.level.justStarted</c>）。</summary>
    [ObservableProperty]
    private string _levelName = string.Empty;
    /// <summary>等级码（归一后，空值 → <c>NONE</c>）。</summary>
    [ObservableProperty]
    private string _levelCode = LevelVisuals.DefaultLevelCode;

    /// <summary>徽章 emoji。</summary>
    [ObservableProperty]
    private string _badgeEmoji = LevelVisuals.Default.Emoji;

    /// <summary>徽章渐变起色。</summary>
    [ObservableProperty]
    private string _gradientFrom = LevelVisuals.Default.GradientFrom;

    /// <summary>徽章渐变终色。</summary>
    [ObservableProperty]
    private string _gradientTo = LevelVisuals.Default.GradientTo;

    /// <summary>徽章强调色。</summary>
    [ObservableProperty]
    private string _accent = LevelVisuals.Default.Accent;

    /// <summary>七彩动态徽章（PANDA_LV7；受 <c>IsAnimationReduced</c> 影响，见 <see cref="IsCelebrationAnimatable"/>）。</summary>
    [ObservableProperty]
    private bool _isAnimatedBadge;

    /// <summary>距下一等级天数（<c>null</c> = 已最高等级）。</summary>
    [ObservableProperty]
    private int? _daysToNextLevel;

    /// <summary>下一等级名。</summary>
    [ObservableProperty]
    private string? _nextLevelName;

    /// <summary>下一等级阈值天数。</summary>
    [ObservableProperty]
    private int? _nextLevelThresholdDays;

    /// <summary>进度条百分比（0..100）。</summary>
    [ObservableProperty]
    private double _progressPercent;

    /// <summary>距下一等级文案。</summary>
    [ObservableProperty]
    private string _progressToNextText = string.Empty;

    /// <summary>是否已是最高等级。</summary>
    [ObservableProperty]
    private bool _isMaxLevel;

    /// <summary>断签间隔天数（> 0 时显示断签提示）。</summary>
    [ObservableProperty]
    private int _gapDays;

    /// <summary>断签截止日（服务端业务时区，**不得本地推算** —— FR-W-PROG-3）。</summary>
    [ObservableProperty]
    private string? _breakDeadlineDate;

    /// <summary>断签清零提示（<c>profile.pointsUnaffected</c> —— FR-W-LV-8）。</summary>
    [ObservableProperty]
    private string _pointsUnaffectedText = string.Empty;

    /// <summary>断签截止日提示文案。</summary>
    [ObservableProperty]
    private string _breakDeadlineText = string.Empty;

    /// <summary>可用补签卡数。</summary>
    [ObservableProperty]
    private int _availableMakeupCards;

    /// <summary>补签卡文案（<c>profile.makeupCards</c>）。</summary>
    [ObservableProperty]
    private string _makeupCardsText = string.Empty;

    /// <summary>补签规则文案（**用服务端 <c>MonthlyGrant</c> / <c>MaxAvailable</c>** —— FR-W-MC-1）。</summary>
    [ObservableProperty]
    private string _makeupRuleText = string.Empty;

    /// <summary>是否为离线缓存态（FR-W-PROG-4）。</summary>
    [ObservableProperty]
    private bool _isOfflineCache;

    /// <summary>离线提示文案。</summary>
    [ObservableProperty]
    private string _offlineNoticeText = string.Empty;

    /// <summary>互动菜单是否整体禁用（离线缓存态）。</summary>
    public bool IsInteractionEnabled => !IsOfflineCache;

    /* ---- 等级反馈（FR-W-LV-4/5/5a） ---- */

    /// <summary>升级庆祝可见（**仅 UPGRADE**）。</summary>
    [ObservableProperty]
    private bool _isCelebrationVisible;

    /// <summary>庆祝标题文案。</summary>
    [ObservableProperty]
    private string _celebrationTitle = string.Empty;

    /// <summary>庆祝正文文案。</summary>
    [ObservableProperty]
    private string _celebrationBody = string.Empty;

    /// <summary>轻量提示可见（RESTORE / RESET）。</summary>
    [ObservableProperty]
    private bool _isFeedbackVisible;

    /// <summary>轻量提示文案（RESTORE / RESET 的克制反馈，FR-W-LV-5/5a）。</summary>
    [ObservableProperty]
    private string _feedbackText = string.Empty;

    /// <summary>关闭按钮文案。</summary>
    public string CloseText => I18n.T("common.close");
    /// <summary>空态文案（流水为空时展示）。</summary>
    public string EmptyTextPlaceholder => I18n.T("common.empty");
    /// <summary>
    /// RESET 引导（「可前往补签日历挽回」）。为真时展示补签日历入口（FR-W-LV-5a）。
    /// </summary>
    [ObservableProperty]
    private bool _isMakeupGuideVisible;

    /// <summary>
    /// 是否允许播放庆祝动画：系统「减少动画」时**必须**关闭（FR-W-UI-11）。
    /// 由 View 层注入（读 <c>IWindowChrome.IsAnimationReduced</c> 的取反）。
    /// </summary>
    [ObservableProperty]
    private bool _isCelebrationAnimatable = true;

    /* ---- 积分流水（FR-W-PT-2/3/7） ---- */

    /// <summary>流水行（分页累积）。</summary>
    public ObservableCollection<PointsLedgerRow> Ledger { get; } = [];

    /// <summary>流水页码（从 1 开始）。</summary>
    [ObservableProperty]
    private int _ledgerPageNumber = 1;

    /// <summary>是否还有下一页。</summary>
    [ObservableProperty]
    private bool _hasMoreLedger;

    /// <summary>流水是否为空。</summary>
    public bool IsLedgerEmpty => Ledger.Count == 0;

    /* ---- 等级说明（FR-W-LV-2） ---- */

    /// <summary>等级配置行（按服务端 <c>sort_order</c>）。</summary>
    public ObservableCollection<LevelConfigRow> LevelConfigs { get; } = [];

    /* ---- 补签日历（FR-W-MC-2..7） ---- */

    /// <summary>补签候选日期（**唯一数据源 = 服务端** —— FR-W-MC-2）。</summary>
    public ObservableCollection<string> MakeupCandidates { get; } = [];

    /// <summary>日历是否为空。</summary>
    public bool IsMakeupEmpty => MakeupCandidates.Count == 0;

    /// <summary>补签失败 / 成功提示。</summary>
    [ObservableProperty]
    private string _makeupStatusText = string.Empty;

    /* ---- 静态 UI 文案 —— 全部走 I18n，XAML 只绑定（V-W-S8） ---- */

    /// <summary>页面标题。</summary>
    public string TitleText => I18n.T("profile.title");

    /// <summary>积分余额标签。</summary>
    public string PointsLabel => I18n.T("profile.points");

    /// <summary>等级标签。</summary>
    public string LevelLabel => I18n.T("profile.level");

    /// <summary>连续天数文案。</summary>
    public string ContinuousDaysText => I18n.T("profile.continuousDays", ContinuousDays);

    /// <summary>最高等级文案。</summary>
    public string MaxLevelText => I18n.T("profile.maxLevel");

    /// <summary>补签日历标题。</summary>
    public string MakeupTitleText => I18n.T("makeup.title");

    /// <summary>「使用补签卡」按钮文案。</summary>
    public string MakeupUseText => I18n.T("makeup.use");

    /// <summary>补签日历为空文案。</summary>
    public string MakeupEmptyText => I18n.T("makeup.noCandidates");

    /// <summary>等级说明标题。</summary>
    public string LevelConfigTitleText => I18n.T("profile.levelConfig");

    /// <summary>积分流水标题。</summary>
    public string LedgerTitleText => I18n.T("profile.pointsHistory");

    /// <summary>流水列标题（时间 / 事由 / 变动 / 变动后余额 —— FR-W-PT-2）。</summary>
    public string LedgerTimeColumnText => I18n.T("points.column.time");

    /// <summary>流水「事由」列标题。</summary>
    public string LedgerReasonColumnText => I18n.T("points.column.reason");

    /// <summary>流水「变动」列标题。</summary>
    public string LedgerChangeColumnText => I18n.T("points.column.change");

    /// <summary>流水「变动后余额」列标题。</summary>
    public string LedgerBalanceColumnText => I18n.T("points.column.balanceAfter");

    /// <summary>加载更多文案。</summary>
    public string LoadMoreText => I18n.T("chat.loadMore");

    /// <summary>互动入口文案（FR-W-INT-*）。</summary>
    public string InteractionText => I18n.T("profile.interaction");

    /// <summary>刷新文案。</summary>
    public string RefreshText => I18n.T("common.refresh");
    /* ------------------------------------------------------------------ */
    /* 命令                                                                */
    /* ------------------------------------------------------------------ */

    /// <summary>刷新首屏聚合（<c>GET /points/overview</c> —— FR-W-PROG-5）。</summary>
    [RelayCommand]
    private async Task RefreshAsync(CancellationToken ct = default)
    {
        try
        {
            var snapshot = await _gamification.RefreshOverviewAsync(ct).ConfigureAwait(true);
            Apply(snapshot);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 刷新失败不得崩溃：保留上次数据（NFR-W-12）。
        }

        _ledgerPage = 1;
        await LoadLedgerPageAsync(1, replace: true).ConfigureAwait(true);
        await LoadMakeupAsync(ct).ConfigureAwait(true);
    }

    /// <summary>加载下一段积分流水（分页 —— FR-W-PT-7）。</summary>
    [RelayCommand]
    private Task LoadMoreLedgerAsync() => HasMoreLedger ? LoadLedgerPageAsync(_ledgerPage + 1, replace: false) : Task.CompletedTask;

    /// <summary>加载等级配置（等级说明页 —— FR-W-LV-2）。</summary>
    [RelayCommand]
    private async Task LoadLevelConfigAsync(CancellationToken ct = default)
    {
        try
        {
            var config = await _gamification.GetLevelConfigAsync(ct).ConfigureAwait(true);
            var rows = config.Levels
                .Select(static entry => new LevelConfigRow(entry))
                .OrderBy(static row => row.SortOrder)
                .ToList();

            await _dispatcher.InvokeAsync(() =>
            {
                LevelConfigs.Clear();
                foreach (var row in rows)
                {
                    LevelConfigs.Add(row);
                }
            }).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 配置拉取失败保留空列表（页面仍可用）。
        }
    }

    /// <summary>
    /// 点击一个补签候选日期：**必须先二次确认**，确认后才调服务端（FR-W-MC-3）。
    /// </summary>
    [RelayCommand]
    private async Task UseMakeupCardAsync(string? targetDate)
    {
        if (string.IsNullOrWhiteSpace(targetDate))
        {
            return;
        }

        if (IsOfflineCache)
        {
            return;
        }

        // 二次确认（<c>makeup.use.confirm</c> 带日期参数）。
        if (!_prompt.Confirm(I18n.T("makeup.use.confirm", targetDate)))
        {
            return;
        }

        try
        {
            var outcome = await _gamification.UseMakeupCardAsync(targetDate).ConfigureAwait(true);

            if (!outcome.Success)
            {
                // 失败按 ErrorCode 显示（未知码回中性兜底），**并刷新日历**（FR-W-MC-6）。
                MakeupStatusText = I18n.T(outcome.I18nKey ?? ErrorCatalog.I18nKeyOf(outcome.ErrorCode));
                await LoadMakeupAsync().ConfigureAwait(true);
                return;
            }

            MakeupStatusText = I18n.T("makeup.use.success");

            if (outcome.LevelFeedback is { } feedback)
            {
                // 补签造成的等级变化同样按三态反馈（RESTORE 克制、UPGRADE 才庆祝 —— FR-W-MC-5）。
                ApplyLevelFeedback(feedback);
            }

            await LoadMakeupAsync().ConfigureAwait(true);
            await RefreshCommand.ExecuteAsync(null).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            MakeupStatusText = I18n.T(ErrorCatalog.UnknownI18nKey);
            await LoadMakeupAsync().ConfigureAwait(true);
        }
    }

    /// <summary>关闭庆祝弹窗（用户点击「确定」时）。</summary>
    [RelayCommand]
    private void DismissCelebration()
    {
        IsCelebrationVisible = false;
        CelebrationTitle = string.Empty;
        CelebrationBody = string.Empty;
    }

    /// <summary>关闭轻量提示。</summary>
    [RelayCommand]
    private void DismissFeedback()
    {
        IsFeedbackVisible = false;
        FeedbackText = string.Empty;
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
        _gamification.LevelChanged -= OnLevelChanged;
        GC.SuppressFinalize(this);
    }

    /* ------------------------------------------------------------------ */
    /* 内部                                                                */
    /* ------------------------------------------------------------------ */

    private void OnProgressChanged(object? sender, ProgressSnapshot snapshot)
        => _dispatcher.Invoke(() => Apply(snapshot));

    private void OnLevelChanged(object? sender, LevelChangeFeedback feedback)
        => _dispatcher.Invoke(() => ApplyLevelFeedback(feedback));

    private async Task LoadLedgerPageAsync(int page, bool replace)
    {
        try
        {
            var paged = await _gamification
                .GetPointsHistoryAsync(page, LedgerPageSize)
                .ConfigureAwait(true);

            var rows = paged.Items.Select(static dto => new PointsLedgerRow(dto)).ToList();

            await _dispatcher.InvokeAsync(() =>
            {
                if (replace)
                {
                    Ledger.Clear();
                }

                foreach (var row in rows)
                {
                    Ledger.Add(row);
                }

                LedgerPageNumber = paged.Page > 0 ? paged.Page : page;
                HasMoreLedger = paged.HasMore;
                OnPropertyChanged(nameof(IsLedgerEmpty));
            }).ConfigureAwait(true);

            _ledgerPage = LedgerPageNumber;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await _dispatcher.InvokeAsync(() =>
            {
                HasMoreLedger = false;
                OnPropertyChanged(nameof(IsLedgerEmpty));
            }).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// 拉补签候选（**唯一数据源 = <c>GetMakeupCandidatesAsync()</c>**，不得本地推算日期 —— FR-W-MC-2）
    /// 与补签卡汇总（规则文案用服务端 <c>MonthlyGrant</c> / <c>MaxAvailable</c> —— FR-W-MC-1）。
    /// </summary>
    private async Task LoadMakeupAsync(CancellationToken ct = default)
    {
        try
        {
            var candidates = await _gamification.GetMakeupCandidatesAsync(CandidatesLimit, ct).ConfigureAwait(true);
            var summary = await _gamification.GetMakeupCardAsync(ct).ConfigureAwait(true);

            await _dispatcher.InvokeAsync(() =>
            {
                MakeupCandidates.Clear();
                foreach (var candidate in candidates.Items)
                {
                    if (!string.IsNullOrWhiteSpace(candidate.Date))
                    {
                        MakeupCandidates.Add(candidate.Date);
                    }
                }

                OnPropertyChanged(nameof(IsMakeupEmpty));

                AvailableMakeupCards = summary.Available;
                MakeupCardsText = I18n.T("profile.makeupCards", summary.Available);
                MakeupRuleText = I18n.T("makeup.rule", summary.MonthlyGrant, summary.MaxAvailable);
            }).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 失败保留上一次日历内容（避免清空后用户误以为无候选）。
        }
    }


    private void Apply(ProgressSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return;
        }

        var visual = LevelVisuals.Resolve(snapshot.LevelCode);

        Balance = snapshot.Balance;
        ContinuousDays = snapshot.ContinuousDays;
        LevelCode = visual.Code;
        BadgeEmoji = visual.Emoji;
        GradientFrom = visual.GradientFrom;
        GradientTo = visual.GradientTo;
        Accent = visual.Accent;
        IsAnimatedBadge = visual.Animated;

        // FR-W-LV-3：默认等级或服务端称号为空 → 中性兜底文案（不得渲染空标题）。
        LevelName = snapshot.IsDefaultLevel || string.IsNullOrWhiteSpace(snapshot.LevelName)
            ? I18n.T("profile.level.justStarted")
            : snapshot.LevelName;

        DaysToNextLevel = snapshot.DaysToNextLevel;
        NextLevelName = snapshot.NextLevelName;
        NextLevelThresholdDays = snapshot.NextLevelThresholdDays;
        IsMaxLevel = snapshot.DaysToNextLevel is null && snapshot.NextLevelThresholdDays is null;

        ProgressPercent = ComputeProgressPercent(snapshot);
        ProgressToNextText = IsMaxLevel
            ? I18n.T("profile.maxLevel")
            : I18n.T(
                "profile.progressToNext",
                snapshot.NextLevelName ?? I18n.T("profile.level.justStarted"),
                Math.Max(0, snapshot.DaysToNextLevel ?? 0));

        GapDays = snapshot.GapDays;
        BreakDeadlineDate = snapshot.BreakDeadlineDate;
        BreakDeadlineText = string.IsNullOrWhiteSpace(snapshot.BreakDeadlineDate)
            ? string.Empty
            : I18n.T("profile.breakDeadline", snapshot.BreakDeadlineDate);

        // FR-W-LV-8：断签导致连续天数清零时，**必须**明确「积分余额未受影响」。
        PointsUnaffectedText = snapshot.GapDays > 0 && snapshot.ContinuousDays == 0
            ? I18n.T("profile.pointsUnaffected")
            : string.Empty;

        AvailableMakeupCards = snapshot.AvailableMakeupCards;
        MakeupCardsText = I18n.T("profile.makeupCards", snapshot.AvailableMakeupCards);

        IsOfflineCache = snapshot.IsOfflineCache;
        OfflineNoticeText = snapshot.IsOfflineCache
            ? I18n.T("common.offlineData", FormatSyncTime(snapshot.SyncedAt))
            : string.Empty;

        OnPropertyChanged(nameof(IsInteractionEnabled));
        OnPropertyChanged(nameof(ContinuousDaysText));
    }

    /// <summary>
    /// 进度条百分比：<c>连续天数 / 下一等级阈值</c>。阈值缺失（已最高等级 / 服务端未下发）时为 100。
    /// </summary>
    private static double ComputeProgressPercent(ProgressSnapshot snapshot)
    {
        if (snapshot.NextLevelThresholdDays is not { } threshold || threshold <= 0)
        {
            return 100d;
        }

        var ratio = (double)snapshot.ContinuousDays / threshold;
        return Math.Clamp(ratio * 100d, 0d, 100d);
    }

    /// <summary>
    /// 三态等级反馈（FR-W-LV-4/5/5a）：
    /// <list type="bullet">
    ///   <item><c>UPGRADE</c>（且 <c>PlayCelebration</c>）→ 弹窗 + 动画 + Toast 三重；</item>
    ///   <item><c>RESTORE</c> → **仅**轻量提示，**不播动画、不弹庆祝**；</item>
    ///   <item><c>RESET</c> → 轻量提示 + 引导去补签日历。</item>
    /// </list>
    /// </summary>
    private void ApplyLevelFeedback(LevelChangeFeedback? feedback)
    {
        _lastFeedback = feedback;

        if (feedback is null)
        {
            return;
        }

        var isUpgrade = string.Equals(feedback.ChangeType, "UPGRADE", StringComparison.Ordinal)
                        && feedback.PlayCelebration;

        if (isUpgrade)
        {
            CelebrationTitle = I18n.T("level.upgrade.title");
            CelebrationBody = I18n.T("level.upgrade.body", feedback.LevelName);
            IsCelebrationVisible = true;
            IsFeedbackVisible = false;
            IsMakeupGuideVisible = false;
            FeedbackText = string.Empty;

            // 「三重」的最后一路：Toast（语义 ID = Level —— C-3；正文已按 FR-W-NOTI-6 截断由服务层处理）。
            _notifications.Notify(
                NotificationCategory.Progress,
                I18n.T("notification.level.title"),
                CelebrationBody);
            return;
        }

        // RESTORE / RESET / 其他：**绝不**弹庆祝、绝不播动画（RESTORE 的克制要求）。
        IsCelebrationVisible = false;
        CelebrationTitle = string.Empty;
        CelebrationBody = string.Empty;

        if (string.Equals(feedback.ChangeType, "RESTORE", StringComparison.Ordinal))
        {
            FeedbackText = I18n.T("level.restore.body", feedback.LevelName);
            IsMakeupGuideVisible = false;
        }
        else if (string.Equals(feedback.ChangeType, "RESET", StringComparison.Ordinal))
        {
            FeedbackText = I18n.T("level.reset.guide");
            IsMakeupGuideVisible = true;
        }
        else
        {
            FeedbackText = string.Empty;
            IsMakeupGuideVisible = false;
        }

        IsFeedbackVisible = !string.IsNullOrEmpty(FeedbackText);
    }

    /// <summary>同步时刻 → 本地 `MM-dd HH:mm`。</summary>
    private static string FormatSyncTime(long unixMilliseconds)
        => MessageItemViewModel
            .FromUnixMilliseconds(unixMilliseconds)
            .ToString("MM-dd HH:mm", CultureInfo.CurrentCulture);
}
