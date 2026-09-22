using Microsoft.Extensions.Logging;
using TKSDesktop.App;
using TKSDesktop.Core.Platform;

namespace TKSDesktop.Core.Services;

/// <summary>
/// 设置应用服务（PRD FR-W-SET-1/5/7 / FR-W-NOTI-7）。
///
/// 职责：把设置落盘（`settings.json`）并**联动平台能力**：
/// <list type="bullet">
///   <item>开机自启开关 ↔ `HKCU\...\Run`（FR-W-SET-5 / FR-W-DSK-3）；</item>
///   <item>全局快捷键 ↔ `RegisterHotKey`（FR-W-SET-7，**失败必须如实返回、不得静默回落**）；</item>
///   <item>主题（含「跟随系统」）↔ 系统偏好（FR-W-SET-1）。</item>
/// </list>
/// </summary>
public sealed class SettingsService : ISettingsService
{
    public event EventHandler? SettingsChanged;
    private readonly SettingsStore _store;
    private readonly IAutoStartManager _autoStart;
    private readonly IGlobalHotkey _hotkey;
    private readonly IWindowChrome _windowChrome;
    private readonly ILogger<SettingsService> _logger;

    public SettingsService(
        SettingsStore store,
        IAutoStartManager autoStart,
        IGlobalHotkey hotkey,
        IWindowChrome windowChrome,
        ILogger<SettingsService> logger)
    {
        _store = store;
        _autoStart = autoStart;
        _hotkey = hotkey;
        _windowChrome = windowChrome;
        _logger = logger;
    }

    /// <inheritdoc />
    public AppSettings Current => _store.Current;

    /// <summary>
    /// 主题是否生效为深色：`dark` → true、`light` → false、`system` → 读系统偏好（FR-W-SET-1）。
    /// </summary>
    public bool IsDarkThemeEffective => Current.Theme switch
    {
        "dark" => true,
        "light" => false,
        _ => _windowChrome.IsSystemDarkTheme,
    };

    /// <summary>最近一次快捷键注册结果（供设置页展示失败原因；<c>null</c> 表示未尝试）。</summary>
    public HotkeyRegisterResult? LastHotkeyResult { get; private set; }

    /// <inheritdoc />
    public Task SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // 1. 落盘（原子写；失败不阻断运行）
        _store.Save(settings);

        // 2. 开机自启联动（FR-W-SET-5）
        ApplyAutoStart(settings.Autostart);

        // 3. 全局快捷键联动（FR-W-SET-7：失败**必须**如实反映，不得静默回落默认值）
        ApplyHotkey(settings.GlobalShortcutEnabled, settings.GlobalShortcut);

        SettingsChanged?.Invoke(this, EventArgs.Empty);

        return Task.CompletedTask;
    }

    private void ApplyAutoStart(bool enabled)
    {
        try
        {
            if (enabled)
            {
                if (!_autoStart.IsEnabled && !_autoStart.Enable())
                {
                    _logger.LogWarning("写入开机自启注册表失败（HKCU Run）");
                }
            }
            else if (_autoStart.IsEnabled || _autoStart.CurrentTargetPath is not null)
            {
                // 关闭自启必须**删除**注册表值（§8.4），不得仅置空。
                if (!_autoStart.Disable())
                {
                    _logger.LogWarning("删除开机自启注册表值失败");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "开机自启联动失败（不阻断运行）");
        }
    }

    private void ApplyHotkey(bool enabled, string combination)
    {
        try
        {
            if (!enabled)
            {
                _hotkey.Unregister();
                LastHotkeyResult = null;
                return;
            }

            LastHotkeyResult = _hotkey.Register(combination);
            if (LastHotkeyResult != HotkeyRegisterResult.Success)
            {
                // EDGE-W-8：被占用/格式非法必须让 UI 可见，且**不回落默认值**。
                _logger.LogWarning("全局快捷键注册失败：{Result}（{Combination}）", LastHotkeyResult, combination);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "全局快捷键联动失败");
        }
    }
}
