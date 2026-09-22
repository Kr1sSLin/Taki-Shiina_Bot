using System.Runtime.InteropServices;

namespace TKSDesktop.Platform.Windows;

/// <summary>
/// 全部 P/Invoke 集中处（PRD §3.4 / §3.5）。
///
/// ⚠️ 只有本文件允许出现 <see cref="DllImportAttribute"/>；其余 <c>Platform/Windows</c> 类一律调用本类，
///    以便静态审计「本端到底调了哪些系统 API」（NFR-W-8 / FR-SEC-1：无 Web 宿主、无遥测）。
/// ⚠️ 所有可能失败的 P/Invoke 均 <c>SetLastError = true</c>，调用方用
///    <see cref="Marshal.GetLastWin32Error"/> 区分「被占用」等可判定失败
///    （EDGE-W-8 要求**不得**静默回落默认值）。
/// </summary>
internal static class NativeMethods
{
    /* ============================== 窗口消息 ============================== */

    /// <summary>全局热键回调消息（<c>RegisterHotKey</c>）。</summary>
    internal const int WmHotkey = 0x0312;

    /// <summary>电源广播（FR-W-CONN-7 / EDGE-W-12）。</summary>
    internal const int WmPowerBroadcast = 0x0218;

    /// <summary>系统设置变化（主题 / DPI —— FR-W-SET-1、EDGE-W-26）。</summary>
    internal const int WmSettingChange = 0x001A;

    /// <summary>显示器配置变化（EDGE-W-26）。</summary>
    internal const int WmDisplayChange = 0x007E;

    /// <summary>窗口跨显示器或缩放级别变化（FR-W-UI-10 / EDGE-W-26）。</summary>
    internal const int WmDpiChanged = 0x02E0;

    /// <summary><c>WM_SETTINGCHANGE</c> 的 <c>lParam</c>：主题相关（Win10+）。</summary>
    internal const string SettingChangeImmersiveColorSet = "ImmersiveColorSet";

    /* ============================== 电源事件 ============================== */

    /// <summary><c>PBT_APMRESUMEAUTOMATIC</c>：自动从睡眠恢复。</summary>
    internal const int PbtApmResumeAutomatic = 0x0012;

    /// <summary><c>PBT_APMRESUMESUSPEND</c>：用户触发恢复。</summary>
    internal const int PbtApmResumeSuspend = 0x0007;

    /* ============================== 热键修饰键 ============================== */

    internal const uint ModAlt = 0x0001;
    internal const uint ModControl = 0x0002;
    internal const uint ModShift = 0x0004;
    internal const uint ModWin = 0x0008;

    /// <summary><c>MOD_NOREPEAT</c>：长按不连发（toggle 语义要求）。</summary>
    internal const uint ModNoRepeat = 0x4000;

    /// <summary><c>RegisterHotKey</c> 的 id 合法上界。</summary>
    internal const int MaxHotkeyId = 0xBFFF;

    /// <summary>
    /// Win32 错误码 1409：<c>ERROR_HOTKEY_ALREADY_REGISTERED</c>。
    /// EDGE-W-8 要求明确提示「该快捷键已被其他程序占用，请更换」。
    /// </summary>
    internal const int ErrorHotkeyAlreadyRegistered = 1409;

    /* ============================== 系统度量 ============================== */

    /// <summary><c>SM_REMOTESESSION</c>：非 0 表示 RDP 会话（EDGE-W-26：DWM 模糊不可用）。</summary>
    internal const int SmRemoteSession = 0x1000;

    /* ============================== 模糊合成 ============================== */

    /// <summary><c>WCA_ACCENT_POLICY</c>。</summary>
    internal const int WcaAccentPolicy = 19;

    /// <summary><c>ACCENT_ENABLE_BLURBEHIND</c>（Win10 模糊）。</summary>
    internal const int AccentEnableBlurBehind = 3;

    /// <summary><c>ACCENT_DISABLED</c>。</summary>
    internal const int AccentDisabled = 0;

    /* ============================== DWM 属性 ============================== */

    /// <summary><c>DWMWA_USE_IMMERSIVE_DARK_MODE</c>（Win10 1809+ 为 19，2004+ 为 20）。</summary>
    internal const int DwmwaUseImmersiveDarkMode = 20;

    /// <summary><c>DWMWA_USE_IMMERSIVE_DARK_MODE</c> 的旧编号（Win10 1809–1903）。</summary>
    internal const int DwmwaUseImmersiveDarkModeLegacy = 19;

    /// <summary><c>DWMWA_WINDOW_CORNER_PREFERENCE</c>（Win11）。</summary>
    internal const int DwmwaWindowCornerPreference = 33;

    /// <summary><c>DWMWA_SYSTEMBACKDROP_TYPE</c>（Win11 22H2+，可表示 Mica/Acrylic）。</summary>
    internal const int DwmwaSystemBackdropType = 38;

    /// <summary><c>DWMWCP_ROUND</c>：圆角窗口。</summary>
    internal const int DwmcpRound = 2;

    /// <summary><c>DWMSBT_TRANSIENTWINDOW</c>（Acrylic）—— 系统级模糊。</summary>
    internal const int DwmsbtTransientWindow = 3;

    /* ============================== 显示器 ============================== */

    /// <summary><c>MONITORINFOF_PRIMARY</c>。</summary>
    internal const uint MonitorinfofPrimary = 0x00000001;

    /// <summary><c>MONITOR_DEFAULTTONEAREST</c>：总返回非 null 显示器。</summary>
    internal const uint MonitorDefaultToNearest = 2;

    /* ============================== 通知状态 ============================== */

    /// <summary><c>QUNS_NOT_PRESENT</c>：用户不在场 → 抑制（FR-W-DSK-10）。</summary>
    internal const int QunsNotPresent = 1;

    /// <summary><c>QUNS_BUSY</c>：全屏应用 / 用户忙 → 抑制（FR-W-DSK-10）。</summary>
    internal const int QunsBusy = 2;

    /// <summary><c>QUNS_RUNNING_D3D_FULL_SCREEN</c> → 抑制（FR-W-DSK-10）。</summary>
    internal const int QunsRunningD3dFullScreen = 3;

    /// <summary><c>QUNS_PRESENTATION_MODE</c> → 抑制（FR-W-DSK-10）。</summary>
    internal const int QunsPresentationMode = 4;

    /// <summary><c>QUNS_ACCEPTS_NOTIFICATIONS</c>：正常，可弹通知。</summary>
    internal const int QunsAcceptNotifications = 5;

    /* ============================== 剪贴板格式 ============================== */

    /// <summary>
    /// <c>PNG</c> 格式名。Windows 10 截图工具与浏览器提供该格式且**保留 alpha**，
    /// 因此优先级高于 <c>CF_DIB</c>（FR-W-IMG-8「PNG 格式优先」）。
    /// </summary>
    internal const string ClipboardFormatPng = "PNG";

    /// <summary><c>CF_DIB</c> 的注册名（无 alpha，必须转 PNG 才能上传）。</summary>
    internal const string ClipboardFormatDib = "DeviceIndependentBitmap";

    /* ============================== 窗口显示 ============================== */

    /// <summary><c>SW_RESTORE</c>：还原最小化窗口（§8.7 第 3 步）。</summary>
    internal const int SwRestore = 9;

    /// <summary>托盘 tooltip 上限（<c>szTip</c> 为 128 个 <c>WCHAR</c>，含终止符）。</summary>
    internal const int MaxTooltipChars = 127;

    /* ============================== user32.dll ============================== */

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr lProcessId);

    /// <summary>
    /// 绕开前台锁定限制（§8.7 第 3 步）：把本线程输入队列附加到当前前台窗口所属线程后再
    /// <see cref="SetForegroundWindow"/>，随后**必须**成对调用并传 <c>false</c> 解除。
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AttachThreadInput(
        uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetDpiForSystem();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);

    internal delegate bool MonitorEnumProc(
        IntPtr hMonitor, IntPtr hdcMonitor, ref Rect lprcMonitor, IntPtr dwData);

    /// <summary>
    /// 枚举显示器（EDGE-W-25）。⚠️ **不引入 WinForms**：按 PRD 用该 API，
    /// 单屏兜底时可用 <c>SystemParameters.WorkArea</c>。
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumDisplayMonitors(
        IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    /// <summary>每台显示器的有效 DPI；失败时由调用方回退到窗口/系统 DPI。</summary>
    [DllImport("shcore.dll", SetLastError = true)]
    internal static extern int GetDpiForMonitor(
        IntPtr hMonitor,
        MonitorDpiType dpiType,
        out uint dpiX,
        out uint dpiY);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr MonitorFromRect(ref Rect lprc, uint dwFlags);

    /// <summary><c>SetWindowCompositionAttribute</c>（Win10 模糊；Win11 上常失败，须降级）。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int SetWindowCompositionAttribute(
        IntPtr hwnd, ref WindowCompositionAttributeData data);

    /// <summary>查询用户通知状态（专注助手 / 勿扰 —— FR-W-DSK-10 / FR-W-NOTI-*）。</summary>
    [DllImport("shell32.dll", SetLastError = true)]
    internal static extern int SHQueryUserNotificationState(out int pquns);

    /* ============================== dwmapi.dll ============================== */

    /// <summary>
    /// 设置 DWM 窗口属性。返回值非 0 表示当前系统不支持该属性
    /// （Win10 早于 1809 常见）——调用方**必须降级而非崩溃**（FR-W-UI-3）。
    /// </summary>
    [DllImport("dwmapi.dll", PreserveSig = true, SetLastError = false)]
    internal static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>查询 DWM 窗口属性（先查询再设置，避免在不支持的版本上产生副作用）。</summary>
    [DllImport("dwmapi.dll", PreserveSig = true, SetLastError = false)]
    internal static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    /// <summary>是否为投递到组合窗口的 DWM 合成（Win7 用，保留以判断桌面合成可用性）。</summary>
    [DllImport("dwmapi.dll", PreserveSig = true, SetLastError = false)]
    internal static extern int DwmIsCompositionEnabled(out int enabled);

    /* ============================== kernel32.dll ============================== */

    /// <summary>当前线程 id（<see cref="AttachThreadInput"/> 的 <c>idAttach</c>）。</summary>
    [DllImport("kernel32.dll")]
    internal static extern uint GetCurrentThreadId();

    /* ============================== shell32.dll / combase.dll ============================== */

    /// <summary>
    /// 声明进程级 AUMID（§14.3 第 1 步）。必须在创建任何窗口之前调用，
    /// 否则 Toast 与开始菜单快捷方式无法匹配（FR-W-NOTI-9）。
    /// </summary>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    /// <summary>
    /// WinRT 初始化。<c>RO_INIT_MULTITHREADED = 1</c>。
    /// <c>S_FALSE (0x1)</c> 与 <c>RPC_E_CHANGED_MODE (0x80010106)</c> 都表示「已初始化过」，须容忍。
    /// </summary>
    [DllImport("combase.dll", ExactSpelling = true)]
    internal static extern int RoInitialize(int initType);

    [DllImport("combase.dll", ExactSpelling = true)]
    internal static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

    [DllImport("combase.dll", ExactSpelling = true)]
    internal static extern int RoActivateInstance(IntPtr activatableClassId, out IntPtr instance);

    [DllImport("combase.dll", ExactSpelling = true)]
    internal static extern int WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string sourceString, int length, out IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    internal static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    internal static extern IntPtr WindowsGetStringRawBuffer(IntPtr hstring, out uint length);

    /* ============================== 结构体 ============================== */

    /// <summary>与 Win32 <c>RECT</c> 二进制兼容。**不得**替换为 <c>System.Windows.Rect</c>。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;

        internal readonly int Width => Right - Left;

        internal readonly int Height => Bottom - Top;
    }

    /// <summary>与 Win32 <c>MONITORINFO</c> 二进制兼容（调用前须设置 <c>Size</c>）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct MonitorInfo
    {
        internal int Size;
        internal Rect Monitor;
        internal Rect Work;
        internal uint Flags;
    }

    internal enum MonitorDpiType
    {
        Effective = 0,
        Angular = 1,
        Raw = 2,
    }

    /// <summary><c>WCA_ACCENT_POLICY</c> 的载荷。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct WindowCompositionAttributeData
    {
        internal int Attribute;
        internal IntPtr Data;
        internal int SizeOfData;
    }

    /// <summary>Win10 <c>ACCENT_POLICY</c>。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct AccentPolicy
    {
        internal int AccentState;
        internal int AccentFlags;
        internal int GradientColor;
        internal int AnimationId;
    }

    /* ============================== 辅助 ============================== */

    /// <summary>读取 HSTRING（**不**释放；释放由调用方用 <see cref="WindowsDeleteString"/> 负责）。</summary>
    internal static string ReadHString(IntPtr hstring)
    {
        if (hstring == IntPtr.Zero)
        {
            return string.Empty;
        }

        var buffer = WindowsGetStringRawBuffer(hstring, out var length);
        return buffer == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUni(buffer, (int)length) ?? string.Empty;
    }

    /// <summary>创建 HSTRING；失败返回 <see cref="IntPtr.Zero"/> 而非抛错。</summary>
    internal static IntPtr CreateHString(string value)
    {
        if (value is null)
        {
            return IntPtr.Zero;
        }

        return WindowsCreateString(value, value.Length, out var hstring) == 0 ? hstring : IntPtr.Zero;
    }

    /// <summary>把文本截断到托盘 tooltip 允许的长度（避免 <c>Shell_NotifyIcon</c> 静默失败）。</summary>
    internal static string ClampTooltip(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return text.Length <= MaxTooltipChars ? text : text[..MaxTooltipChars];
    }

    /// <summary>把异常安全转为可记录（且**不含** token）的短文本。</summary>
    internal static string Describe(Exception ex) => $"{ex.GetType().Name}: {ex.Message}";
}
