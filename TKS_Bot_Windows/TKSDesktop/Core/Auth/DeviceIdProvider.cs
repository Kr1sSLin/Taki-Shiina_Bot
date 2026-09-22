using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using TKSDesktop.Contracts;
using TKSDesktop.Core.Platform;

namespace TKSDesktop.Core.Auth;

/// <summary>
/// `device.json` 的降级载荷（**仅**在 DPAPI 不可用时使用；`deviceId` 本身不是机密）。
/// </summary>
internal sealed class DeviceFilePayload
{
    [JsonPropertyName("deviceId")]
    public string? DeviceId { get; set; }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = null,
        WriteIndented = true,
    };

    public static DeviceFilePayload? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<DeviceFilePayload>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public string ToJson() => JsonSerializer.Serialize(this, Options);
}

/// <summary>
/// 设备标识提供者（FR-W-AUTH-2 / EDGE-W-27）。
///
/// <list type="bullet">
///   <item>格式固定为 <c>device_{uuidv4 小写连字符}</c>（与后端 <c>f"device_{uuid.uuid4().hex}"</c>
///         及 Android <c>"device_${UUID.randomUUID()}"</c> 同一前缀约定）；</item>
///   <item>首次生成后**稳定持久化**：v1.1 起随凭据写入 `credentials.bin`（
///         <see cref="CredentialPayload.DeviceId"/>）；</item>
///   <item><see cref="IPaths.DeviceFile"/>（`device.json`）**仅**作为 DPAPI 不可用时的降级载体 ——
///         即凭据无法持久化时仍要保证设备身份稳定（否则每次启动都会被后端视为新设备）；</item>
///   <item>读取失败 / 解析失败 → **重新生成并写回，不崩溃**（EDGE-W-27）。</item>
/// </list>
/// </summary>
public sealed class DeviceIdProvider
{
    /// <summary>`deviceId` 前缀。</summary>
    public const string Prefix = "device_";

    private readonly string _deviceFilePath;
    private readonly ILogger<DeviceIdProvider> _logger;
    private readonly object _gate = new();

    private string? _cached;

    /// <param name="deviceFilePath">`device.json` 路径（<see cref="IPaths.DeviceFile"/>）。</param>
    /// <param name="logger">日志。</param>
    public DeviceIdProvider(string deviceFilePath, ILogger<DeviceIdProvider> logger)
    {
        _deviceFilePath = deviceFilePath;
        _logger = logger;
    }

    /// <summary>用 <see cref="IPaths.DeviceFile"/> 构造。</summary>
    public DeviceIdProvider(IPaths paths, ILogger<DeviceIdProvider> logger)
        : this(paths.DeviceFile, logger)
    {
    }

    /// <summary>降级载体的文件路径。</summary>
    public string DeviceFilePath => _deviceFilePath;

    /// <summary>
    /// 生成一个新的合规 deviceId（<c>device_{uuidv4 小写连字符}</c>）。
    /// </summary>
    public static string NewDeviceId() => string.Concat(Prefix, Guid.NewGuid().ToString("D"));

    /// <summary>
    /// 校验 deviceId 是否符合约定格式（宽松：只要求非空且带 <c>device_</c> 前缀 ——
    /// 旧版后端可能给出 <c>device_{hex}</c> 形态，**不得**因此丢弃既有身份）。
    /// </summary>
    public static bool IsValidDeviceId(string? deviceId)
        => !string.IsNullOrWhiteSpace(deviceId)
           && deviceId.Length > Prefix.Length
           && deviceId.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>
    /// 取当前 deviceId：内存缓存 → 凭据里的值 → `device.json` → 新生成并写回。
    /// 读取 / 解析失败**不崩溃**（EDGE-W-27）。
    /// </summary>
    /// <param name="persistedDeviceId">
    /// 凭据中已持久化的 deviceId（来自 `credentials.bin`）；为空则回落到 `device.json`。
    /// </param>
    public string GetOrCreate(string? persistedDeviceId = null)
    {
        lock (_gate)
        {
            if (IsValidDeviceId(_cached))
            {
                return _cached!;
            }

            // ① 凭据里的 deviceId（首选来源，FR-W-AUTH-3a）
            if (IsValidDeviceId(persistedDeviceId))
            {
                _cached = persistedDeviceId!;
                return _cached;
            }

            // ② 降级载体 device.json
            var fromFile = TryReadDeviceFile();
            if (IsValidDeviceId(fromFile))
            {
                _cached = fromFile!;
                return _cached;
            }


            // ③ 新生成并写回（读失败 / 解析失败 / 不存在都走这里，且不崩溃）
            var generated = NewDeviceId();
            _cached = generated;
            TryWriteDeviceFile(generated);
            _logger.LogInformation("已生成新的 deviceId（持久化到 {File}）", _deviceFilePath);
            return generated;
        }
    }

    /// <summary>把已知的 deviceId 写入降级载体（DPAPI 不可用时保证身份稳定）。</summary>
    public bool TryPersist(string deviceId)
    {
        if (!IsValidDeviceId(deviceId))
        {
            return false;
        }

        lock (_gate)
        {
            _cached = deviceId;
        }

        return TryWriteDeviceFile(deviceId);
    }

    /// <summary>仅测试 / 排障用：清空内存缓存（不触碰磁盘）。</summary>
    internal void ClearCache()
    {
        lock (_gate)
        {
            _cached = null;
        }
    }

    private string? TryReadDeviceFile()
    {
        try
        {
            if (!File.Exists(_deviceFilePath))
            {
                return null;
            }

            var json = File.ReadAllText(_deviceFilePath);
            var payload = DeviceFilePayload.TryParse(json);
            if (payload is null)
            {
                // EDGE-W-27：解析失败不崩溃，走重新生成分支并写回。
                _logger.LogWarning("device.json 解析失败，将重新生成 deviceId file={File}", _deviceFilePath);
                return null;
            }

            return payload.DeviceId;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _logger.LogWarning(ex, "device.json 读取失败，将重新生成 deviceId file={File}", _deviceFilePath);
            return null;
        }
    }

    private bool TryWriteDeviceFile(string deviceId)
    {
        try
        {
            var directory = Path.GetDirectoryName(_deviceFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = new DeviceFilePayload { DeviceId = deviceId }.ToJson();

            // 原子写：临时文件 + File.Replace（与凭据写入同策略，避免半截文件）。
            var tmp = Path.Combine(
                string.IsNullOrEmpty(directory) ? "." : directory,
                string.Concat(Path.GetRandomFileName(), ".tmp"));

            try
            {
                File.WriteAllText(tmp, json);
                if (File.Exists(_deviceFilePath))
                {
                    File.Replace(tmp, _deviceFilePath, destinationBackupFileName: null, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(tmp, _deviceFilePath);
                }
            }
            finally
            {
                if (File.Exists(tmp))
                {
                    File.Delete(tmp);
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // 写失败不阻断运行：本次仍使用内存中的 deviceId。
            _logger.LogWarning(ex, "device.json 写入失败（本次运行仍使用内存中的 deviceId）file={File}", _deviceFilePath);
            return false;
        }
    }
}
