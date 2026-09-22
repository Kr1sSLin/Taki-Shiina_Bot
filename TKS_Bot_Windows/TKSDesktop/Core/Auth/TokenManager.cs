using Microsoft.Extensions.Logging;
using TKSDesktop.Contracts;
using TKSDesktop.Contracts.Dtos;
using TKSDesktop.Core.Network;
using TKSDesktop.Core.Platform;

namespace TKSDesktop.Core.Auth;

/// <summary>启动时凭据恢复结果（决定是否直接进主界面、是否提示重新登录）。</summary>
public enum CredentialRestoreStatus
{
    /// <summary>没有凭据文件 —— 首次启动 / 已退出登录，显示登录页。</summary>
    NoCredentials,

    /// <summary>恢复成功（空闲未超 7 天）。</summary>
    Restored,

    /// <summary>文件存在但无法解密 / 内容损坏 —— 提示「登录状态已失效，请重新登录」，**文件保留**。</summary>
    Corrupted,

    /// <summary>空闲超过 7 天（FR-W-AUTH-13）—— 凭据已被清除，需重新登录。</summary>
    IdleExpired,
}

/// <summary>凭据恢复明细。</summary>
public sealed record CredentialRestoreResult(
    CredentialRestoreStatus Status,
    bool Authenticated,
    string? UserId,
    string? DeviceId);

/// <summary>续签时传给实现方的参数（由 <see cref="AuthService"/> 负责真正的 HTTP 调用）。</summary>
public sealed record TokenRefreshInvocation(string RefreshToken, string DeviceId);

/// <summary>
/// 续签实现委托：调用 <c>POST /auth/refresh</c>（**无信封**）。
/// 返回 <see cref="ApiResult{T}"/>；**不得**抛异常（抛出的异常会被收敛为网络失败并保留凭据）。
/// </summary>
public delegate Task<ApiResult<AuthTokensDto>> TokenRefreshDelegate(
    TokenRefreshInvocation invocation,
    CancellationToken cancellationToken);

/// <summary>凭据被判失效的事件参数。</summary>
public sealed record CredentialsInvalidatedEventArgs(string Reason, ApiError? Error);

/// <summary>
/// Token 生命周期与 401 互斥续签（FR-W-AUTH-4 / FR-W-AUTH-5 / FR-W-AUTH-6 / FR-W-AUTH-11 / FR-W-AUTH-13）。
///
/// 职责与硬约束：
/// <list type="number">
///   <item>**单一在途续签**：在途 <see cref="Task{TResult}"/> 复用 + <see cref="SemaphoreSlim"/> 互斥 ——
///         同一时刻只允许一个 refresh 在途，其余等待者**复用同一结果**（避免 Refresh Token 轮转互相作废）；</item>
///   <item>**轮转**：新 refreshToken 覆盖旧值并**立刻持久化**；</item>
///   <item>**仅 401/403 才清凭据**（<see cref="ErrorCatalog.ShouldClearCredentials"/>）；
///         网络故障 / 超时**保留凭据、不踢人**（EDGE-W-2）；</item>
///   <item>**主动续签**：剩余有效期 &lt; <see cref="ProtocolConstants.ProactiveRefreshThresholdMs"/>（60s）时，
///         在建连 / 首个请求前先续签（FR-W-AUTH-11）；</item>
///   <item>**空闲上限 7 天**：<see cref="CredentialPayload.LastUsedAt"/> 每次成功校验 / 续签时刷新，
///         超过 <see cref="ProtocolConstants.OfflineCredentialMaxIdleMs"/> 视为过期并清除凭据；</item>
///   <item>凭据**只**经 <see cref="CredentialStore"/>（DPAPI 密文）落盘；DPAPI 不可用时仅内存持有
///         并**明确上报**（EDGE-W-11 分支 ①）。</item>
/// </list>
/// </summary>
public sealed class TokenManager : IAccessTokenProvider, IDisposable
{
    /// <summary>`lastUsedAt` 落盘节流间隔：避免每个请求都触发一次 DPAPI 写盘。</summary>
    public const long LastUsedPersistIntervalMs = 5 * 60 * 1000;

    private readonly CredentialStore _store;
    private readonly DeviceIdProvider _deviceIds;
    private readonly ILogger<TokenManager> _logger;
    private readonly Func<long> _nowMs;

    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly object _stateGate = new();

    private CredentialPayload? _current;
    private Task<AccessTokenRefreshOutcome>? _inFlightRefresh;
    private bool _disposed;

    /// <param name="store">DPAPI 凭据存储。</param>
    /// <param name="deviceIds">deviceId 提供者。</param>
    /// <param name="logger">日志（**禁止**记录 Token / 密文）。</param>
    /// <param name="nowMsProvider">可选时钟注入（默认 UTC epoch 毫秒），便于测试 7 天空闲判定。</param>
    public TokenManager(
        CredentialStore store,
        DeviceIdProvider deviceIds,
        ILogger<TokenManager> logger,
        Func<long>? nowMsProvider = null)
    {
        _store = store;
        _deviceIds = deviceIds;
        _logger = logger;
        _nowMs = nowMsProvider ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    /// <summary>
    /// 续签的 HTTP 实现（由 <see cref="AuthService"/> 注入；未注入时续签直接失败，不会静默成功）。
    /// </summary>
    public TokenRefreshDelegate? RefreshInvoker { get; set; }

    /// <summary>
    /// 凭据被判定失效（HTTP 401/403 且续签未成功）时触发 —— 订阅方负责断开 WS 并跳登录页。
    /// ⚠️ 网络故障**不会**触发本事件（凭据必须保留）。
    /// </summary>
    public event EventHandler<CredentialsInvalidatedEventArgs>? CredentialsInvalidated;

    /// <summary>续签成功（含轮转后的新 refreshToken 已持久化）时触发。</summary>
    public event EventHandler? TokenRefreshed;

    /* ---------------------------------------------------------------------- */
    /* 状态                                                                    */
    /* ---------------------------------------------------------------------- */

    /// <summary>凭据存储状态（设置页���示：已加密保存 / 未保存 / 损坏 / 加密不可用）。</summary>
    public CredentialStoreStatus StorageStatus => _store.Status;

    /// <summary>DPAPI 是否可用（不可用时不会持久化）。</summary>
    public bool IsEncryptionAvailable => _store.IsEncryptionAvailable;

    /// <summary>当前是否已登录（有 Refresh Token 即可续签）。</summary>
    public bool IsAuthenticated
    {
        get
        {
            lock (_stateGate)
            {
                return _current?.HasTokens == true;
            }
        }
    }

    /// <inheritdoc />
    public bool CanRefresh => !string.IsNullOrEmpty(RefreshToken);

    /// <summary>当前 Access Token；未登录为 <c>null</c>。</summary>
    public string? AccessToken
    {
        get
        {
            lock (_stateGate)
            {
                return _current?.AccessToken;
            }
        }
    }

    /// <summary>当前 Refresh Token（仅续签内部使用，**不得**记日志）。</summary>
    public string? RefreshToken
    {
        get
        {
            lock (_stateGate)
            {
                return _current?.RefreshToken;
            }
        }
    }

    /// <summary>当前 userId。</summary>
    public string? UserId
    {
        get
        {
            lock (_stateGate)
            {
                return _current?.UserId;
            }
        }
    }

    /// <summary>当前 deviceId（凭据里没有时经 <see cref="DeviceIdProvider"/> 解析，绝不返回空）。</summary>
    public string DeviceId
    {
        get
        {
            string? persisted;
            lock (_stateGate)
            {
                persisted = _current?.DeviceId;
            }

            return _deviceIds.GetOrCreate(persisted);
        }
    }

    /// <summary>Access Token 绝对过期时刻（epoch 毫秒）；未登录为 <c>null</c>。</summary>
    public long? AccessTokenExpiresAt
    {
        get
        {
            lock (_stateGate)
            {
                return _current?.AccessTokenExpiresAt;
            }
        }
    }

    /* ---------------------------------------------------------------------- */
    /* 启动恢复                                                                */
    /* ---------------------------------------------------------------------- */

    /// <summary>
    /// 启动时载入凭据（FR-W-AUTH-3 / FR-W-AUTH-3b / FR-W-AUTH-13）。
    /// **不抛错**：损坏 → <see cref="CredentialRestoreStatus.Corrupted"/>（文件保留）；
    /// 空闲 &gt; 7 天 → <see cref="CredentialRestoreStatus.IdleExpired"/>（清除凭据）。
    /// </summary>
    public CredentialRestoreResult Restore()
    {
        var loaded = _store.Load();

        if (loaded.Status == SecretReadStatus.NotFound)
        {
            lock (_stateGate)
            {
                _current = null;
            }

            return new CredentialRestoreResult(CredentialRestoreStatus.NoCredentials, false, null, DeviceId);
        }

        if (loaded.Status == SecretReadStatus.Corrupted || loaded.Payload is null)
        {
            // FR-W-AUTH-3b：**不静默删除文件**，只提示重新登录。
            lock (_stateGate)
            {
                _current = null;
            }

            _logger.LogWarning(
                "凭据无法解密 / 已损坏，需重新登录（文件已保留）file={File}",
                _store.FilePath);
            return new CredentialRestoreResult(CredentialRestoreStatus.Corrupted, false, null, DeviceId);
        }

        var payload = loaded.Payload;
        var now = _nowMs();

        if (payload.IsIdleExpired(now))
        {
            // FR-W-AUTH-13：空闲超 7 天 → 视为过期，清除凭据并要求重新登录。
            _logger.LogInformation("凭据空闲超过 7 天，已清除，需要重新登录（FR-W-AUTH-13）");
            Clear();
            return new CredentialRestoreResult(CredentialRestoreStatus.IdleExpired, false, null, DeviceId);
        }

        var deviceId = _deviceIds.GetOrCreate(payload.DeviceId);

        // 设备身份落定：凭据里缺 deviceId 时回填，保证下次启动稳定（FR-W-AUTH-2）。
        var normalized = string.IsNullOrEmpty(payload.DeviceId) ? payload.WithDeviceId(deviceId) : payload;

        lock (_stateGate)
        {
            _current = normalized;
        }

        // 载入即算一次「成功校验」：刷新 lastUsedAt（落盘节流）。
        TouchLastUsed(force: false);

        _logger.LogInformation(
            "已恢复登录状态 userId={UserId} storage={Storage}",
            normalized.UserId, _store.Status);

        return new CredentialRestoreResult(
            CredentialRestoreStatus.Restored,
            true,
            normalized.UserId,
            deviceId);
    }

    /* ---------------------------------------------------------------------- */
    /* 登录 / 续签 / 退出                                                       */
    /* ---------------------------------------------------------------------- */

    /// <summary>
    /// 用一次登录 / 续签结果更新内存状态并**立刻持久化**（轮转的 refreshToken 必须马上落盘，
    /// 否则进程崩溃会丢失唯一有效的 Refresh Token）。
    /// </summary>
    /// <returns>落盘结果；失败时 <see cref="CredentialSaveOutcome.Success"/> 为 <c>false</c>，
    /// 但内存状态仍然生效（仅内存持有 Token，EDGE-W-11 分支 ①）。</returns>
    public CredentialSaveOutcome ApplyTokens(AuthTokensDto tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        var now = _nowMs();
        var deviceId = string.IsNullOrEmpty(tokens.DeviceId) ? DeviceId : tokens.DeviceId;

        var payload = CredentialPayload.FromTokens(
            tokens.AccessToken,
            tokens.RefreshToken,
            tokens.UserId,
            deviceId,
            tokens.ExpiresIn,
            now);

        lock (_stateGate)
        {
            _current = payload;
        }

        // deviceId 也要稳定：即使凭据无法持久化，也落到降级载体（FR-W-AUTH-2 / EDGE-W-27）。
        _deviceIds.TryPersist(deviceId);

        var outcome = _store.Save(payload);
        if (!outcome.Success)
        {
            _logger.LogWarning(
                "凭据未能持久化（仅内存持有，重启需重新登录）：{Reason}",
                outcome.FailureReason);
        }

        return outcome;
    }

    /// <summary>
    /// 「成功校验」记账（FR-W-AUTH-13）：刷新 <see cref="CredentialPayload.LastUsedAt"/>，
    /// 落盘按 <see cref="LastUsedPersistIntervalMs"/> 节流。
    /// </summary>
    public void MarkCredentialsUsed() => TouchLastUsed(force: false);

    /// <summary>
    /// 剩余有效期是否低于主动续签阈值（FR-W-AUTH-11）。
    /// 未登录时返回 <c>false</c>（无可续签对象）。
    /// </summary>
    public bool IsProactiveRefreshDue()
    {
        CredentialPayload? payload;
        lock (_stateGate)
        {
            payload = _current;
        }

        return payload is not null
               && payload.IsAccessTokenExpired(_nowMs(), ProtocolConstants.ProactiveRefreshThresholdMs);
    }

    /// <summary>
    /// 主动续签（FR-W-AUTH-11）：在建连 / 首个请求前调用；未到期时**不发起**请求，直接返回 <c>true</c>。
    /// </summary>
    public async Task<bool> EnsureFreshTokenAsync(CancellationToken cancellationToken = default)
    {
        if (!IsAuthenticated)
        {
            return false;
        }

        if (!IsProactiveRefreshDue())
        {
            return true;
        }

        _logger.LogInformation("Access Token 即将过期，主动续签（FR-W-AUTH-11）");
        var outcome = await RefreshAsync(cancellationToken).ConfigureAwait(false);
        return outcome.Success && !string.IsNullOrEmpty(outcome.AccessToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// FR-W-AUTH-5 的实现：先单飞（复用同一 <see cref="Task{TResult}"/>），再以信号量互斥保证
    /// 网络往返期间只有一次轮转；等待者拿到的是**同一结果**，而不是各自发起轮转。
    /// **永不抛错**：所有失败都收敛为 <see cref="AccessTokenRefreshOutcome"/>。
    /// </remarks>
    public Task<AccessTokenRefreshOutcome> RefreshAsync(CancellationToken cancellationToken = default)
    {
        lock (_stateGate)
        {
            if (_inFlightRefresh is not null && !_inFlightRefresh.IsCompleted)
            {
                _logger.LogDebug("续签已在途，等待者复用同一结果（FR-W-AUTH-5）");
                return _inFlightRefresh;
            }

            var inFlight = RefreshCoreAsync(cancellationToken);
            _inFlightRefresh = inFlight;
            return inFlight;
        }
    }

    /// <summary>
    /// 退出登录（FR-W-AUTH-7）：清除内存凭据 + 删除 `credentials.bin`。
    /// 本地聊天记录默认保留（与另两端语义一致）。
    /// </summary>
    public void Clear()
    {
        lock (_stateGate)
        {
            _current = null;
        }

        _store.Delete();
        _logger.LogInformation("已清除本地凭据");
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⚠️ **只有 401/403 才允许走到清除路径**；网络故障没有 HTTP 状态码，
    /// 因此 <see cref="ErrorCatalog.ShouldClearCredentials"/> 为 <c>false</c> 时直接返回、保留凭据（EDGE-W-2）。
    /// </remarks>
    public void OnUnauthorized(string reason, int? httpStatus)
    {
        if (!ErrorCatalog.ShouldClearCredentials(httpStatus))
        {
            _logger.LogDebug(
                "OnUnauthorized 被调用但状态码非 401/403（{Status}），保留凭据 reason={Reason}",
                httpStatus, reason);
            return;
        }

        _logger.LogWarning(
            "服务端拒绝鉴权（{Reason}，httpStatus={Status}）：判定凭据失效，触发重新登录流程",
            reason, httpStatus);

        Clear();
        CredentialsInvalidated?.Invoke(
            this,
            new CredentialsInvalidatedEventArgs(reason, Error: null));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _refreshGate.Dispose();
    }

    /* ---------------------------------------------------------------------- */
    /* 内部                                                                    */
    /* ---------------------------------------------------------------------- */

    private async Task<AccessTokenRefreshOutcome> RefreshCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return AccessTokenRefreshOutcome.Failed(
                new ApiError(ApiFailureKind.Cancelled, "refresh cancelled"),
                credentialsInvalidated: false);
        }

        try
        {
            return await DoRefreshAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 兜底：任何意外异常都收敛为网络类失败（**保留凭据**），绝不抛给调用方。
            _logger.LogWarning(ex, "续签过程中出现未预期异常，按网络故障处理并保留凭据");
            return AccessTokenRefreshOutcome.Failed(
                new ApiError(ApiFailureKind.Network, "refresh failed: " + ex.Message),
                credentialsInvalidated: false);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task<AccessTokenRefreshOutcome> DoRefreshAsync(CancellationToken cancellationToken)
    {
        CredentialPayload? payload;
        lock (_stateGate)
        {
            payload = _current;
        }

        if (payload is null || string.IsNullOrEmpty(payload.RefreshToken))
        {
            _logger.LogWarning("无 Refresh Token，无法续签");
            return AccessTokenRefreshOutcome.Failed(
                new ApiError(ApiFailureKind.Unauthorized, "no refresh token available", ErrorCatalog.TokenInvalid, 401),
                credentialsInvalidated: false);
        }

        var invoker = RefreshInvoker;
        if (invoker is null)
        {
            _logger.LogError("续签实现未注入（RefreshInvoker 为 null），无法续签");
            return AccessTokenRefreshOutcome.Failed(
                new ApiError(ApiFailureKind.Configuration, "refresh invoker is not configured"),
                credentialsInvalidated: false);
        }

        var deviceId = DeviceId;
        ApiResult<AuthTokensDto> result;
        try
        {
            result = await invoker(
                new TokenRefreshInvocation(payload.RefreshToken, deviceId),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return AccessTokenRefreshOutcome.Failed(
                new ApiError(ApiFailureKind.Cancelled, "refresh cancelled"),
                credentialsInvalidated: false);
        }
        catch (Exception ex)
        {
            // 实现方违约抛了异常：按网络故障处理，**保留**凭据。
            _logger.LogWarning(ex, "续签调用抛出异常，按网络故障处理并保留凭据");
            return AccessTokenRefreshOutcome.Failed(
                new ApiError(ApiFailureKind.Network, "refresh invocation failed: " + ex.Message),
                credentialsInvalidated: false);
        }

        if (!result.IsSuccess)
        {
            var error = result.Error!;

            // ⚠️ 网络故障 / 超时（没有 HTTP 状态码）：**保留**本地 Token 稍后重试，绝不当作凭据失效（EDGE-W-2）。
            if (error.HttpStatus is null)
            {
                _logger.LogWarning(
                    "续签遇到网络故障 / 超时，保留本地凭据待重试 kind={Kind} traceId={TraceId}",
                    error.Kind, error.TraceId);
                return AccessTokenRefreshOutcome.Failed(error, credentialsInvalidated: false);
            }

            // 401 / 403（或鉴权类业务码）→ 凭据确实失效，清凭据并通知跳登录页。
            if (error.ShouldClearCredentials || ErrorCatalog.IsAuthFailure(error.Code))
            {
                _logger.LogWarning(
                    "续签被拒（凭据失效）httpStatus={Status} code={Code} traceId={TraceId}",
                    error.HttpStatus, error.Code, error.TraceId);
                Clear();
                CredentialsInvalidated?.Invoke(
                    this,
                    new CredentialsInvalidatedEventArgs(AuthFailureReasons.RefreshFailed, error));
                return AccessTokenRefreshOutcome.Failed(error, credentialsInvalidated: true);
            }

            // 非鉴权类错误（如 5xx / 40002）：**不清凭据**，交由上层按可重试错误处理。
            _logger.LogError(
                "续签失败（非鉴权错误）httpStatus={Status} code={Code} traceId={TraceId}",
                error.HttpStatus, error.Code, error.TraceId);
            return AccessTokenRefreshOutcome.Failed(error, credentialsInvalidated: false);
        }

        if (!result.TryGetValue(out var tokens) || string.IsNullOrEmpty(tokens.AccessToken))
        {
            _logger.LogError("续签响应缺少 accessToken（形状异常），保留旧凭据");
            return AccessTokenRefreshOutcome.Failed(
                new ApiError(ApiFailureKind.InvalidResponse, "refresh response had no access token", TraceId: result.TraceId),
                credentialsInvalidated: false);
        }

        // 轮转：新 refreshToken 覆盖旧值并**立刻持久化**（ApplyTokens 内部同步落盘）。
        var save = ApplyTokens(tokens);
        _logger.LogInformation(
            "Token 续签成功（refreshToken 已轮转并持久化 saved={Saved}）", save.Success);

        TokenRefreshed?.Invoke(this, EventArgs.Empty);

        return AccessTokenRefreshOutcome.Ok(tokens.AccessToken);
    }

    /// <summary>
    /// 刷新 <see cref="CredentialPayload.LastUsedAt"/>；<paramref name="force"/> 为 <c>false</c> 时
    /// 落盘节流（内存值总是更新，磁盘最多每 5 分钟写一次）。
    /// </summary>
    private void TouchLastUsed(bool force)
    {
        CredentialPayload updated;

        lock (_stateGate)
        {
            if (_current is null)
            {
                return;
            }

            var now = _nowMs();
            var shouldPersist = force || (now - _current.LastUsedAt) >= LastUsedPersistIntervalMs;
            updated = _current.WithLastUsedAt(now);
            _current = updated;

            if (!shouldPersist)
            {
                // 内存值已更新（避免长时间运行的进程被判空闲超期），磁盘留到下一个窗口再写。
                return;
            }
        }

        var outcome = _store.Save(updated);
        if (!outcome.Success)
        {
            _logger.LogDebug("lastUsedAt 持久化失败（仅内存更新）：{Reason}", outcome.FailureReason);
        }
    }
}
