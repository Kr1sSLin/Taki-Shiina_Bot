using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TKSDesktop.Platform.Windows;

/// <summary>
/// 跨实例激活请求（§8.7 第 2 步的 JSON 载荷）。
/// </summary>
/// <param name="Action">动作；本端目前只有 <c>activate</c>。</param>
/// <param name="Route">可选：目标页面/路由（供接管后续命令行参数）。</param>
/// <param name="MessageId">可选：需滚动定位的消息 id（FR-W-NOTI-4）。</param>
public sealed record ActivateRequest(
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("route")] string? Route,
    [property: JsonPropertyName("messageId")] string? MessageId)
{
    /// <summary>§8.7 第 2 步的固定动作名。</summary>
    public const string ActionActivate = "activate";

    /// <summary>构造 <c>{"action":"activate","route":null,"messageId":null}</c>（逐字对应 PRD 载荷）。</summary>
    public static ActivateRequest Default() => new(ActionActivate, null, null);
}

/// <summary>
/// 单实例守卫（PRD §8.7 / FR-W-DSK-4）。
///
/// 步骤（与 PRD 逐条对应）：
/// <list type="number">
///   <item>命名互斥体 <c>Local\TKSDesktop.SingleInstance.{用户名}</c>；
///         <c>Local\</c> 前缀保证不跨会话（多用户同时登录时互不干扰）；</item>
///   <item>命名管道 <c>TKSDesktop.IPC.{用户名}</c> 发送
///         <c>{"action":"activate","route":null,"messageId":null}</c>（JSON）；</item>
///   <item>已有实例收到后**唤起并聚焦**窗口：<c>Process.MainWindowHandle</c> + <c>SetForegroundWindow</c>；
///         失败时用 <c>AttachThreadInput</c> + <c>ShowWindow(SW_RESTORE)</c> + <c>SetForegroundWindow</c>
///         技巧绕开前台锁定限制；</item>
///   <item>新实例**立即退出**（返回码 0）；</item>
///   <item>⚠️ 异常路径：互斥体被持有但命名管道连接失败（前一次异常退出遗留）→
///         **等待 3 秒后放弃唤醒并直接退出**，绝不出现两个实例同时运行
///         （否则会争抢同一 SQLite 与同一 WS 设备位）。</item>
/// </list>
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    /// <summary>§8.7 第 5 步：管道连接失败后的等待上限。</summary>
    public static readonly TimeSpan WakeUpGiveUpTimeout = TimeSpan.FromSeconds(3);

    /// <summary>互斥体名称前缀（`Local\` 保证不跨会话）。</summary>
    public const string MutexNamePrefix = @"Local\TKSDesktop.SingleInstance.";

    /// <summary>命名管道名称前缀。</summary>
    public const string PipeNamePrefix = "TKSDesktop.IPC.";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly string _mutexName;
    private readonly string _pipeName;

    private Mutex? _mutex;
    private bool _ownsMutex;
    private bool _disposed;

    private CancellationTokenSource? _listenCts;
    private Task? _listenTask;

    public SingleInstanceGuard(string? userName = null)
    {
        var user = SanitizeUserName(userName ?? Environment.UserName);
        _mutexName = MutexNamePrefix + user;
        _pipeName = PipeNamePrefix + user;
    }

    /// <summary>互斥体名称（诊断/自检用）。</summary>
    public string MutexName => _mutexName;

    /// <summary>命名管道名称（诊断/自检用）。</summary>
    public string PipeName => _pipeName;

    /// <summary>本进程是否持有单实例锁（即本进程是首个实例）。</summary>
    public bool IsOwner => _ownsMutex;

    /// <summary>
    /// 已有实例收到激活请求时触发（§8.7 第 3 步：唤起并聚焦窗口）。
    /// 事件在**线程池线程**触发，订阅方须自行切到 UI 线程。
    /// </summary>
    public event EventHandler<ActivateRequest>? ActivateRequested;

    /// <summary>
    /// §8.7 第 1 步：尝试获取命名互斥体。
    /// </summary>
    /// <returns><c>true</c> 表示本进程是首个实例（应继续启动）；<c>false</c> 表示已有实例在运行。</returns>
    public bool TryAcquire()
    {
        if (_disposed)
        {
            return false;
        }

        if (_ownsMutex)
        {
            return true;
        }

        try
        {
            // initiallyOwned: true + createdNew 判定，避免竞态窗口。
            _mutex = new Mutex(initiallyOwned: true, _mutexName, out var createdNew);
            if (!createdNew)
            {
                // 互斥体已存在：可能是另一个存活实例，也可能是上/上次异常退出遗留。
                // 本端**不**依赖「AbandonedMutexException」来判断，因为 .NET 在 createdNew=false
                // 时不会抛出该异常；真正的判定放在唤醒阶段（见 NotifyExistingInstanceAsync）。
                _mutex.Dispose();
                _mutex = null;
                _ownsMutex = false;
                return false;
            }

            _ownsMutex = true;
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            // 同一用户下同名互斥体但权限不足（极少见）：保守视为「已存在」，不抢。
            _mutex?.Dispose();
            _mutex = null;
            _ownsMutex = false;
            return false;
        }
        catch (Exception)
        {
            _mutex?.Dispose();
            _mutex = null;
            _ownsMutex = false;
            return false;
        }
    }

    /// <summary>
    /// §8.7 第 2 步：向已有实例发送激活请求。
    ///
    /// ⚠️ 第 5 步的异常路径：连接失败（前一次异常退出遗留互斥体）时，
    /// 会等待至多 <see cref="WakeUpGiveUpTimeout"/> 后返回 <c>false</c>，
    /// 调用方**必须**随即退出（返回码 0），不得继续启动。
    /// </summary>
    /// <param name="request">要发送的请求；为 <c>null</c> 时发送 <see cref="ActivateRequest.Default"/>。</param>
    /// <returns>是否成功送达已有实例���</returns>
    public async Task<bool> NotifyExistingInstanceAsync(ActivateRequest? request = null)
    {
        var payload = JsonSerializer.Serialize(request ?? ActivateRequest.Default(), JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(payload);

        var deadline = DateTime.UtcNow + WakeUpGiveUpTimeout;

        // 重试直到 3 秒上限：已有实例的监听循环可能在「刚换完管道」的瞬间不可连，
        // 短暂重试能显著降低误判；超过上限必须放弃（否则两个实例会并存）。
        while (DateTime.UtcNow < deadline)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            try
            {
                using var client = new NamedPipeClientStream(
                    serverName: ".",
                    pipeName: _pipeName,
                    direction: PipeDirection.Out,
                    options: PipeOptions.Asynchronous);

                // 单次连接尝试不超过剩余时间。
                using var connectCts = new CancellationTokenSource(remaining);
                await client.ConnectAsync(connectCts.Token).ConfigureAwait(false);

                await client.WriteAsync(bytes, connectCts.Token).ConfigureAwait(false);
                await client.FlushAsync(connectCts.Token).ConfigureAwait(false);

                return true;
            }
            catch (OperationCanceledException)
            {
                // 超时：落到循环条件判定后放弃。
            }
            catch (TimeoutException)
            {
                // 同上。
            }
            catch (IOException)
            {
                // 管道忙 / 已有实例刚退出：短等待后重试（仍在 3 秒预算内）。
                await Task.Delay(TimeSpan.FromMilliseconds(150)).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(150)).ConfigureAwait(false);
            }
            catch (Exception)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(150)).ConfigureAwait(false);
            }
        }

        return false;
    }

    /// <summary>
    /// 首实例启动监听循环：接收后续实例的激活请求并触发 <see cref="ActivateRequested"/>。
    /// 幂等。
    /// </summary>
    public void StartListening()
    {
        if (_disposed || !_ownsMutex || _listenTask is not null)
        {
            return;
        }

        _listenCts = new CancellationTokenSource();
        var token = _listenCts.Token;
        _listenTask = Task.Run(() => ListenLoopAsync(token), CancellationToken.None);
    }

    /// <summary>
    /// §8.7 第 3 步：唤起并聚焦已有窗口。
    /// 先试 <c>Process.MainWindowHandle</c> + <c>SetForegroundWindow</c>；
    /// 失败时用 <c>AttachThreadInput</c> 技巧绕开前台锁定限制。
    /// </summary>
    /// <returns>是否成功把窗口带到前台。</returns>
    public static bool BringExistingWindowToFront(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            var handle = process.MainWindowHandle;

            // MainWindowHandle 可能在窗口尚未创建时返回 0：等一小会儿再取一次。
            if (handle == IntPtr.Zero)
            {
                process.WaitForInputIdle(milliseconds: 1500);
                process.Refresh();
                handle = process.MainWindowHandle;
            }

            if (handle == IntPtr.Zero)
            {
                return false;
            }

            return ForceForeground(handle);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 绕过前台锁定限制的聚焦序列（§8.7 第 3 步的「AttachThreadInput 技巧」）。
    /// </summary>
    public static bool ForceForeground(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            // ① 最小化时必须先还原，否则 SetForegroundWindow 无效。
            if (NativeMethods.IsIconic(windowHandle))
            {
                _ = NativeMethods.ShowWindow(windowHandle, NativeMethods.SwRestore);
            }

            // ② 直接尝试（当前进程是前台进程时即可成功）。
            if (NativeMethods.SetForegroundWindow(windowHandle))
            {
                return true;
            }

            // ③ 绕开前台锁定：把本线程输入队列附加到「当前前台窗口所属线程」。
            var foreground = NativeMethods.GetForegroundWindow();
            var foregroundThread = foreground == IntPtr.Zero
                ? 0
                : NativeMethods.GetWindowThreadProcessId(foreground, IntPtr.Zero);
            var currentThread = NativeMethods.GetCurrentThreadId();

            if (foregroundThread == 0 || foregroundThread == currentThread)
            {
                // 无前台窗口或已是本线程：再试一次还原 + 聚焦。
                _ = NativeMethods.ShowWindow(windowHandle, NativeMethods.SwRestore);
                return NativeMethods.SetForegroundWindow(windowHandle);
            }

            var attached = NativeMethods.AttachThreadInput(currentThread, foregroundThread, true);
            try
            {
                _ = NativeMethods.ShowWindow(windowHandle, NativeMethods.SwRestore);
                return NativeMethods.SetForegroundWindow(windowHandle);
            }
            finally
            {
                // ⚠️ 必须成对解除，否则本线程输入队列会被永久附加到别的线程。
                if (attached)
                {
                    _ = NativeMethods.AttachThreadInput(currentThread, foregroundThread, false);
                }
            }
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>释放互斥体并停止监听（§8.7：含 <c>ReleaseMutex</c>）。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _listenCts?.Cancel();
        }
        catch (Exception)
        {
            // 取消失败不阻断退出。
        }

        try
        {
            // 监听任务不 Join：进程即将退出，阻塞等待反而会拖慢退场。
            _ = _listenTask?.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);
        }
        catch (Exception)
        {
            // 忽略。
        }

        _listenCts?.Dispose();
        _listenCts = null;
        _listenTask = null;

        if (_ownsMutex)
        {
            try
            {
                _mutex?.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // 未持有该互斥体（异常路径）时 ReleaseMutex 会抛 ApplicationException —— 忽略。
            }
            catch (Exception)
            {
                // 忽略：句柄释放会由 Dispose 完成。
            }

            _ownsMutex = false;
        }

        try
        {
            _mutex?.Dispose();
        }
        catch (Exception)
        {
            // 忽略。
        }
        finally
        {
            _mutex = null;
        }

        _disposed = true;
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 管道名与互斥体名中的用户名净化：去掉反斜杠/冒号等非法字符，避免路径与句柄名冲突。
    /// </summary>
    private static string SanitizeUserName(string userName)
    {
        if (string.IsNullOrWhiteSpace(userName))
        {
            return "default";
        }

        var builder = new StringBuilder(userName.Length);
        foreach (var ch in userName)
        {
            builder.Append(char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.' ? ch : '_');
        }

        return builder.Length == 0 ? "default" : builder.ToString();
    }

    /// <summary>
    /// 监听循环：串行处理连接（NamedPipeServerStream 同一名称同时只能有一个实例）。
    /// 每轮重新创建一个管道实例，避免「连接一次后无法复用」。
    /// </summary>
    private async Task ListenLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(token).ConfigureAwait(false);

                var request = await ReadRequestAsync(server, token).ConfigureAwait(false);
                if (request is not null)
                {
                    RaiseActivateRequested(request);
                }
            }
            catch (OperationCanceledException)
            {
                // 正常退出。
            }
            catch (IOException)
            {
                // 客户端中途断开：继续监听下一个。
            }
            catch (Exception)
            {
                // 任何异常都不得终止监听循环，否则后续实例会「等待 3 秒后放弃」并误退出。
            }
            finally
            {
                try
                {
                    server?.Dispose();
                }
                catch (Exception)
                {
                    // 忽略。
                }
            }
        }
    }

    private static async Task<ActivateRequest?> ReadRequestAsync(Stream stream, CancellationToken token)
    {
        var buffer = new byte[4096];
        var memory = new MemoryStream();

        while (memory.Length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            memory.Write(buffer, 0, read);
        }

        var json = Encoding.UTF8.GetString(memory.ToArray());
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ActivateRequest>(json, JsonOptions);
        }
        catch (JsonException)
        {
            // 载荷非法：忽略而不断开监听（可能是旧版本客户端发来的格式）。
            return null;
        }
    }

    private void RaiseActivateRequested(ActivateRequest request)
    {
        try
        {
            ActivateRequested?.Invoke(this, request);
        }
        catch (Exception)
        {
            // 订阅者异常不得终止监听循环。
        }
    }
}
