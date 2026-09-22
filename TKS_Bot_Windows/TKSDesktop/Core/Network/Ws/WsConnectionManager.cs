using Microsoft.Extensions.Logging;
using TKSDesktop.Contracts;
using TKSDesktop.Contracts.Dtos;
using TKSDesktop.Core.Network;

namespace TKSDesktop.Core.Network.Ws;

/// <summary>
/// 连接状态机（§10.1；与另两端逐值一致，**不得**漂移）。
/// </summary>
public enum ConnectionState
{
    /// <summary>无凭据 / Token 失效，未连接。</summary>
    Unauthenticated,

    /// <summary>正在建连（首次）。</summary>
    Connecting,

    /// <summary>已连接。</summary>
    Connected,

    /// <summary>断线后按退避等待重连。</summary>
    Reconnecting,

    /// <summary>正在刷新 Token（握手 401/403 或收到 <c>auth.expired</c> / 4001）。</summary>
    Refreshing,

    /// <summary>重连达上限，进入降级态（开放 REST 降级通道）。</summary>
    Degraded,

    /// <summary>已主动断开 / 全局离线。</summary>
    Disconnected,
}

/// <summary>状态快照。</summary>
public sealed record ConnectionStateSnapshot(
    ConnectionState State,
    int Attempt,
    bool Degraded,
    string? LastError)
{
    /// <summary>是否处于降级态（可用 REST 降级通道）。</summary>
    public bool IsDegraded => Degraded || State == ConnectionState.Degraded;

    /// <summary>是否已连接。</summary>
    public bool IsConnected => State == ConnectionState.Connected;
}

/// <summary>连接建立的事件参数。</summary>
public sealed record ConnectionOpenedEventArgs(bool FullSync, int ReconnectAttempt);

/// <summary>连接关闭的事件参数。</summary>
public sealed record ConnectionClosedEventArgs(int Code, string Reason, bool AuthFailure);

/// <summary>
/// WS 长连接与状态机（FR-W-CONN-1..8 / 陷阱 1、2、6）。
///
/// <list type="bullet">
///   <item>连接 `{wsBaseUrl}` + <see cref="ProtocolConstants.WsChatPath"/>（`/ws/chat`），
///         鉴权走 `Authorization` 请求头（**不得**用 query `?token=`）；</item>
///   <item>心跳：每 <see cref="ProtocolConstants.HeartbeatIntervalMs"/>（25s）发
///         `{"type":"ping","payload":{"timestamp":&lt;ms&gt;}}`；**发送失败立即触发重连**；</item>
///   <item>退避：`2^n` 秒、上限 <see cref="ProtocolConstants.ReconnectMaxMs"/>（60s）、
///         最多 <see cref="ProtocolConstants.ReconnectMaxAttempts"/>（15）次；
///         连接成功后计数**归零**；达上限 → <see cref="ConnectionState.Degraded"/>；</item>
///   <item>**4001 特例**（陷阱 6 / FR-W-CONN-4）：握手关闭码 4001 或先收到 `auth.expired` **不得**
///         按可重试网络错误处理 → 走 Token 刷新流程，刷新成功后才重连，且**重连前清空退避计数**；</item>
///   <item>连接断开时把在途请求标记为 <c>CONNECTION_LOST</c> 并对外发事件；
///         重连成功后触发**全量同步**（`since=0`）—— 通过事件 / 标志暴露，同步动作由他人实现。</item>
/// </list>
/// </summary>
public sealed class WsConnectionManager : IDisposable
{
    private readonly WsClient _client;
    private readonly IAccessTokenProvider _tokens;
    private readonly Func<string> _wsBaseUrlProvider;
    private readonly ILogger<WsConnectionManager> _logger;
    private readonly object _gate = new();

    private ConnectionStateSnapshot _state = new(ConnectionState.Unauthenticated, 0, false, null);
    private CancellationTokenSource? _lifetimeCts;
    private Task? _heartbeatLoop;
    private bool _stopped = true;
    private bool _needFullSyncOnNextOpen = true;
    private bool _disposed;

    /// <param name="client">底层长连接。</param>
    /// <param name="tokens">Token 提供者（续签实现方自带互斥）。</param>
    /// <param name="wsBaseUrlProvider">WS 基址（运行时读取）。</param>
    /// <param name="logger">日志。</param>
    public WsConnectionManager(
        WsClient client,
        IAccessTokenProvider tokens,
        Func<string> wsBaseUrlProvider,
        ILogger<WsConnectionManager> logger)
    {
        _client = client;
        _tokens = tokens;
        _wsBaseUrlProvider = wsBaseUrlProvider;
        _logger = logger;

        _client.Opened += OnClientOpened;
        _client.Closed += OnClientClosed;
        _client.FrameReceived += OnClientFrame;
        _client.SendFailed += OnClientSendFailed;
        _client.PendingRequestsLost += OnPendingRequestsLost;
    }

    /* ---------------------------------------------------------------------- */
    /* 对外事件                                                                */
    /* ---------------------------------------------------------------------- */

    /// <summary>连接状态变化。</summary>
    public event EventHandler<ConnectionStateSnapshot>? StateChanged;

    /// <summary>连接建立；<c>FullSync == true</c> 时订阅方必须执行 `since=0` 全量同步（FR-W-CONN-6）。</summary>
    public event EventHandler<ConnectionOpenedEventArgs>? Opened;

    /// <summary>连接关闭（含被顶号 / 4001 鉴权失败）。</summary>
    public event EventHandler<ConnectionClosedEventArgs>? Closed;

    /// <summary>已识别的服务端帧（按 <see cref="WsServerFrame"/> 子类转发）。</summary>
    public event EventHandler<WsInboundFrame>? FrameReceived;

    /// <summary>进入降级态（重连达上限；开放 REST 降级通道 FR-W-CHAT-9）。</summary>
    public event EventHandler? Degraded;

    /// <summary>断开时的在途请求 id 列表（标记为 <c>CONNECTION_LOST</c>，不是静默丢弃）。</summary>
    public event EventHandler<IReadOnlyList<string>>? PendingRequestsLost;

    /* ---------------------------------------------------------------------- */
    /* 状态                                                                    */
    /* ---------------------------------------------------------------------- */

    /// <summary>当前状态快照。</summary>
    public ConnectionStateSnapshot State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>当前状态（简写）。</summary>
    public ConnectionState CurrentState => State.State;

    /// <summary>是否已连接。</summary>
    public bool IsConnected => State.IsConnected && _client.IsOpen;

    /// <summary>是否处于降级态。</summary>
    public bool IsDegraded => State.IsDegraded;

    /// <summary>
    /// 下一次连接建立时是否需要 `since=0` 全量同步。
    /// 首次登录、手动重连、系统唤醒补拉、以及任何重连成功后都为 <c>true</c>（FR-W-CONN-6 / FR-W-SYNC-4）。
    /// </summary>
    public bool NeedsFullSync => _needFullSyncOnNextOpen;

    /* ---------------------------------------------------------------------- */
    /* 生命周期                                                                */
    /* ---------------------------------------------------------------------- */

    /// <summary>
    /// 登录成功后启动：建连并开始心跳。
    /// </summary>
    /// <param name="accessToken">Access Token；为 <c>null</c> 时从 <see cref="IAccessTokenProvider"/> 取，
    /// 仍取不到则置 <see cref="ConnectionState.Unauthenticated"/>。</param>
    /// <param name="cancellationToken">取消。</param>
    public async Task<bool> ConnectAsync(string? accessToken = null, CancellationToken cancellationToken = default)
    {
        _stopped = false;
        _needFullSyncOnNextOpen = true;

        lock (_gate)
        {
            _lifetimeCts ??= new CancellationTokenSource();
        }

        StartHeartbeatLoop();

        var token = accessToken;
        if (string.IsNullOrEmpty(token))
        {
            token = _tokens.AccessToken;
        }

        if (string.IsNullOrEmpty(token))
        {
            // FR-W-AUTH-11：建连前若 Token 缺失但可续签，先尝试续签。
            var refreshed = await RefreshTokenAsync(cancellationToken).ConfigureAwait(false);
            token = refreshed.Success ? refreshed.AccessToken : _tokens.AccessToken;
        }

        if (string.IsNullOrEmpty(token))
        {
            _logger.LogWarning("无 Access Token，无法建立 WS 连接");
            PatchState(ConnectionState.Unauthenticated, attempt: 0, degraded: false, lastError: "no-token");
            return false;
        }

        return await ConnectWithTokenAsync(token, isRetry: false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>主动断开（退出登录 FR-W-AUTH-7 / 关闭应用）。</summary>
    public async Task DisconnectAsync(string reason = "client disconnect", CancellationToken cancellationToken = default)
    {
        _stopped = true;

        CancellationTokenSource? lifetime;
        lock (_gate)
        {
            lifetime = _lifetimeCts;
            _lifetimeCts = null;
        }

        try
        {
            lifetime?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已释放。
        }

        lifetime?.Dispose();

        await _client.CloseAsync(reason, cancellationToken).ConfigureAwait(false);
        PatchState(ConnectionState.Disconnected, attempt: 0, degraded: false, lastError: null);
    }

    /// <summary>
    /// 手动 / 自动重连请求。
    /// <paramref name="manual"/> 为 <c>true</c> 时**重置退避计数**并置全量同步标记（FR-W-CONN-6）；
    /// 自动触发（系统唤醒 / 网络恢复 / EDGE-W-12）则立即重连、不等退避。
    /// </summary>
    public async Task<bool> RequestReconnectAsync(bool manual, CancellationToken cancellationToken = default)
    {
        if (_stopped)
        {
            _stopped = false;
        }

        lock (_gate)
        {
            _lifetimeCts ??= new CancellationTokenSource();
        }

        if (manual)
        {
            _logger.LogInformation("手动重连：重置退避计数并标记全量同步（FR-W-CONN-6）");
            _needFullSyncOnNextOpen = true;
            PatchState(ConnectionState.Reconnecting, attempt: 0, degraded: false, lastError: "manual-reconnect");
        }
        else
        {
            _logger.LogInformation("立即重连（跳过退避）：系统唤醒 / 网络恢复");
            _needFullSyncOnNextOpen = true;
        }

        StartHeartbeatLoop();

        var token = _tokens.AccessToken;
        if (string.IsNullOrEmpty(token))
        {
            var refreshed = await RefreshTokenAsync(cancellationToken).ConfigureAwait(false);
            token = refreshed.Success ? refreshed.AccessToken : null;
        }

        if (string.IsNullOrEmpty(token))
        {
            PatchState(ConnectionState.Unauthenticated, attempt: 0, degraded: false, lastError: "no-token");
            return false;
        }

        return await ConnectWithTokenAsync(token, isRetry: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>发送一条聊天消息（幂等键 <paramref name="requestId"/> 在**帧顶层** —— 陷阱 1）。</summary>
    public bool SendChatMessageAsync(string requestId, string? content, IReadOnlyList<ChatImagePayloadDto>? images = null)
        => _client.SendChatMessage(requestId, content, images);

    /// <summary>立即发送一次心跳（常规心跳由内部 25s 循环负责，此方法供唤醒后补发）。</summary>
    public bool SendPingAsync() => _client.SendPing();

    /// <summary>
    /// 标记「下一次连接建立时需要全量同步」（系统唤醒补拉 / 游标失效等场景）。
    /// </summary>
    public void MarkFullSyncRequired() => _needFullSyncOnNextOpen = true;

    /* ---------------------------------------------------------------------- */
    /* 内部：建连                                                              */
    /* ---------------------------------------------------------------------- */

    private async Task<bool> ConnectWithTokenAsync(string token, bool isRetry, CancellationToken cancellationToken)
    {
        if (isRetry)
        {
            PatchState(ConnectionState.Reconnecting, attempt: _state.Attempt, degraded: false, lastError: _state.LastError);
        }
        else
        {
            PatchState(
                _state.Attempt > 0 ? ConnectionState.Reconnecting : ConnectionState.Connecting,
                attempt: _state.Attempt,
                degraded: false,
                lastError: null);
        }

        var ok = await _client.ConnectAsync(_wsBaseUrlProvider(), token, cancellationToken).ConfigureAwait(false);
        if (!ok)
        {
            // 具体原因通过 Closed / StateChanged 派发；重连调度在 OnClientClosed 里完成。
            _logger.LogWarning("WS 建连未成功（等待重连调度）");
        }

        return ok;
    }

    private void OnClientOpened(object? sender, EventArgs e)
    {
        // FR-W-CONN-3：连接成功后计数归零。
        var fullSync = _needFullSyncOnNextOpen;
        var attempt = _state.Attempt;
        _needFullSyncOnNextOpen = false;

        PatchState(ConnectionState.Connected, attempt: 0, degraded: false, lastError: null);
        _logger.LogInformation("WS 连接成功（fullSync={FullSync}，此前尝试 {Attempt} 次）", fullSync, attempt);

        Opened?.Invoke(this, new ConnectionOpenedEventArgs(fullSync, attempt));
    }

    private void OnClientClosed(object? sender, WsCloseInfo info)
    {
        var authFailure = info.AuthFailure;

        PatchState(_state.State, attempt: _state.Attempt, degraded: _state.Degraded, lastError: info.Reason);
        Closed?.Invoke(this, new ConnectionClosedEventArgs(info.Code, info.Reason, authFailure));

        if (_stopped)
        {
            return;
        }

        if (authFailure)
        {
            // ⚠️ 陷阱 6 / FR-W-CONN-4：4001（或先收到 auth.expired）**不得**按可重试网络错误处理。
            _logger.LogInformation(
                "鉴权失败关闭（closeCode={Code}），走 Token 刷新流程（不按网络错误重试）",
                info.Code);

            PatchState(ConnectionState.Refreshing, attempt: 0, degraded: false, lastError: null);
            _ = RefreshAndReconnectAsync();
            return;
        }

        ScheduleReconnect("closed:" + info.Code.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private void OnClientFrame(object? sender, WsInboundFrame inbound)
    {
        if (inbound.Frame is WsAuthExpiredFrame)
        {
            // 后端先 accept 再下发 auth.expired，随后以 4001 关闭（陷阱 6）。
            _logger.LogInformation("收到 auth.expired，进入刷新流程");
            PatchState(ConnectionState.Refreshing, attempt: _state.Attempt, degraded: false, lastError: "auth-expired");
            _ = RefreshAndReconnectAsync();
        }

        FrameReceived?.Invoke(this, inbound);
    }

    private void OnClientSendFailed(object? sender, Exception ex)
    {
        if (_stopped)
        {
            return;
        }

        // FR-W-CONN-2：心跳 / 发送失败**立即**触发重连。
        _logger.LogWarning("心跳或发送失败（{Error}），立即触发重连", ex.Message);
        _ = _client.CloseAsync("send-failed").ConfigureAwait(false);
        ScheduleReconnect("send-failed");
    }

    private void OnPendingRequestsLost(object? sender, IReadOnlyList<string> lost)
        => PendingRequestsLost?.Invoke(this, lost);

    /* ---------------------------------------------------------------------- */
    /* 内部：刷新与重连                                                        */
    /* ---------------------------------------------------------------------- */

    private async Task RefreshAndReconnectAsync()
    {
        var outcome = await RefreshTokenAsync(CancellationToken.None).ConfigureAwait(false);

        if (_stopped)
        {
            return;
        }

        if (outcome.Success && !string.IsNullOrEmpty(outcome.AccessToken))
        {
            // 刷新成功 → 用新 Token 重连；FR-W-CONN-4 要求**重连前清空退避计数**。
            _logger.LogInformation("Token 刷新成功，重置退避计数后用新 Token 重连");
            PatchState(ConnectionState.Connecting, attempt: 0, degraded: false, lastError: null);
            await ConnectWithTokenAsync(outcome.AccessToken, isRetry: false, CancellationToken.None)
                .ConfigureAwait(false);
            return;
        }

        if (!_tokens.CanRefresh)
        {
            _logger.LogWarning("凭据已彻底失效（无 Refresh Token），进入未认证态");
            PatchState(ConnectionState.Unauthenticated, attempt: 0, degraded: false, lastError: "credentials-invalid");
            return;
        }

        // 刷新未成功但凭据仍在（**网络问题**）：保留凭据，按普通流程复位退避后重试（EDGE-W-2）。
        _logger.LogInformation("刷新未成功但凭据仍有效（可能网络抖动），复位退避后按普通流程重试");
        PatchState(ConnectionState.Reconnecting, attempt: 0, degraded: false, lastError: "refresh-network-failure");
        ScheduleReconnect("refresh-network-failure");
    }

    private Task<AccessTokenRefreshOutcome> RefreshTokenAsync(CancellationToken cancellationToken)
        => _tokens.RefreshAsync(cancellationToken);

    private void ScheduleReconnect(string reason)
    {
        if (_stopped)
        {
            return;
        }

        var attempt = _state.Attempt + 1;

        if (attempt > ProtocolConstants.ReconnectMaxAttempts)
        {
            // FR-W-CONN-5：达上限 → Degraded（开放 REST 降级通道）。
            _logger.LogError(
                "重连次数达上限（{Attempts}），进入降级态（开放 REST 降级通道）",
                ProtocolConstants.ReconnectMaxAttempts);

            _needFullSyncOnNextOpen = true;
            PatchState(
                ConnectionState.Degraded,
                attempt: ProtocolConstants.ReconnectMaxAttempts,
                degraded: true,
                lastError: reason);

            Degraded?.Invoke(this, EventArgs.Empty);
            return;
        }

        // FR-W-CONN-3：退避 2^n 秒，上限 60 秒。
        var delayMs = ComputeBackoffMs(attempt);
        _logger.LogInformation("计划重连 attempt={Attempt} delayMs={DelayMs} reason={Reason}", attempt, delayMs, reason);

        PatchState(ConnectionState.Reconnecting, attempt, degraded: false, lastError: reason);

        _ = Task.Run(
            async () =>
            {
                try
                {
                    await Task.Delay(delayMs, LifetimeToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                if (_stopped)
                {
                    return;
                }

                var token = _tokens.AccessToken;
                if (string.IsNullOrEmpty(token))
                {
                    var refreshed = await RefreshTokenAsync(CancellationToken.None).ConfigureAwait(false);
                    token = refreshed.Success ? refreshed.AccessToken : null;
                }

                if (string.IsNullOrEmpty(token))
                {
                    PatchState(ConnectionState.Unauthenticated, attempt: 0, degraded: false, lastError: "no-token");
                    return;
                }

                await ConnectWithTokenAsync(token, isRetry: true, CancellationToken.None).ConfigureAwait(false);
            },
            CancellationToken.None);
    }

    /// <summary>
    /// 退避时长：`min(ReconnectBaseMs * 2^(attempt-1), ReconnectMaxMs)`（2^n 秒、上限 60s）。
    /// </summary>
    public static int ComputeBackoffMs(int attempt)
    {
        if (attempt <= 0)
        {
            return 0;
        }

        // 2^(attempt-1)：attempt 可能超过 31，先做上限保护再移位，避免溢出。
        var capped = Math.Min(attempt - 1, 20);
        long delay = (long)ProtocolConstants.ReconnectBaseMs << capped;
        return (int)Math.Min(delay, ProtocolConstants.ReconnectMaxMs);
    }

    private CancellationToken LifetimeToken
    {
        get
        {
            lock (_gate)
            {
                return _lifetimeCts?.Token ?? CancellationToken.None;
            }
        }
    }

    /* ---------------------------------------------------------------------- */
    /* 内部：心跳                                                              */
    /* ---------------------------------------------------------------------- */

    private void StartHeartbeatLoop()
    {
        lock (_gate)
        {
            if (_heartbeatLoop is not null && !_heartbeatLoop.IsCompleted)
            {
                return;
            }

            _lifetimeCts ??= new CancellationTokenSource();
            _heartbeatLoop = Task.Run(() => HeartbeatLoopAsync(_lifetimeCts.Token), CancellationToken.None);
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        // FR-W-CONN-2：每 25 秒发送一次；发送失败由 SendFailed 事件立即触发重连。
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(ProtocolConstants.HeartbeatIntervalMs));

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!_client.IsOpen)
                {
                    continue;
                }

                if (!_client.SendPing())
                {
                    _logger.LogWarning("心跳发送失败，触发重连（FR-W-CONN-2）");
                    await _client.CloseAsync("heartbeat-failed", cancellationToken).ConfigureAwait(false);
                    ScheduleReconnect("heartbeat-failed");
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常退出。
        }
        catch (Exception ex)
        {
            _logger.LogDebug("心跳循环退出 error={Error}", ex.Message);
        }
    }

    /* ---------------------------------------------------------------------- */
    /* 内部：状态                                                              */
    /* ---------------------------------------------------------------------- */

    private void PatchState(ConnectionState state, int attempt, bool degraded, string? lastError)
    {
        var snapshot = new ConnectionStateSnapshot(state, attempt, degraded, lastError);

        lock (_gate)
        {
            if (_state == snapshot)
            {
                return;
            }

            _state = snapshot;
        }

        _logger.LogDebug("连接状态变更 state={State} attempt={Attempt} degraded={Degraded}", state, attempt, degraded);
        StateChanged?.Invoke(this, snapshot);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stopped = true;

        _client.Opened -= OnClientOpened;
        _client.Closed -= OnClientClosed;
        _client.FrameReceived -= OnClientFrame;
        _client.SendFailed -= OnClientSendFailed;
        _client.PendingRequestsLost -= OnPendingRequestsLost;

        CancellationTokenSource? lifetime;
        lock (_gate)
        {
            lifetime = _lifetimeCts;
            _lifetimeCts = null;
            _heartbeatLoop = null;
        }

        try
        {
            lifetime?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已释放。
        }

        lifetime?.Dispose();
        _client.Dispose();
    }
}
