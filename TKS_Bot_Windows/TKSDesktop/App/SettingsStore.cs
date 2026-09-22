using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace TKSDesktop.App;

/// <summary>
/// 设置存取（PRD §9.9 / FR-W-SET-* / V-W-S7）。
///
/// ⚠️ 解析失败（用户手工篡改）→ **回退默认值并记 warning，不崩溃**（EDGE-W-27）。
/// ⚠️ 写入为原子：临时文件 + `File.Replace`。
/// </summary>
public sealed class SettingsStore
{
    /// <summary>
    /// JSON 选项。
    ///
    /// ⚠️ **不设** <see cref="JsonSerializerOptions.PropertyNamingPolicy"/>（V-W-S2 / FR-W-PROTO-1）：
    /// 全仓不得出现「依赖全局驼峰/下划线转换」的反序列化路径 —— 服务端字段名写错不会报错，
    /// 只会静默变成默认值。settings.json 的键名与 <see cref="AppSettings"/> 属性名**逐字相同**
    /// （小写开头，如 <c>apiBaseUrl</c>），无需任何策略转换。
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly string _filePath;
    private readonly ILogger<SettingsStore> _logger;
    private readonly object _gate = new();

    public SettingsStore(string filePath, ILogger<SettingsStore> logger)
    {
        _filePath = filePath;
        _logger = logger;
    }

    /// <summary>当前设置（内存态）。</summary>
    public AppSettings Current { get; private set; } = new();

    /// <summary>
    /// 载入设置。文件不存在 → 默认值；解析失败 → 默认值 + warning。
    /// </summary>
    public AppSettings Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_filePath))
            {
                Current = new AppSettings();
                return Current;
            }

            try
            {
                var json = File.ReadAllText(_filePath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                Current = loaded ?? new AppSettings();
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "settings.json 无法解析，已回退默认值（EDGE-W-27）");
                Current = new AppSettings();
            }

            return Current;
        }
    }

    /// <summary>整体保存（原子写）。</summary>
    public void Save(AppSettings settings)
    {
        lock (_gate)
        {
            Current = settings;
            SaveCore(settings);
        }
    }

    /// <summary>重置为默认值并写盘（`--reset-config`）。</summary>
    public AppSettings Reset()
    {
        lock (_gate)
        {
            Current = new AppSettings();
            SaveCore(Current);
            return Current;
        }
    }

    private void SaveCore(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            var json = JsonSerializer.Serialize(settings, JsonOptions);

            var dir = Path.GetDirectoryName(_filePath)!;
            var tmp = Path.Combine(dir, Path.GetRandomFileName());
            File.WriteAllText(tmp, json);

            if (File.Exists(_filePath))
            {
                File.Replace(tmp, _filePath, null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tmp, _filePath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "settings.json 写入失败（不阻断运行）");
        }
    }
}
