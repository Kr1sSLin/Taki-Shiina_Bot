namespace TKSDesktop.Core.Platform;

/// <summary>
/// 开机自启（PRD §8.4 / FR-W-DSK-3）。
///
/// ⚠️ 只写 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`，值名 `TKS Desktop`，
///    命令行 `"&lt;exe&gt;" --hidden`。**禁止**写 `HKLM`。
/// ⚠️ 读取判定：与**当前可执行文件路径**比对（规范化 + 大小写不敏感）；不匹配时视为「未开启」，
///    但**不得自动覆盖**，须如实展示「当前指向：&lt;路径&gt;」。
/// ⚠️ 关闭自启：**删除**该值，不得仅置空。
/// </summary>
public interface IAutoStartManager
{
    /// <summary>当前自启是否已开启**且**指向本程序（用于开关初值）。</summary>
    bool IsEnabled { get; }

    /// <summary>注册表中当前指向的可执行文件路径（用于「当前指向」展示）；未设置时为 <c>null</c>。</summary>
    string? CurrentTargetPath { get; }

    /// <summary>开启自启（写入带 <c>--hidden</c> 的命令行）。返回是否成功。</summary>
    bool Enable();

    /// <summary>关闭自启（**删除**注册表值）。返回是否成功。</summary>
    bool Disable();
}

/// <summary>全局快捷键注册结果。</summary>
public enum HotkeyRegisterResult
{
    /// <summary>注册成功。</summary>
    Success,

    /// <summary>被其他程序占用（EDGE-W-8：必须给出明确提示，**不得静默回落默认值**）。</summary>
    AlreadyInUse,

    /// <summary>组合键解析失败（格式非法）。</summary>
    InvalidCombination,

    /// <summary>其他系统层失败。</summary>
    Failed,
}

/// <summary>
/// 全局快捷键（PRD §3.5 / FR-W-DSK-2 / EDGE-W-8）。
///
/// Windows 侧用 `RegisterHotKey`，**无 Wayland 类限制**；被占用时必须明确失败而非静默失效。
/// </summary>
public interface IGlobalHotkey
{
    /// <summary>当前是否已成功注册。</summary>
    bool IsRegistered { get; }

    /// <summary>当前生效的组合键（未注册时为 <c>null</c>）。</summary>
    string? RegisteredCombination { get; }

    /// <summary>
    /// 注册（或重新注册）快捷键。<paramref name="combination"/> 形如 `Control+Alt+T`。
    /// </summary>
    HotkeyRegisterResult Register(string combination);

    /// <summary>注销当前快捷键。</summary>
    void Unregister();

    /// <summary>按下快捷键时触发（toggle 语义由订阅方实现：窗口隐藏则唤起并聚焦输入框，已聚焦则收回托盘）。</summary>
    event EventHandler? Pressed;
}

/// <summary>
/// 剪贴板图片读取（FR-W-IMG-8 / FR-W-DSK-5）。
///
/// 优先 `CF_DIB` / `CF_BITMAP`，**PNG 格式优先**（`CF_DIB` 无 alpha 时转为 PNG）。
/// 剪贴板为空或无图片时必须给出明确提示，**不得静默无反应**。
/// </summary>
public interface IClipboardImage
{
    /// <summary>剪贴板中是否有图片。</summary>
    bool HasImage();

    /// <summary>
    /// 读取剪贴板图片并编码为 PNG 字节。
    /// </summary>
    /// <returns>成功返回 PNG 字节；无图片时返回 <c>null</c>。</returns>
    byte[]? TryReadPng();
}

/// <summary>
/// 窗口外观与生命周期的平台能力（FR-W-UI-3 / FR-W-UI-6 / EDGE-W-25 / EDGE-W-26）。
/// </summary>
public interface IWindowChrome
{
    /// <summary>系统窗口级模糊（DWM）是否可用。不可用时前端必须降级为不透明渐变（FR-W-UI-3）。</summary>
    bool IsSystemBlurAvailable { get; }

    /// <summary>
    /// 在窗口句柄创建后应用系统级 Acrylic / blur 背景。
    /// 返回是否实际应用成功；失败时前端必须使用不透明降级样式。
    /// </summary>
    bool TryApplySystemBlur(IntPtr hwnd, bool dark);

    /// <summary>系统是否要求「减少动画」（FR-W-UI-11）。</summary>
    bool IsAnimationReduced { get; }

    /// <summary>当前系统的日间/夜间偏好（FR-W-SET-1 的「跟随系统」）。</summary>
    bool IsSystemDarkTheme { get; }

    /// <summary>系统主题偏好变化时触发（FR-W-SET-1 要求**监听**该变化）。</summary>
    event EventHandler? SystemThemeChanged;

    /// <summary>
    /// 校验窗口矩形是否与任一台显示器工作区相交；
    /// 不相交时返回主屏居中的矩形（EDGE-W-25：避免显示器变化后窗口落到屏幕外）。
    /// </summary>
    (double X, double Y, double Width, double Height) EnsureOnScreen(
        double x, double y, double width, double height, IntPtr windowHandle = default);
}

/// <summary>
/// 电源与网络可用性事件（FR-W-CONN-7 / FR-W-CONN-10 / FR-W-REM-7 / EDGE-W-12）。
/// </summary>
public interface IPowerEvents
{
    /// <summary>系统从休眠/待机唤醒（`PBT_APMRESUMEAUTOMATIC` / `PBT_APMRESUMESUSPEND`）。</summary>
    event EventHandler? Resumed;

    /// <summary>系统网络可用性发生变化（对标 Linux 端 `net.isOnline` 语义）。</summary>
    event EventHandler<bool>? NetworkAvailabilityChanged;

    /// <summary>当前是否在线。</summary>
    bool IsOnline { get; }

    /// <summary>启动监听。</summary>
    void Start();
}
