using System.Runtime.InteropServices;
using Microsoft.Win32;
using TKSDesktop.Core.Platform;

namespace TKSDesktop.Platform.Windows;

/// <summary>
/// 窗口外观与多屏校正（PRD FR-W-UI-3 / FR-W-UI-6 / FR-W-UI-11、FR-W-SET-1、EDGE-W-25 / EDGE-W-26）。
///
/// <list type="bullet">
///   <item><see cref="IsSystemBlurAvailable"/>：DWM 系统模糊。RDP 会话（<c>SM_REMOTESESSION != 0</c>）
///         或 DWM 合成不可用时返回 <c>false</c>；**降级为不透明渐变由 UI 层负责**，本类不代劳；</item>
///   <item><see cref="IsAnimationReduced"/>：读 <c>SystemParameters.ClientAreaAnimation</c>
///         并订阅 <c>SystemParameters.StaticPropertyChanged</c>；</item>
///   <item><see cref="IsSystemDarkTheme"/>：读
///         <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize\AppsUseLightTheme</c>
///         （0 = 深色），并订阅 <c>SystemEvents.UserPreferenceChanged</c> 触发 <see cref="SystemThemeChanged"/>；</item>
///   <item><see cref="EnsureOnScreen"/>：P/Invoke <c>EnumDisplayMonitors</c> / <c>GetMonitorInfo</c>
///         （**不引入 WinForms**）。与任一显示器工作区相交则原样返回，否则返回主屏居中矩形。</item>
/// </list>
/// </summary>
public sealed class WindowChromeService : IWindowChrome, IDisposable
{
    /// <summary>系统「显示动画」的注册表镜像（部分场景比 SystemParameters 更早更新）。</summary>
    private const string PersonalizeKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>「应用使用浅色主题」值名（0 = 深色）。</summary>
    public const string AppsUseLightThemeValueName = "AppsUseLightTheme";

    private readonly bool _systemEventsSubscribed;
    private bool _disposed;
    private bool? _cachedBlurAvailability;

    public WindowChromeService()
    {
        try
        {
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            _systemEventsSubscribed = true;
        }
        catch (Exception)
        {
            // 无桌面会话 / 消息泵不可用时订阅失败 —— 主题变化事件降级为「不推送」，
            // 上层仍可在每次进入设置页时主动读取 IsSystemDarkTheme（FR-W-SET-1 的兜底）。
            _systemEventsSubscribed = false;
        }
    }

    /// <inheritdoc />
    public event EventHandler? SystemThemeChanged;

    /// <inheritdoc />
    /// <remarks>
    /// 判定顺序（任一不满足即不可用 → UI 层降级为不透明渐变，FR-W-UI-3）：
    /// ① RDP 会话（<c>SM_REMOTESESSION</c>，EDGE-W-26）；
    /// ② DWM 未启用合成。
    /// 结果缓存：会话类型与合成状态在进程生命周期内基本不变。
    /// </remarks>
    public bool IsSystemBlurAvailable
    {
        get
        {
            if (_cachedBlurAvailability is { } cached)
            {
                return cached;
            }

            var available = false;
            try
            {
                if (NativeMethods.GetSystemMetrics(NativeMethods.SmRemoteSession) == 0
                    && NativeMethods.DwmIsCompositionEnabled(out var enabled) == 0
                    && enabled != 0)
                {
                    available = true;
                }
            }
            catch (DllNotFoundException)
            {
                // 极老的系统 / 精简镜像无 dwmapi：降级。
                available = false;
            }
            catch (Exception)
            {
                available = false;
            }

            _cachedBlurAvailability = available;
            return available;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// FR-W-UI-11：尊重系统「在 Windows 中显示动画」设置。
    /// 关闭时上层必须禁用液态玻璃动画与升级庆祝动画。
    /// </remarks>
    public bool IsAnimationReduced
    {
        get
        {
            try
            {
                // ClientAreaAnimation = false 表示「已要求减少动画」。
                return !System.Windows.SystemParameters.ClientAreaAnimation;
            }
            catch (Exception)
            {
                // 读取失败时「不减少动画」（宁可保留动画也不误判为无障碍偏好）。
                return false;
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// FR-W-SET-1：读取 <c>AppsUseLightTheme</c>（0 = 深色）。
    /// 值缺失时按「浅色」处理（Windows 默认）。
    /// </remarks>
    public bool IsSystemDarkTheme
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKeyPath, writable: false);
                if (key?.GetValue(AppsUseLightThemeValueName) is int lightTheme)
                {
                    return lightTheme == 0;
                }
            }
            catch (Exception)
            {
                // 策略受限时按浅色处理，不抛错（EDGE-W-27：配置读取失败必须回退默认值）。
            }

            return false;
        }
    }

    /// <summary>
    /// FR-W-UI-3 的窗口级模糊应用（由窗口在 <c>SourceInitialized</c> 后调用）。
    /// 返回是否**实际**应用成功；失败时调用方必须改用不透明渐变。
    /// </summary>
    public bool TryApplySystemBlur(IntPtr hwnd, bool dark)
    {
        if (hwnd == IntPtr.Zero || !IsSystemBlurAvailable)
        {
            return false;
        }

        try
        {
            // 标题栏/系统菜单的深浅色必须与应用主题同步。
            var darkMode = dark ? 1 : 0;
            _ = NativeMethods.DwmSetWindowAttribute(
                hwnd, NativeMethods.DwmwaUseImmersiveDarkMode, ref darkMode, sizeof(int));
            _ = NativeMethods.DwmSetWindowAttribute(
                hwnd, NativeMethods.DwmwaUseImmersiveDarkModeLegacy, ref darkMode, sizeof(int));

            var cornerPreference = NativeMethods.DwmcpRound;
            _ = NativeMethods.DwmSetWindowAttribute(
                hwnd, NativeMethods.DwmwaWindowCornerPreference, ref cornerPreference, sizeof(int));

            // WPF 的客户区默认会盖住系统材质；先把 DWM frame 延伸到整个客户区。
            var margins = new NativeMethods.Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            _ = NativeMethods.DwmExtendFrameIntoClientArea(hwnd, ref margins);

            // Win11 22H2+ 优先用系统背景类型（Acrylic），失败再退回 Win10 的 accent blur。
            var backdrop = NativeMethods.DwmsbtTransientWindow;
            if (NativeMethods.DwmSetWindowAttribute(
                    hwnd, NativeMethods.DwmwaSystemBackdropType, ref backdrop, sizeof(int)) == 0)
            {
                return true;
            }

            var policy = new NativeMethods.AccentPolicy
            {
                AccentState = NativeMethods.AccentEnableAcrylicBlurBehind,
                AccentFlags = 2,
                // ACCENT_POLICY 使用 AABBGGRR；保留足够透明度让背后内容参与折射。
                GradientColor = dark ? unchecked((int)0x99221B18) : unchecked((int)0x66FFFFFF),
                AnimationId = 0,
            };

            var size = Marshal.SizeOf<NativeMethods.AccentPolicy>();
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(policy, buffer, fDeleteOld: false);
                var data = new NativeMethods.WindowCompositionAttributeData
                {
                    Attribute = NativeMethods.WcaAccentPolicy,
                    Data = buffer,
                    SizeOfData = size,
                };

                if (NativeMethods.SetWindowCompositionAttribute(hwnd, ref data) != 0)
                {
                    return true;
                }

                // 较老 Win10 不支持 Acrylic 状态，最后降级到普通 blur-behind。
                policy.AccentState = NativeMethods.AccentEnableBlurBehind;
                policy.AccentFlags = 0;
                policy.GradientColor = 0;
                Marshal.StructureToPtr(policy, buffer, fDeleteOld: false);
                return NativeMethods.SetWindowCompositionAttribute(hwnd, ref data) != 0;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// EDGE-W25 / FR-W-UI-6：矩形与任一台显示器**工作区**相交则原样返回；
    /// 否则回退主屏居中。使用 P/Invoke 枚举显示器，**不引入 WinForms**。
    /// </remarks>
    public (double X, double Y, double Width, double Height) EnsureOnScreen(
        double x, double y, double width, double height, IntPtr windowHandle = default)
    {
        // 尺寸本身非法时先给一个可用下限，避免返回 0 尺寸窗口。
        var safeWidth = width > 0 ? width : 1;
        var safeHeight = height > 0 ? height : 1;

        try
        {
            var workAreas = EnumerateWorkAreas(windowHandle);
            if (workAreas.Count > 0 && IntersectsAny(workAreas, x, y, safeWidth, safeHeight))
            {
                return (x, y, safeWidth, safeHeight);
            }

            return CenterOnPrimary(workAreas, safeWidth, safeHeight, windowHandle);
        }
        catch (Exception)
        {
            // 枚举失败（无桌面会话 / API 受限）→ 用 WPF 的单屏兜底（PRD 允许）。
            return CenterOnPrimary([], safeWidth, safeHeight, windowHandle);
        }
    }

    /// <summary>释放 <c>SystemEvents</c> 订阅（静态事件必须显式退订，否则本对象无法回收）。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_systemEventsSubscribed)
        {
            try
            {
                SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            }
            catch (Exception)
            {
                // 退订失败不阻断退出。
            }
        }

        _disposed = true;
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 枚举所有显示器的工作区（排除任务栏区域 —— FR-W-UI-6 要求「不相交则回退主屏居中」，
    /// 用工作区而非全屏矩形才能避开任务栏遮挡）。
    /// </summary>
    private static List<LogicalWorkArea> EnumerateWorkAreas(IntPtr windowHandle)
    {
        var results = new List<LogicalWorkArea>();

        // 回调期间的异常不能跨 P/Invoke 边界抛出，因此内部全部吞掉并只收集成功项。
        var callback = new NativeMethods.MonitorEnumProc((IntPtr monitor, IntPtr hdc, ref NativeMethods.Rect rect, IntPtr data) =>
        {
            _ = hdc;
            _ = rect;
            _ = data;

            var info = new NativeMethods.MonitorInfo
            {
                Size = Marshal.SizeOf<NativeMethods.MonitorInfo>(),
            };

            if (NativeMethods.GetMonitorInfo(monitor, ref info))
            {
                results.Add(new LogicalWorkArea(info.Work, GetMonitorScale(monitor, windowHandle)));
            }

            return true;
        });

        _ = NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);

        // 显式保持委托存活直到调用结束（否则可能被 GC 提前回收）。
        GC.KeepAlive(callback);
        return results;
    }

    private static bool IntersectsAny(List<LogicalWorkArea> workAreas, double x, double y, double width, double height)
    {
        var right = x + width;
        var bottom = y + height;

        foreach (var area in workAreas)
        {
            var work = area.ToLogical();
            // 严格相交（仅接触边界不算，因为窗口可能完全贴在屏幕边缘外）。
            if (x < work.Right && right > work.Left && y < work.Bottom && bottom > work.Top)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 主屏居中。主屏取自 <c>MONITORINFOF_PRIMARY</c>；枚举不可用时退回
    /// <c>SystemParameters.WorkArea</c>（单屏兜底，PRD 明确允许）。
    /// </summary>
    private static (double X, double Y, double Width, double Height) CenterOnPrimary(
        List<LogicalWorkArea> workAreas, double width, double height, IntPtr windowHandle)
    {
        var work = TryGetPrimaryWorkArea(windowHandle) ?? workAreas.FirstOrDefault();

        if (work is { } logicalWork
            && logicalWork.Pixels.Right > logicalWork.Pixels.Left
            && logicalWork.Pixels.Bottom > logicalWork.Pixels.Top)
        {
            var workRect = logicalWork.ToLogical();
            var areaWidth = workRect.Right - workRect.Left;
            var areaHeight = workRect.Bottom - workRect.Top;

            var x = workRect.Left + Math.Max(0, (areaWidth - width) / 2);
            var y = workRect.Top + Math.Max(0, (areaHeight - height) / 2);
            return (x, y, width, height);
        }

        // WPF 兜底：单屏工作区。
        var fallback = System.Windows.SystemParameters.WorkArea;
        return (
            fallback.Left + Math.Max(0, (fallback.Width - width) / 2),
            fallback.Top + Math.Max(0, (fallback.Height - height) / 2),
            width,
            height);
    }

    /// <summary>主显示器工作区；找不到返回 <c>null</c>。</summary>
    private static LogicalWorkArea? TryGetPrimaryWorkArea(IntPtr windowHandle)
    {
        LogicalWorkArea? primary = null;

        var callback = new NativeMethods.MonitorEnumProc((IntPtr monitor, IntPtr hdc, ref NativeMethods.Rect rect, IntPtr data) =>
        {
            _ = hdc;
            _ = rect;
            _ = data;

            var info = new NativeMethods.MonitorInfo
            {
                Size = Marshal.SizeOf<NativeMethods.MonitorInfo>(),
            };

            if (NativeMethods.GetMonitorInfo(monitor, ref info)
                && (info.Flags & NativeMethods.MonitorinfofPrimary) != 0)
            {
                primary = new LogicalWorkArea(info.Work, GetMonitorScale(monitor, windowHandle));

                // 找到主屏即停止枚举。
                return false;
            }

            return true;
        });

        _ = NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        return primary;
    }

    private static double GetMonitorScale(IntPtr monitor, IntPtr windowHandle)
    {
        try
        {
            if (NativeMethods.GetDpiForMonitor(
                    monitor,
                    NativeMethods.MonitorDpiType.Effective,
                    out var dpiX,
                    out _)
                == 0 && dpiX > 0)
            {
                return dpiX / 96d;
            }
        }
        catch (DllNotFoundException)
        {
            // Windows 10+ has shcore.dll; retain the fallback for restricted images.
        }
        catch (EntryPointNotFoundException)
        {
            // Retain the fallback for older Windows images.
        }
        catch (Exception)
        {
            // DPI is advisory for geometry validation; fallback below is safe.
        }

        try
        {
            if (windowHandle != IntPtr.Zero)
            {
                var dpi = NativeMethods.GetDpiForWindow(windowHandle);
                if (dpi > 0)
                {
                    return dpi / 96d;
                }
            }

            var systemDpi = NativeMethods.GetDpiForSystem();
            return systemDpi > 0 ? systemDpi / 96d : 1d;
        }
        catch (Exception)
        {
            return 1d;
        }
    }

    private readonly record struct LogicalWorkArea(NativeMethods.Rect Pixels, double Scale)
    {
        internal NativeMethods.Rect ToLogical() => new()
        {
            Left = (int)Math.Round(Pixels.Left / Scale),
            Top = (int)Math.Round(Pixels.Top / Scale),
            Right = (int)Math.Round(Pixels.Right / Scale),
            Bottom = (int)Math.Round(Pixels.Bottom / Scale),
        };
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        _ = sender;

        // 主题/无障碍偏好都可能变：General（含颜色主题）与 Accessibility 都要上报，避免漏报。
        if (e.Category is not (UserPreferenceCategory.General
            or UserPreferenceCategory.Accessibility
            or UserPreferenceCategory.Color))
        {
            return;
        }

        // 主题可能刚刚变化：失效模糊可用性缓存并广播（FR-W-SET-1 要求**监听**该变化）。
        _cachedBlurAvailability = null;
        SystemThemeChanged?.Invoke(this, EventArgs.Empty);
    }
}
