using Microsoft.Win32;
using TKSDesktop.Core.Platform;

namespace TKSDesktop.Platform.Windows;

/// <summary>
/// 开机自启（PRD §8.4 / FR-W-DSK-3 / FR-W-SET-5 / V-W-B5）。
///
/// 契约（逐条对应 §8.4）：
/// <list type="bullet">
///   <item>只写 <c>HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run</c>，
///         值名 <see cref="ValueName"/>；**禁止**写 <c>HKLM</c>（无需管理员权限）；</item>
///   <item>命令行 <c>"&lt;exe路径&gt;" --hidden</c> —— **必须带引号**（路径含空格时否则会被拆成多个参数）；</item>
///   <item><see cref="IsEnabled"/>：注册表值存在**且**与 <see cref="Environment.ProcessPath"/> 规范化后
///         大小写不敏感相等；不匹配时视为「未开启」，但**不得自动覆盖**
///         （可能指向另一个安装副本，由设置页如实展示「当前指向：&lt;路径&gt;」）；</item>
///   <item><see cref="Disable"/>：**删除**该值（<see cref="RegistryKey.DeleteValue"/>），不是置空。</item>
/// </list>
/// </summary>
public sealed class AutoStartManager : IAutoStartManager
{
    /// <summary>注册表值名（§8.4 定值，勿改）。</summary>
    public const string ValueName = "TKS Desktop";

    /// <summary>Run 子键路径（仅 HKCU）。</summary>
    public const string RunSubKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>静默启动参数（FR-W-AUTH-12：开机静默启动到托盘并自动建连）。</summary>
    public const string HiddenArgument = "--hidden";

    private readonly string _expectedExecutablePath;

    /// <param name="expectedExecutablePath">
    /// 期望的可执行文件路径；默认取 <see cref="Environment.ProcessPath"/>（即当前进程）。
    /// 自检场景可显式传入以便断言「指向本程序」的判定逻辑。
    /// </param>
    public AutoStartManager(string? expectedExecutablePath = null)
    {
        _expectedExecutablePath = expectedExecutablePath
            ?? Environment.ProcessPath
            ?? string.Empty;
    }

    /// <summary>本端期望的可执行文件路径（规范化后，用于与注册表值比对）。</summary>
    public string ExpectedExecutablePath => Normalize(_expectedExecutablePath);

    /// <inheritdoc />
    public bool IsEnabled
    {
        get
        {
            var target = CurrentTargetPath;
            if (string.IsNullOrEmpty(target))
            {
                return false;
            }

            // 规范化 + 大小写不敏感比对（§8.4）。不匹配 => 未开启，但**不覆盖**。
            return string.Equals(Normalize(target), Normalize(_expectedExecutablePath), StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// 解析规则：去掉包裹的双引号后取命令行的首个 token（即 exe 路径）。
    /// 未设置值或读取失败时返回 <c>null</c>。
    /// </remarks>
    public string? CurrentTargetPath
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunSubKeyPath, writable: false);
                if (key?.GetValue(ValueName) is not string raw || string.IsNullOrWhiteSpace(raw))
                {
                    return null;
                }

                return ParseExecutablePath(raw);
            }
            catch (Exception)
            {
                // 注册表不可读（策略限制）=> 视为未开启，不得抛错阻断设置页。
                return null;
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>写入带引号的命令行 + <c>--hidden</c>。</remarks>
    public bool Enable()
    {
        var executable = Normalize(_expectedExecutablePath);
        if (string.IsNullOrEmpty(executable))
        {
            return false;
        }

        var commandLine = BuildCommandLine(executable);

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunSubKeyPath, writable: true);
            if (key is null)
            {
                return false;
            }

            key.SetValue(ValueName, commandLine, RegistryValueKind.String);
            return true;
        }
        catch (Exception)
        {
            // 写失败（策略禁止 / 权限）=> 返回 false，由设置页如实提示。
            return false;
        }
    }

    /// <inheritdoc />
    /// <remarks>**删除**该值（§8.4：不得仅置空）。值不存在时视为成功（幂等）。</remarks>
    public bool Disable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunSubKeyPath, writable: true);
            if (key is null)
            {
                return false;
            }

            if (key.GetValue(ValueName) is null)
            {
                // 幂等：本来就没有，视为已达成目标状态。
                return true;
            }

            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return key.GetValue(ValueName) is null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 构造注册表命令行：<c>"&lt;exe路径&gt;" --hidden</c>（**带引号**，§8.4 明确要求）。
    /// </summary>
    public static string BuildCommandLine(string executablePath) => $"\"{executablePath}\" {HiddenArgument}";

    /// <summary>
    /// 从注册表命令行解析出 exe 路径：优先取被引号包裹的部分（支持路径含空格），
    /// 否则取第一个空白分隔的 token。
    /// </summary>
    public static string? ParseExecutablePath(string commandLine)
    {
        var trimmed = commandLine.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        if (trimmed[0] == '"')
        {
            var closing = trimmed.IndexOf('"', 1);
            return closing > 1 ? trimmed[1..closing] : null;
        }

        var space = trimmed.IndexOf(' ', StringComparison.Ordinal);
        return space < 0 ? trimmed : trimmed[..space];
    }

    /// <summary>
    /// 路径规范化：展开环境变量、取绝对路径、去掉尾随分隔符。
    /// 失败时原样返回（**不**抛错：注册表里可能存的是无效路径，仍要能展示给用户）。
    /// </summary>
    public static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(path.Trim()).Trim('"');
            var full = Path.GetFullPath(expanded);
            return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception)
        {
            return path.Trim().Trim('"');
        }
    }

    /// <summary>
    /// 用于设置页展示「当前指向」的文案片段（i18n key 由 UI 层解析；此处只回传路径）。
    /// </summary>
    public string? CurrentTargetForDisplay => CurrentTargetPath;

}
