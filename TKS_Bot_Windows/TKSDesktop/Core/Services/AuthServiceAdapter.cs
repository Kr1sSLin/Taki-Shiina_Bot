using Microsoft.Extensions.Logging;
using TKSDesktop.Contracts;
using TKSDesktop.Core.Auth;
using TKSDesktop.Core.Platform;

namespace TKSDesktop.Core.Services;

/// <summary>
/// 认证服务适配器（PRD §6.1）。
///
/// 把 <see cref="Core.Auth.AuthService"/>（+ <see cref="TokenManager"/>）适配为 UI 层唯一可见的
/// 窄接口 <see cref="IAuthService"/> —— 重点是**把实现层的失败分类翻译为界面分组**
/// （<see cref="LoginFailureReason"/>），使 ViewModel 不必引用 `Core.Auth`（NFR-W-14）。
///
/// <para>凭据恢复、401/403 才清凭据、主动续签、7 天空闲上限等**业务判断全部**在
/// <see cref="Core.Auth.AuthService"/> / <see cref="TokenManager"/> 内实现；
/// 本适配器**不重复判断**，只做形状与枚举的映射，避免出现第二处逻辑而分叉。</para>
/// </summary>
public sealed class AuthServiceAdapter : IAuthService
{
    private readonly Core.Auth.AuthService _authService;
    private readonly TokenManager _tokens;
    private readonly IPaths _paths;
    private readonly Func<CancellationToken, Task>? _disconnect;
    private readonly ILogger<AuthServiceAdapter>? _logger;

    public AuthServiceAdapter(
        Core.Auth.AuthService authService,
        TokenManager tokens,
        IPaths paths,
        Func<CancellationToken, Task>? disconnect = null,
        ILogger<AuthServiceAdapter>? logger = null)
    {
        _authService = authService;
        _tokens = tokens;
        _paths = paths;
        _disconnect = disconnect;
        _logger = logger;
    }

    /// <inheritdoc />
    public AuthState State => _tokens.IsAuthenticated ? AuthState.Authenticated : AuthState.Unauthenticated;

    /// <inheritdoc />
    public string? UserId => _tokens.UserId;

    /// <inheritdoc />
    public string DeviceId => _authService.CurrentDeviceId();

    /// <inheritdoc />
    public CredentialViewStatus CredentialStatus => new(
        IsEncryptionAvailable: _tokens.IsEncryptionAvailable,
        HasPersistedCredential: SafeFileExists(_paths.CredentialsFile),
        CredentialFilePath: _paths.CredentialsFile);

    /// <inheritdoc />
    public async Task<LoginOutcome> LoginAsync(string username, string password, CancellationToken ct = default)
    {
        var outcome = await _authService.LoginAsync(username, password, ct).ConfigureAwait(false);

        if (outcome.Success)
        {
            // 凭据只在内存（DPAPI 不可用）时，登录页需要如实告知 —— 由调用方读 CredentialStatus 判断。
            return new LoginOutcome(true, LoginFailureReason.Unknown, null, string.Empty);
        }

        var reason = MapReason(outcome.FailureReason);
        var errorCode = outcome.Error?.Code?.ToString(CultureInfo.InvariantCulture);

        // ⚠️ 文案优先级：服务端错误码的专属文案 > 界面分组文案 > 通用兜底。
        var key = ErrorCatalog.I18nKeyOf(outcome.Error?.Code);
        if (key == ErrorCatalog.UnknownI18nKey)
        {
            key = FallbackKeyFor(reason);
        }

        _logger?.LogInformation(
            "登录失败：reason={Reason} code={Code} key={Key}",
            reason, errorCode, key);

        return new LoginOutcome(false, reason, errorCode, key);
    }

    /// <inheritdoc />
    /// <remarks>委托给 <see cref="Core.Auth.AuthService.RestoreCredentials"/>：四态分类与控制流全在其内部。</remarks>
    public CredentialRestoreResult TryRestore() => _authService.RestoreCredentials();

    /// <inheritdoc />
    public async Task LogoutAsync(CancellationToken ct = default)
    {
        try
        {
            if (_disconnect is not null)
            {
                await _disconnect(ct).ConfigureAwait(false);
            }
        }
        finally
        {
            // 即使 WS 关闭失败也必须清内存凭据与 credentials.bin。
            _authService.Logout();
        }
    }

    /// <summary>
    /// 实现层失败原因 → 界面分组（FR-W-AUTH-8 / EDGE-W-2 / EDGE-W-3）。
    /// </summary>
    private static LoginFailureReason MapReason(Auth.LoginFailureReason reason) => reason switch
    {
        Auth.LoginFailureReason.InvalidCredentials => LoginFailureReason.InvalidCredentials,
        Auth.LoginFailureReason.DeviceNotAllowed => LoginFailureReason.DeviceNotAllowed,
        Auth.LoginFailureReason.DeviceKicked => LoginFailureReason.KickedOut,
        Auth.LoginFailureReason.ServerError => LoginFailureReason.ServerError,
        // ⚠️ 网络故障必须单独成组：**凭据保留、不踢人**，提示可重试（EDGE-W-2）。
        Auth.LoginFailureReason.Network => LoginFailureReason.NetworkError,
        _ => LoginFailureReason.Unknown,
    };

    private static string FallbackKeyFor(LoginFailureReason reason) => reason switch
    {
        LoginFailureReason.InvalidCredentials => "error.api.40101",
        LoginFailureReason.DeviceNotAllowed => "error.api.40301",
        LoginFailureReason.DeviceLimitExceeded => "error.api.40302",
        LoginFailureReason.KickedOut => "login.kickedOut",
        LoginFailureReason.NetworkError => ProtocolConstants.ClientErrorConnectionLost,
        LoginFailureReason.ServerError => "error.api.5000",
        _ => "login.failed",
    };

    private static bool SafeFileExists(string path)
    {
        try
        {
            return File.Exists(path);
        }
        catch (Exception)
        {
            // 权限 / 路径异常不得让设置页崩溃（EDGE-W-27 精神）。
            return false;
        }
    }
}
