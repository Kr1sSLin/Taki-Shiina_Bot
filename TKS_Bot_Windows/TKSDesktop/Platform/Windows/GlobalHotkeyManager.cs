using System.Runtime.InteropServices;
using System.Windows.Interop;
using TKSDesktop.Core.Platform;

namespace TKSDesktop.Platform.Windows;

/// <summary>
/// 全局快捷键（PRD FR-W-DSK-2 / FR-W-SET-7 / EDGE-W-8）。
///
/// 契约：
/// <list type="bullet">
///   <item>组合键形如 <c>Control+Alt+T</c>：修饰键支持 <c>Control</c>/<c>Ctrl</c>/<c>Alt</c>/<c>Shift</c>/<c>Win</c>，
///         主键支持单字母、数字、<c>F1</c>–<c>F24</c> 与常见编辑键；</item>
///   <item>**必须**用 <see cref="Marshal.GetLastWin32Error"/> 区分
///         「被占用」（<c>ERROR_HOTKEY_ALREADY_REGISTERED = 1409</c>）→ <see cref="HotkeyRegisterResult.AlreadyInUse"/>；
///         解析失败 → <see cref="HotkeyRegisterResult.InvalidCombination"/>；
///         **不得**静默回落默认值（EDGE-W-8）；</item>
///   <item><see cref="Pressed"/> 在 UI 线程触发（<c>HwndSource</c> 消息回调本就在 UI 线程，此处再显式确保）。</item>
/// </list>
///
/// ⚠️ 需要一个消息窗口接收 <c>WM_HOTKEY</c>：这里用 <c>HWND_MESSAGE</c> 父窗口的
///    <see cref="HwndSource"/>，它不显示任何 UI，也不需要 <c>Application</c> 已启动。
/// </summary>
public sealed class GlobalHotkeyManager : IGlobalHotkey, IDisposable
{
    /// <summary><c>HWND_MESSAGE</c>：消息专用窗口的伪父句柄。</summary>
    private static readonly IntPtr HwndMessage = new(-3);

    /// <summary><c>RegisterHotKey</c> 的 id（进程内唯一即可）。</summary>
    private const int HotkeyId = 0x5453; // "TS"

    /// <summary>Win32 <c>ERROR_HOTKEY_ALREADY_REGISTERED</c>（EDGE-W-8 的关键判定值）。</summary>
    public const int ErrorHotkeyAlreadyRegistered = 1409;

    /// <summary>支持的修饰键别名（大小写不敏感）。</summary>
    private static readonly Dictionary<string, uint> ModifierAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["control"] = NativeMethods.ModControl,
        ["ctrl"] = NativeMethods.ModControl,
        ["alt"] = NativeMethods.ModAlt,
        ["shift"] = NativeMethods.ModShift,
        ["win"] = NativeMethods.ModWin,
        ["windows"] = NativeMethods.ModWin,
        ["super"] = NativeMethods.ModWin,
    };

    /// <summary>命名键 → 虚拟键码（字母/数字之外的常见键）。</summary>
    private static readonly Dictionary<string, uint> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["space"] = 0x20,
        ["enter"] = 0x0D,
        ["return"] = 0x0D,
        ["esc"] = 0x1B,
        ["escape"] = 0x1B,
        ["tab"] = 0x09,
        ["backspace"] = 0x08,
        ["delete"] = 0x2E,
        ["insert"] = 0x2D,
        ["home"] = 0x24,
        ["end"] = 0x23,
        ["pageup"] = 0x21,
        ["pagedown"] = 0x22,
        ["left"] = 0x25,
        ["up"] = 0x26,
        ["right"] = 0x27,
        ["down"] = 0x28,
        ["plus"] = 0xBB,
        ["minus"] = 0xBD,
        ["comma"] = 0xBC,
        ["period"] = 0xBE,
        ["slash"] = 0xBF,
        ["semicolon"] = 0xBA,
        ["quote"] = 0xDE,
        ["backslash"] = 0xDC,
        ["bracketleft"] = 0xDB,
        ["bracketright"] = 0xDD,
        ["grave"] = 0xC0,
    };

    private readonly IUiDispatcher? _dispatcher;
    private readonly object _gate = new();

    private HwndSource? _source;
    private bool _registered;
    private bool _disposed;

    public GlobalHotkeyManager(IUiDispatcher? dispatcher = null)
    {
        _dispatcher = dispatcher;
    }

    /// <inheritdoc />
    public bool IsRegistered => _registered;

    /// <inheritdoc />
    public string? RegisteredCombination { get; private set; }

    /// <inheritdoc />
    public event EventHandler? Pressed;

    /// <summary>最近一次失败的 Win32 错误码（供设置页/自检展示，0 表示无）。</summary>
    public int LastWin32Error { get; private set; }

    /// <inheritdoc />
    /// <remarks>
    /// ⚠️ EDGE-W-8：失败时**不得**静默回落默认组合键。调用方须把
    /// <see cref="HotkeyRegisterResult.AlreadyInUse"/> 如实展示为「该快捷键已被其他程序占用，请更换」。
    /// </remarks>
    public HotkeyRegisterResult Register(string combination)
    {
        if (_disposed)
        {
            return HotkeyRegisterResult.Failed;
        }

        lock (_gate)
        {
            // 解析失败 → InvalidCombination（不猜、不回落）。
            if (!TryParse(combination, out var modifiers, out var virtualKey))
            {
                return HotkeyRegisterResult.InvalidCombination;
            }

            // 重新注册前先注销旧的，避免同一进程内自冲突得出假的 AlreadyInUse。
            UnregisterCore();

            if (!EnsureMessageWindow(out var failure))
            {
                LastWin32Error = failure;
                return HotkeyRegisterResult.Failed;
            }

            // MOD_NOREPEAT：长按不连发（FR-W-DSK-2 的 toggle 语义要求单次触发）。
            var mods = modifiers | NativeMethods.ModNoRepeat;
            if (!NativeMethods.RegisterHotKey(_source!.Handle, HotkeyId, mods, virtualKey))
            {
                var error = Marshal.GetLastWin32Error();
                LastWin32Error = error;

                return error == ErrorHotkeyAlreadyRegistered
                    ? HotkeyRegisterResult.AlreadyInUse
                    : HotkeyRegisterResult.Failed;
            }

            LastWin32Error = 0;
            _registered = true;
            RegisteredCombination = combination;
            return HotkeyRegisterResult.Success;
        }
    }

    /// <inheritdoc />
    public void Unregister()
    {
        lock (_gate)
        {
            UnregisterCore();
        }
    }

    /// <summary>释放消息窗口与热键（幂等）。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        lock (_gate)
        {
            UnregisterCore();
            try
            {
                _source?.Dispose();
            }
            catch (Exception)
            {
                // 消息窗口释放失败不阻断退出。
            }
            finally
            {
                _source = null;
            }
        }

        _disposed = true;
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 解析 `Modifier+Modifier+Key`：
    /// 至少一个修饰键 + 恰好一个主键；顺序任意，重复修饰键容忍。
    /// </summary>
    public static bool TryParse(string? combination, out uint modifiers, out uint virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;

        if (string.IsNullOrWhiteSpace(combination))
        {
            return false;
        }

        var parts = combination.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            // 至少需要「修饰键 + 主键」；裸键不做全局热键（会吞掉全局输入）。
            return false;
        }

        var keySeen = false;
        foreach (var part in parts)
        {
            if (ModifierAliases.TryGetValue(part, out var modifier))
            {
                modifiers |= modifier;
                continue;
            }

            if (keySeen)
            {
                // 出现第二个主键 => 非法组合。
                modifiers = 0;
                return false;
            }

            if (!TryParseKey(part, out virtualKey))
            {
                modifiers = 0;
                return false;
            }

            keySeen = true;
        }

        if (!keySeen || modifiers == 0)
        {
            modifiers = 0;
            virtualKey = 0;
            return false;
        }

        return true;
    }

    /// <summary>单个主键 → 虚拟键码。</summary>
    private static bool TryParseKey(string token, out uint virtualKey)
    {
        virtualKey = 0;

        if (token.Length == 1)
        {
            var ch = char.ToUpperInvariant(token[0]);

            // 字母 A–Z → 0x41–0x5A；数字 0–9 → 0x30–0x39（与 Win32 虚拟键码一致）。
            if (ch is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                virtualKey = ch;
                return true;
            }

            // 其它单字符（如逗号）走命名表回退，避免误判为控制字符。
        }

        // F1–F24 → 0x70–0x87
        if (token.Length is 2 or 3 && (token[0] is 'F' or 'f'))
        {
            if (int.TryParse(token.AsSpan(1), out var index) && index is >= 1 and <= 24)
            {
                virtualKey = (uint)(0x70 + index - 1);
                return true;
            }
        }

        if (NamedKeys.TryGetValue(token, out var named))
        {
            virtualKey = named;
            return true;
        }

        return false;
    }

    private bool EnsureMessageWindow(out int failure)
    {
        failure = 0;

        if (_source is not null && _source.Handle != IntPtr.Zero)
        {
            return true;
        }

        try
        {
            var parameters = new HwndSourceParameters("TKSDesktop.HotkeySink")
            {
                // HWND_MESSAGE：不参与窗口枚举、不显示、不需要 Application 已启动。
                ParentWindow = HwndMessage,
                WindowStyle = 0,
                Width = 0,
                Height = 0,
            };

            _source = new HwndSource(parameters);
            _source.AddHook(WndProc);
            return _source.Handle != IntPtr.Zero;
        }
        catch (Exception)
        {
            failure = Marshal.GetLastWin32Error();
            return false;
        }
    }

    private void UnregisterCore()
    {
        if (_registered && _source is { } source && source.Handle != IntPtr.Zero)
        {
            _ = NativeMethods.UnregisterHotKey(source.Handle, HotkeyId);
        }

        _registered = false;
        RegisteredCombination = null;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != NativeMethods.WmHotkey || wParam.ToInt64() != HotkeyId)
        {
            return IntPtr.Zero;
        }

        handled = true;

        // HwndSource 的消息回调已在创建它的 UI 线程上；这里再经 IUiDispatcher 走一次，
        // 以便宿主在自检/无 Application 场景下用假 dispatcher 替代（FR-W-ARCH-4）。
        if (_dispatcher is { } dispatcher)
        {
            dispatcher.Invoke(() => Pressed?.Invoke(this, EventArgs.Empty));
        }
        else
        {
            Pressed?.Invoke(this, EventArgs.Empty);
        }

        return IntPtr.Zero;
    }
}
