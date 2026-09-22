namespace TKSDesktop.Core.Platform;

/// <summary>
/// 路径解析（PRD §3.5 / §9.9 / FR-W-DSK-12 / FR-W-DSK-14）。
///
/// 三分根语义对标 Linux 端的 XDG 三分：
/// <list type="bullet">
///   <item>配置根：`settings.json` / `device.json`（覆盖变量 `TKS_CONFIG_DIR`）</item>
///   <item>数据根：`tks.db` / `attachments/`（覆盖变量 `TKS_DATA_DIR`）</item>
///   <item>状态根：日志等（覆盖变量 `TKS_STATE_DIR`）</item>
/// </list>
///
/// ⚠️ 三个环境变量**必须优先于默认路径**（便携运行、自动化测试与沙箱环境）。
/// ⚠️ 便携版必须忽略 `%APPDATA%` 而强制使用程序目录下的 `.\data\`（§14.4）。
/// </summary>
public interface IPaths
{
    /// <summary>配置根目录（`settings.json` / `device.json`）。</summary>
    string ConfigDir { get; }

    /// <summary>数据根目录（`tks.db` / `attachments/`）。</summary>
    string DataDir { get; }

    /// <summary>状态根目录。</summary>
    string StateDir { get; }

    /// <summary>日志目录（JSON 行，滚动 7 天）。</summary>
    string LogsDir { get; }

    /// <summary>附件私有目录（FR-W-SEC-6 的路径前缀校验基准）。</summary>
    string AttachmentsDir { get; }

    /// <summary>`settings.json` 完整路径。</summary>
    string SettingsFile { get; }

    /// <summary>DPAPI 凭据文件路径（密文，`credentials.bin`）。</summary>
    string CredentialsFile { get; }

    /// <summary>DPAPI 不可用时的 `deviceId` 降级载体。</summary>
    string DeviceFile { get; }

    /// <summary>SQLite 数据库文件路径。</summary>
    string DatabaseFile { get; }

    /// <summary>程序所在目录（便携版判定与自启命令行构造用）。</summary>
    string AppDir { get; }

    /// <summary>是否为便携版运行（`.\portable.flag` 或程序目录可写且存在 `data\`）。</summary>
    bool IsPortable { get; }

    /// <summary>启动时创建全部目录（FR-W-DSK-14：首次启动即建好，避免后续写失败）。</summary>
    void EnsureDirectories();
}
