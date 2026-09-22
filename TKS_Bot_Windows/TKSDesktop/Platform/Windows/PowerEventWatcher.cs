using System.Net.NetworkInformation;
using System.Windows.Interop;
using TKSDesktop.Core.Platform;

namespace TKSDesktop.Platform.Windows;

/// <summary>
/// 电源与网络可用性事件（PRD FR-W-CONN-7 / FR-W-CONN-10 / FR-W-REM-7、EDGE-W-12）。
///
/// <list type="bullet">
///   <item>用 <see cref="HwndSource"/> 钩子监听 <c>WM_POWERBROADCAST</c>：
///         <c>PBT_APMRESUMEAUTOMATIC (0x0012)</c> / <c>PBT_APMRESUMESUSPEND (0x0007)</c> → <see cref="Resumed"/>；</item>
///   <item>网络可用性用 <see cref="NetworkChange.NetworkAvailabilityChanged"/> →
///         <see cref="NetworkAvailabilityChanged(bool)"/>；<see cref="IsOnline"/> 用
///         <see cref="NetworkInterface.GetIsNetworkAvailable"/>
///         （对标 Linux 端 <c>net.isOnline</c> 语义）；</item>
///   <item>⚠️ 唤醒后上层须**立即重连而非等待退避**、校验并补发过期提醒、触发一次全量同步（EDGE-W-12）。</item>
/// </list>
/// </summary>
public sealed class PowerEventWatcher : IPowerEvents, IDisposable
{
    private readonly object _gate = new();

    private HwndSource? _source;
    private bool _networkSubscribed;
    private bool _started;
    private bool _disposed;

    /// <inheritdoc />
    public event EventHandler? Resumed;

    /// <inheritdoc />
    public event EventHandler<bool>? NetworkAvailabilityChanged;

    /// <inheritdoc />
    /// <remarks>
    /// 无网络接口时返回 <c>false</c>（与 Linux 端 <c>net.isOnline</c> 的「无连接」语义一致）。
    /// </remarks>
    public bool IsOnline
    {
        get
        {
            try
            {
                return NetworkInterface.GetIsNetworkAvailable();
            }
            catch (Exception)
            {
                // API 不可用时按「离线」处理，让上层走「已断开」展示而非误判在线。
                return false;
            }
        }
    }

    /// <summary>是否已启动监听（诊断用）。</summary>
    public bool IsStarted => _started;

    /// <inheritdoc />
    /// <remarks>幂等：重复调用只生效一次。</remarks>
    public void Start()
    {
        if (_disposed)
        {
            return;
        }

        lock (_gate)
        {
            if (_started)
            {
                return;
            }

            _started = true;
            EnsureMessageWindow();
            EnsureNetworkSubscription();
        }
    }

    /// <summary>停止监听并释放（幂等）。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        lock (_gate)
        {
            if (_networkSubscribed)
            {
                try
                {
                    NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
                }
                catch (Exception)
                {
                    // 退订失败不阻断退出。
                }

                _networkSubscribed = false;
            }

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

            _started = false;
        }

        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private void EnsureMessageWindow()
    {
        if (_source is not null && _source.Handle != IntPtr.Zero)
        {
            return;
        }

        try
        {
            var parameters = new HwndSourceParameters("TKSDesktop.PowerSink")
            {
                // Message-only 窗口不接收系统广播，使用不可见的顶层窗口。
                ParentWindow = IntPtr.Zero,
                WindowStyle = 0,
                Width = 0,
                Height = 0,
            };

            _source = new HwndSource(parameters);
            _source.AddHook(WndProc);
        }
        catch (Exception)
        {
            // 无桌面会话（自检模式）下建不出消息窗口：Resumed 事件降级为「永不触发」，
            // 上层仍可凭 IsOnline 轮询兜底（NFR-W-15 要求自检不依赖图形会话）。
            _source = null;
        }
    }

    private void EnsureNetworkSubscription()
    {
        if (_networkSubscribed)
        {
            return;
        }

        try
        {
            NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
            _networkSubscribed = true;
        }
        catch (Exception)
        {
            _networkSubscribed = false;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        _ = hwnd;
        _ = lParam;

        if (msg != NativeMethods.WmPowerBroadcast)
        {
            return IntPtr.Zero;
        }

        var powerEvent = wParam.ToInt32();
        if (powerEvent is NativeMethods.PbtApmResumeAutomatic or NativeMethods.PbtApmResumeSuspend)
        {
            handled = true;

            // EDGE-W-12：唤醒后上层须立即重连（不等退避）、补发过期提醒、触发全量同步。
            Resumed?.Invoke(this, EventArgs.Empty);
        }

        // 其余电源事件（挂起/电量/状态变化）本端不关心，交回默认处理。
        return IntPtr.Zero;
    }

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        _ = sender;

        try
        {
            NetworkAvailabilityChanged?.Invoke(this, e.IsAvailable);
        }
        catch (Exception)
        {
            // 订阅者异常不得回溯到系统事件源（否则会终止后续通知）。
        }
    }
}
