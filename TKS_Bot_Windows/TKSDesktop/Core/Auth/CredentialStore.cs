using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using TKSDesktop.Core.Platform;

namespace TKSDesktop.Core.Auth;

/// <summary>
/// 凭据存储状态（供设置页展示：**已加密保存** / **未保存**）。
/// </summary>
public enum CredentialStoreStatus
{
    /// <summary>尚无凭据文件（首次启动 / 已退出登录）。</summary>
    NotSaved,

    /// <summary>凭据以 DPAPI 密文保存。</summary>
    EncryptedSaved,

    /// <summary>文件存在但**无法解密**（换 Windows 账户 / 文件损坏）—— 需重新登录。</summary>
    Corrupted,

    /// <summary>DPAPI 不可用 → **不持久化**，本次运行仅在内存中持有 Token（EDGE-W-11 分支 ①）。</summary>
    EncryptionUnavailable,
}

/// <summary>凭据落盘结果。</summary>
public sealed record CredentialSaveOutcome(bool Success, CredentialStoreStatus Status, string? FailureReason)
{
    /// <summary>成功（密文已原子写入）。</summary>
    public static CredentialSaveOutcome Ok() => new(true, CredentialStoreStatus.EncryptedSaved, null);

    /// <summary>DPAPI 加密封装失败 → **不持久化**，仅在内存持有（不得明文降级）。</summary>
    public static CredentialSaveOutcome CryptoFailed(string reason)
        => new(false, CredentialStoreStatus.EncryptionUnavailable, reason);

    /// <summary>I/O 失败。</summary>
    public static CredentialSaveOutcome IoFailed(string reason)
        => new(false, CredentialStoreStatus.NotSaved, reason);
}

/// <summary>凭据读取结果。</summary>
public sealed record CredentialLoadResult(SecretReadStatus Status, CredentialPayload? Payload)
{
    /// <summary>是否成功解出载荷。</summary>
    public bool HasPayload => Status == SecretReadStatus.Success && Payload is not null;

    public static CredentialLoadResult NotFound() => new(SecretReadStatus.NotFound, null);

    public static CredentialLoadResult Corrupted() => new(SecretReadStatus.Corrupted, null);

    public static CredentialLoadResult Ok(CredentialPayload payload) => new(SecretReadStatus.Success, payload);
}

/// <summary>
/// 凭据持久化的**强类型载荷层**（P0 / FR-W-AUTH-3、3a、3b / FR-W-SEC-2 / V-W-S4）。
///
/// 职责边界（W-P5：平台差异只允许出现在平台层）：
/// <list type="bullet">
///   <item>本类只负责「<see cref="CredentialPayload"/> ⇄ 字节」与状态分类；</item>
///   <item>**DPAPI 加密与原子落盘由 <see cref="ISecretStore"/> 的平台实现承担**
///         （<c>Platform/Windows/ThemeAwareSecretStore</c>）。本类**不**直接调用
///         <c>ProtectedData</c>、也**不**直接写文件 —— DPAPI 属 Windows 专有能力，
///         在 Core 层调用会同时违反 W-P5 与 NFR-W-14。</item>
/// </list>
///
/// ⚠️ 历史缺陷（已在本次修复中消除）：本类曾自带一份 DPAPI + 原子写实现并直接实现
/// <see cref="ISecretStore"/>，与平台层实现**并存**。两份实现写出的**文件格式不同**
/// （平台层带魔法前缀信封，本类写裸 DPAPI 输出），却指向同一个 <c>credentials.bin</c>；
/// 更严重的是运行时用本类、而 <c>--selftest</c> 只验证平台层，形成
/// 「自检全绿但真实路径未被测」的风险（PRD FR-W-TEST-3 明确禁止）。现在只有一条实现路径。
/// </summary>
public sealed class CredentialStore
{
    private readonly ISecretStore _secretStore;
    private readonly string _filePath;
    private readonly ILogger<CredentialStore> _logger;
    private readonly object _gate = new();

    private CredentialStoreStatus _status = CredentialStoreStatus.NotSaved;

    /// <param name="secretStore">平台层加密存储（DPAPI 密文读写；**唯一**的落盘实现）。</param>
    /// <param name="filePath"><c>credentials.bin</c> 完整路径（<see cref="IPaths.CredentialsFile"/>）。</param>
    /// <param name="logger">日志（**禁止**记录 Token / 密文 / base64）。</param>
    public CredentialStore(ISecretStore secretStore, string filePath, ILogger<CredentialStore> logger)
    {
        _secretStore = secretStore;
        _filePath = filePath;
        _logger = logger;
    }

    /// <summary>用 <see cref="IPaths.CredentialsFile"/> 构造。</summary>
    public CredentialStore(ISecretStore secretStore, IPaths paths, ILogger<CredentialStore> logger)
        : this(secretStore, paths.CredentialsFile, logger)
    {
    }

    /// <summary>凭据文件路径（密文）。</summary>
    public string FilePath => _filePath;

    /// <summary>当前存储状态（设置页展示用：已加密保存 / 未保存 / 损坏 / 加密不可用）。</summary>
    public CredentialStoreStatus Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    /// <summary>文件当前是否存在。</summary>
    public bool Exists => File.Exists(_filePath);

    /// <summary>
    /// DPAPI（<see cref="System.Security.Cryptography.DataProtectionScope.CurrentUser"/>）
    /// 是否可用 —— 由平台实现做一次真实往返探测，本类只转发结论。
    /// </summary>
    public bool IsEncryptionAvailable => _secretStore.IsEncryptionAvailable;

    /* ---------------------------------------------------------------------- */
    /* 高层 API（强类型载荷）                                                    */
    /* ---------------------------------------------------------------------- */

    /// <summary>
    /// 读取并解密凭据。
    /// <list type="bullet">
    ///   <item>文件不存在 → <see cref="SecretReadStatus.NotFound"/>（**不报错**，首次启动）；</item>
    ///   <item>DPAPI 不可用 → 同样按 <see cref="SecretReadStatus.NotFound"/> 处理，
    ///         但状态置 <see cref="CredentialStoreStatus.EncryptionUnavailable"/>，**不删文件**；</item>
    ///   <item>解密失败 / JSON 损坏 → <see cref="SecretReadStatus.Corrupted"/>，**不删文件**（FR-W-AUTH-3b）。
    /// </item>
    /// </list>
    /// </summary>
    public CredentialLoadResult Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_filePath))
            {
                _status = CredentialStoreStatus.NotSaved;
                return CredentialLoadResult.NotFound();
            }

            if (!_secretStore.IsEncryptionAvailable)
            {
                _logger.LogWarning(
                    "DPAPI 不可用，无法读取已存在的凭据文件；保持文件不动并要求重新登录 file={File}",
                    _filePath);
                _status = CredentialStoreStatus.EncryptionUnavailable;
                return CredentialLoadResult.Corrupted();
            }

            var read = _secretStore.TryRead(out var plain);
            switch (read)
            {
                case SecretReadStatus.NotFound:
                    _status = CredentialStoreStatus.NotSaved;
                    return CredentialLoadResult.NotFound();

                case SecretReadStatus.Corrupted:
                    // 换 Windows 账户 / 文件损坏：**不得**静默删除文件（FR-W-AUTH-3b）。
                    _logger.LogWarning(
                        "DPAPI 解密失败（可能换了 Windows 账户或文件损坏），保留文件并要求重新登录 file={File}",
                        _filePath);
                    _status = CredentialStoreStatus.Corrupted;
                    return CredentialLoadResult.Corrupted();

                default:
                    var payload = CredentialPayload.Deserialize(plain);
                    CryptographicOperations.ZeroMemory(plain);

                    if (payload is null)
                    {
                        _logger.LogWarning(
                            "凭据内容无法解析（字段缺失 / 非法 JSON），视为损坏 file={File}",
                            _filePath);
                        _status = CredentialStoreStatus.Corrupted;
                        return CredentialLoadResult.Corrupted();
                    }

                    _status = CredentialStoreStatus.EncryptedSaved;
                    return CredentialLoadResult.Ok(payload);
            }
        }
    }

    /// <summary>
    /// 加密并**原子**写入凭据。DPAPI 不可用或加密封装失败时**不写任何文件**、返回失败原因
    /// （EDGE-W-11 分支 ①：调用方据此改为「仅内存持有 Token」）。**永不**降级为明文。
    /// </summary>
    public CredentialSaveOutcome Save(CredentialPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (!payload.HasTokens)
        {
            // 不应该发生：调用方必须传完整凭据。宁可拒绝也不落半截数据。
            return CredentialSaveOutcome.IoFailed("credential payload has no access/refresh token");
        }

        if (!_secretStore.IsEncryptionAvailable)
        {
            lock (_gate)
            {
                _status = CredentialStoreStatus.EncryptionUnavailable;
            }

            // ⚠️ EDGE-W-11 分支 ①：**不持久化**，仅内存持有 Token，并明确返回失败原因。
            //    这里绝不写入明文 —— 不存在明文降级分支。
            _logger.LogWarning(
                "DPAPI 不可用：本次不持久化凭据（仅内存持有 Token，需重新登录）file={File}",
                _filePath);
            return CredentialSaveOutcome.CryptoFailed("DPAPI is not available for the current user");
        }

        var plain = payload.SerializeToUtf8Bytes();
        bool written;
        try
        {
            written = _secretStore.TryWrite(plain);
        }
        finally
        {
            // 明文只在栈上短暂存在：立即清零，降低内存残留窗口。
            CryptographicOperations.ZeroMemory(plain);
        }

        if (!written)
        {
            _logger.LogWarning("凭据密文写入失败（不阻断运行，仅内存持有）file={File}", _filePath);
            return CredentialSaveOutcome.IoFailed("credentials ciphertext write failed");
        }

        lock (_gate)
        {
            _status = CredentialStoreStatus.EncryptedSaved;
        }

        return CredentialSaveOutcome.Ok();
    }

    /// <summary>删除凭据文件（退出登录 / 空闲超期）。幂等，**不抛错**。</summary>
    public void Delete()
    {
        lock (_gate)
        {
            _secretStore.Delete();

            if (!File.Exists(_filePath))
            {
                _status = CredentialStoreStatus.NotSaved;
            }

            // 文件仍在（删除失败）：保持原状态，避免谎报「已清除」。
            _logger.LogDebug("凭据删除流程完成 file={File} exists={Exists}", _filePath, File.Exists(_filePath));
        }
    }
}
