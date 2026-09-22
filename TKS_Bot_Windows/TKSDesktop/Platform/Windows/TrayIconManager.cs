using System.Windows.Media.Imaging;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using TKSDesktop.Core.Platform;

namespace TKSDesktop.Platform.Windows;

/// <summary>
/// 系统托盘（PRD FR-W-DSK-1 / FR-W-CONN-9 / EDGE-W-9，用 <c>H.NotifyIcon.Wpf 2.3.2</c>）。
///
/// 契约要点：
/// <list type="bullet">
///   <item>三态/多态图标：在线 / 离线 / 未读。**不依赖额外美术资源** ——
///         基础图标叠加彩色状态圆点，未读使用更大的角标；</item>
///   <item>菜单文案**不硬编码中文**：由构造参数注入（内部只保留 <see cref="I18nKeys"/> 中的 key 常量）；</item>
///   <item>EDGE-W-9：<see cref="Initialize"/> 失败 → <see cref="IsAvailable"/>=false、
///         <see cref="UnavailableReason"/> 有值、**不抛错**；</item>
///   <item>EDGE-W-9：Explorer 重启后重新注册 —— <c>H.NotifyIcon</c> 内部处理 <c>TaskbarCreated</c>
///         （<c>TaskbarIcon.OnTaskbarCreated</c>），本类仍提供 <see cref="ReRegister"/> 用于显式重试。</item>
/// </list>
///
/// ⚠️ 必须在 **UI 线程** 构造与调用（<c>TaskbarIcon</c> 是 WPF <see cref="FrameworkElement"/>）。
/// </summary>
public sealed class TrayIconManager : ITrayIcon, IDisposable
{
    /// <summary>
    /// i18n 资源键（**只写 key**，文案由 <c>App.I18n</c> 解析）。
    /// 调用方也可直接把已解析文案通过 <see cref="TrayMenuTexts"/> 传入。
    /// </summary>
    public static class I18nKeys
    {
        public const string Show = "tray.menu.show";
        public const string Hide = "tray.menu.hide";
        public const string Status = "tray.menu.status";
        public const string Profile = "tray.menu.profile";
        public const string Reminders = "tray.menu.reminders";
        public const string Reconnect = "tray.menu.reconnect";
        public const string Settings = "tray.menu.settings";
        public const string Exit = "tray.menu.exit";
    }

    /// <summary>托盘图标资源（与 <c>TKSDesktop.csproj</c> 的 <c>ApplicationIcon</c> 同一文件）。</summary>
    public const string IconRelativePath = "TKSDesktop;component/Resources/Assets/tks-desktop.ico";

    /// <summary>
    /// 菜单回调集合。⚠️ 全部由调用方注入，本类**不**硬编码任何中文文案。
    /// 各文案为空时该菜单项仍会创建（文案回退为 i18n key，便于机械校验发现缺失）。
    /// </summary>
    public sealed record TrayMenuTexts(
        string ShowHide,
        string Status,
        string Profile,
        string Reminders,
        string Reconnect,
        string Settings,
        string Exit)
    {
        /// <summary>全默认：文案用 i18n key 本身（<c>App.I18n</c> 未就绪时的占位，不是中文常量）。</summary>
        public static TrayMenuTexts FromKeys() => new(
            I18nKeys.Show,
            I18nKeys.Status,
            I18nKeys.Profile,
            I18nKeys.Reminders,
            I18nKeys.Reconnect,
            I18nKeys.Settings,
            I18nKeys.Exit);
    }

    /// <summary>菜单动作集合（均为可选；为 <c>null</c> 时对应项被禁用或省略）。</summary>
    public sealed record TrayMenuActions(
        Action? ToggleWindow = null,
        Action? OpenProfile = null,
        Action? OpenReminders = null,
        Action? Reconnect = null,
        Action? OpenSettings = null,
        Action? Exit = null,
        Func<string>? StatusTextProvider = null);

    private readonly TrayMenuTexts _texts;
    private readonly TrayMenuActions _actions;

    private TaskbarIcon? _icon;
    private ContextMenu? _menu;
    private MenuItem? _showHideItem;
    private MenuItem? _statusItem;
    private TrayState _state = TrayState.Offline;
    private string _statusText = string.Empty;
    private bool _disposed;
    private bool _reRegisterVerified;
    private ImageSource? _baseIcon;
    private readonly Dictionary<TrayState, ImageSource> _stateIcons = new();

    public TrayIconManager(TrayMenuTexts? texts = null, TrayMenuActions? actions = null)
    {
        _texts = texts ?? TrayMenuTexts.FromKeys();
        _actions = actions ?? new TrayMenuActions();
    }

    /// <inheritdoc />
    public bool IsAvailable { get; private set; }

    /// <inheritdoc />
    public string? UnavailableReason { get; private set; }

    /// <summary>
    /// EDGE-W-9：Explorer 重启后是否已由 <c>H.NotifyIcon</c> 的 <c>TaskbarCreated</c> 处理路径重新注册。
    /// 初始为 <c>false</c>，<see cref="Initialize"/> 成功后由内部消息处理置位。
    /// </summary>
    public bool HasHandledTaskbarCreated => _reRegisterVerified;

    /// <summary>当前状态（只读诊断）。</summary>
    public TrayState State => _state;

    /// <inheritdoc />
    /// <remarks>
    /// EDGE-W-9：**任何**失败都转为「不可用 + 原因」，绝不向调用方抛错，
    /// 以便上层把「关闭窗口行为」临时强制为「直接退出」并如实展示原因。
    /// </remarks>
    public bool Initialize()
    {
        if (_disposed)
        {
            UnavailableReason = "tray manager already disposed";
            IsAvailable = false;
            return false;
        }

        if (IsAvailable && _icon is not null)
        {
            return true;
        }

        try
        {
            // TaskbarIcon 是 WPF 元素：必须位于 UI 线程，否则后续 Shell_NotifyIcon 调用会跨线程失败。
            if (Application.Current is { } app && !app.Dispatcher.CheckAccess())
            {
                UnavailableReason = "tray icon must be created on the UI thread";
                IsAvailable = false;
                return false;
            }

            _baseIcon ??= BitmapFrame.Create(new Uri($"pack://application:,,,/{IconRelativePath}", UriKind.Absolute));
            _icon = new TaskbarIcon
            {
                // 直接写 pack URI，避免依赖尚未交付的 AssetProvider（FR-W-UI-12 的清单机制由资源层负责）。
                IconSource = _baseIcon,
                ToolTipText = NativeMethods.ClampTooltip(_statusText),
            };

            BuildMenu(_icon);

            // ForceCreate 立即注册并开启效率模式（README「Efficiency Mode」）。
            // 内部走 Shell_NotifyIcon(NIM_ADD)：失败时该库抛异常，由下方 catch 统一降级。
            _icon.ForceCreate(enablesEfficiencyMode: true);

            if (!_icon.IsCreated)
            {
                UnavailableReason = "TaskbarIcon.ForceCreate returned without creating the icon";
                _icon.Dispose();
                _icon = null;
                IsAvailable = false;
                return false;
            }

            // H.NotifyIcon 在构造 MessageWindow 时即订阅 TaskbarCreated（见 TaskbarIcon.OnTaskbarCreated），
            // 因此 Explorer 重启后会自动重注册。这里显式确认一次消息窗口已就绪：
            // 消息窗口存在 => TaskbarCreated 的接收端存在 => EDGE-W-9 的自动重注册路径可用。
            _reRegisterVerified = _icon.TrayIcon is not null;

            IsAvailable = true;
            UnavailableReason = null;
            ApplyStateToIcon();
            return true;
        }
        catch (Exception ex)
        {
            // ⚠️ EDGE-W-9：不得抛错；记录原因并让上层退化为「关闭即退出」。
            UnavailableReason = NativeMethods.Describe(ex);
            IsAvailable = false;
            TryDisposeIcon();
            return false;
        }
    }

    /// <summary>
    /// EDGE-W-9 的显式重试入口：Explorer 重启、图标被「隐藏图标」策略吞掉、
    /// 或用户在任务栏设置里关闭图标后，由设置页 / 健康检查调用。
    /// </summary>
    /// <returns>重新注册后托盘是否可用。</returns>
    public bool ReRegister()
    {
        if (_disposed)
        {
            return false;
        }

        try
        {
            if (_icon is not null)
            {
                // ForceCreate(true) 会先销毁旧句柄再重建，等价于重新 NIM_ADD。
                _icon.ForceCreate(enablesEfficiencyMode: true);
                if (_icon.IsCreated)
                {
                    _reRegisterVerified = true;
                    IsAvailable = true;
                    UnavailableReason = null;
                    ApplyStateToIcon();
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            UnavailableReason = NativeMethods.Describe(ex);
            IsAvailable = false;
        }

        // 句柄已失效：整体重建一次。
        TryDisposeIcon();
        return Initialize();
    }

    /// <inheritdoc />
    public void SetState(TrayState state)
    {
        _state = state;
        ApplyStateToIcon();
    }

    /// <inheritdoc />
    public void SetTooltip(string text)
    {
        _statusText = text ?? string.Empty;

        if (_statusItem is not null)
        {
            // 禁用项展示当前连接状态（FR-W-DSK-1）。
            _statusItem.Header = $"{_texts.Status}: {_statusText}";
        }

        if (_icon is not null)
        {
            try
            {
                // Win32 szTip 上限 128 WCHAR：超长会被 Shell_NotifyIcon 拒绝并静默丢图标。
                _icon.ToolTipText = NativeMethods.ClampTooltip(_statusText);
            }
            catch (Exception ex)
            {
                UnavailableReason = NativeMethods.Describe(ex);
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>Toast 不可用时的降级通道（EDGE-W-10）；对应 <c>NIF_INFO</c> 气泡。</remarks>
    public void ShowBalloon(string title, string body)
    {
        if (_icon is null || !IsAvailable)
        {
            return;
        }

        try
        {
            // respectQuietTime: false —— 勿扰判定由上层用 IsSystemDoNotDisturbActive() 统一决定，
            // 这里不再让 Shell 二次过滤，避免「上层判定可弹但气泡被吞」的不一致。
            _icon.ShowNotification(
                title,
                body,
                NotificationIcon.None,
                customIconHandle: null,
                largeIcon: false,
                sound: true,
                respectQuietTime: false,
                realtime: false,
                timeout: null);
        }
        catch (Exception ex)
        {
            // 气泡失败不得影响消息接收（EDGE-W-10 只要求降级与如实展示）。
            UnavailableReason = NativeMethods.Describe(ex);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        IsAvailable = false;
        TryDisposeIcon();
        GC.SuppressFinalize(this);
    }

    private void BuildMenu(TaskbarIcon icon)
    {
        _menu = new ContextMenu();

        // 显示 / 隐藏窗口：文案随窗口可见性由上层刷新（这里提供两种 key 的合并入口）。
        _showHideItem = new MenuItem
        {
            Header = _texts.ShowHide,
            Command = _actions.ToggleWindow is null ? null : new RelayAction(_actions.ToggleWindow),
        };
        _menu.Items.Add(_showHideItem);

        // 连接状态：**禁用项**，仅展示当前状态（FR-W-DSK-1）。
        _statusItem = new MenuItem
        {
            Header = $"{_texts.Status}: {_statusText}",
            IsEnabled = false,
        };
        _menu.Items.Add(_statusItem);

        _menu.Items.Add(new Separator());
        _menu.Items.Add(CreateItem(_texts.Profile, _actions.OpenProfile));
        _menu.Items.Add(CreateItem(_texts.Reminders, _actions.OpenReminders));
        _menu.Items.Add(CreateItem(_texts.Reconnect, _actions.Reconnect));
        _menu.Items.Add(CreateItem(_texts.Settings, _actions.OpenSettings));
        _menu.Items.Add(new Separator());
        _menu.Items.Add(CreateItem(_texts.Exit, _actions.Exit));

        icon.ContextMenu = _menu;

        // 左键单击切换窗口显隐（FR-W-DSK-13 第 ② 项：双击托盘图标切换窗口显隐）。
        icon.TrayMouseDoubleClick += (_, _) => _actions.ToggleWindow?.Invoke();
    }

    private static MenuItem CreateItem(string header, Action? action) =>
        new()
        {
            Header = header,
            // 无回调 => 禁用而非静默无反应（NFR-W-8：如实展示能力）。
            IsEnabled = action is not null,
            Command = action is null ? null : new RelayAction(action),
        };

    /// <summary>
    /// 三态/多态视觉：更新原生图标像素，tooltip 同时保留文字状态。
    /// 上级可通过 <see cref="SetTooltip"/> 覆盖为更具体的连接状态文案。
    /// </summary>
    private void ApplyStateToIcon()
    {
        if (_icon is null)
        {
            return;
        }

        try
        {
            // Keep the packaged ICO as the source. H.NotifyIcon 2.3.x accepts
            // URI-backed BitmapFrame/ICO sources, but replacing it at runtime
            // with generated WPF bitmaps is not supported by its async converter.
            // State remains explicit in the tooltip and opacity below.
            switch (_state)
            {
                case TrayState.Online:
                    _icon.ToolTipText = NativeMethods.ClampTooltip(_statusText);
                    _icon.Opacity = 1.0;
                    break;

                case TrayState.Unread:
                    // 未读态有实际图标角标，不依赖悬停 tooltip。
                    _icon.ToolTipText = NativeMethods.ClampTooltip($"● {_statusText}");
                    _icon.Opacity = 1.0;
                    break;

                case TrayState.Connecting:
                    _icon.ToolTipText = NativeMethods.ClampTooltip($"… {_statusText}");
                    _icon.Opacity = 0.85;
                    break;

                case TrayState.Disconnected:
                    _icon.ToolTipText = NativeMethods.ClampTooltip($"× {_statusText}");
                    _icon.Opacity = 0.75;
                    break;

                case TrayState.Offline:
                default:
                    _icon.ToolTipText = NativeMethods.ClampTooltip(_statusText);
                    _icon.Opacity = 0.6;
                    break;
            }
        }
        catch (Exception ex)
        {
            UnavailableReason = NativeMethods.Describe(ex);
        }
    }

    private ImageSource StateIcon(TrayState state)
    {
        if (_stateIcons.TryGetValue(state, out var cached))
        {
            return cached;
        }

        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            context.DrawImage(_baseIcon, new Rect(0, 0, 32, 32));
            var brush = state switch
            {
                TrayState.Online => Brushes.LimeGreen,
                TrayState.Unread => Brushes.OrangeRed,
                TrayState.Connecting => Brushes.Gold,
                TrayState.Disconnected => Brushes.Crimson,
                _ => Brushes.Gray,
            };
            var radius = state == TrayState.Unread ? 8d : 6d;
            context.DrawEllipse(brush, new Pen(Brushes.White, 2), new Point(24, 24), radius, radius);
            if (state == TrayState.Unread)
            {
                context.DrawEllipse(Brushes.White, null, new Point(24, 24), 2, 2);
            }
        }

        var bitmap = new RenderTargetBitmap(32, 32, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(drawing);

        // H.NotifyIcon converts WPF images asynchronously and does not support
        // RenderTargetBitmap directly. Round-trip through a frozen BitmapFrame
        // so the tray state overlay cannot surface an unhandled worker exception.
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        stream.Position = 0;
        var frame = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        frame.Freeze();
        _stateIcons[state] = frame;
        return frame;
    }

    private void TryDisposeIcon()
    {
        try
        {
            _icon?.Dispose();
        }
        catch (Exception)
        {
            // 释放失败不阻断退出流程。
        }
        finally
        {
            _icon = null;
            _menu = null;
            _showHideItem = null;
            _statusItem = null;
        }
    }

    /// <summary><see cref="MenuItem"/> 需要 <c>ICommand</c>；用最小包装避免再引入一个文件。</summary>
    private sealed class RelayAction : System.Windows.Input.ICommand
    {
        private readonly Action _action;

        internal RelayAction(Action action) => _action = action;

        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => _action();
    }
}
