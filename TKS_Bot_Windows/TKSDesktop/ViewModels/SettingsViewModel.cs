using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TKSDesktop.App;
using TKSDesktop.Contracts;
using TKSDesktop.Core.Platform;
using TKSDesktop.Core.Services;

namespace TKSDesktop.ViewModels;

/// <summary>服务地址连通性测试结果（三态各一文案 —— FR-W-SET-4 / EDGE-W-28）。</summary>
public enum ConnectionTestOutcome
{
    /// <summary>未测试。</summary>
    NotTested,

    /// <summary>连接正常。</summary>
    Ok,

    /// <summary>服务可达，但健康检查路径不可用（404 中间态）。</summary>
    HealthUnavailable,

    /// <summary>无法确认服务可达。</summary>
    Unreachable,
}

/// <summary>
/// 设置页视图模型（FR-W-SET-1..12 / FR-W-NOTI-7 / FR-W-SEC-5 / FR-W-AUTH-7/10 / EDGE-W-8/9/10）。
///
/// <list type="bullet">
///   <item>主题三态 + **监听系统主题变化**（FR-W-SET-1）；</item>
///   <item>城市：**截断 `#` 之后内容**；</item>
///   <item>服务地址：可改 + 「由 REST 推导」+ 连通性测试三态文案（FR-W-SET-4）；</item>
///   <item>清空本地会话 **必须** 调 <c>IChatService.ClearLocalConversationAsync()</c>（与聊天页共用 —— FR-W-CHAT-12）；</item>
///   <item>全局快捷键失败**必须**显示 <c>settings.hotkey.inUse</c>，**不得静默回落**（EDGE-W-8）；</item>
///   <item>勿扰时段**支持跨午夜**（由 <c>DoNotDisturbSettings.ContainsLocal</c> 承担）；</item>
///   <item>托盘不可用时**临时强制「直接退出」**并提示 <c>settings.tray.unavailable</c>（EDGE-W-9）；</item>
///   <item>打开目录**只允许** <c>explorer.exe &lt;已校验存在的目录&gt;</c>（FR-W-SEC-5）；</item>
///   <item>检查更新**纯本地 `version.json`**，缺文件静默视为无更新（FR-W-SET-9）。</item>
/// </list>
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject, IDisposable
{
    private const string ThemeSystem = "system";
    private const string ThemeLight = "light";
    private const string ThemeDark = "dark";
    private const string CloseBehaviorTray = "tray";
    private const string CloseBehaviorQuit = "quit";
    private const string SendKeyCtrlEnter = "ctrl+enter";
    private const string UpdateManifestFileName = "version.json";

    private readonly ISettingsService _settings;
    private readonly IAuthService _auth;
    private readonly IChatService _chat;
    private readonly IMediaService _media;
    private readonly IConnectivityTest _connectivity;
    private readonly IUserPrompt _prompt;
    private readonly IShellLauncher _shell;
    private readonly IAutoStartManager _autoStart;
    private readonly IGlobalHotkey _hotkey;
    private readonly ITrayIcon _tray;
    private readonly IWindowChrome _windowChrome;
    private readonly IPaths _paths;
    private readonly IUiDispatcher _dispatcher;

    private bool _disposed;
    private bool _loading;

    /// <summary>构造。</summary>
    public SettingsViewModel(
        ISettingsService settings,
        IAuthService auth,
        IChatService chat,
        IMediaService media,
        IConnectivityTest connectivity,
        IUserPrompt prompt,
        IShellLauncher shell,
        IAutoStartManager autoStart,
        IGlobalHotkey hotkey,
        ITrayIcon tray,
        IWindowChrome windowChrome,
        IPaths paths,
        IUiDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(auth);
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(media);
        ArgumentNullException.ThrowIfNull(connectivity);
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(autoStart);
        ArgumentNullException.ThrowIfNull(hotkey);
        ArgumentNullException.ThrowIfNull(tray);
        ArgumentNullException.ThrowIfNull(windowChrome);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(dispatcher);

        _settings = settings;
        _auth = auth;
        _chat = chat;
        _media = media;
        _connectivity = connectivity;
        _prompt = prompt;
        _shell = shell;
        _autoStart = autoStart;
        _hotkey = hotkey;
        _tray = tray;
        _windowChrome = windowChrome;
        _paths = paths;
        _dispatcher = dispatcher;

        // FR-W-SET-1：**必须监听**系统主题变化（「跟随系统」时界面随之切换）。
        _windowChrome.SystemThemeChanged += OnSystemThemeChanged;

        LoadFromSettings();
    }

    /// <summary>请求切换主题（View 层据此重算资源字典并播放揭示动画 —— FR-W-UI-4）。</summary>
    public event EventHandler<bool>? ThemeApplied;

    /// <summary>请求退出登录（View 层回到登录页）。</summary>
    public event EventHandler? LogoutRequested;

    /* ---- 主题（FR-W-SET-1） ---- */

    /// <summary>`system` / `light` / `dark`。</summary>
    [ObservableProperty]
    private string _theme = ThemeSystem;

    /// <summary>是否跟随系统。</summary>
    public bool IsThemeSystem => string.Equals(Theme, ThemeSystem, StringComparison.Ordinal);

    /// <summary>是否日间。</summary>
    public bool IsThemeLight => string.Equals(Theme, ThemeLight, StringComparison.Ordinal);

    /// <summary>是否夜间。</summary>
    public bool IsThemeDark => string.Equals(Theme, ThemeDark, StringComparison.Ordinal);

    /* ---- 城市与服务地址（FR-W-SET-2/3/4） ---- */

    /// <summary>天气城市（<c>#</c> 之后内容被截断）。</summary>
    [ObservableProperty]
    private string _city = string.Empty;

    /// <summary>REST 基址。</summary>
    [ObservableProperty]
    private string _apiBaseUrl = string.Empty;

    /// <summary>WS 基址。</summary>
    [ObservableProperty]
    private string _wsBaseUrl = string.Empty;

    /// <summary>最近一次连通性测试结果。</summary>
    [ObservableProperty]
    private ConnectionTestOutcome _testOutcome = ConnectionTestOutcome.NotTested;

    /* ---- 本地数据（FR-W-SET-10/11） ---- */

    /// <summary>是否已加密保存凭据（DPAPI）。</summary>
    [ObservableProperty]
    private bool _isCredentialEncrypted;

    /// <summary>是否已持久化凭据。</summary>
    [ObservableProperty]
    private bool _hasPersistedCredential;

    /// <summary>凭据文件路径。</summary>
    [ObservableProperty]
    private string _credentialFilePath = string.Empty;

    /// <summary>附件占用空间文案。</summary>
    [ObservableProperty]
    private string _attachmentsSizeText = string.Empty;

    /// <summary>清理入口的天数参数（默认 30）。</summary>
    [ObservableProperty]
    private int _cleanupOlderThanDays = 30;

    /// <summary>清理结果文案。</summary>
    [ObservableProperty]
    private string _storageStatusText = string.Empty;

    /* ---- 自启 / 关闭行为 / 快捷键 / 发送键（FR-W-SET-5..8） ---- */

    /// <summary>开机自启开关。</summary>
    [ObservableProperty]
    private bool _autostart;

    /// <summary>自启注册表当前指向（<c>null</c> 时隐藏）。</summary>
    [ObservableProperty]
    private string? _autostartTarget;

    /// <summary>关闭窗口行为（`tray` / `quit`）。</summary>
    [ObservableProperty]
    private string _closeBehavior = CloseBehaviorTray;

    /// <summary>是否最小化到托盘。</summary>
    public bool IsCloseToTray => string.Equals(CloseBehavior, CloseBehaviorTray, StringComparison.Ordinal);

    /// <summary>是否关闭即退出。</summary>
    public bool IsCloseToQuit => string.Equals(CloseBehavior, CloseBehaviorQuit, StringComparison.Ordinal);

    /// <summary>托盘是否可用（EDGE-W-9：不可用时临时强制「直接退出」）。</summary>
    [ObservableProperty]
    private bool _isTrayAvailable = true;

    /// <summary>托盘不可用提示（<c>settings.tray.unavailable</c>）。</summary>
    [ObservableProperty]
    private string _trayUnavailableText = string.Empty;

    /// <summary>全局快捷键是否启用。</summary>
    [ObservableProperty]
    private bool _globalShortcutEnabled = true;

    /// <summary>全局快捷键组合键（如 `Control+Alt+T`）。</summary>
    [ObservableProperty]
    private string _globalShortcut = ProtocolConstants.DefaultGlobalShortcut;

    /// <summary>快捷键状态文案（成功 / 被占用 / 格式非法 —— **失败不得静默**）。</summary>
    [ObservableProperty]
    private string _hotkeyStatusText = string.Empty;

    /// <summary>是否显示快捷键状态。</summary>
    [ObservableProperty]
    private bool _isHotkeyStatusVisible;

    /// <summary>发送键（`enter` / `ctrl+enter`）。</summary>
    [ObservableProperty]
    private string _sendKey = "enter";

    /// <summary>是否 Enter 发送。</summary>
    public bool IsSendKeyEnter => !string.Equals(SendKey, SendKeyCtrlEnter, StringComparison.Ordinal);

    /// <summary>是否 Ctrl+Enter 发送。</summary>
    public bool IsSendKeyCtrlEnter => string.Equals(SendKey, SendKeyCtrlEnter, StringComparison.Ordinal);

    /* ---- 通知（FR-W-NOTI-7） ---- */

    /// <summary>聊天通知开关。</summary>
    [ObservableProperty]
    private bool _notifyChat = true;

    /// <summary>问候通知开关。</summary>
    [ObservableProperty]
    private bool _notifyGreeting = true;

    /// <summary>提醒通知开关。</summary>
    [ObservableProperty]
    private bool _notifyReminder = true;

    /// <summary>错误通知开关。</summary>
    [ObservableProperty]
    private bool _notifyError = true;

    /// <summary>进度通知开关。</summary>
    [ObservableProperty]
    private bool _notifyProgress = true;

    /// <summary>通知能力状态（Toast / 降级托盘气泡 —— EDGE-W-10）。</summary>
    [ObservableProperty]
    private string _notificationCapability = string.Empty;

    /* ---- 勿扰时段（支持跨午夜） ---- */

    /// <summary>是否启用勿扰时段。</summary>
    [ObservableProperty]
    private bool _dndEnabled;

    /// <summary>勿扰开始时刻 `HH:mm`。</summary>
    [ObservableProperty]
    private string _dndStart = "23:00";

    /// <summary>勿扰结束时刻 `HH:mm`。</summary>
    [ObservableProperty]
    private string _dndEnd = "08:00";

    /* ---- 关于（FR-W-SET-9） ---- */

    /// <summary>退出登录时同时清除本地数据。</summary>
    [ObservableProperty]
    private bool _logoutClearLocalData;

    /// <summary>检查更新结果文案（缺文件时为空 —— **静默视为无更新**）。</summary>
    [ObservableProperty]
    private string _updateStatusText = string.Empty;

    /// <summary>版本号（**唯一来源 = <c>App.AppVersion</c>**，不得硬编码）。</summary>
    public string VersionText => App.AppVersion.Informational;

    /// <summary>构建日期（取程序集文件时间，非硬编码）。</summary>
    public string BuildDateText => ReadBuildDate();

    /// <summary>产物形态（安装版 / 便携版）。</summary>
    public string DistributionText => App.AppVersion.DistributionKind(_paths.IsPortable);

    /// <summary>后端地址（当前设置值）。</summary>
    public string BackendText => ApiBaseUrl;

    /// <summary>设备 ID（`device_{uuidv4}` —— FR-W-AUTH-2）。</summary>
    public string DeviceIdText => _auth.DeviceId;

    /* ---- 静态 UI 文案（全部走 I18n，XAML 只绑定 —— V-W-S8） ---- */

    /// <summary>页面标题。</summary>
    public string TitleText => I18n.T("settings.title");

    /// <summary>主题分组标题。</summary>
    public string ThemeLabel => I18n.T("settings.theme");

    /// <summary>「跟随系统」。</summary>
    public string ThemeSystemText => I18n.T("settings.theme.system");

    /// <summary>「日间」。</summary>
    public string ThemeLightText => I18n.T("settings.theme.light");

    /// <summary>「夜间」。</summary>
    public string ThemeDarkText => I18n.T("settings.theme.dark");

    /// <summary>城市标签。</summary>
    public string CityLabel => I18n.T("settings.city");

    /// <summary>城市提示。</summary>
    public string CityHint => I18n.T("settings.city.hint");

    /// <summary>服务地址分组标题。</summary>
    public string ServerLabel => I18n.T("settings.server");

    /// <summary>REST 地址标签。</summary>
    public string ApiBaseUrlLabel => I18n.T("settings.apiBaseUrl");

    /// <summary>WS 地址标签。</summary>
    public string WsBaseUrlLabel => I18n.T("settings.wsBaseUrl");

    /// <summary>「由 REST 地址推导」按钮文案。</summary>
    public string DeriveWsText => I18n.T("settings.deriveWs");

    /// <summary>「测试连通性」按钮文案。</summary>
    public string TestConnectionText => I18n.T("settings.testConnection");

    /// <summary>清空本地会话按钮文案。</summary>
    public string ClearLocalText => I18n.T("settings.clearLocal");

    /// <summary>自启开关文案。</summary>
    public string AutostartLabel => I18n.T("settings.autostart");

    /// <summary>自启当前指向文案（无指向时为空）。</summary>
    public string AutostartCurrentText => AutostartTarget is null
        ? string.Empty
        : I18n.T("settings.autostart.current", AutostartTarget);

    /// <summary>关闭行为标签。</summary>
    public string CloseBehaviorLabel => I18n.T("settings.closeBehavior");

    /// <summary>「最小化到托盘」。</summary>
    public string CloseBehaviorTrayText => I18n.T("settings.closeBehavior.tray");

    /// <summary>「直接退出」。</summary>
    public string CloseBehaviorQuitText => I18n.T("settings.closeBehavior.quit");

    /// <summary>全局快捷键标签。</summary>
    public string HotkeyLabel => I18n.T("settings.hotkey");

    /// <summary>全局快捷键开关文案。</summary>
    public string HotkeyEnabledLabel => I18n.T("settings.hotkey.enabled");

    /// <summary>发送键标签。</summary>
    public string SendKeyLabel => I18n.T("settings.sendKey");

    /// <summary>「Enter 发送」。</summary>
    public string SendKeyEnterText => I18n.T("settings.sendKey.enter");

    /// <summary>「Ctrl+Enter 发送」。</summary>
    public string SendKeyCtrlEnterText => I18n.T("settings.sendKey.ctrlenter");

    /// <summary>通知分组标题。</summary>
    public string NotificationsLabel => I18n.T("settings.notifications");

    /// <summary>聊天通知。</summary>
    public string NotifyChatText => I18n.T("settings.notifications.chat");

    /// <summary>问候通知。</summary>
    public string NotifyGreetingText => I18n.T("settings.notifications.greeting");

    /// <summary>提醒通知。</summary>
    public string NotifyReminderText => I18n.T("settings.notifications.reminder");

    /// <summary>错误通知。</summary>
    public string NotifyErrorText => I18n.T("settings.notifications.error");

    /// <summary>进度通知。</summary>
    public string NotifyProgressText => I18n.T("settings.notifications.progress");

    /// <summary>勿扰分组标题。</summary>
    public string DndLabel => I18n.T("settings.dnd");

    /// <summary>勿扰开关文案。</summary>
    public string DndEnabledText => I18n.T("settings.dnd.enabled");

    /// <summary>勿扰开始标签。</summary>
    public string DndStartText => I18n.T("settings.dnd.start");

    /// <summary>勿扰结束标签。</summary>
    public string DndEndText => I18n.T("settings.dnd.end");

    /// <summary>勿扰说明（**跨午夜**语义）。</summary>
    public string DndHintText => I18n.T("settings.dnd.hint");

    /// <summary>数据与存储分组标题。</summary>
    public string DataLabel => I18n.T("settings.data");

    /// <summary>凭据存储状态标签。</summary>
    public string StorageCredentialsLabel => I18n.T("settings.storage.credentials");

    /// <summary>凭据已加密文案。</summary>
    public string StorageEncryptedText => I18n.T("settings.storage.encrypted");

    /// <summary>凭据未保存文案（**不得降级为明文**）。</summary>
    public string StorageNotSavedText => I18n.T("settings.storage.notSaved");

    /// <summary>凭据存储状态的实际文案（二选一）。</summary>
    public string StorageStatusTextValue =>
        IsCredentialEncrypted ? StorageEncryptedText : StorageNotSavedText;

    /// <summary>凭据文件路径标签。</summary>
    public string StoragePathLabel => I18n.T("settings.storage.path");

    /// <summary>附件占用文案（<c>settings.storage.attachments</c>）。</summary>
    public string AttachmentsLabel => I18n.T("settings.storage.attachments", AttachmentsSizeText);

    /// <summary>清理入口文案。</summary>
    public string CleanupText => I18n.T("settings.storage.cleanup");

    /// <summary>导出分组标题。</summary>
    public string ExportLabel => I18n.T("settings.export");

    /// <summary>导出 JSON。</summary>
    public string ExportJsonText => I18n.T("settings.export.json");

    /// <summary>导出纯文本。</summary>
    public string ExportTextText => I18n.T("settings.export.text");

    /// <summary>打开日志目录。</summary>
    public string OpenLogsText => I18n.T("settings.logs");

    /// <summary>打开数据目录。</summary>
    public string OpenDataDirText => I18n.T("settings.dataDir");

    /// <summary>关于分组标题。</summary>
    public string AboutLabel => I18n.T("settings.about");

    /// <summary>版本标签。</summary>
    public string AboutVersionLabel => I18n.T("settings.about.version");

    /// <summary>构建日期标签。</summary>
    public string AboutBuildDateLabel => I18n.T("settings.about.buildDate");

    /// <summary>产物形态标签。</summary>
    public string AboutDistributionLabel => I18n.T("settings.about.distribution");

    /// <summary>设备 ID 标签。</summary>
    public string AboutDeviceIdLabel => I18n.T("settings.about.deviceId");

    /// <summary>后端地址标签。</summary>
    public string AboutServerLabel => I18n.T("settings.about.server");

    /// <summary>检查更新按钮文案。</summary>
    public string CheckUpdateText => I18n.T("settings.about.checkUpdate");

    /// <summary>退出登录按钮文案。</summary>
    public string LogoutText => I18n.T("settings.logout");

    /// <summary>退出登录时清除本地数据勾选项文案。</summary>
    public string LogoutClearDataText => I18n.T("settings.logout.clearData");

    /// <summary>保存按钮文案。</summary>
    public string SaveText => I18n.T("common.save");

    /// <summary>关闭按钮文案。</summary>
    public string CloseText => I18n.T("common.close");

    /// <summary>刷新按钮文案。</summary>
    public string RefreshText => I18n.T("common.refresh");

    /* ------------------------------------------------------------------ */
    /* 命令                                                                */
    /* ------------------------------------------------------------------ */

    /// <summary>把 <paramref name="themeValue"/>（`system` / `light` / `dark`）设为当前主题并立即应用。</summary>
    [RelayCommand]
    private async Task SetThemeAsync(string? themeValue)
    {
        Theme = NormalizeTheme(themeValue);
        await ApplyAndSaveAsync().ConfigureAwait(true);
        ThemeApplied?.Invoke(this, _settings.IsDarkThemeEffective);
    }

    /// <summary>设置关闭行为；RadioButton 只读派生属性使用 OneWay 显示，由命令回写源值。</summary>
    [RelayCommand]
    private void SetCloseBehavior(string? behavior)
    {
        var normalized = NormalizeCloseBehavior(behavior);
        CloseBehavior = !IsTrayAvailable && normalized == CloseBehaviorTray
            ? CloseBehaviorQuit
            : normalized;
    }

    /// <summary>设置发送键；RadioButton 只读派生属性使用 OneWay 显示，由命令回写源值。</summary>
    [RelayCommand]
    private void SetSendKey(string? sendKey)
        => SendKey = string.Equals(sendKey, SendKeyCtrlEnter, StringComparison.Ordinal)
            ? SendKeyCtrlEnter
            : "enter";

    /// <summary>由 REST 地址推导 WS 地址（FR-W-CFG-4）。</summary>
    [RelayCommand]
    private void DeriveWsBaseUrl() => WsBaseUrl = UrlPolicy.DeriveWsBaseUrl(ApiBaseUrl);

    /// <summary>测试连通性（三态文案：ok / healthUnavailable / unreachable —— FR-W-SET-4）。</summary>
    [RelayCommand]
    private async Task TestConnectionAsync(CancellationToken ct = default)
    {
        var state = await _connectivity.TestAsync(ApiBaseUrl, ct).ConfigureAwait(true);

        TestOutcome = state switch
        {
            ConnectivityState.Reachable => ConnectionTestOutcome.Ok,
            ConnectivityState.HealthEndpointUnavailable => ConnectionTestOutcome.HealthUnavailable,
            _ => ConnectionTestOutcome.Unreachable,
        };

        OnPropertyChanged(nameof(TestConnectionResultText));
    }

    /// <summary>
    /// 清空本地会话。⚠️ **必须**调 <see cref="IChatService.ClearLocalConversationAsync"/>
    /// （与聊天页共用同一实现 —— FR-W-CHAT-12），并二次确认。
    /// </summary>
    [RelayCommand]
    private async Task ClearLocalAsync()
    {
        if (!_prompt.Confirm(I18n.T("chat.clear.confirm")))
        {
            return;
        }

        await _chat.ClearLocalConversationAsync().ConfigureAwait(true);
        StorageStatusText = I18n.T("chat.clear.done");
    }

    /// <summary>清理超过 N 天的本地附件（二次确认 —— FR-W-SET-11）。</summary>
    [RelayCommand]
    private async Task CleanupAttachmentsAsync(CancellationToken ct = default)
    {
        if (!_prompt.Confirm(I18n.T("settings.storage.cleanup.confirm", CleanupOlderThanDays)))
        {
            return;
        }

        var removed = await _media.CleanupAttachmentsAsync(CleanupOlderThanDays, ct).ConfigureAwait(true);
        StorageStatusText = I18n.T("settings.storage.cleanup.done", removed);
        await RefreshStorageAsync(ct).ConfigureAwait(true);
    }

    /// <summary>
    /// 导出聊天记录（JSON）。由 View 层给出目标路径（<c>SaveFileDialog</c>）。
    /// </summary>
    public async Task ExportJsonAsync(string targetPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return;
        }

        var records = await LoadAllMessagesAsync(ct).ConfigureAwait(true);
        var json = System.Text.Json.JsonSerializer.Serialize(records);
        await File.WriteAllTextAsync(targetPath, json, ct).ConfigureAwait(true);
        StorageStatusText = I18n.T("settings.export.done", targetPath);
    }

    /// <summary>导出聊天记录（纯文本）。</summary>
    public async Task ExportTextAsync(string targetPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return;
        }

        var records = await LoadAllMessagesAsync(ct).ConfigureAwait(true);
        var lines = records.Select(static record =>
            string.Create(
                CultureInfo.InvariantCulture,
                $"[{record.Timestamp}] {record.Role}: {record.Content}"));

        await File.WriteAllLinesAsync(targetPath, lines, ct).ConfigureAwait(true);
        StorageStatusText = I18n.T("settings.export.done", targetPath);
    }

    /// <summary>
    /// 打开日志目录。⚠️ FR-W-SEC-5：只允许 <c>explorer.exe &lt;已校验存在的目录&gt;</c>，
    /// 目录不存在时静默返回（**不得**拼接用户输入）。
    /// </summary>
    [RelayCommand]
    private void OpenLogsDirectory() => _shell.OpenDirectory(_paths.LogsDir);

    /// <summary>打开数据目录（同样受 FR-W-SEC-5 约束）。</summary>
    [RelayCommand]
    private void OpenDataDirectory() => _shell.OpenDirectory(_paths.DataDir);

    /// <summary>检查更新：**纯本地 `version.json`**，不发起任何网络请求（FR-W-SET-9）。</summary>
    [RelayCommand]
    private async Task CheckUpdateAsync(CancellationToken ct = default)
    {
        var manifest = Path.Combine(_paths.AppDir, UpdateManifestFileName);

        // ⚠️ 缺文件 → **静默视为无更新**（不得报错、不得提示）。
        if (!File.Exists(manifest))
        {
            UpdateStatusText = string.Empty;
            return;
        }

        try
        {
            var json = await File.ReadAllTextAsync(manifest, ct).ConfigureAwait(true);
            using var document = System.Text.Json.JsonDocument.Parse(json);

            var version = document.RootElement.TryGetProperty("version", out var node)
                ? node.GetString()
                : null;

            UpdateStatusText = string.IsNullOrWhiteSpace(version)
                ? I18n.T("settings.about.noUpdateSource")
                : I18n.T("settings.about.localManifest", version);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
        {
            // 清单损坏按「无更新」处理（不打扰用户）。
            UpdateStatusText = string.Empty;
        }
    }

    /// <summary>退出登录（含「同时清除本地数据」勾选 —— FR-W-AUTH-7）。</summary>
    [RelayCommand]
    private async Task LogoutAsync(CancellationToken ct = default)
    {
        if (!_prompt.Confirm(I18n.T("settings.logout.confirm")))
        {
            return;
        }

        if (LogoutClearLocalData)
        {
            // 与聊天页共用同一清空实现（FR-W-CHAT-12）。
            await _chat.ClearLocalConversationAsync().ConfigureAwait(true);
        }

        await _auth.LogoutAsync(ct).ConfigureAwait(true);
        LogoutRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>保存全部设置（落盘 + 平台联动）。</summary>
    [RelayCommand]
    private async Task SaveAsync(CancellationToken ct = default)
    {
        var settings = _settings.Current;

        settings.Theme = NormalizeTheme(Theme);
        settings.City = TruncateCity(City);
        settings.ApiBaseUrl = UrlPolicy.NormalizeApiBaseUrl(ApiBaseUrl);
        settings.WsBaseUrl = WsBaseUrl.TrimEnd('/');

        // EDGE-W-9：托盘不可用时**临时强制**「直接退出」，并如实提示。
        settings.CloseBehavior = IsTrayAvailable ? CloseBehavior : CloseBehaviorQuit;

        settings.Autostart = Autostart;
        settings.AutostartHidden = true;
        settings.GlobalShortcutEnabled = GlobalShortcutEnabled;
        settings.GlobalShortcut = GlobalShortcut.Trim();
        settings.SendKey = string.Equals(SendKey, SendKeyCtrlEnter, StringComparison.Ordinal)
            ? SendKeyCtrlEnter
            : "enter";

        settings.Notifications.Chat = NotifyChat;
        settings.Notifications.Greeting = NotifyGreeting;
        settings.Notifications.Reminder = NotifyReminder;
        settings.Notifications.Error = NotifyError;
        settings.Notifications.Progress = NotifyProgress;

        settings.DoNotDisturb.Enabled = DndEnabled;
        settings.DoNotDisturb.Start = DndStart;
        settings.DoNotDisturb.End = DndEnd;

        await _settings.SaveAsync(settings, ct).ConfigureAwait(true);

        // EDGE-W-8：快捷键注册失败**必须**在界面可见（服务层已把结果交给平台层）。
        RefreshHotkeyStatusFromPlatform();

        TrayUnavailableText = IsTrayAvailable ? string.Empty : I18n.T("settings.tray.unavailable");
        OnPropertyChanged(nameof(VersionText));
        OnPropertyChanged(nameof(AutostartCurrentText));
        OnPropertyChanged(nameof(StorageStatusTextValue));
        OnPropertyChanged(nameof(BackendText));
    }

    /// <summary>刷新存储与平台能力状态（进入设置页时调用）。</summary>
    public async Task RefreshStorageAsync(CancellationToken ct = default)
    {
        try
        {
            var status = _auth.CredentialStatus;
            IsCredentialEncrypted = status.IsEncryptionAvailable;
            HasPersistedCredential = status.HasPersistedCredential;
            CredentialFilePath = status.CredentialFilePath;

            var bytes = await _media.GetAttachmentsSizeAsync(ct).ConfigureAwait(true);
            AttachmentsSizeText = FormatSize(bytes);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AttachmentsSizeText = I18n.T("common.unknown");
        }

        AutostartTarget = _autoStart.CurrentTargetPath;
        IsTrayAvailable = _tray.IsAvailable;
        TrayUnavailableText = IsTrayAvailable ? string.Empty : I18n.T("settings.tray.unavailable");

        RaiseDerived();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _windowChrome.SystemThemeChanged -= OnSystemThemeChanged;
        GC.SuppressFinalize(this);
    }

    /* ------------------------------------------------------------------ */
    /* 内部                                                                */
    /* ------------------------------------------------------------------ */

    /// <summary>连通性测试结果文案（三态不同 —— EDGE-W-28）。</summary>
    public string TestConnectionResultText => TestOutcome switch
    {
        ConnectionTestOutcome.Ok => I18n.T("settings.testConnection.ok"),
        ConnectionTestOutcome.HealthUnavailable => I18n.T("settings.testConnection.healthUnavailable"),
        ConnectionTestOutcome.Unreachable => I18n.T("settings.testConnection.unreachable"),
        _ => string.Empty,
    };

    private void LoadFromSettings()
    {
        _loading = true;

        var current = _settings.Current;
        Theme = NormalizeTheme(current.Theme);
        City = current.City;
        ApiBaseUrl = current.ApiBaseUrl;
        WsBaseUrl = current.WsBaseUrl;
        Autostart = current.Autostart;
        AutostartTarget = _autoStart.CurrentTargetPath;
        CloseBehavior = NormalizeCloseBehavior(current.CloseBehavior);
        GlobalShortcutEnabled = current.GlobalShortcutEnabled;
        GlobalShortcut = current.GlobalShortcut;
        SendKey = string.Equals(current.SendKey, SendKeyCtrlEnter, StringComparison.Ordinal)
            ? SendKeyCtrlEnter
            : "enter";

        NotifyChat = current.Notifications.Chat;
        NotifyGreeting = current.Notifications.Greeting;
        NotifyReminder = current.Notifications.Reminder;
        NotifyError = current.Notifications.Error;
        NotifyProgress = current.Notifications.Progress;

        DndEnabled = current.DoNotDisturb.Enabled;
        DndStart = current.DoNotDisturb.Start;
        DndEnd = current.DoNotDisturb.End;

        IsTrayAvailable = _tray.IsAvailable;
        TrayUnavailableText = IsTrayAvailable ? string.Empty : I18n.T("settings.tray.unavailable");

        _loading = false;
        RaiseDerived();
    }

    private async Task ApplyAndSaveAsync()
    {
        var settings = _settings.Current;
        settings.Theme = NormalizeTheme(Theme);
        await _settings.SaveAsync(settings).ConfigureAwait(true);
    }

    private void OnSystemThemeChanged(object? sender, EventArgs e)
        => _dispatcher.Invoke(() =>
        {
            // 仅当处于「跟随系统」时才需要重算（FR-W-SET-1 的监听要求）。
            if (!IsThemeSystem)
            {
                return;
            }

            ThemeApplied?.Invoke(this, _settings.IsDarkThemeEffective);
        });

    /// <summary>把平台层最近一次快捷键注册结果映射为界面文案（失败**不得静默**）。</summary>
    private void RefreshHotkeyStatusFromPlatform()
    {
        if (!GlobalShortcutEnabled)
        {
            HotkeyStatusText = string.Empty;
            IsHotkeyStatusVisible = false;
            return;
        }

        if (_hotkey.IsRegistered)
        {
            HotkeyStatusText = I18n.T("settings.hotkey.ok");
            IsHotkeyStatusVisible = true;
            return;
        }

        // 未注册成功：如实提示（被占用文案优先，其次格式非法）。
        HotkeyStatusText = I18n.T("settings.hotkey.inUse");
        IsHotkeyStatusVisible = true;
    }

    private async Task<List<ExportRecord>> LoadAllMessagesAsync(CancellationToken ct)
    {
        var messages = await _chat.SearchAsync(string.Empty, ProtocolConstants.ServerHistoryLimit * 4).ConfigureAwait(true);
        _ = ct;

        return messages
            .Select(static view => new ExportRecord(view.Timestamp, view.Role, view.Content))
            .ToList();
    }

    /// <summary>导出记录（JSON 友好）。</summary>
    public sealed record ExportRecord(long Timestamp, string Role, string Content);

    /// <summary>城市截断：**丢弃 `#` 及其后内容**（FR-W-SET-3）。</summary>
    internal static string TruncateCity(string? city)
    {
        var value = (city ?? string.Empty).Trim();
        var hash = value.IndexOf('#', StringComparison.Ordinal);
        return hash >= 0 ? value[..hash].Trim() : value;
    }

    private static string NormalizeTheme(string? value) => value switch
    {
        ThemeLight => ThemeLight,
        ThemeDark => ThemeDark,
        _ => ThemeSystem,
    };

    private static string NormalizeCloseBehavior(string? value)
        => string.Equals(value, CloseBehaviorQuit, StringComparison.Ordinal) ? CloseBehaviorQuit : CloseBehaviorTray;

    private static string ReadBuildDate()
    {
        try
        {
            var location = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(location) || !File.Exists(location))
            {
                return I18n.T("common.unknown");
            }

            return File.GetLastWriteTime(location).ToString("yyyy-MM-dd", CultureInfo.CurrentCulture);
        }
        catch (Exception)
        {
            return I18n.T("common.unknown");
        }
    }

    /// <summary>字节数 → 人类可读（MB 保留一位小数）。</summary>
    internal static string FormatSize(long bytes)
    {
        const double Megabyte = 1024d * 1024d;
        if (bytes < 0)
        {
            return I18n.T("common.unknown");
        }

        return bytes >= Megabyte
            ? (bytes / Megabyte).ToString("0.0", CultureInfo.CurrentCulture) + " MB"
            : (bytes / 1024d).ToString("0", CultureInfo.CurrentCulture) + " KB";
    }

    private void RaiseDerived()
    {
        OnPropertyChanged(nameof(IsThemeSystem));
        OnPropertyChanged(nameof(IsThemeLight));
        OnPropertyChanged(nameof(IsThemeDark));
        OnPropertyChanged(nameof(IsCloseToTray));
        OnPropertyChanged(nameof(IsCloseToQuit));
        OnPropertyChanged(nameof(IsSendKeyEnter));
        OnPropertyChanged(nameof(IsSendKeyCtrlEnter));
        OnPropertyChanged(nameof(TestConnectionResultText));
        OnPropertyChanged(nameof(AutostartCurrentText));
        OnPropertyChanged(nameof(StorageStatusTextValue));
        OnPropertyChanged(nameof(AttachmentsLabel));
        OnPropertyChanged(nameof(VersionText));
        OnPropertyChanged(nameof(BuildDateText));
        OnPropertyChanged(nameof(DistributionText));
        OnPropertyChanged(nameof(BackendText));
        OnPropertyChanged(nameof(DeviceIdText));
    }

    partial void OnThemeChanged(string value) => RaiseDerived();

    partial void OnCloseBehaviorChanged(string value) => RaiseDerived();

    partial void OnSendKeyChanged(string value) => RaiseDerived();

    partial void OnAutostartTargetChanged(string? value) => OnPropertyChanged(nameof(AutostartCurrentText));

    partial void OnAttachmentsSizeTextChanged(string value) => OnPropertyChanged(nameof(AttachmentsLabel));

    partial void OnTestOutcomeChanged(ConnectionTestOutcome value)
        => OnPropertyChanged(nameof(TestConnectionResultText));

    partial void OnCredentialFilePathChanged(string value) => RaiseDerived();

    partial void OnIsTrayAvailableChanged(bool value)
    {
        if (_loading)
        {
            return;
        }

        // EDGE-W-9：托盘不可用时把关闭行为**临时强制**为「直接退出」。
        if (!value)
        {
            CloseBehavior = CloseBehaviorQuit;
        }
    }

    partial void OnGlobalShortcutEnabledChanged(bool value) => RefreshHotkeyStatusFromPlatform();

    partial void OnApiBaseUrlChanged(string value) => OnPropertyChanged(nameof(BackendText));
}
