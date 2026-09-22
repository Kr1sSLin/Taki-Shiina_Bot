using System.Reflection;

namespace TKSDesktop.App;

/// <summary>
/// 版本信息（PRD §14.2 / FR-W-SET-9）。
///
/// ⚠️ 版本号**单一来源**是 `.csproj` 的 <c>&lt;Version&gt;</c>（写入 `AssemblyInformationalVersion`）。
///    **禁止**在代码里硬编码版本字符串 —— 另一端曾出现「Gradle 已是新版本而 UI 仍显示旧版本」的缺陷。
/// </summary>
public static class AppVersion
{
    /// <summary>程序集信息版本（含预发布标签），如 `1.0.0`。</summary>
    public static string Informational =>
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
        ?? "0.0.0";

    /// <summary>文件版本（`FileVersionInfo` 语义），如 `1.0.0.0`。</summary>
    public static string FileVersion =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0.0";

    /// <summary>产品名。</summary>
    public const string ProductName = "TKS Desktop";

    /// <summary>可执行文件名（无空格，避免注册表与命令行引号问题 —— §14.2）。</summary>
    public const string ExecutableName = "TKSDesktop.exe";

    /// <summary>产物形态（安装版 / 便携版）。</summary>
    public static string DistributionKind(bool isPortable) => isPortable ? "portable" : "installed";
}
