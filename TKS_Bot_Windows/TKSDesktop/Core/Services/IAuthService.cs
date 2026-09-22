using TKSDesktop.Contracts;
using TKSDesktop.Core.Auth;

namespace TKSDesktop.Core.Services;

/// <summary>认证状态（界面用；PRD §6.1）。</summary>
public enum AuthState
{
    /// <summary>无凭据（首次启动或已退出登录）。</summary>
    Unauthenticated,

    /// <summary>正在登录。</summary>
    SigningIn,

    /// <summary>已登录且凭据可用。</summary>
    Authenticated,
}

/// <summary>
/// 登录失败原因（**界面投影**，PRD FR-W-AUTH-8 / EDGE-W-2 / EDGE-W-3）。
///
/// ⚠️ 刻意与 `Core.Auth.LoginFailureReason` 分开：后者是**实现层**分类（含 `DeviceKicked` /
///    `InvalidRequest` 等更细的语义），本枚举是**界面契约**（决定提示文案分组）。
///    ViewModel 只依赖本枚举，从而不依赖 `Core.Auth`（分层约束 NFR-W-14）。
/// </summary>
public enum LoginFailureReason
{
    /// <summary>用户名或密码错误（`40101`）。</summary>
    InvalidCredentials,

    /// <summary>该设备不在白名单（`40301`，仅运维配置 `BOT_DEVICE_TOKENS` 时可能发生）。</summary>
    DeviceNotAllowed,

    /// <summary>设备数超限（`40302`）—— **后端死代码**，仅作兜底文案。</summary>
    DeviceLimitExceeded,

    /// <summary>
    /// 被顶号（EDGE-W-3）：服务端 `RefreshTokenStore.issue()` 直接删除最旧设备的 refresh token，
    /// **不报错**。必须与网络故障区分，否则用户会误以为是自己网络问题。
    /// </summary>
    KickedOut,

    /// <summary>
    /// 网络故障 / 超时 —— **凭据必须保留、不踢人**，只提示可重试（EDGE-W-2）。
    /// </summary>
    NetworkError,

    /// <summary>服务端故障（5xx / 50301）。</summary>
    ServerError,

    /// <summary>其他未分类失败。</summary>
    Unknown,
}

/// <summary>
/// 登录结果（**界面投影**）。
///
/// ⚠️ 不含任何凭据字段：Token **只**存在于 `Core.Auth.TokenManager` 内存态，
///    绝不经 ViewModel 传递（FR-W-SEC-2 / V-W-S4）。
/// </summary>
/// <param name="Success">是否登录成功。</param>
/// <param name="Reason">失败原因（成功时为 <see cref="LoginFailureReason.Unknown"/>，不应被读取）。</param>
/// <param name="ErrorCode">服务端 / 客户端错误码字符串（可能为 <c>null</c>）。</param>
/// <param name="I18nKey">必然是**已登记**的界面文案 key（未知码回落中性兜底）。</param>
public sealed record LoginOutcome(
    bool Success,
    LoginFailureReason Reason,
    string? ErrorCode,
    string I18nKey);

/// <summary>凭据存储状态（界面展示用；映射自 `Core.Auth.CredentialStoreStatus`）。</summary>
/// <param name="IsEncryptionAvailable">DPAPI 是否可用。为 <c>false</c> 时凭据不会落盘（**不降级为明文**）。</param>
/// <param name="HasPersistedCredential">凭据文件当前是否存在。</param>
/// <param name="CredentialFilePath">凭据文件路径（设置页展示用 —— FR-W-AUTH-10）。</param>
public sealed record CredentialViewStatus(
    bool IsEncryptionAvailable,
    bool HasPersistedCredential,
    string CredentialFilePath);

/// <summary>
/// 认证服务（PRD §6.1 / FR-W-AUTH-1..13）。
///
/// ⚠️ 这是给 **UI 层**的窄接口：ViewModel 只依赖它，**不得**直接依赖 `Core.Auth` 的具体类
///    （`AuthService` / `TokenManager` 等属实现细节）。
/// ⚠️ 结果类型刻意使用本命名空间下的**界面投影**（<see cref="LoginOutcome"/> /
///    <see cref="LoginFailureReason"/> / <see cref="CredentialViewStatus"/>），
///    但**恢复状态**复用 `Core.Auth.CredentialRestoreStatus`（启动协调器需要完整四态语义，
///    且该枚举本身就是契约级分类，不属实现细节）。
/// </summary>
public interface IAuthService
{
    /// <summary>当前认证状态。</summary>
    AuthState State { get; }

    /// <summary>当前用户 id（未登录时为 <c>null</c>）。</summary>
    string? UserId { get; }

    /// <summary>稳定持久化的设备 id（`device_{uuidv4}` —— FR-W-AUTH-2）。</summary>
    string DeviceId { get; }

    /// <summary>凭据存储状态（设置页「关于」展示 —— FR-W-AUTH-10）。</summary>
    CredentialViewStatus CredentialStatus { get; }

    /// <summary>
    /// 登录（FR-W-AUTH-1/3/8）。成功时凭据经 DPAPI 加密持久化；
    /// 失败按 <see cref="LoginFailureReason"/> 给出界面可直接使用的分组。
    /// </summary>
    Task<LoginOutcome> LoginAsync(string username, string password, CancellationToken ct = default);

    /// <summary>
    /// 启动时恢复登录态（FR-W-AUTH-12 / 13 / 3b / EDGE-W-11）。
    /// ⚠️ 实现**不得**在 `Corrupted` 时静默删除凭据文件（FR-W-AUTH-3b）。
    /// </summary>
    Auth.CredentialRestoreResult TryRestore();

    /// <summary>退出登录：清除内存凭据并删除 `credentials.bin`、断开 WS（FR-W-AUTH-7）。</summary>
    Task LogoutAsync(CancellationToken ct = default);
}
