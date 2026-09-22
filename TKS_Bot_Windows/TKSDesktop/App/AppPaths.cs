using System.Diagnostics.CodeAnalysis;
using TKSDesktop.Core.Platform;

namespace TKSDesktop.App;

/// <summary>
/// 路径解析实现（PRD §9.9 路径规范 / FR-W-DSK-12 / FR-W-DSK-14 / §14.4 便携版）。
///
/// 默认三分根（安装版）：
/// <code>
/// 配置根  %APPDATA%\TKS Desktop\                （settings.json / device.json / credentials.bin）
/// 数据根  %APPDATA%\TKS Desktop\data\           （tks.db / attachments\）
/// 状态根  %APPDATA%\TKS Desktop\state\          （logs\）
/// </code>
/// 便携版（存在 <c>.\portable.flag</c>，或程序目录可写且存在 <c>data\</c>）时三根全部指向
/// <c>&lt;程序目录&gt;\data\{config,state,attachments}</c>，并**忽略 `%APPDATA%`**（不污染系统）。
/// </summary>
public sealed class AppPaths : IPaths
{
    private const string AppFolderName = "TKS Desktop";
    private const string PortableFlagName = "portable.flag";

    /// <summary>环境变量覆盖名（FR-W-DSK-14：**必须优先于默认路径**）。</summary>
    public const string EnvConfigDir = "TKS_CONFIG_DIR";
    public const string EnvDataDir = "TKS_DATA_DIR";
    public const string EnvStateDir = "TKS_STATE_DIR";

    public AppPaths(string? appDir = null, IReadOnlyDictionary<string, string?>? environment = null)
    {
        var env = environment ?? ReadEnvironment();
        AppDir = appDir ?? AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        IsPortable = DetectPortable(AppDir);

        // 便携版强制作用域；否则 %APPDATA%\TKS Desktop\
        var baseDir = IsPortable
            ? Path.Combine(AppDir, "data")
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                AppFolderName);

        ConfigDir = ResolveOverride(env, EnvConfigDir)
            ?? (IsPortable ? Path.Combine(baseDir, "config") : baseDir);

        DataDir = ResolveOverride(env, EnvDataDir)
            ?? (IsPortable ? baseDir : Path.Combine(baseDir, "data"));

        StateDir = ResolveOverride(env, EnvStateDir)
            ?? Path.Combine(baseDir, "state");

        LogsDir = Path.Combine(StateDir, "logs");
        AttachmentsDir = Path.Combine(DataDir, "attachments");
        SettingsFile = Path.Combine(ConfigDir, "settings.json");
        CredentialsFile = Path.Combine(ConfigDir, "credentials.bin");
        DeviceFile = Path.Combine(ConfigDir, "device.json");
        DatabaseFile = Path.Combine(DataDir, "tks.db");
    }

    public string ConfigDir { get; }

    public string DataDir { get; }

    public string StateDir { get; }

    public string LogsDir { get; }

    public string AttachmentsDir { get; }

    public string SettingsFile { get; }

    public string CredentialsFile { get; }
    public string DeviceFile { get; }

    public string DatabaseFile { get; }

    public string AppDir { get; }

    public bool IsPortable { get; }

    /// <summary>
    /// 便携版判定（§14.4）：① 存在 <c>.\portable.flag</c>；② 或「程序目录可写且存在 <c>data\</c> 子目录」。
    /// </summary>
    [SuppressMessage("Design", "CA1031", Justification = "探测失败必须降级而非崩溃（EDGE-W-27）。")]
    public static bool DetectPortable(string appDir)
    {
        try
        {
            if (File.Exists(Path.Combine(appDir, PortableFlagName)))
            {
                return true;
            }

            var dataSub = Path.Combine(appDir, "data");
            if (!Directory.Exists(dataSub))
            {
                return false;
            }

            // 程序目录可写判定：尝试建一个临时文件（放 C:\Program Files\ 时不可写）。
            var probe = Path.Combine(appDir, $".tks-write-probe-{Guid.NewGuid():N}");
            using (var _ = File.Create(probe, 1, FileOptions.DeleteOnClose))
            {
                // 立即关闭并由 DeleteOnClose 清理。
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 便携版要求程序目录可写；不可写时**明确报错**，不得静默回退到 `%APPDATA%`
    /// （否则用户以为数据在 U 盘里，实际在系统盘 —— §14.4）。
    /// </summary>
    public string? ValidatePortableWritable()
    {
        if (!IsPortable)
        {
            return null;
        }

        try
        {
            Directory.CreateDirectory(ConfigDir);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>启动时创建全部目录（FR-W-DSK-14）。</summary>
    public void EnsureDirectories()
    {
        Directory.CreateDirectory(ConfigDir);
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(StateDir);
        Directory.CreateDirectory(LogsDir);
        Directory.CreateDirectory(AttachmentsDir);
    }

    /// <summary>
    /// 附件路径越界校验（FR-W-SEC-6 / EDGE-W-29）：
    /// 把路径规范化后断言位于附件私有目录内；越界必须拒绝加载。
    /// </summary>
    public bool IsInsideAttachments(string? localPath)
    {
        if (string.IsNullOrWhiteSpace(localPath))
        {
            return false;
        }

        try
        {
            var root = Path.GetFullPath(AttachmentsDir)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var target = Path.GetFullPath(localPath.Trim());

            return target.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string? ResolveOverride(IReadOnlyDictionary<string, string?> env, string key)
    {
        if (env.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
        {
            return Path.GetFullPath(value.Trim());
        }

        return null;
    }

    private static Dictionary<string, string?> ReadEnvironment()
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in new[] { EnvConfigDir, EnvDataDir, EnvStateDir })
        {
            result[key] = Environment.GetEnvironmentVariable(key);
        }

        return result;
    }
}
