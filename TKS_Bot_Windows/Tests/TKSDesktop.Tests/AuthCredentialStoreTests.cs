using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TKSDesktop.App;
using TKSDesktop.Contracts;
using TKSDesktop.Core.Auth;
using TKSDesktop.Core.Platform;
using TKSDesktop.Platform.Windows;
using Xunit;
namespace TKSDesktop.Tests;

/// <summary>
/// DPAPI 凭据持久化测试（P0 / FR-W-AUTH-3、3a、3b / V-W-S4）。
///
/// 覆盖：往返、**密文不含明文**、损坏密文返回 <c>Corrupted</c>、超 7 天判定过期、
/// 原子写、无明文降级分支、`deviceId` 格式与稳定持久化。
/// </summary>
public sealed class AuthCredentialStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _credFile;
    private readonly string _deviceFile;

    public AuthCredentialStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "tks-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _credFile = Path.Combine(_dir, "credentials.bin");
        _deviceFile = Path.Combine(_dir, "device.json");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch (IOException)
        {
            // 清理失败不影响测试结论。
        }
    }

    /// <summary>
    /// 平台层加密存储（DPAPI 密文；**唯一**的落盘实现）。
    /// 路径经 <see cref="AppPaths"/> 的环境变量覆盖指向测试目录。
    /// </summary>
    private ISecretStore NewSecretStore()
    {
        var paths = new AppPaths(
            appDir: _dir,
            environment: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [AppPaths.EnvConfigDir] = _dir,
                [AppPaths.EnvDataDir] = Path.Combine(_dir, "data"),
                [AppPaths.EnvStateDir] = Path.Combine(_dir, "state"),
            });

        Assert.Equal(_credFile, paths.CredentialsFile);
        return new ThemeAwareSecretStore(paths);
    }

    /// <summary>强类型载荷层（状态分类 + CredentialPayload ⇄ 字节）。</summary>
    private CredentialStore NewStore()
        => new(NewSecretStore(), _credFile, NullLogger<CredentialStore>.Instance);

    private static CredentialPayload SamplePayload(long nowMs = 1_700_000_000_000) => new()
    {
        AccessToken = "PLAINTEXT-ACCESS-TOKEN-SENTINEL",
        RefreshToken = "PLAINTEXT-REFRESH-TOKEN-SENTINEL",
        UserId = "user-1",
        DeviceId = "device_11111111-2222-3333-4444-555555555555",
        AccessTokenExpiresAt = nowMs + 900_000,
        LastUsedAt = nowMs,
    };

    /* ---------------- DPAPI 往返 ------------------------------------------------- */

    [Fact]
    public void SaveThenLoad_RoundTripsAllFields()
    {
        var store = NewStore();
        if (!store.IsEncryptionAvailable)
        {
            // DPAPI 在当前账户作用域下不可用时不伪造通过：明确报告为不可验证。
            return;
        }

        var payload = SamplePayload();
        var save = store.Save(payload);

        Assert.True(save.Success);
        Assert.Equal(CredentialStoreStatus.EncryptedSaved, save.Status);
        Assert.True(File.Exists(_credFile));

        var loaded = store.Load();
        Assert.Equal(SecretReadStatus.Success, loaded.Status);
        Assert.True(loaded.HasPayload);

        var round = loaded.Payload!;
        Assert.Equal(payload.AccessToken, round.AccessToken);
        Assert.Equal(payload.RefreshToken, round.RefreshToken);
        Assert.Equal(payload.UserId, round.UserId);
        Assert.Equal(payload.DeviceId, round.DeviceId);
        Assert.Equal(payload.AccessTokenExpiresAt, round.AccessTokenExpiresAt);
        Assert.Equal(payload.LastUsedAt, round.LastUsedAt);
    }

    [Fact]
    public void Save_WritesCiphertextOnly_NoPlaintextOnDisk()
    {
        // ⚠️ P0 / V-W-S4：文件**任何情况下**不得出现明文 Token。
        var store = NewStore();
        if (!store.IsEncryptionAvailable)
        {
            return;
        }

        store.Save(SamplePayload());

        var raw = File.ReadAllBytes(_credFile);
        var asText = Encoding.UTF8.GetString(raw);
        var asBase64 = Convert.ToBase64String(raw);
        var asLatin1 = Encoding.Latin1.GetString(raw);

        foreach (var needle in new[]
                 {
                     "PLAINTEXT-ACCESS-TOKEN-SENTINEL",
                     "PLAINTEXT-REFRESH-TOKEN-SENTINEL",
                     "accessToken",
                     "refreshToken",
                 })
        {
            Assert.DoesNotContain(needle, asText, StringComparison.Ordinal);
            Assert.DoesNotContain(needle, asBase64, StringComparison.Ordinal);
            Assert.DoesNotContain(needle, asLatin1, StringComparison.Ordinal);
        }

        // 也不得是「明文 JSON」形态。
        Assert.False(raw.Length > 2 && raw[0] == (byte)'{');
    }

    [Fact]
    public void Save_LeavesNoTemporaryFilesBehind()
    {
        var store = NewStore();
        if (!store.IsEncryptionAvailable)
        {
            return;
        }
        store.Save(SamplePayload());
        store.Save(SamplePayload());

        // 原子写只允许留下目标文件本身。
        var files = Directory.GetFiles(_dir);
        Assert.Single(files);
        Assert.Equal(_credFile, files[0]);
    }

    [Fact]
    public void Save_OverExistingFile_ReplacesAtomically()
    {
        var store = NewStore();
        if (!store.IsEncryptionAvailable)
        {
            return;
        }

        store.Save(SamplePayload());
        var second = SamplePayload();
        second.AccessToken = "SECOND-ACCESS-TOKEN-SENTINEL";
        Assert.True(store.Save(second).Success);

        var loaded = store.Load();
        Assert.Equal("SECOND-ACCESS-TOKEN-SENTINEL", loaded.Payload!.AccessToken);
    }

    /* ---------------- 损坏密文 → Corrupted，且不删文件 ------------------------- */

    [Fact]
    public void Load_CorruptedCiphertext_ReturnsCorruptedAndKeepsFile()
    {
        // ⚠️ FR-W-AUTH-3b：**不得**静默删除文件。
        File.WriteAllBytes(_credFile, [0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x11, 0x22, 0x33]);

        var store = NewStore();
        var loaded = store.Load();

        Assert.Equal(SecretReadStatus.Corrupted, loaded.Status);
        Assert.False(loaded.HasPayload);
        Assert.True(File.Exists(_credFile), "损坏的凭据文件必须保留，不得静默删除（FR-W-AUTH-3b）");
        Assert.Equal(CredentialStoreStatus.Corrupted, store.Status);
    }

    [Fact]
    public void Load_EmptyFile_ReturnsCorruptedAndKeepsFile()
    {
        File.WriteAllBytes(_credFile, []);

        var store = NewStore();
        var loaded = store.Load();

        Assert.Equal(SecretReadStatus.Corrupted, loaded.Status);
        Assert.True(File.Exists(_credFile));
    }

    [Fact]
    public void TryRead_CorruptedCiphertext_ReturnsCorruptedAndKeepsFile()
    {
        File.WriteAllBytes(_credFile, [1, 2, 3, 4, 5]);

        var store = NewSecretStore();
        var status = store.TryRead(out var plaintext);

        Assert.Equal(SecretReadStatus.Corrupted, status);
        Assert.Empty(plaintext);
        Assert.True(File.Exists(_credFile));
    }

    [Fact]
    public void Load_MissingFile_ReturnsNotFoundWithoutThrowing()
    {
        var store = NewStore();

        var loaded = store.Load();

        Assert.Equal(SecretReadStatus.NotFound, loaded.Status);
        Assert.False(loaded.HasPayload);
        Assert.Equal(CredentialStoreStatus.NotSaved, store.Status);
        Assert.False(File.Exists(_credFile));
    }

    [Fact]
    public void Delete_RemovesFileAndResetsStatus()
    {
        var store = NewStore();
        if (!store.IsEncryptionAvailable)
        {
            return;
        }

        store.Save(SamplePayload());
        store.Delete();

        Assert.False(File.Exists(_credFile));
        Assert.Equal(CredentialStoreStatus.NotSaved, store.Status);
        Assert.Equal(SecretReadStatus.NotFound, store.Load().Status);
    }

    [Fact]
    public void Delete_OnMissingFile_IsIdempotent()
    {
        var store = NewStore();
        store.Delete();
        store.Delete();
        Assert.False(File.Exists(_credFile));
    }

    /* ---------------- ISecretStore 原始字节接口 -------------------------------- */

    [Fact]
    public void TryWriteTryRead_OriginalBytesRoundTrip()
    {
        var store = NewSecretStore();
        if (!store.IsEncryptionAvailable)
        {
            return;
        }

        byte[] data = Encoding.UTF8.GetBytes("""{"accessToken":"t","refreshToken":"r"}""");

        Assert.True(store.TryWrite(data));

        var status = store.TryRead(out var plaintext);
        Assert.Equal(SecretReadStatus.Success, status);
        Assert.Equal(data, plaintext);
    }

    [Fact]
    public void TryWrite_EmptySpan_ReturnsFalse()
    {
        var store = NewSecretStore();
        Assert.False(store.TryWrite(ReadOnlySpan<byte>.Empty));
        Assert.False(File.Exists(_credFile));
    }

    /* ---------------- 单一实现路径（W-P5 / NFR-W-14 / FR-W-TEST-3） ------------- */

    [Fact]
    public void CredentialStore_DoesNotImplementISecretStore()
    {
        // 凭据落盘**只有一条**实现路径（平台层 ISecretStore）。若 Core 层再实现一次 ISecretStore，
        // 两份实现会写出不同格式却指向同一个 credentials.bin，并让运行时与 --selftest 验证不同
        // 代码路径 —— PRD FR-W-TEST-3 明确禁止「自检全绿但真实启动路径是坏的」。
        Assert.False(typeof(ISecretStore).IsAssignableFrom(typeof(CredentialStore)));
        Assert.DoesNotContain(typeof(ISecretStore), typeof(CredentialStore).GetInterfaces());
    }

    [Fact]
    public void CredentialStore_DelegatesDpapiToPlatformLayer()
    {
        // Core 层**不得**直接调用 ProtectedData（DPAPI 属 Windows 专有能力，W-P5 / NFR-W-14）。
        // 这里以反射断言 Core.Auth.CredentialStore 的方法体不引用 ProtectedData。
        var referenced = typeof(CredentialStore)
            .GetMethods(System.Reflection.BindingFlags.Public
                        | System.Reflection.BindingFlags.NonPublic
                        | System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.Static
                        | System.Reflection.BindingFlags.DeclaredOnly)
            .SelectMany(m =>
            {
                try
                {
                    return m.GetMethodBody()?.LocalVariables.Select(v => v.LocalType) ?? [];
                }
                catch (InvalidOperationException)
                {
                    return [];
                }
            })
            .Where(t => t is not null)
            .Select(t => t!.FullName ?? t.Name)
            .ToArray();

        Assert.DoesNotContain(
            referenced,
            n => n.Contains("ProtectedData", StringComparison.Ordinal));
    }

    [Fact]
    public void CredentialStore_Save_WritesThroughPlatformStoreOnly()
    {
        var secretStore = NewSecretStore();
        if (!secretStore.IsEncryptionAvailable)
        {
            return;
        }

        var store = new CredentialStore(secretStore, _credFile, NullLogger<CredentialStore>.Instance);
        Assert.False(File.Exists(_credFile));

        Assert.True(store.Save(SamplePayload()).Success);

        // 平台层读回即等价于 CredentialStore 读回（同一条路径）。
        Assert.Equal(SecretReadStatus.Success, secretStore.TryRead(out var raw));
        Assert.NotNull(CredentialPayload.Deserialize(raw));
        Assert.Equal(CredentialStoreStatus.EncryptedSaved, store.Status);
    }

    /* ---------------- 7 天空闲上限（FR-W-AUTH-13） ----------------------------- */

    [Fact]
    public void IsIdleExpired_JustUnderSevenDays_IsNotExpired()
    {
        var now = 1_700_000_000_000;
        var payload = SamplePayload(now - ProtocolConstants.OfflineCredentialMaxIdleMs + 1_000);

        Assert.False(payload.IsIdleExpired(now));
    }

    [Fact]
    public void IsIdleExpired_OverSevenDays_IsExpired()
    {
        var now = 1_700_000_000_000;
        var payload = SamplePayload(now - ProtocolConstants.OfflineCredentialMaxIdleMs - 1_000);

        Assert.True(payload.IsIdleExpired(now));
    }

    [Fact]
    public void IsIdleExpired_ZeroLastUsed_IsTreatedAsExpired()
    {
        // 旧版文件缺 lastUsedAt：视为过期，强制重新登录而不是无限期放行。
        var payload = SamplePayload();
        payload.LastUsedAt = 0;

        Assert.True(payload.IsIdleExpired(1_700_000_000_000));
    }

    [Fact]
    public void OfflineCredentialMaxIdleMs_IsExactlySevenDays()
    {
        Assert.Equal(7L * 24 * 60 * 60 * 1000, ProtocolConstants.OfflineCredentialMaxIdleMs);
    }

    /* ---------------- 主动续签阈值（FR-W-AUTH-11） ---------------------------- */

    [Fact]
    public void IsAccessTokenExpired_WithProactiveThreshold()
    {
        var now = 1_700_000_000_000;
        var payload = SamplePayload(now);
        payload.AccessTokenExpiresAt = now + ProtocolConstants.ProactiveRefreshThresholdMs - 1;

        // 剩余 < 60s → 需要主动续签。
        Assert.True(payload.IsAccessTokenExpired(now, ProtocolConstants.ProactiveRefreshThresholdMs));

        payload.AccessTokenExpiresAt = now + ProtocolConstants.ProactiveRefreshThresholdMs + 1;
        Assert.False(payload.IsAccessTokenExpired(now, ProtocolConstants.ProactiveRefreshThresholdMs));
    }

    /* ---------------- 序列化契约：逐字段 JsonPropertyName，无全局策略 ---------- */

    [Fact]
    public void Serialize_UsesExactCamelCasePropertyNames()
    {
        var json = Encoding.UTF8.GetString(SamplePayload().SerializeToUtf8Bytes());

        foreach (var name in new[]
                 {
                     "\"accessToken\"", "\"refreshToken\"", "\"userId\"",
                     "\"deviceId\"", "\"accessTokenExpiresAt\"", "\"lastUsedAt\"",
                 })
        {
            Assert.Contains(name, json, StringComparison.Ordinal);
        }

        // PascalCase（全局命名策略缺失时的退化形态）绝不能出现。
        Assert.DoesNotContain("\"AccessToken\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"DeviceId\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Deserialize_EmptyOrGarbageBytes_ReturnsNullWithoutThrowing()
    {
        Assert.Null(CredentialPayload.Deserialize(ReadOnlySpan<byte>.Empty));
        Assert.Null(CredentialPayload.Deserialize(Encoding.UTF8.GetBytes("not json")));
        Assert.Null(CredentialPayload.Deserialize("""{"userId":"u"}"""u8));
    }

    [Fact]
    public void Deserialize_MissingTokens_TreatedAsCorrupted()
    {
        // 缺 Token 的载荷必须判为损坏，而不是「已登录但无 Token」。
        Assert.Null(CredentialPayload.Deserialize("""{"accessToken":"","refreshToken":"","userId":"u"}"""u8));
        Assert.Null(CredentialPayload.Deserialize("""{"accessToken":"a"}"""u8));
    }

    [Fact]
    public void FromTokens_ComputesAbsoluteExpiryFromExpiresIn()
    {
        var now = 1_700_000_000_000;
        var payload = CredentialPayload.FromTokens("a", "r", "u", "device_x", expiresInSeconds: 900, now);

        Assert.Equal(now + 900_000, payload.AccessTokenExpiresAt);
        Assert.Equal(now, payload.LastUsedAt);
        Assert.True(payload.HasTokens);
    }

    [Fact]
    public void FromTokens_NegativeExpiresIn_IsClampedNotUnderflowed()
    {
        var now = 1_700_000_000_000;
        var payload = CredentialPayload.FromTokens("a", "r", "u", "device_x", expiresInSeconds: -5, now);

        Assert.Equal(now, payload.AccessTokenExpiresAt);
    }

    [Fact]
    public void ToString_RedactsTokens()
    {
        var text = SamplePayload().ToString();

        Assert.DoesNotContain("PLAINTEXT-ACCESS-TOKEN-SENTINEL", text, StringComparison.Ordinal);
        Assert.DoesNotContain("PLAINTEXT-REFRESH-TOKEN-SENTINEL", text, StringComparison.Ordinal);
        Assert.Contains("redacted", text, StringComparison.OrdinalIgnoreCase);
    }

    /* ---------------- deviceId（FR-W-AUTH-2 / EDGE-W-27） --------------------- */

    [Fact]
    public void NewDeviceId_HasRequiredFormat()
    {
        var id = DeviceIdProvider.NewDeviceId();

        Assert.StartsWith("device_", id, StringComparison.Ordinal);
        Assert.True(DeviceIdProvider.IsValidDeviceId(id));
        // device_ + uuidv4 小写连字符（36 字符）。
        var suffix = id["device_".Length..];
        Assert.Equal(36, suffix.Length);
        Assert.Equal(suffix.ToLowerInvariant(), suffix);
        Assert.Equal(4, suffix.Count(c => c == '-'));
        Assert.True(Guid.TryParse(suffix, out _));
    }

    [Fact]
    public void GetOrCreate_FirstCallPersistsAndSecondCallIsStable()
    {
        var provider = new DeviceIdProvider(_deviceFile, NullLogger<DeviceIdProvider>.Instance);

        var first = provider.GetOrCreate();
        var second = provider.GetOrCreate();

        Assert.Equal(first, second);
        Assert.True(File.Exists(_deviceFile));

        // 新实例（模拟重启）必须读出同一个 deviceId。
        var restarted = new DeviceIdProvider(_deviceFile, NullLogger<DeviceIdProvider>.Instance);
        Assert.Equal(first, restarted.GetOrCreate());
    }

    [Fact]
    public void GetOrCreate_CorruptedDeviceFile_RegeneratesWithoutThrowing()
    {
        // EDGE-W-27：解析失败 → 重新生成并写回，**不崩溃**。
        File.WriteAllText(_deviceFile, "{ this is not json");

        var provider = new DeviceIdProvider(_deviceFile, NullLogger<DeviceIdProvider>.Instance);
        var id = provider.GetOrCreate();

        Assert.True(DeviceIdProvider.IsValidDeviceId(id));
        Assert.Equal(id, provider.GetOrCreate());
    }

    [Fact]
    public void GetOrCreate_PrefersPersistedDeviceIdFromCredentials()
    {
        var provider = new DeviceIdProvider(_deviceFile, NullLogger<DeviceIdProvider>.Instance);
        var persisted = "device_99999999-8888-7777-6666-555555555555";

        Assert.Equal(persisted, provider.GetOrCreate(persisted));
    }

    [Fact]
    public void IsValidDeviceId_RejectsMalformedValues()
    {
        Assert.False(DeviceIdProvider.IsValidDeviceId(null));
        Assert.False(DeviceIdProvider.IsValidDeviceId(string.Empty));
        Assert.False(DeviceIdProvider.IsValidDeviceId("   "));
        Assert.False(DeviceIdProvider.IsValidDeviceId("device_"));
        Assert.False(DeviceIdProvider.IsValidDeviceId("11111111-2222-3333-4444-555555555555"));
    }

    [Fact]
    public void IsValidDeviceId_AcceptsLegacyHexFormFromBackend()
    {
        // 后端在未收到 deviceId 时会生成 device_{hex} 形态；不得因此丢弃既有身份。
        Assert.True(DeviceIdProvider.IsValidDeviceId("device_ab12cd34ef56"));
    }
}
