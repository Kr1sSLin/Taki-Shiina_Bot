namespace TKSDesktop.Core.Platform;

/// <summary>
/// 凭据加密存储（PRD §3.5 / FR-W-SEC-2 / FR-W-AUTH-3）。
///
/// ⚠️ **契约（P0，V-W-S4）**：凭据**必须**为 **DPAPI 密文**（`ProtectedData`，
///    `DataProtectionScope.CurrentUser`），**任何情况下不得以明文落盘**。
/// ⚠️ **不存在**明文降级分支（不移植 Linux 端的 `allowPlaintextCredentials`）。
/// ⚠️ 读失败必须区分「明文不可用/加密不可用」与「密文损坏」；损坏时**不静默清除文件**。
/// </summary>
public interface ISecretStore
{
    /// <summary>加密保护是否可用（DPAPI 在当前用户账户下是否可用）。</summary>
    bool IsEncryptionAvailable { get; }

    /// <summary>
    /// 加密并原子写入凭据文件。
    /// </summary>
    /// <returns>
    /// 成功返回 <c>true</c>；DPAPI 不可用或写入失败返回 <c>false</c>。
    /// **不得**在失败时降级为明文、也**不得**静默失败（EDGE-W-11）。
    /// </returns>
    bool TryWrite(ReadOnlySpan<byte> plaintext);

    /// <summary>读取并解密凭据文件。适用于「文件存在但读取/解密失败」的场景。</summary>
    /// <param name="plaintext">解密后的原始字节。</param>
    /// <returns>
    /// <see cref="SecretReadStatus"/>：<c>NotFound</c>（首次启动）/ <c>Success</c> /
    /// <c>Corrupted</c>（换账户、文件损坏 —— **不得**清除文件，视为凭据损坏）。
    /// </returns>
    SecretReadStatus TryRead(out byte[] plaintext);

    /// <summary>删除凭据文件（退出登录 / 空闲超期；EDGE-W-2）。</summary>
    void Delete();
}

/// <summary>凭据读取结果。</summary>
public enum SecretReadStatus
{
    /// <summary>文件不存在（首次启动或已退出登录）。</summary>
    NotFound,

    /// <summary>读取且解密成功。</summary>
    Success,

    /// <summary>
    /// 文件存在但解密失败（换 Windows 账户 / 文件损坏 / 密钥不匹配）。
    /// 提示「登录状态已失效，请重新登录」，**不静默清除文件**（FR-W-AUTH-3b）。
    /// </summary>
    Corrupted,
}
