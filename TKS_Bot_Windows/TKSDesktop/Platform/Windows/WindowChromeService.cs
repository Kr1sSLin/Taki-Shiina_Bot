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
    public bool TryApplySystemBlur(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsSystemBlurAvailable)
        {
            return false;
        }

        try
        {
            // Win11 22H2+ 优先用系统背景类型（Acrylic），失败再退回 Win10 的 accent blur。
            var backdrop = NativeMethods.DwmsbtTransientWindow;
            if (NativeMethods.DwmSetWindowAttribute(
                    hwnd, NativeMethods.DwmwaSystemBackdropType, ref backdrop, sizeof(int)) == 0)
            {
                return true;
            }

            var policy = new NativeMethods.AccentPolicy
            {
                AccentState = NativeMethods.AccentEnableBlurBehind,
                AccentFlags = 0,
                GradientColor = 0,
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
        double x, double y, double width, double height)
    {
        // 尺寸本身非法时先给一个可用下限，避免返回 0 尺寸窗口。
        var safeWidth = width > 0 ? width : 1;
        var safeHeight = height > 0 ? height : 1;

        try
        {
            var workAreas = EnumerateWorkAreas();
            if (workAreas.Count > 0 && IntersectsAny(workAreas, x, y, safeWidth, safeHeight))
            {
                return (x, y, safeWidth, safeHeight);
            }

            return CenterOnPrimary(workAreas, safeWidth, safeHeight);
        }
        catch (Exception)
        {
            // 枚举失败（无桌面会话 / API 受限）→ 用 WPF 的单屏兜底（PRD 允许）。
            return CenterOnPrimary([], safeWidth, safeHeight);
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
    private static List<NativeMethods.Rect> EnumerateWorkAreas()
    {
        var results = new List<NativeMethods.Rect>();

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
                results.Add(info.Work);
            }

            return true;
        });

        _ = NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);

        // 显式保持委托存活直到调用结束（否则可能被 GC 提前回收）。
        GC.KeepAlive(callback);
        return results;
    }

    private static bool IntersectsAny(List<NativeMethods.Rect> workAreas, double x, double y, double width, double height)
    {
        var right = x + width;
        var bottom = y + height;

        foreach (var area in workAreas)
        {
            // 严格相交（仅接触边界不算，因为窗口可能完全贴在屏幕边缘外）。
            if (x < area.Right && right > area.Left && y < area.Bottom && bottom > area.Top)
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
        List<NativeMethods.Rect> workAreas, double width, double height)
    {
        var work = TryGetPrimaryWorkArea() ?? workAreas.FirstOrDefault();

        if (work is { Right: > 0, Bottom: > 0 })
        {
            var areaWidth = work.Right - work.Left;
            var areaHeight = work.Bottom - work.Top;

            var x = work.Left + Math.Max(0, (areaWidth - width) / 2);
            var y = work.Top + Math.Max(0, (areaHeight - height) / 2);
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
    private static NativeMethods.Rect? TryGetPrimaryWorkArea()
    {
        NativeMethods.Rect? primary = null;

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
                primary = info.Work;

                // 找到主屏即停止枚举。
                return false;
            }

            return true;
        });

        _ = NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        return primary;
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
