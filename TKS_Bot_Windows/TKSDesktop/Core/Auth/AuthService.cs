using Microsoft.Extensions.Logging;
using TKSDesktop.Contracts;
using TKSDesktop.Contracts.Dtos;
using TKSDesktop.Core.Network;

namespace TKSDesktop.Core.Auth;

/// <summary>登录失败原因（强类型，**不把错误编码进 message**）。</summary>
public enum LoginFailureReason
{
    /// <summary>用户名 / 密码错误（401 + 40101）。</summary>
    InvalidCredentials,

    /// <summary>设备不在白名单（403 + 40301）。</summary>
    DeviceNotAllowed,

    /// <summary>该设备已被服务端移除 / 被顶号（401 + 40102）。</summary>
    DeviceKicked,

    /// <summary>参数缺失（400 + 40001 / 40002）。</summary>
    InvalidRequest,

    /// <summary>网络故障 / 超时 —— **凭据不受影响**，可重试。</summary>
    Network,

    /// <summary>服务端故障（5xx / 50301）。</summary>
    ServerError,

    /// <summary>其他未分类失败。</summary>
    Unknown,
}

/// <summary>登录结果。</summary>
public sealed record LoginOutcome(
    bool Success,
    LoginFailureReason FailureReason,
    ApiError? Error,
    string? UserId,
    string? DeviceId,
    CredentialStoreStatus StorageStatus)
{
    /// <summary>凭据是否只在内存中（DPAPI 不可用；重启需重新登录）。</summary>
    public bool IsMemoryOnly => Success && StorageStatus != CredentialStoreStatus.EncryptedSaved;
}

/// <summary>续签状态（供设置页 / 排障展示）。</summary>
public sealed record AuthStatus(
    bool Authenticated,
    string? UserId,
    string DeviceId,
    CredentialStoreStatus StorageStatus,
    bool IsEncryptionAvailable,
    long? AccessTokenExpiresAt,
    bool RequiresReLogin);

/// <summary>凭据彻底失效时触发（订阅方负责断开 WS、跳登录页）。</summary>
public sealed record AuthExpiredEventArgs(string Reason, ApiError? Error);

/// <summary>
/// 认证服务（FR-W-AUTH-1..13 / EDGE-W-2 / EDGE-W-11 / §5.2）。
///
/// <list type="bullet">
///   <item>`login`：`POST /auth/login`（**无信封**），成功后**立刻**持久化 DPAPI 密文凭据；</item>
///   <item>`refresh`：`POST /auth/refresh`（**无信封**），由 <see cref="TokenManager"/> 保证
///         **单一在途 + 轮转后立刻持久化**（FR-W-AUTH-5）；</item>
///   <item>`logout`：清除内存凭据 + 删除 `credentials.bin`（FR-W-AUTH-7）；</item>
///   <item>`自动恢复`：启动时载入凭据，区分「无凭据 / 恢复成功 / 损坏 / 空闲超 7 天」；</item>
///   <item>**区分网络故障与凭据失效**：只拿到 401/403 才算失效；网络故障**保留**凭据、不踢人（EDGE-W-2）。</item>
/// </list>
/// </summary>
public sealed class AuthService : IAccessTokenProvider
{
    private readonly TokenManager _tokens;
    private readonly RestClient _rest;
    private readonly DeviceIdProvider _deviceIds;
    private readonly ILogger<AuthService> _logger;

    /// <summary>
    /// Refresh Token TTL（30 天，服务端 `AUTH_REFRESH_TTL_DAYS`）。
    /// 用于区分「被静默顶号」与「Refresh Token 自然超期」。
    /// </summary>
    public static readonly TimeSpan RefreshTokenTtl = TimeSpan.FromDays(30);

    private long _sessionStartedAtMs;
    private bool _expiredNotified;

    /// <param name="tokens">Token 生命周期（互斥续签实现）。</param>
    /// <param name="rest">REST 客户端。</param>
    /// <param name="deviceIds">deviceId 提供者。</param>
    /// <param name="logger">日志（**禁止**记录 token / password）。</param>
    public AuthService(
        TokenManager tokens,
        RestClient rest,
        DeviceIdProvider deviceIds,
        ILogger<AuthService> logger)
    {
        _tokens = tokens;
        _rest = rest;
        _deviceIds = deviceIds;
        _logger = logger;

        // 续签由本服务真正发 HTTP；TokenManager 负责互斥与轮转持久化。
        _tokens.RefreshInvoker = InvokeRefreshAsync;
        _tokens.CredentialsInvalidated += OnCredentialsInvalidated;
    }

    /// <summary>登录成功（Token 已持久化）时触发。</summary>
    public event EventHandler<AuthTokensDto>? Authenticated;

    /// <summary>凭据彻底失效，需要重新登录时触发（EDGE-W-2）。</summary>
    public event EventHandler<AuthExpiredEventArgs>? AuthExpired;

    /// <summary>退出登录时触发。</summary>
    public event EventHandler? LoggedOut;

    /// <summary>Token 续签成功（轮转后的 refreshToken 已落盘）时触发。</summary>
    public event EventHandler? TokenRefreshed
    {
        add => _tokens.TokenRefreshed += value;
        remove => _tokens.TokenRefreshed -= value;
    }

    /* ---------------------------------------------------------------------- */
    /* IAccessTokenProvider（供 RestClient / WsConnectionManager 使用）          */
    /* ---------------------------------------------------------------------- */

    /// <inheritdoc />
    public string? AccessToken => _tokens.AccessToken;

    /// <inheritdoc />
    public bool CanRefresh => _tokens.CanRefresh;

    /// <inheritdoc />
    public Task<AccessTokenRefreshOutcome> RefreshAsync(CancellationToken cancellationToken)
        => _tokens.RefreshAsync(cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// ⚠️ 委托给 <see cref="TokenManager.OnUnauthorized"/>：**只有 401/403 才会清凭据**；
    /// 传 <c>null</c>（网络故障）时保留凭据、不踢人（EDGE-W-2）。
    /// </remarks>
    public void OnUnauthorized(string reason, int? httpStatus)
        => _tokens.OnUnauthorized(reason, httpStatus);

    /* ---------------------------------------------------------------------- */
    /* 自动恢复（FR-W-AUTH-3 / 3b / 13）                                        */
    /* ---------------------------------------------------------------------- */

    /// <summary>
    /// 启动时恢复登录状态。**不抛错**。
    /// 恢复成功后自动刷新 `lastUsedAt`（FR-W-AUTH-13 的成功校验记账）。
    /// </summary>
    public CredentialRestoreResult RestoreCredentials()
    {
        var result = _tokens.Restore();

        switch (result.Status)
        {
            case CredentialRestoreStatus.Restored:
                _sessionStartedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                _expiredNotified = false;
                _logger.LogInformation("已自动恢复登录状态（storage={Storage}）", _tokens.StorageStatus);
                break;

            case CredentialRestoreStatus.Corrupted:
                _logger.LogWarning("本地凭据损坏 / 无法解密，需要重新登录（文件已保留）");
                break;

            case CredentialRestoreStatus.IdleExpired:
                _logger.LogInformation("本地凭据空闲超过 7 天，已清除，需要重新登录");
                break;

            case CredentialRestoreStatus.NoCredentials:
            default:
                _logger.LogInformation("无本地凭据，显示登录页");
                break;
        }

        return result;
    }

    /* ---------------------------------------------------------------------- */
    /* 登录 / 续签 / 退出                                                       */
    /* ---------------------------------------------------------------------- */

    /// <summary>
    /// 登录：`POST /auth/login`（**无信封**，§5.2）。
    /// **不抛错**：所有失败（含非法密码）都返回带 <see cref="LoginFailureReason"/> 的结果。
    /// </summary>
    /// <param name="username">用户名。</param>
    /// <param name="password">密码（**只用于本次请求，绝不落盘、绝不写日志**）。</param>
    /// <param name="cancellationToken">取消。</param>
    public async Task<LoginOutcome> LoginAsync(string username, string password, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            var error = new ApiError(
                ApiFailureKind.Business,
                "username/password required",
                ErrorCatalog.ParamMissing,
                null);
            return new LoginOutcome(
                false,
                LoginFailureReason.InvalidRequest,
                error,
                null,
                CurrentDeviceId(),
                _tokens.StorageStatus);
        }

        var deviceId = _deviceIds.GetOrCreate();

        var result = await _rest.PostEnvelopeLessAsync<AuthTokensDto>(
            "auth/login",
            new LoginRequestDto { Username = username, Password = password, DeviceId = deviceId },
            authenticated: false,
            allowRefresh: false,
            timeoutMs: ProtocolConstants.RestTimeoutMs,
            cancellationToken).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            var error = result.Error!;

            // ⚠️ 登录失败**不影响**既有凭据，也**不覆盖**本地已保存的凭据。
            if (error.HttpStatus is not null && ErrorCatalog.ShouldClearCredentials(error.HttpStatus) is false
                && error.HttpStatus == 401)
            {
                // 401 但登录路径：只说明本次输入错误，**不是**凭据失效。
                _logger.LogWarning("登录被拒（用户名或密码错误）traceId={TraceId}", error.TraceId);
                return new LoginOutcome(
                    false,
                    LoginFailureReason.InvalidCredentials,
                    error,
                    null,
                    deviceId,
                    _tokens.StorageStatus);
            }

            var reason = ClassifyLoginFailure(error);
            _logger.LogWarning(
                "登录失败 reason={Reason} httpStatus={Status} code={Code} traceId={TraceId}",
                reason, error.HttpStatus, error.Code, error.TraceId);

            return new LoginOutcome(false, reason, error, null, deviceId, _tokens.StorageStatus);
        }

        if (!result.TryGetValue(out var tokens)
            || string.IsNullOrEmpty(tokens.AccessToken)
            || string.IsNullOrEmpty(tokens.RefreshToken))
        {
            _logger.LogError("登录响应缺少 accessToken / refreshToken（形状异常）traceId={TraceId}", result.TraceId);
            return new LoginOutcome(
                false,
                LoginFailureReason.Unknown,
                new ApiError(ApiFailureKind.InvalidResponse, "login response missing tokens", TraceId: result.TraceId),
                null,
                deviceId,
                _tokens.StorageStatus);
        }

        // 凭据落盘（DPAPI 密文）；DPAPI 不可用时**仅内存持有**并明确上报（EDGE-W-11 分支 ①）。
        var save = _tokens.ApplyTokens(tokens);

        _sessionStartedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _expiredNotified = false;

        _logger.LogInformation(
            "登录成功 userId={UserId} deviceId={DeviceId} storage={Storage}",
            tokens.UserId, tokens.DeviceId, save.Status);

        Authenticated?.Invoke(this, tokens);

        return new LoginOutcome(true, LoginFailureReason.Unknown, null, tokens.UserId, tokens.DeviceId, save.Status);
    }

    /// <summary>
    /// 主动续签（FR-W-AUTH-11）：剩余有效期 &lt; 60s 时先续签；未到期直接返回 <c>true</c>。
    /// 建连 / 首个请求前调用。
    /// </summary>
    public Task<bool> EnsureFreshTokenAsync(CancellationToken cancellationToken = default)
        => _tokens.EnsureFreshTokenAsync(cancellationToken);

    /// <summary>主动续签一次的强类型结果（供需要展示原因的调用方使用）。</summary>
    public Task<AccessTokenRefreshOutcome> RefreshNowAsync(CancellationToken cancellationToken = default)
        => _tokens.RefreshAsync(cancellationToken);

    /// <summary>
    /// WS 收到 `auth.expired` 或 4001 关闭时调用（FR-W-AUTH-6 / 陷阱 6）。
    /// </summary>
    /// <returns>刷新成功返回 <c>true</c>；失败返回 <c>false</c>（订阅方据此决定是否跳登录页）。</returns>
    public async Task<bool> HandleWsAuthExpiredAsync(CancellationToken cancellationToken = default)
    {
        var outcome = await _tokens.RefreshAsync(cancellationToken).ConfigureAwait(false);

        if (outcome.Success && !string.IsNullOrEmpty(outcome.AccessToken))
        {
            return true;
        }

        // 网络故障导致刷新未成功时：**不清凭据、不跳登录页**，交由重连退避继续尝试（EDGE-W-2）。
        if (!_expiredNotified && _tokens.CanRefresh)
        {
            _logger.LogWarning("WS 鉴权失效但刷新未成功（可能是网络问题），保留凭据并稍后重试");
        }

        return false;
    }

    /// <summary>
    /// 退出登录（FR-W-AUTH-7）：清除 Token、删除 `credentials.bin`；本地聊天记录**保留**。
    /// ⚠️ 服务端**没有** `/auth/logout` 路由，因此本方法只做本地清理（不发网络请求）。
    /// </summary>
    public void Logout()
    {
        _tokens.Clear();
        _sessionStartedAtMs = 0;
        _expiredNotified = false;

        _logger.LogInformation("已退出登录（本地凭据已清除）");
        LoggedOut?.Invoke(this, EventArgs.Empty);
    }

    /* ---------------------------------------------------------------------- */
    /* 状态                                                                    */
    /* ---------------------------------------------------------------------- */

    /// <summary>当前认证状态（供设置页展示；**不含**任何 Token 内容）。</summary>
    public AuthStatus Describe()
        => new(
            Authenticated: _tokens.IsAuthenticated,
            UserId: _tokens.UserId,
            DeviceId: _tokens.DeviceId,
            StorageStatus: _tokens.StorageStatus,
            IsEncryptionAvailable: _tokens.IsEncryptionAvailable,
            AccessTokenExpiresAt: _tokens.AccessTokenExpiresAt,
            RequiresReLogin: _expiredNotified);

    /// <summary>当前 deviceId（**绝不**为空；读取失败会重新生成并写回 —— EDGE-W-27）。</summary>
    public string CurrentDeviceId() => _deviceIds.GetOrCreate(_tokens.DeviceId);

    /// <summary>凭据存储状态（设置页展示：已加密保存 / 未保存 / 损坏 / 加密不可用）。</summary>
    public CredentialStoreStatus StorageStatus => _tokens.StorageStatus;

    /// <summary>标记一次「成功校验」，刷新 `lastUsedAt`（FR-W-AUTH-13）。</summary>
    public void MarkCredentialsUsed() => _tokens.MarkCredentialsUsed();

    /* ---------------------------------------------------------------------- */
    /* 内部                                                                    */
    /* ---------------------------------------------------------------------- */

    /// <summary>
    /// 真正发 `POST /auth/refresh`（**无信封**）。由 <see cref="TokenManager"/> 在互斥保护下调用。
    /// </summary>
    private Task<ApiResult<AuthTokensDto>> InvokeRefreshAsync(
        TokenRefreshInvocation invocation,
        CancellationToken cancellationToken)
        => _rest.PostEnvelopeLessAsync<AuthTokensDto>(
            "auth/refresh",
            new RefreshRequestDto { RefreshToken = invocation.RefreshToken },
            authenticated: false,
            // ⚠️ 刷新请求自身**不得**再触发续签，否则递归。
            allowRefresh: false,
            timeoutMs: ProtocolConstants.RestTimeoutMs,
            cancellationToken);

    private void OnCredentialsInvalidated(object? sender, CredentialsInvalidatedEventArgs e)
    {
        if (_expiredNotified)
        {
            return;
        }

        _expiredNotified = true;

        var reason = ClassifyExpiryReason();
        if (reason == "kicked")
        {
            _logger.LogWarning("检测到被其他设备顶号（服务端已移除本地 Refresh Token）");
        }
        else
        {
            _logger.LogInformation("登录已过期，需要重新登录 reason={Reason}", reason);
        }

        AuthExpired?.Invoke(this, new AuthExpiredEventArgs(reason, e.Error));
    }

    /// <summary>
    /// EDGE-W-2：区分「被静默顶号」与「Refresh Token 自然超期」。
    ///
    /// 依据：后端 `RefreshTokenStore.issue()` 在设备数超限时**直接删除最旧设备的 refresh token**
    /// （`40302` 是死代码，从不抛出）。因此收到 40102 时，若本次会话建立尚在 Refresh TTL（30 天）内，
    /// 则极可能被其他设备顶掉。
    /// </summary>
    private string ClassifyExpiryReason()
    {
        if (_sessionStartedAtMs <= 0)
        {
            return "expired";
        }

        var elapsedMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _sessionStartedAtMs;
        return elapsedMs < RefreshTokenTtl.TotalMilliseconds ? "kicked" : "expired";
    }

    private static LoginFailureReason ClassifyLoginFailure(ApiError error)
    {
        // 网络故障 / 超时：没有 HTTP 状态码（EDGE-W-2：**不算**凭据失效，可重试）。
        if (error.HttpStatus is null)
        {
            return error.Kind == ApiFailureKind.Configuration
                ? LoginFailureReason.Unknown
                : LoginFailureReason.Network;
        }

        if (error.Code == ErrorCatalog.DeviceNotAllowed)
        {
            return LoginFailureReason.DeviceNotAllowed;
        }

        if (error.Code == ErrorCatalog.TokenInvalid)
        {
            return LoginFailureReason.DeviceKicked;
        }

        if (error.Code is ErrorCatalog.AuthFailed)
        {
            return LoginFailureReason.InvalidCredentials;
        }

        if (error.Code is ErrorCatalog.ParamMissing or ErrorCatalog.CityEmpty)
        {
            return LoginFailureReason.InvalidRequest;
        }

        if (error.HttpStatus is 401 or 403)
        {
            return LoginFailureReason.InvalidCredentials;
        }

        if (error.HttpStatus >= 500 || error.Code == ErrorCatalog.ServiceUnavailable)
        {
            return LoginFailureReason.ServerError;
        }

        return LoginFailureReason.Unknown;
    }
}
