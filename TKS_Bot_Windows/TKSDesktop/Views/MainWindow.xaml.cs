using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using TKSDesktop.App;
using TKSDesktop.Core.Platform;
using TKSDesktop.Platform.Windows;
using TKSDesktop.ViewModels;


namespace TKSDesktop.Views;

/// <summary>
/// 主窗口（FR-W-UI-1..7/11、FR-W-DSK-6/8/13、FR-W-SET-6、EDGE-W-9/25/26）。
///
/// <list type="bullet">
///   <item>几何恢复经 <c>IWindowChrome.EnsureOnScreen</c>（越界 → 主屏居中 —— EDGE-W-25）；</item>
///   <item>关闭行为：<c>tray</c> → 隐藏到托盘（**不是** Close）；<c>quit</c> → 退出；
///         托盘不可用时临时强制「直接退出」（EDGE-W-9）；</item>
///   <item>窗口内快捷键：Esc / Ctrl+F / Ctrl+, / Ctrl+L / Ctrl+Shift+R（FR-W-DSK-6）；</item>
///   <item>主题揭示动画：<c>Storyboard</c> + <c>EllipseGeometry</c> 的 RadiusX/RadiusY，约 400ms（FR-W-UI-4）；
///         系统「减少动画」时**不播放**（FR-W-UI-11）；</item>
///   <item>「记忆档案」「积分流水」可独立弹出（共享同一宿主服务 —— FR-W-DSK-8）。</item>
/// </list>
/// </summary>
public partial class MainWindow : Window
{
    private const string ThemeSystem = "system";

    private readonly IServiceProvider _provider;
    private readonly MainViewModel _viewModel;
    private readonly IWindowChrome _windowChrome;
    private readonly IPaths _paths;
    private readonly ILogger<MainWindow> _logger;
    private readonly SettingsViewModel _settingsViewModel;
    private readonly double _uiScaleFactor;

    private bool _initialized;
    private bool _applyingGeometry;
    private HwndSource? _hwndSource;
    private IntPtr _windowHandle;
    private Task _initialization = Task.CompletedTask;
    private Window? _pointsLedgerWindow;
    private Window? _memoryArchiveWindow;

    /// <summary>构造（由 <c>WindowFactory</c> 调用）。</summary>
    public MainWindow(IServiceProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        InitializeComponent();

        _provider = provider;
        _viewModel = provider.GetRequiredService<MainViewModel>();
        _windowChrome = provider.GetRequiredService<IWindowChrome>();
        _paths = provider.GetRequiredService<IPaths>();
        _logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger<MainWindow>();
        _settingsViewModel = provider.GetRequiredService<SettingsViewModel>();
        _uiScaleFactor = UiScale.Normalize(provider.GetRequiredService<CliOptions>().ForceDeviceScaleFactor);
        UiScale.Apply(RootGrid, _uiScaleFactor);

        // 附件私有目录前缀供图片加载器做越界校验（FR-W-SEC-6 / EDGE-W-29）。
        AppContext.SetData("Tks.AttachmentsDir", _paths.AttachmentsDir);

        DataContext = _viewModel;

        // 立即使当前主题生效（含「跟随系统」解析结果 —— FR-W-SET-1）。
        ThemeApplier.Apply(Application.Current.Resources, provider.GetRequiredService<Core.Services.ISettingsService>().IsDarkThemeEffective);

        AttachViewModelEvents();
        _settingsViewModel.LogoutRequested += OnLogoutRequested;

        SourceInitialized += OnSourceInitialized;
        Activated += OnActivated;
        Deactivated += OnDeactivated;
        LocationChanged += OnGeometryChanged;
        SizeChanged += OnGeometryChanged;
        Closing += OnClosing;
        Closed += OnClosed;
    }

    /// <summary>由窗口工厂调用的启动入口（可见性与首屏装配分离，支持静默到托盘）。</summary>
    public void Start(bool startHidden)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;

        // 首屏装配与建连联动必须执行，即使窗口不显示（--hidden —— FR-W-DSK-3）。
        _initialization = InitializeAsync();

        if (!startHidden)
        {
            ShowWindow(startHidden: false);
        }
    }

    /// <summary>显示窗口（<paramref name="startHidden"/> 为真时不显示）。</summary>
    public void ShowWindow(bool startHidden)
    {
        if (startHidden)
        {
            return;
        }

        ApplyGeometry();

        if (IsVisible)
        {
            ActivateAndFocus();
            return;
        }

        Show();
        ActivateAndFocus();
    }

    /// <summary>隐藏到托盘（<c>closeBehavior == "tray"</c> / Esc —— FR-W-SET-6 / FR-W-DSK-6）。</summary>
    public void HideToTray()
    {
        _viewModel.OnDeactivated();
        Hide();
    }

    /// <summary>唤起并聚焦输入框（全局快捷键 / 托盘 / 单实例 —— FR-W-DSK-2）。</summary>
    public void ActivateAndFocus()
    {
        if (_provider.GetRequiredService<Core.Services.IAuthService>().State != Core.Services.AuthState.Authenticated)
        {
            WindowFactory.ShowLogin(_provider, noticeKey: null);
            return;
        }

        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        _ = Activate();
        ChatPane.FocusInput();
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (IsVisible && WindowState != WindowState.Minimized)
            {
                _ = Activate();
                ChatPane.FocusInput();
            }
        }));
        _ = _viewModel.OnActivatedAsync();
    }

    /// <summary>托盘与全局快捷键的显示/隐藏切换。</summary>
    public void ToggleVisibility()
    {
        if (IsVisible && IsActive)
        {
            HideToTray();
        }
        else
        {
            ActivateAndFocus();
        }
    }

    /// <summary>退出登录/凭据失效时隐藏主窗口并关闭其附属面板。</summary>
    public void PrepareForReauthentication()
    {
        foreach (Window owned in OwnedWindows.Cast<Window>().ToArray())
        {
            owned.Close();
        }

        HideToTray();
    }

    /// <summary>滚动定位到指定消息（点击通知后 —— FR-W-NOTI-4）。</summary>
    public void ScrollToMessage(string messageId)
    {
        if (string.IsNullOrWhiteSpace(messageId))
        {
            return;
        }

        ActivateAndFocus();
        _ = LocateMessageAsync(messageId);
    }

    private async Task LocateMessageAsync(string messageId)
    {
        try
        {
            await _initialization.ConfigureAwait(true);
            await _viewModel.Chat.LocateMessageAsync(messageId).ConfigureAwait(true);
            ChatPane.FocusInput();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "通知消息定位失败");
        }
    }

    private async Task InitializeAsync()
    {
        try
        {
            await _viewModel.InitializeAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 首屏装配失败不得让窗口不可用（NFR-W-12）。
            _logger.LogWarning(ex, "主窗口首屏装配失败");
        }
    }

    private void AttachViewModelEvents()
    {
        _viewModel.PanelRequested += OnPanelRequested;
        _viewModel.ActivateRequested += (_, _) => ToggleVisibility();
        _viewModel.HideToTrayRequested += (_, _) => HideToTray();
        _viewModel.ExitRequested += (_, _) => RequestExit();
        _viewModel.ThemeRevealRequested += OnThemeRevealRequested;
        _viewModel.FocusInputRequested += (_, _) => ChatPane.FocusInput();
        _viewModel.SearchRequested += OnSearchRequested;

        ChatPane.ImageOpenRequested += OnImageOpenRequested;
        _viewModel.Chat.InteractionMenuRequested += OnInteractionRequested;
        var menu = _provider.GetRequiredService<InteractionMenuViewModel>();
        InteractionPane.DataContext = menu;
        menu.CloseRequested += OnInteractionClosed;
        menu.Sent += OnInteractionSent;
        _provider.GetRequiredService<Core.Services.IGamificationService>().LevelChanged += OnLevelFeedback;
        InteractionOverlay.MouseDown += (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, InteractionOverlay)) OnInteractionClosed(this, EventArgs.Empty);
        };
    }

    private async void OnInteractionRequested(object? sender, EventArgs e)
    {
        var menu = _provider.GetRequiredService<InteractionMenuViewModel>();
        if (!menu.IsSending) menu.MessageText = _viewModel.Chat.InputText;
        InteractionOverlay.Visibility = Visibility.Visible;
        await menu.LoadAsync();
    }

    private void OnInteractionClosed(object? sender, EventArgs e) => InteractionOverlay.Visibility = Visibility.Collapsed;

    private void OnInteractionSent(object? sender, string text)
    {
        if (_viewModel.Chat.InputText == text) _viewModel.Chat.InputText = string.Empty;
    }

    private void OnLevelFeedback(object? sender, Core.Services.LevelChangeFeedback feedback)
    {
        if (feedback.ChangeType is "RESTORE" or "RESET")
        {
            _viewModel.Chat.NoticeText = feedback.ChangeType == "RESTORE"
                ? I18n.T("level.restore.body", feedback.LevelName)
                : I18n.T("level.reset.body") + " " + I18n.T("profile.pointsUnaffected") + " " + I18n.T("level.reset.guide");
            _viewModel.Chat.IsNoticeVisible = true;
        }
        if (!feedback.PlayCelebration || feedback.ChangeType != "UPGRADE") return;
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new PandaBadge
        {
            Style = (Style)FindResource("Tks.PandaBadge"),
            LevelCode = feedback.LevelCode,
            Diameter = 88,
            IsAnimated = !_windowChrome.IsAnimationReduced && feedback.LevelCode == "PANDA_LV7"
        });
        panel.Children.Add(new TextBlock
        {
            Text = I18n.T("level.upgrade.body", feedback.LevelName),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 20,
            Margin = new Thickness(0, 12, 0, 12)
        });
        var popup = new Window
        {
            Owner = this,
            Title = I18n.T("level.upgrade.title"),
            Content = panel,
            Width = 340,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false
        };
        var close = new Button { Content = I18n.T("common.close"), Padding = new Thickness(12, 6, 12, 6) };
        close.Click += (_, _) => popup.Close();
        panel.Children.Add(close);
        if (!_windowChrome.IsAnimationReduced)
            panel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(400)));
        ApplyChildTheme(popup);
        UiScale.Apply(panel, _uiScaleFactor);
        popup.Width = UiScale.ToWindow(340, _uiScaleFactor);
        popup.Show();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _windowHandle = new WindowInteropHelper(this).Handle;
        _hwndSource = HwndSource.FromHwnd(_windowHandle);
        _hwndSource?.AddHook(WindowMessageHook);
        ApplyGeometry();

        ApplySystemGlass(ThemeApplier.Current == true);
    }

    /// <summary>应用系统玻璃背景，并按真实结果同步透明/不透明降级状态。</summary>
    private void ApplySystemGlass(bool dark)
    {
        if (_windowHandle == IntPtr.Zero)
        {
            return;
        }

        var applied = _windowChrome.TryApplySystemBlur(_windowHandle, dark);
        _viewModel.IsSystemBlurAvailable = applied;

        if (applied)
        {
            Background = Brushes.Transparent;
        }
        else
        {
            SetResourceReference(BackgroundProperty, "Tks.Brush.Background");
        }

        _logger.LogDebug("系统液态玻璃应用结果：{Applied}（句柄 {Handle}）", applied, _windowHandle);
    }

    /// <summary>应用几何：越界时经 <c>EnsureOnScreen</c> 回主屏居中（EDGE-W-25）。</summary>
    private void ApplyGeometry()
    {
        if (_applyingGeometry)
        {
            return;
        }

        _applyingGeometry = true;
        try
        {
            var logicalWidth = Math.Max(MainViewModel.MinWindowWidth, _viewModel.WindowWidth);
            var logicalHeight = Math.Max(MainViewModel.MinWindowHeight, _viewModel.WindowHeight);
            var width = UiScale.ToWindow(logicalWidth, _uiScaleFactor);
            var height = UiScale.ToWindow(logicalHeight, _uiScaleFactor);

            MinWidth = UiScale.ToWindow(MainViewModel.MinWindowWidth, _uiScaleFactor);
            MinHeight = UiScale.ToWindow(MainViewModel.MinWindowHeight, _uiScaleFactor);
            Width = width;
            Height = height;

            if (double.IsNaN(_viewModel.WindowLeft) || double.IsNaN(_viewModel.WindowTop))
            {
                return;
            }

            var (x, y, safeWidth, safeHeight) = _windowChrome.EnsureOnScreen(
                _viewModel.WindowLeft,
                _viewModel.WindowTop,
                width,
                height,
                _windowHandle);

            Left = x;
            Top = y;
            Width = Math.Max(MinWidth, safeWidth);
            Height = Math.Max(MinHeight, safeHeight);
        }
        finally
        {
            _applyingGeometry = false;
        }
    }

    private void OnGeometryChanged(object? sender, EventArgs e)
    {
        if (_applyingGeometry || WindowState != WindowState.Normal)
        {
            return;
        }

        _viewModel.UpdateGeometry(
            UiScale.ToLogical(Width, _uiScaleFactor),
            UiScale.ToLogical(Height, _uiScaleFactor),
            Left,
            Top);
    }

    private IntPtr WindowMessageHook(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        _ = hwnd;
        _ = wParam;
        _ = lParam;
        _ = handled;

        if (message is NativeMethods.WmDisplayChange or NativeMethods.WmDpiChanged)
        {
            // Let WPF apply its own DPI transition first, then revalidate the saved
            // DIP geometry against the newly enumerated physical work areas.
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(ApplyGeometry));
        }

        return IntPtr.Zero;
    }

    private void OnActivated(object? sender, EventArgs e) => _ = _viewModel.OnActivatedAsync();

    private void OnDeactivated(object? sender, EventArgs e) => _viewModel.OnDeactivated();

    /// <summary>
    /// 关闭行为（FR-W-SET-6 / FR-W-DSK-13 / EDGE-W-9）：
    /// 由 <see cref="MainViewModel.HandleCloseRequest"/> 决定是隐藏到托盘还是真正退出。
    /// </summary>
    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_viewModel.HandleCloseRequest())
        {
            // 取消关闭：已改为隐藏到托盘。
            e.Cancel = true;
            _logger.LogDebug("关闭被拦截：已最小化到托盘");
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _hwndSource?.RemoveHook(WindowMessageHook);
        _hwndSource = null;
        _settingsViewModel.LogoutRequested -= OnLogoutRequested;
        _viewModel.Chat.InteractionMenuRequested -= OnInteractionRequested;
        var menu = _provider.GetRequiredService<InteractionMenuViewModel>();
        menu.CloseRequested -= OnInteractionClosed;
        menu.Sent -= OnInteractionSent;
        _provider.GetRequiredService<Core.Services.IGamificationService>().LevelChanged -= OnLevelFeedback;
        _viewModel.Dispose();
        ThemeApplier.Apply(Resources, false);
    }

    private void OnLogoutRequested(object? sender, EventArgs e)
        => Dispatcher.BeginInvoke(() => WindowFactory.ReturnToLogin(_provider, noticeKey: null));

    private void RequestExit()
    {
        try
        {
            Application.Current?.Shutdown();
        }
        catch (InvalidOperationException)
        {
            Close();
        }
    }

    /// <summary>轻量提示条关闭（用户已阅读 —— 不改变业务状态）。</summary>
    private void DismissNotice_Click(object sender, RoutedEventArgs e) => _viewModel.Chat.ClearNotice();

    /// <summary>窗口内快捷键（FR-W-DSK-6）。</summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        var control = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                if (_viewModel.Chat.IsSearchOpen)
                {
                    _viewModel.Chat.CloseSearchCommand.Execute(null);
                    return;
                }
                if (InteractionOverlay.Visibility == Visibility.Visible)
                {
                    OnInteractionClosed(this, EventArgs.Empty);
                    return;
                }
                _viewModel.HideToTrayCommand.Execute(null);
                return;

            case Key.F when control:
                e.Handled = true;
                _viewModel.OpenSearchCommand.Execute(null);
                return;

            case Key.OemComma when control:
                e.Handled = true;
                _viewModel.OpenSettingsCommand.Execute(null);
                return;

            case Key.L when control:
                e.Handled = true;
                _viewModel.FocusInputCommand.Execute(null);
                return;

            case Key.R when control && shift:
                e.Handled = true;
                _viewModel.ManualReconnectCommand.Execute(null);
                return;

            default:
                return;
        }
    }

    /* ------------------------------------------------------------------ */
    /* 面板 / 独立窗口（FR-W-DSK-8）                                        */
    /* ------------------------------------------------------------------ */

    private void OnPanelRequested(object? sender, MainPanel panel)
    {
        if (_provider.GetRequiredService<Core.Services.IAuthService>().State != Core.Services.AuthState.Authenticated)
        {
            WindowFactory.ShowLogin(_provider, noticeKey: null);
            return;
        }

        switch (panel)
        {
            case MainPanel.History:
                ShowEmbedded(() => new HistoryView { DataContext = _provider.GetRequiredService<HistoryViewModel>() },
                    "history.title",
                    initialize: view => ((HistoryViewModel)view.DataContext).LoadAsync());
                return;

            case MainPanel.Reminders:
                ShowEmbedded(() => new RemindersView { DataContext = _provider.GetRequiredService<RemindersViewModel>() },
                    "reminder.title",
                    initialize: view => ((RemindersViewModel)view.DataContext).LoadAsync());
                return;

            case MainPanel.Settings:
                ShowSettings();
                return;

            case MainPanel.Profile:
                ShowProfile();
                return;

            case MainPanel.PointsLedger:
                ShowPointsLedgerWindow();
                return;

            case MainPanel.MemoryArchive:
                ShowMemoryArchiveWindow();
                return;
        }
    }

    private void ShowSettings()
    {
        var viewModel = _provider.GetRequiredService<SettingsViewModel>();
        _ = viewModel.RefreshStorageAsync();

        ShowEmbedded(
            () => new SettingsView { DataContext = viewModel },
            "settings.title",
            initialize: null);
    }

    private void ShowProfile()
    {
        var viewModel = _provider.GetRequiredService<ProfileViewModel>();
        viewModel.IsCelebrationAnimatable = _viewModel.IsCelebrationAnimationEnabled;
        _ = viewModel.RefreshCommand.ExecuteAsync(null);

        ShowEmbedded(
            () => new ProfileView { DataContext = viewModel },
            "profile.title",
            initialize: null);
    }

    /// <summary>「积分流水」独立弹出（共享同一宿主服务 —— FR-W-DSK-8）。</summary>
    private void ShowPointsLedgerWindow()
    {
        var viewModel = _provider.GetRequiredService<ProfileViewModel>();

        if (_pointsLedgerWindow is { IsVisible: true } existing)
        {
            _ = existing.Activate();
            return;
        }

        _pointsLedgerWindow = CreateChildWindow(
            new ProfileView { DataContext = viewModel },
            I18n.T("profile.pointsHistory"));

        _pointsLedgerWindow.Closed += (_, _) => _pointsLedgerWindow = null;
        _pointsLedgerWindow.Show();
    }

    /// <summary>「记忆档案」独立弹出（共享同一宿主服务 —— FR-W-DSK-8）。</summary>
    private void ShowMemoryArchiveWindow()
    {
        var viewModel = _provider.GetRequiredService<HistoryViewModel>();

        if (_memoryArchiveWindow is { IsVisible: true } existing)
        {
            _ = existing.Activate();
            return;
        }

        _memoryArchiveWindow = CreateChildWindow(
            new HistoryView { DataContext = viewModel },
            I18n.T("history.facts"));

        _memoryArchiveWindow.Closed += (_, _) => _memoryArchiveWindow = null;
        _memoryArchiveWindow.Show();

        _ = viewModel.LoadAsync();
    }

    private Window CreateChildWindow(UIElement content, string title)
    {
        if (content is FrameworkElement element)
        {
            UiScale.Apply(element, _uiScaleFactor);
        }

        var child = new Window
        {
            Title = title,
            Owner = this,
            Width = UiScale.ToWindow(640, _uiScaleFactor),
            Height = UiScale.ToWindow(560, _uiScaleFactor),
            MinWidth = UiScale.ToWindow(MainViewModel.MinWindowWidth, _uiScaleFactor),
            MinHeight = UiScale.ToWindow(420, _uiScaleFactor),
            Content = content,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (Brush)FindResource("Tks.Brush.Background"),
            Foreground = (Brush)FindResource("Tks.Brush.TextPrimary"),
            FontFamily = (FontFamily)FindResource("Tks.FontFamily"),
            FontSize = (double)FindResource("Tks.Font.Body"),
        };
        ApplyChildTheme(child);
        return child;
    }

    private static void ApplyChildTheme(Window child)
    {
        child.SetResourceReference(BackgroundProperty, "Tks.Brush.Background");
        child.SetResourceReference(ForegroundProperty, "Tks.Brush.TextPrimary");
        VisualPreferences.SetUseOpaqueFallback(child, true);
    }

    /// <summary>把 <paramref name="factory"/> 产出的内容放到主窗口内的浮层（同一窗口内可达 —— FR-W-HIS-*）。</summary>
    private void ShowEmbedded(Func<FrameworkElement> factory, string titleKey, Func<FrameworkElement, Task>? initialize)
    {
        var panel = factory();
        UiScale.Apply(panel, _uiScaleFactor);

        var host = new Window
        {
            Title = I18n.T(titleKey),
            Owner = this,
            Width = UiScale.ToWindow(720, _uiScaleFactor),
            Height = UiScale.ToWindow(620, _uiScaleFactor),
            MinWidth = UiScale.ToWindow(MainViewModel.MinWindowWidth, _uiScaleFactor),
            MinHeight = UiScale.ToWindow(420, _uiScaleFactor),
            Content = panel,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (Brush)FindResource("Tks.Brush.Background"),
            Foreground = (Brush)FindResource("Tks.Brush.TextPrimary"),
            FontFamily = (FontFamily)FindResource("Tks.FontFamily"),
            FontSize = (double)FindResource("Tks.Font.Body"),
        };

        ApplyChildTheme(host);
        host.Show();

        if (initialize is not null)
        {
            _ = initialize(panel);
        }
    }

    private void OnSearchRequested(object? sender, EventArgs e)
    {
        ChatPane.OpenSearch();
    }

    private void OnImageOpenRequested(object? sender, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        var viewer = new ImageViewerWindow(path)
        {
            Owner = this,
            Width = UiScale.ToWindow(900, _uiScaleFactor),
            Height = UiScale.ToWindow(700, _uiScaleFactor),
            MinWidth = UiScale.ToWindow(480, _uiScaleFactor),
            MinHeight = UiScale.ToWindow(360, _uiScaleFactor),
        };

        if (viewer.Content is FrameworkElement root)
        {
            UiScale.Apply(root, _uiScaleFactor);
        }

        viewer.Show();
    }

    /* ------------------------------------------------------------------ */
    /* 主题揭示动画（FR-W-UI-4 / FR-W-UI-11）                               */
    /* ------------------------------------------------------------------ */

    private void OnThemeRevealRequested(object? sender, bool dark)
    {
        ThemeRevealImage.Visibility = Visibility.Collapsed;
        ThemeRevealImage.Source = null;
        var width = RootGrid.ActualWidth;
        var height = RootGrid.ActualHeight;
        var dpi = VisualTreeHelper.GetDpi(this);
        if (_windowChrome.IsAnimationReduced || ThemeApplier.Current == dark
            || width <= 0 || height <= 0 || width * height * dpi.DpiScaleX * dpi.DpiScaleY > 16_000_000)
        {
            ThemeApplier.Apply(Application.Current.Resources, dark);
            ApplySystemGlass(dark);
            return;
        }

        // Preserve the old surface; the expanding hole reveals the actual new UI.
        var snapshot = new RenderTargetBitmap((int)Math.Ceiling(width * dpi.DpiScaleX),
            (int)Math.Ceiling(height * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        snapshot.Render(RootGrid);
        snapshot.Freeze();
        ThemeApplier.Apply(Application.Current.Resources, dark);
        ApplySystemGlass(dark);
        var hole = new EllipseGeometry(new Point(width / 2, height / 2), 0, 0);
        var mask = new CombinedGeometry(GeometryCombineMode.Exclude,
            new RectangleGeometry(new Rect(0, 0, width, height)), hole);
        ThemeRevealImage.Source = snapshot;
        ThemeRevealImage.Clip = mask;
        ThemeRevealImage.Visibility = Visibility.Visible;
        var radius = Math.Sqrt(width * width + height * height) / 2 + 1;
        var animation = new DoubleAnimation(0, radius, TryGetRevealDuration());
        animation.Completed += (_, _) =>
        {
            if (ReferenceEquals(ThemeRevealImage.Source, snapshot))
            {
                ThemeRevealImage.Visibility = Visibility.Collapsed;
                ThemeRevealImage.Source = null;
                ThemeRevealImage.Clip = null;
            }
        };
        hole.BeginAnimation(EllipseGeometry.RadiusXProperty, animation);
        hole.BeginAnimation(EllipseGeometry.RadiusYProperty, animation);
    }

    /// <summary>揭示动画时长（取自 tokens，约 400ms —— FR-W-UI-4）。</summary>
    private Duration TryGetRevealDuration()
        => TryFindResource("Tks.Duration.ThemeReveal") is Duration duration
            ? duration
            : new Duration(TimeSpan.FromMilliseconds(400));
}
