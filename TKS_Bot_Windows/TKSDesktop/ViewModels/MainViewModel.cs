using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.ComponentModel;
using TKSDesktop.App;
using TKSDesktop.Core.Platform;
using TKSDesktop.Core.Services;

namespace TKSDesktop.ViewModels;

/// <summary>主窗口内可打开的面板 / 独立窗口（FR-W-DSK-8 / FR-W-HIS-*）。</summary>
public enum MainPanel
{
    /// <summary>历史与记忆（**必须有可达入口** —— 工具栏按钮）。</summary>
    History,

    /// <summary>个人中心（含积分流水 / 补签日历 / 等级说明）。</summary>
    Profile,

    /// <summary>已排程提醒。</summary>
    Reminders,

    /// <summary>设置。</summary>
    Settings,

    /// <summary>积分流水（可独立弹出 —— FR-W-DSK-8）。</summary>
    PointsLedger,

    /// <summary>记忆档案（可独立弹出 —— FR-W-DSK-8）。</summary>
    MemoryArchive,
}

/// <summary>
/// 主窗口视图模型（FR-W-UI-1/5/6/7/11、FR-W-DSK-6、FR-W-SET-6、FR-W-CONN-9、EDGE-W-9/25/26）。
///
/// <list type="bullet">
///   <item>窗口最小 520×640；尺寸 / 位置记忆（FR-W-UI-6）；恢复前经 <c>IWindowChrome.EnsureOnScreen</c>（EDGE-W-25）；</item>
///   <item>系统模糊不可用 → 不透明渐变降级（FR-W-UI-3）；<c>IsAnimationReduced</c> 关动画（FR-W-UI-11）；</item>
///   <item>托盘关闭行为：<c>tray</c> → <c>HideToTray()</c>；<c>quit</c> → 退出；托盘不可用时临时强制退出（EDGE-W-9）；</item>
///   <item>窗口内快捷键：Esc / Ctrl+F / Ctrl+, / Ctrl+L / Ctrl+Shift+R（FR-W-DSK-6）。</item>
/// </list>
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    /// <summary>最小宽度（FR-W-UI-6）。</summary>
    public const double MinWindowWidth = 520d;

    /// <summary>最小高度（FR-W-UI-6）。</summary>
    public const double MinWindowHeight = 640d;

    private const string CloseBehaviorTray = "tray";

    private readonly ISettingsService _settings;
    private readonly IWindowChrome _windowChrome;
    private readonly ITrayIcon _tray;
    private readonly IGlobalHotkey _hotkey;
    private readonly IChatService _chat;
    private readonly IUserNotificationService _notifications;
    private readonly IUiDispatcher _dispatcher;

    private bool _disposed;

    /// <summary>构造。</summary>
    public MainViewModel(
        ChatViewModel chat,
        ISettingsService settings,
        IWindowChrome windowChrome,
        ITrayIcon tray,
        IGlobalHotkey hotkey,
        IChatService chatService,
        IUserNotificationService notifications,
        IUiDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(windowChrome);
        ArgumentNullException.ThrowIfNull(tray);
        ArgumentNullException.ThrowIfNull(hotkey);
        ArgumentNullException.ThrowIfNull(chatService);
        ArgumentNullException.ThrowIfNull(notifications);
        ArgumentNullException.ThrowIfNull(dispatcher);

        Chat = chat;
        _settings = settings;
        _windowChrome = windowChrome;
        _tray = tray;
        _hotkey = hotkey;
        _chat = chatService;
        _notifications = notifications;
        _dispatcher = dispatcher;

        // FR-W-UI-11：尊重系统「减少动画」。
        IsAnimationReduced = windowChrome.IsAnimationReduced;

        // FR-W-UI-3：系统模糊不可用时降级为不透明渐变（**不得**保留玻璃动画）。
        IsSystemBlurAvailable = windowChrome.IsSystemBlurAvailable;

        _windowChrome.SystemThemeChanged += OnSystemThemeChanged;
        _settings.SettingsChanged += OnSettingsChanged;
        _hotkey.Pressed += OnHotkeyPressed;

        _chat.ConnectionChanged += OnConnectionChanged;
        Chat.PropertyChanged += OnChatPropertyChanged;
        ApplyTrayState(_chat.State);
    }

    /// <summary>请求打开某个面板 / 窗口（View 层决定内嵌还是独立窗口 —— FR-W-DSK-8）。</summary>
    public event EventHandler<MainPanel>? PanelRequested;

    /// <summary>请求唤起并聚焦主窗口（全局快捷键 / 托盘 —— FR-W-DSK-2）。</summary>
    public event EventHandler? ActivateRequested;

    /// <summary>请求隐藏到托盘（FR-W-SET-6 的 <c>tray</c> 分支）。</summary>
    public event EventHandler? HideToTrayRequested;

    /// <summary>请求真正退出进程（<c>quit</c> 分支 / 托盘不可用时的强制退出）。</summary>
    public event EventHandler? ExitRequested;

    /// <summary>请求播放主题揭示动画（FR-W-UI-4，由 View 层用 Storyboard 实现）。</summary>
    public event EventHandler<bool>? ThemeRevealRequested;

    /// <summary>请求聚焦输入框（Ctrl+L —— FR-W-DSK-6）。</summary>
    public event EventHandler? FocusInputRequested;

    /// <summary>请求打开搜索（Ctrl+F —— FR-W-DSK-6）。</summary>
    public event EventHandler? SearchRequested;

    /// <summary>聊天视图模型（内嵌在主窗口中部）。</summary>
    public ChatViewModel Chat { get; }

    /* ---- 窗口几何（FR-W-UI-6 / EDGE-W-25） ---- */

    /// <summary>窗口宽度（不小于 <see cref="MinWindowWidth"/>）。</summary>
    [ObservableProperty]
    private double _windowWidth = 900d;

    /// <summary>窗口高度（不小于 <see cref="MinWindowHeight"/>）。</summary>
    [ObservableProperty]
    private double _windowHeight = 720d;

    /// <summary>窗口左上角 X（已过 <c>EnsureOnScreen</c> 校验）。</summary>
    [ObservableProperty]
    private double _windowLeft = double.NaN;

    /// <summary>窗口左上角 Y（已过 <c>EnsureOnScreen</c> 校验）。</summary>
    [ObservableProperty]
    private double _windowTop = double.NaN;

    /* ---- 无障碍 / 降级（FR-W-UI-3 / FR-W-UI-11） ---- */

    /// <summary>系统是否要求减少动画（关玻璃动画与庆祝动画 —— FR-W-UI-11）。</summary>
    [ObservableProperty]
    private bool _isAnimationReduced;

    /// <summary>系统窗口级模糊是否可用（<c>false</c> → 不透明渐变降级 —— FR-W-UI-3）。</summary>
    [ObservableProperty]
    private bool _isSystemBlurAvailable = true;

    /// <summary>是否允许液态玻璃动画（系统减少动画时为 <c>false</c>）。</summary>
    public bool IsGlassAnimationEnabled => !IsAnimationReduced && IsSystemBlurAvailable;

    /// <summary>是否允许升级庆祝动画（系统减少动画时为 <c>false</c>）。</summary>
    public bool IsCelebrationAnimationEnabled => !IsAnimationReduced;

    /// <summary>是否需要不透明降级样式（系统模糊不可用）。</summary>
    public bool UseOpaqueFallback => !IsSystemBlurAvailable;

    /* ---- 托盘与关闭行为（FR-W-SET-6 / FR-W-DSK-13 / EDGE-W-9） ---- */

    /// <summary>托盘是否可用。</summary>
    [ObservableProperty]
    private bool _isTrayAvailable = true;

    /// <summary>托盘不可用提示（EDGE-W-9）。</summary>
    [ObservableProperty]
    private string _trayUnavailableText = string.Empty;

    /// <summary>连接状态文案（托 tooltip / 顶部状态复用）。</summary>
    [ObservableProperty]
    private string _connectionTooltip = string.Empty;

    /* ---- 静态 UI 文案（XAML 只绑定 —— V-W-S8） ---- */

    /// <summary>窗口标题。</summary>
    public string WindowTitle => I18n.T("app.name");

    /// <summary>历史与记忆入口文案（**工具栏按钮** —— 必须可达）。</summary>
    public string HistoryEntryText => I18n.T("history.title");

    /// <summary>个人中心入口文案。</summary>
    public string ProfileEntryText => I18n.T("profile.title");

    /// <summary>积分流水入口文案（可独立弹出）。</summary>
    public string PointsLedgerEntryText => I18n.T("profile.pointsHistory");

    /// <summary>记忆档案入口文案（可独立弹出）。</summary>
    public string MemoryArchiveEntryText => I18n.T("history.facts");

    /// <summary>提醒入口文案。</summary>
    public string RemindersEntryText => I18n.T("tray.menu.reminders");

    /// <summary>设置入口文案。</summary>
    public string SettingsEntryText => I18n.T("settings.title");

    /// <summary>重新连接入口文案。</summary>
    public string ReconnectEntryText => I18n.T("conn.reconnect");

    /// <summary>搜索入口文案（Ctrl+F）。</summary>
    public string SearchEntryText => I18n.T("chat.search.placeholder");

    /* ------------------------------------------------------------------ */
    /* 生命周期                                                            */
    /* ------------------------------------------------------------------ */

    /// <summary>
    /// 窗口首次显示时调用：恢复几何（含越界校正）、首屏加载、托盘初始化与状态同步。
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        RestoreGeometry();

        IsTrayAvailable = _tray.Initialize();
        if (_settings.Current.GlobalShortcutEnabled)
        {
            _ = _hotkey.Register(_settings.Current.GlobalShortcut);
        }
        else
        {
            _hotkey.Unregister();
        }

        await Chat.InitializeAsync(ct).ConfigureAwait(true);
        await Chat.RefreshUnreadAsync(ct).ConfigureAwait(true);

        ApplyTrayState(_chat.State);
    }

    /// <summary>窗口被激活（显示 / 唤起）时调用。</summary>
    public Task OnActivatedAsync(CancellationToken ct = default)
    {
        _notifications.IsWindowFocused = true;
        _notifications.ClearMessageNotifications();
        return Chat.MarkVisibleIncomingReadAsync(ct);
    }

    /// <summary>窗口失活（隐藏到托盘 / 最小化）时调用。</summary>
    public void OnDeactivated()
    {
        // 窗口不在前台时才允许弹聊天通知（FR-W-NOTI-2）。
        _notifications.IsWindowFocused = false;
    }

    /// <summary>窗口尺寸 / 位置变化时调用（落盘在关闭时统一执行）。</summary>
    public void UpdateGeometry(double width, double height, double left, double top)
    {
        if (double.IsNaN(width) || double.IsNaN(height) || width <= 0 || height <= 0)
        {
            return;
        }

        WindowWidth = Math.Max(MinWindowWidth, width);
        WindowHeight = Math.Max(MinWindowHeight, height);

        if (!double.IsNaN(left) && !double.IsNaN(top))
        {
            WindowLeft = left;
            WindowTop = top;
        }
    }

    /// <summary>
    /// 窗口关闭请求。返回 <c>true</c> 表示**取消关闭**（已改为隐藏到托盘）。
    ///
    /// ⚠️ FR-W-SET-6 / FR-W-DSK-13：<c>closeBehavior == "tray"</c> → <see cref="HideToTrayRequested"/>（**不是** 退出）；
    ///    <c>"quit"</c> → <see cref="ExitRequested"/>。
    /// ⚠️ EDGE-W-9：托盘不可用时**临时强制**「直接退出」。
    /// </summary>
    public bool HandleCloseRequest()
    {
        PersistGeometry();

        if (!IsTrayAvailable)
        {
            ExitRequested?.Invoke(this, EventArgs.Empty);
            return false;
        }

        if (string.Equals(_settings.Current.CloseBehavior, CloseBehaviorTray, StringComparison.Ordinal))
        {
            HideToTrayRequested?.Invoke(this, EventArgs.Empty);
            return true;
        }

        ExitRequested?.Invoke(this, EventArgs.Empty);
        return false;
    }

    /// <summary>托盘「退出」菜单 / 快捷键退出。</summary>
    public void RequestExit()
    {
        PersistGeometry();
        ExitRequested?.Invoke(this, EventArgs.Empty);
    }

    /* ------------------------------------------------------------------ */
    /* 快捷键命令（FR-W-DSK-6）                                            */
    /* ------------------------------------------------------------------ */

    /// <summary>Ctrl+F：打开搜索。</summary>
    [RelayCommand]
    private void OpenSearch() => SearchRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Ctrl+,：打开设置。</summary>
    [RelayCommand]
    private void OpenSettings() => PanelRequested?.Invoke(this, MainPanel.Settings);

    /// <summary>Ctrl+L：聚焦输入框。</summary>
    [RelayCommand]
    private void FocusInput() => FocusInputRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Ctrl+Shift+R：手动重连（置 full-sync 标志 —— FR-W-CONN-6）。</summary>
    [RelayCommand]
    private void ManualReconnect() => _ = Chat.ReconnectCommand.ExecuteAsync(null);

    /// <summary>Esc：收回托盘（FR-W-DSK-6）。</summary>
    [RelayCommand]
    private void HideToTray()
    {
        if (!IsTrayAvailable)
        {
            // 托盘不可用：Esc 不能「藏起无处可寻的窗口」，改为明确提示（NFR-W-8）。
            TrayUnavailableText = I18n.T("settings.tray.unavailable");
            return;
        }

        PersistGeometry();
        HideToTrayRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>工具栏：历史与记忆（**可达入口** —— 不得「已实现但 UI 不可达」）。</summary>
    [RelayCommand]
    private void OpenHistory() => PanelRequested?.Invoke(this, MainPanel.History);

    /// <summary>工具栏 / 独立窗口：个人中心。</summary>
    [RelayCommand]
    private void OpenProfile() => PanelRequested?.Invoke(this, MainPanel.Profile);

    /// <summary>工具栏 / 独立窗口：积分流水。</summary>
    [RelayCommand]
    private void OpenPointsLedger() => PanelRequested?.Invoke(this, MainPanel.PointsLedger);

    /// <summary>工具栏 / 独立窗口：记忆档案。</summary>
    [RelayCommand]
    private void OpenMemoryArchive() => PanelRequested?.Invoke(this, MainPanel.MemoryArchive);

    /// <summary>托盘菜单：已排程提醒。</summary>
    [RelayCommand]
    private void OpenReminders() => PanelRequested?.Invoke(this, MainPanel.Reminders);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _windowChrome.SystemThemeChanged -= OnSystemThemeChanged;
        _settings.SettingsChanged -= OnSettingsChanged;
        _hotkey.Pressed -= OnHotkeyPressed;
        _chat.ConnectionChanged -= OnConnectionChanged;
        Chat.PropertyChanged -= OnChatPropertyChanged;
        _hotkey.Unregister();
        _tray.Dispose();
        Chat.Dispose();

        GC.SuppressFinalize(this);
    }

    /* ------------------------------------------------------------------ */
    /* 内部                                                                */
    /* ------------------------------------------------------------------ */

    /// <summary>
    /// 恢复窗口几何：尺寸夹到下限；位置经 <c>EnsureOnScreen</c>（越界 → 主屏居中 —— EDGE-W-25）。
    /// </summary>
    private void RestoreGeometry()
    {
        var saved = _settings.Current.Window;

        var width = Math.Max(MinWindowWidth, saved.Width > 0 ? saved.Width : WindowWidth);
        var height = Math.Max(MinWindowHeight, saved.Height > 0 ? saved.Height : WindowHeight);

        if (saved.X is { } x && saved.Y is { } y)
        {
            // ⚠️ 恢复前必须校验：显示器拔掉 / 分辨率变化后不得落到屏幕外（EDGE-W-25）。
            var (safeX, safeY, safeWidth, safeHeight) = _windowChrome.EnsureOnScreen(x, y, width, height);
            WindowLeft = safeX;
            WindowTop = safeY;
            WindowWidth = Math.Max(MinWindowWidth, safeWidth);
            WindowHeight = Math.Max(MinWindowHeight, safeHeight);
            return;
        }

        WindowWidth = width;
        WindowHeight = height;
        WindowLeft = double.NaN;
        WindowTop = double.NaN;
    }

    private void PersistGeometry()
    {
        if (double.IsNaN(WindowWidth) || double.IsNaN(WindowHeight))
        {
            return;
        }

        var settings = _settings.Current;
        settings.Window.Width = Math.Max(MinWindowWidth, WindowWidth);
        settings.Window.Height = Math.Max(MinWindowHeight, WindowHeight);
        settings.Window.X = double.IsNaN(WindowLeft) ? null : WindowLeft;
        settings.Window.Y = double.IsNaN(WindowTop) ? null : WindowTop;

        // 落盘失败不阻断退出。
        _ = _settings.SaveAsync(settings);
    }

    private void ApplyTrayState(ConnectionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var tooltip = I18n.T(snapshot.Status switch
        {
            ConnectionStatus.Connected => "conn.status.connected",
            ConnectionStatus.Connecting => "conn.status.connecting",
            ConnectionStatus.Reconnecting => "conn.status.reconnecting",
            ConnectionStatus.Refreshing => "conn.status.refreshing",
            ConnectionStatus.Degraded => "conn.status.degraded",
            _ => "conn.status.disconnected",
        }, snapshot.Attempt);

        ConnectionTooltip = tooltip;

        if (!IsTrayAvailable)
        {
            return;
        }

        _tray.SetTooltip(tooltip);
        if (Chat.UnreadNotificationCount > 0 && snapshot.Status == ConnectionStatus.Connected)
        {
            _tray.SetState(TrayState.Unread);
            return;
        }

        _tray.SetState(snapshot.Status switch
        {
            ConnectionStatus.Connected => TrayState.Online,
            ConnectionStatus.Connecting or ConnectionStatus.Reconnecting or ConnectionStatus.Refreshing => TrayState.Connecting,
            ConnectionStatus.Disconnected or ConnectionStatus.Degraded => TrayState.Disconnected,
            _ => TrayState.Offline,
        });
    }

    private void OnConnectionChanged(object? sender, ConnectionSnapshot snapshot)
        => _dispatcher.Invoke(() => ApplyTrayState(snapshot));

    private void OnSystemThemeChanged(object? sender, EventArgs e)
        => _dispatcher.Invoke(() =>
        {
            IsAnimationReduced = _windowChrome.IsAnimationReduced;
            IsSystemBlurAvailable = _windowChrome.IsSystemBlurAvailable;
            RaiseAccessibility();

            // 「跟随系统」时按新主题重算并播放揭示动画（FR-W-UI-4）。
            if (!string.Equals(_settings.Current.Theme, "system", StringComparison.Ordinal))
            {
                return;
            }

            ThemeRevealRequested?.Invoke(this, _settings.IsDarkThemeEffective);
        });

    private void OnSettingsChanged(object? sender, EventArgs e)
        => _dispatcher.Invoke(() => ThemeRevealRequested?.Invoke(this, _settings.IsDarkThemeEffective));

    private void OnHotkeyPressed(object? sender, EventArgs e)
        => _dispatcher.Invoke(() =>
        {
            // toggle 语义：窗口隐藏则唤起并聚焦，已聚焦则收回托盘（FR-W-DSK-2）。
            ActivateRequested?.Invoke(this, EventArgs.Empty);
        });

    private void OnChatPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.Equals(e.PropertyName, nameof(ChatViewModel.UnreadNotificationCount), StringComparison.Ordinal))
        {
            return;
        }

        _dispatcher.Invoke(() =>
        {
            if (Chat.UnreadNotificationCount > 0 && IsTrayAvailable)
            {
                _tray.SetState(TrayState.Unread);
            }
            else
            {
                ApplyTrayState(_chat.State);
            }
        });
    }

    partial void OnIsAnimationReducedChanged(bool value) => RaiseAccessibility();

    partial void OnIsSystemBlurAvailableChanged(bool value) => RaiseAccessibility();

    partial void OnIsTrayAvailableChanged(bool value)
    {
        TrayUnavailableText = value ? string.Empty : I18n.T("settings.tray.unavailable");
        OnPropertyChanged(nameof(UseOpaqueFallback));
    }

    private void RaiseAccessibility()
    {
        OnPropertyChanged(nameof(IsGlassAnimationEnabled));
        OnPropertyChanged(nameof(IsCelebrationAnimationEnabled));
        OnPropertyChanged(nameof(UseOpaqueFallback));
    }

    /// <summary>播放主题揭示动画（FR-W-UI-4）：系统减少动画时**不得**播放。</summary>
    public void RequestThemeReveal(bool dark)
    {
        if (IsAnimationReduced)
        {
            return;
        }

        ThemeRevealRequested?.Invoke(this, dark);
    }
}
