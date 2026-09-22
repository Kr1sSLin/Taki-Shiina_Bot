using System.Text.Json;
using System.Text.Json.Serialization;
using TKSDesktop.Contracts;

namespace TKSDesktop.Core.Auth;

/// <summary>
/// `credentials.bin` 的**明文载荷模型**（落盘前由 DPAPI 加密；见 <see cref="CredentialStore"/>）。
///
/// ⚠️ 字段与序列化硬约束（FR-W-AUTH-3 / FR-W-AUTH-3a / FR-W-PROTO-2）：
/// <list type="bullet">
///   <item>**逐字段** <see cref="JsonPropertyNameAttribute"/>，**禁用**全局命名策略
///         （写错字段名不报错、只会静默取到默认值）；</item>
///   <item>时间一律为 **epoch 毫秒**（<c>long</c>），避免时区/格式歧义；</item>
///   <item><see cref="LastUsedAt"/> **每次成功校验 / 续签时刷新**，用于 7 天空闲上限判定
///         （FR-W-AUTH-13 / <see cref="ProtocolConstants.OfflineCredentialMaxIdleMs"/>）。</item>
/// </list>
/// </summary>
public sealed class CredentialPayload
{
    /// <summary>Access Token（JWT）。</summary>
    [JsonPropertyName("accessToken")]
    public string AccessToken { get; set; } = string.Empty;

    /// <summary>Refresh Token（**轮转**：每次续签后由新值覆盖）。</summary>
    [JsonPropertyName("refreshToken")]
    public string RefreshToken { get; set; } = string.Empty;

    /// <summary>服务端 userId。</summary>
    [JsonPropertyName("userId")]
    public string UserId { get; set; } = string.Empty;

    /// <summary>`device_{uuidv4}`（FR-W-AUTH-2），v1.1 起随凭据持久化。</summary>
    [JsonPropertyName("deviceId")]
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>Access Token **绝对**过期时刻（epoch 毫秒），用于主动续签判定（FR-W-AUTH-11）。</summary>
    [JsonPropertyName("accessTokenExpiresAt")]
    public long AccessTokenExpiresAt { get; set; }

    /// <summary>最近一次成功校验 / 续签时刻（epoch 毫秒）。**每次成功校验时刷新**（FR-W-AUTH-13）。</summary>
    [JsonPropertyName("lastUsedAt")]
    public long LastUsedAt { get; set; }

    /// <summary>
    /// 序列化选项：**无** <see cref="JsonSerializerOptions.PropertyNamingPolicy"/>，
    /// 字段名完全由 <see cref="JsonPropertyNameAttribute"/> 决定。
    /// </summary>
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = null,
        DictionaryKeyPolicy = null,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>本地是否存在可用的持久化凭据内容（缺任一 Token 即视为无效）。</summary>
    [JsonIgnore]
    public bool HasTokens => !string.IsNullOrEmpty(AccessToken) && !string.IsNullOrEmpty(RefreshToken);

    /// <summary>序列化为 UTF-8 字节（**仅用于送入 DPAPI**，绝不直接落盘）。</summary>
    public byte[] SerializeToUtf8Bytes() => JsonSerializer.SerializeToUtf8Bytes(this, Options);

    /// <summary>
    /// 从 UTF-8 字节反序列化。解析失败（损坏 / 非 JSON / 空）返回 <c>null</c>，**不抛错**
    /// 从 UTF-8 字节反序列化。解析失败（损坏 / 非 JSON / 空）返回 <c>null</c>，**不抛错**
    /// —— 调用方据此返回 <see cref="Core.Platform.SecretReadStatus.Corrupted"/>（换账户 / 文件损坏）。
    /// </summary>
    public static CredentialPayload? Deserialize(ReadOnlySpan<byte> utf8Bytes)
    {
        if (utf8Bytes.IsEmpty)
        {
            return null;
        }

        try
        {
            var loaded = JsonSerializer.Deserialize<CredentialPayload>(utf8Bytes, Options);
            if (loaded is null)
            {
                return null;
            }

            // 字段缺失会落成默认值；空 Token 视为损坏而不是「已登录但无 Token」。
            return loaded.HasTokens ? loaded : null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>用一次新的登录 / 续签结果构造载荷（<see cref="LastUsedAt"/> 置为当前时刻）。</summary>
    /// <param name="accessToken">新 Access Token。</param>
    /// <param name="refreshToken">新 Refresh Token（轮转后的值）。</param>
    /// <param name="userId">userId。</param>
    /// <param name="deviceId">deviceId。</param>
    /// <param name="expiresInSeconds">服务端 <c>expiresIn</c>（秒）。</param>
    /// <param name="nowMs">当前时刻（epoch 毫秒），便于测试注入。</param>
    public static CredentialPayload FromTokens(
        string accessToken,
        string refreshToken,
        string userId,
        string deviceId,
        int expiresInSeconds,
        long nowMs)
        => new()
        {
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            UserId = userId,
            DeviceId = deviceId,
            AccessTokenExpiresAt = nowMs + (Math.Max(0, expiresInSeconds) * 1000L),
            LastUsedAt = nowMs,
        };

    /// <summary>返回刷新了 <see cref="LastUsedAt"/> 的副本（**不可变式**更新，避免共享可变状态）。</summary>
    public CredentialPayload WithLastUsedAt(long nowMs) => new()
    {
        AccessToken = AccessToken,
        RefreshToken = RefreshToken,
        UserId = UserId,
        DeviceId = DeviceId,
        AccessTokenExpiresAt = AccessTokenExpiresAt,
        LastUsedAt = nowMs,
    };

    /// <summary>
    /// 空闲是否超过 <see cref="ProtocolConstants.OfflineCredentialMaxIdleMs"/>（7 天，FR-W-AUTH-13）。
    /// <see cref="LastUsedAt"/> 为 0（旧版文件）时视为**过期**，强制重新登录而不是无限期放行。
    /// </summary>
    public bool IsIdleExpired(long nowMs)
    {
        if (LastUsedAt <= 0)
        {
            return true;
        }

        return nowMs - LastUsedAt > ProtocolConstants.OfflineCredentialMaxIdleMs;
    }

    /// <summary>
    /// 返回回填了 <see cref="DeviceId"/> 的副本（v1.1 前写入的旧凭据缺 deviceId 时使用，
    /// 保证下次启动设备身份稳定 —— FR-W-AUTH-2）。
    /// </summary>
    public CredentialPayload WithDeviceId(string deviceId) => new()
    {
        AccessToken = AccessToken,
        RefreshToken = RefreshToken,
        UserId = UserId,
        DeviceId = deviceId,
        AccessTokenExpiresAt = AccessTokenExpiresAt,
        LastUsedAt = LastUsedAt,
    };

    /// <summary>
    /// Access Token 是否已过期或在 <paramref name="skewMs"/> 内过期（主动续签判定，FR-W-AUTH-11）。
    /// </summary>
    public bool IsAccessTokenExpired(long nowMs, long skewMs = ProtocolConstants.ProactiveRefreshThresholdMs)
        => AccessTokenExpiresAt - skewMs <= nowMs;

    /// <summary>访问令牌剩余有效期（毫秒，可为负）。</summary>
    public long AccessTokenRemainingMs(long nowMs) => AccessTokenExpiresAt - nowMs;

    /// <summary>
    /// 排障文本：**不含**任何 Token 内容（FR-W-SEC-3 禁止把 Token 写日志）。
    /// </summary>
    public override string ToString()
        => string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"CredentialPayload(user={UserId}, device={DeviceId}, accessToken=<redacted>, refreshToken=<redacted>, accessTokenExpiresAt={AccessTokenExpiresAt}, lastUsedAt={LastUsedAt})");
}
