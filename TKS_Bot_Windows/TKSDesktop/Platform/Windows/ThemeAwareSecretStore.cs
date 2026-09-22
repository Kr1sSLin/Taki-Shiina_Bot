using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using TKSDesktop.Core.Platform;

namespace TKSDesktop.Platform.Windows;

/// <summary>
/// DPAPI 凭据存储（PRD §3.6 FR-W-SEC-2 / FR-W-SEC-2a、§3.5）。
///
/// 契约（P0，V-W-S4）：
/// <list type="bullet">
///   <item>凭据**必须**为 DPAPI 密文（<see cref="ProtectedData"/>，范围 <see cref="DataProtectionScope.CurrentUser"/>）；</item>
///   <item>**不存在**明文降级分支（不移植 Linux 端的 <c>allowPlaintextCredentials</c>）——
///         本类没有任何「跳过加密直接写盘」的代码路径，DPAPI 不可用时 <see cref="TryWrite"/> 直接返回 <c>false</c>；</item>
///   <item>写入**原子**：临时文件 → <see cref="File.Replace(string,string,string,bool)"/>（目标不存在时退化为 <see cref="File.Move(string,string)"/>）；</item>
///   <item>读失败区分「文件不存在」（<see cref="SecretReadStatus.NotFound"/>）与
///         「密文损坏 / 换 Windows 账户」（<see cref="SecretReadStatus.Corrupted"/>），
///         后者**不得删除文件**（FR-W-AUTH-3b）；</item>
///   <item>**不得**把任何明文写入磁盘的任何文件 —— 临时文件里也只有密文。</item>
/// </list>
///
/// ⚠️ 便携版落盘位置（§14.4）由 <see cref="IPaths"/> 决定，本类只按传入路径工作。
/// </summary>
public sealed class ThemeAwareSecretStore : ISecretStore
{
    /// <summary>
    /// DPAPI 可选熵：固定为 <c>null</c>。
    /// **不**引入自有熵，否则多端/多版本格式演进时会因为「熵从哪来」而无法解密。
    /// </summary>
    private static readonly byte[]? OptionalEntropy = null;

    /// <summary>
    /// 加密载荷信封前缀 <c>"TKSC"</c> + 版本 1。
    /// 用于把「非本端写出的内容」与「本端密文但密钥失效」区分开，
    /// 两者都报 <see cref="SecretReadStatus.Corrupted"/>，但前者说明文件被外部替换过。
    /// </summary>
    private static readonly byte[] Magic = [0x54, 0x4B, 0x53, 0x43, 0x01];

    /// <summary>写入串行化：避免同进程内两个写入互相覆盖彼此的临时文件。</summary>
    private readonly object _writeGate = new();

    private readonly string _credentialsFile;

    /// <summary>
    /// <see cref="IsEncryptionAvailable"/> 的探测缓存。
    /// 用 <see cref="int"/> 而非 <c>bool?</c>，以便 <see cref="Interlocked"/> 无锁读写：
    /// 0 = 未探测，1 = 可用，-1 = 不可用。
    /// </summary>
    private int _encryptionProbe;

    public ThemeAwareSecretStore(IPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _credentialsFile = paths.CredentialsFile;
    }

    /// <summary>诊断用：凭据文件完整路径（**不含**内容）。</summary>
    public string CredentialsFilePath => _credentialsFile;

    /// <inheritdoc />
    /// <remarks>
    /// 探测方式：对一个小样本执行 <see cref="ProtectedData.Protect"/> 再 <c>Unprotect</c> 校验往返。
    /// 域策略 / 用户配置文件损坏时该调用会抛 <see cref="CryptographicException"/>，即视为不可用（EDGE-W-11）。
    /// 结果缓存，避免设置页每次刷新都触碰系统调用。
    /// </remarks>
    public bool IsEncryptionAvailable
    {
        get
        {
            if (Volatile.Read(ref _encryptionProbe) is var cached && cached != 0)
            {
                return cached > 0;
            }

            var available = ProbeEncryption();
            Interlocked.CompareExchange(ref _encryptionProbe, available ? 1 : -1, 0);
            return Volatile.Read(ref _encryptionProbe) > 0;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// 失败**不降级为明文**（EDGE-W-11 / FR-W-SEC-2）：DPAPI 不可用或写入异常时返回 <c>false</c>，
    /// 由调用方改为「本次运行仅在内存持有 Token」并如实提示用户。
    /// </remarks>
    public bool TryWrite(ReadOnlySpan<byte> plaintext)
    {
        if (plaintext.IsEmpty)
        {
            // 空载荷不得落盘：会写出一个「空凭据文件」，下次启动被判为损坏（Corrupted）并触发
            // 「登录状态已失效」提示 —— 属于凭据格式约束，不是调用方可忽略的边界。
            return false;
        }

        if (!IsEncryptionAvailable)
        {
            return false;
        }

        byte[] ciphertext;
        try
        {
            ciphertext = Protect(plaintext);
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }

        try
        {
            return WriteAtomically(ciphertext);
        }
        finally
        {
            // 密文本身无需保密，但仍及时清零以减少内存驻留。
            CryptographicOperations.ZeroMemory(ciphertext);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⚠️ <see cref="SecretReadStatus.Corrupted"/> 时**不删除文件**：
    /// 换 Windows 账户后重新登录回来该文件仍可能可读，删除会掩盖真实故障（FR-W-AUTH-3b / EDGE-W-11）。
    /// </remarks>
    public SecretReadStatus TryRead(out byte[] plaintext)
    {
        plaintext = [];

        if (!File.Exists(_credentialsFile))
        {
            return SecretReadStatus.NotFound;
        }

        byte[] ciphertext;
        try
        {
            ciphertext = File.ReadAllBytes(_credentialsFile);
        }
        catch (IOException)
        {
            return SecretReadStatus.Corrupted;
        }
        catch (UnauthorizedAccessException)
        {
            // ACL 被外部改动 / 属主变更 —— 归入损坏，但**不**删除文件。
            return SecretReadStatus.Corrupted;
        }

        if (ciphertext.Length == 0)
        {
            // 空文件 = 原子写入的中断残留，视为损坏。
            return SecretReadStatus.Corrupted;
        }

        try
        {
            plaintext = Unprotect(ciphertext);
            return SecretReadStatus.Success;
        }
        catch (CryptographicException)
        {
            // 换 Windows 账户 / 密钥不匹配 / 文件被篡改 / 信封前缀不符。
            plaintext = [];
            return SecretReadStatus.Corrupted;
        }
        catch (PlatformNotSupportedException)
        {
            plaintext = [];
            return SecretReadStatus.Corrupted;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ciphertext);
        }
    }

    /// <inheritdoc />
    /// <remarks>退出登录 / 空闲超期（EDGE-W-2）。文件不存在时不抛错。</remarks>
    public void Delete()
    {
        try
        {
            File.Delete(_credentialsFile);
        }
        catch (IOException)
        {
            // 删除失败不阻断退出登录流程：下次 TryWrite 会覆盖。
        }
        catch (UnauthorizedAccessException)
        {
            // 同上。
        }
    }

    private static bool ProbeEncryption()
    {
        try
        {
            var sample = new byte[] { 0x54, 0x4B, 0x53 }; // "TKS"
            var sealed_ = ProtectedData.Protect(sample, OptionalEntropy, DataProtectionScope.CurrentUser);
            var roundTrip = ProtectedData.Unprotect(sealed_, OptionalEntropy, DataProtectionScope.CurrentUser);
            return roundTrip.AsSpan().SequenceEqual(sample);
        }
        catch (Exception)
        {
            // 加密不可用是**预期**降级路径（域策略 / 配置文件损坏），不得崩溃（EDGE-W-11）。
            return false;
        }
    }

    private static byte[] Protect(ReadOnlySpan<byte> plaintext)
    {
        var buffer = plaintext.ToArray();
        try
        {
            var payload = ProtectedData.Protect(buffer, OptionalEntropy, DataProtectionScope.CurrentUser);
            return WrapWithMagic(payload);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private static byte[] Unprotect(byte[] stored)
    {
        var payload = stored.AsSpan();
        if (payload.Length <= Magic.Length || !payload[..Magic.Length].SequenceEqual(Magic))
        {
            // 前缀不符 = 不是本端写出的信封（被外部程序截断/覆盖）。
            throw new CryptographicException("credentials payload is not a TKS DPAPI envelope");
        }

        return ProtectedData.Unprotect(
            payload[Magic.Length..].ToArray(), OptionalEntropy, DataProtectionScope.CurrentUser);
    }

    private static byte[] WrapWithMagic(byte[] payload)
    {
        var result = new byte[Magic.Length + payload.Length];
        Magic.CopyTo(result, 0);
        payload.CopyTo(result, Magic.Length);
        return result;
    }

    /// <summary>
    /// 原子写入（FR-W-SEC-2a）：
    /// ① 写同目录临时文件（同一卷才能 <see cref="File.Replace(string,string,string,bool)"/>）；
    /// ② 立即把 ACL 收紧为「仅当前用户」；
    /// ③ <see cref="File.Replace(string,string,string,bool)"/> 原子替换；目标不存在时退化为 <see cref="File.Move(string,string)"/>。
    ///
    /// ⚠️ 临时文件里也只有**密文**：中途崩溃不会在任何路径留下明文。
    /// </summary>
    private bool WriteAtomically(byte[] ciphertext)
    {
        lock (_writeGate)
        {
            var directory = Path.GetDirectoryName(_credentialsFile);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temp = _credentialsFile + $".{Guid.NewGuid():N}.tmp";
            try
            {
                WriteTightened(temp, ciphertext);

                if (File.Exists(_credentialsFile))
                {
                    // ignoreMetadataErrors: true —— ACL 元数据差异不阻断替换；
                    // 替换后下方 TightenAcl 会重新收紧目标文件权限。
                    File.Replace(
                        temp, _credentialsFile, destinationBackupFileName: null, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(temp, _credentialsFile, overwrite: false);
                }

                // 兜底收紧：File.Move / File.Replace 可能带过来源文件 ACL。
                TightenAcl(_credentialsFile);
                return true;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
            catch (PlatformNotSupportedException)
            {
                // 极少数宿主不支持 ACL API；此时密文已写入，仅权限未能收紧。
                return File.Exists(_credentialsFile);
            }
            finally
            {
                TryDelete(temp);
            }
        }
    }

    /// <summary>
    /// 以收紧后的权限写入文件。
    ///
    /// ⚠️ Windows 没有 POSIX 的「0600」位（<c>File.SetUnixFileMode</c> 在此无效）：
    ///    PRD FR-W-SEC-2a 要求「以当前用户为唯一读者、文件不设共享写权限」，
    ///    因此这里的做法是 ① <see cref="FileShare.None"/> 独占打开（不共享写权限）
    ///    ② 显式 ACL 授权当前用户并移除继承来的非当前用户 ACE（见 <see cref="TightenAcl"/>）。
    ///    ACL 调整若因组策略 / 非 NTFS 失败**不阻断写入** —— 首要约束是「不落明文」，密文已满足。
    /// </summary>
    private static void WriteTightened(string path, byte[] ciphertext)
    {
        using (var stream = new FileStream(
                   path,
                   FileMode.CreateNew,
                   FileAccess.Write,
                   FileShare.None,
                   bufferSize: 4096,
                   FileOptions.WriteThrough))
        {
            stream.Write(ciphertext, 0, ciphertext.Length);
            stream.Flush(flushToDisk: true);
        }

        TightenAcl(path);
    }

    /// <summary>
    /// 把文件 ACL 收紧为「仅当前用户 完全控制」并断开继承。
    /// 失败时静默返回 <c>false</c>（内容始终是 DPAPI 密文，见 FR-W-SEC-2）。
    /// </summary>
    private static bool TightenAcl(string path)
    {
        try
        {
            var userSid = WindowsIdentity.GetCurrent().User;
            if (userSid is null)
            {
                return false;
            }

            var fileInfo = new FileInfo(path);
            var security = fileInfo.GetAccessControl();

            // ① 断开继承并丢弃继承来的 ACE（否则 BUILTIN\Users / Everyone 仍可能可读）。
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            // ② 移除所有非当前用户的显式规则。
            foreach (FileSystemAccessRule rule in security.GetAccessRules(
                         includeExplicit: true, includeInherited: false, targetType: typeof(SecurityIdentifier)))
            {
                if (rule.IdentityReference.Equals(userSid))
                {
                    continue;
                }

                security.RemoveAccessRule(new FileSystemAccessRule(
                    rule.IdentityReference,
                    rule.FileSystemRights,
                    rule.InheritanceFlags,
                    rule.PropagationFlags,
                    rule.AccessControlType));
            }

            // ③ 保证当前用户仍有完全控制（否则下次写入会失败）。
            security.AddAccessRule(new FileSystemAccessRule(
                userSid,
                FileSystemRights.FullControl,
                InheritanceFlags.None,
                PropagationFlags.None,
                AccessControlType.Allow));

            fileInfo.SetAccessControl(security);
            return true;
        }
        catch (Exception)
        {
            // 组策略受限 / 非 NTFS / 网络盘：无法收紧 ACL。文件仍是密文，不阻断流程。
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // 残留 *.tmp 无害（内容为密文），且读取路径只认 credentials.bin。
        }
        catch (UnauthorizedAccessException)
        {
            // 同上。
        }
    }
}
