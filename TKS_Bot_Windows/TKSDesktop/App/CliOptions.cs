namespace TKSDesktop.App;

/// <summary>
/// 命令行参数（PRD FR-W-DSK-9）。
///
/// 支持：
/// <list type="bullet">
///   <item><c>--hidden</c>：**有持久化凭据时**静默启动到托盘并自动建连；无凭据时忽略该参数并显示登录页。</item>
///   <item><c>--version</c>：打印版本号后退出（版本号来自程序集元数据，不得硬编码）。</item>
///   <item><c>--reset-config</c>：重置设置为默认值。</item>
///   <item><c>--force-device-scale-factor=&lt;n&gt;</c>：DPI 缩放覆盖（FR-W-UI-10）。</item>
///   <item><c>--selftest</c>：无图形界面自检模式（NFR-W-15 / FR-W-TEST-1）。</item>
/// </list>
/// </summary>
public sealed record CliOptions
{
    /// <summary>静默启动到托盘（需凭据可用）。</summary>
    public bool Hidden { get; init; }

    /// <summary>仅打印版本后退出。</summary>
    public bool ShowVersion { get; init; }

    /// <summary>重置配置文件为默认值。</summary>
    public bool ResetConfig { get; init; }

    /// <summary>无图形界面自检模式。</summary>
    public bool SelfTest { get; init; }

    public NotificationActivation? Notification { get; init; }

    /// <summary>DPI 缩放覆盖值（如 1.25）；未指定时为 <c>null</c>。</summary>
    public double? ForceDeviceScaleFactor { get; init; }

    /// <summary>未知参数（记录日志，不阻断启动）。</summary>
    public IReadOnlyList<string> Unknown { get; init; } = [];

    /// <summary>解析命令行参数。**不抛错**：非法值降级为 <c>null</c> 并记入 <see cref="Unknown"/>。</summary>
    public static CliOptions Parse(IReadOnlyList<string> args)
    {
        var hidden = false;
        var version = false;
        var reset = false;
        var selfTest = false;
        double? scale = null;
        NotificationActivation? notification = null;
        var unknown = new List<string>();

        foreach (var raw in args)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var arg = raw.Trim();
            if (NotificationActivation.Parse(arg) is { } activation)
            {
                notification = activation;
            }
            else if (arg.Equals("--hidden", StringComparison.OrdinalIgnoreCase))
            {
                hidden = true;
            }
            else if (arg.Equals("--version", StringComparison.OrdinalIgnoreCase) || arg.Equals("-v", StringComparison.OrdinalIgnoreCase))
            {
                version = true;
            }
            else if (arg.Equals("--reset-config", StringComparison.OrdinalIgnoreCase))
            {
                reset = true;
            }
            else if (arg.Equals("--selftest", StringComparison.OrdinalIgnoreCase))
            {
                selfTest = true;
            }
            else if (arg.StartsWith("--force-device-scale-factor=", StringComparison.OrdinalIgnoreCase))
            {
                var value = arg["--force-device-scale-factor=".Length..];
                if (double.TryParse(value, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
                {
                    scale = parsed;
                }
                else
                {
                    unknown.Add(arg);
                }
            }
            else
            {
                unknown.Add(arg);
            }
        }

        return new CliOptions
        {
            Hidden = hidden,
            ShowVersion = version,
            ResetConfig = reset,
            SelfTest = selfTest,
            Notification = notification,
            ForceDeviceScaleFactor = scale,
            Unknown = unknown,
        };
    }
}
