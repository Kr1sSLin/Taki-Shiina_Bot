using System.Text.Json.Serialization;

namespace TKSDesktop.App;

/// <summary>
/// 应用设置（PRD §9.9 / FR-W-SET-* / V-W-S7）。
///
/// ⚠️ 键集合与 Linux 端 `DEFAULT_SETTINGS` **语义一致**（V-W-S7 会比对键名）。
/// ⚠️ 本端**没有** `allowPlaintextCredentials` 键（OQ-W-1 定案：Windows 侧 DPAPI 恒可用，
///    不实现明文降级路径 —— §1.6 / FR-W-SEC-2）。
///
/// ⚠️ 每个属性**逐字**标注 <see cref="JsonPropertyNameAttribute"/>（V-W-S2 / FR-W-PROTO-1）：
///    `SettingsStore` 不设全局命名策略，键名只能来自显式标注。这也保证磁盘上的
///    `settings.json` 键名与 Linux 端保持一致（camelCase），不会因为策略变更而漂移。
/// </summary>
public sealed class AppSettings
{
    /// <summary>REST 基址（§5.1）。</summary>
    [JsonPropertyName("apiBaseUrl")]
    public string ApiBaseUrl { get; set; } = Contracts.ProtocolConstants.DefaultApiBaseUrl;

    /// <summary>WS 基址（§5.1）。</summary>
    [JsonPropertyName("wsBaseUrl")]
    public string WsBaseUrl { get; set; } = Contracts.ProtocolConstants.DefaultWsBaseUrl;

    /// <summary>主题：`system` / `light` / `dark`。</summary>
    [JsonPropertyName("theme")]
    public string Theme { get; set; } = "system";

    /// <summary>天气城市（服务端 `GET/PUT /settings/city`）。</summary>
    [JsonPropertyName("city")]
    public string City { get; set; } = string.Empty;

    /// <summary>开机自启（FR-W-SET-5）。</summary>
    [JsonPropertyName("autostart")]
    public bool Autostart { get; set; }

    /// <summary>自启时静默到托盘（写入 `--hidden`）。</summary>
    [JsonPropertyName("autostartHidden")]
    public bool AutostartHidden { get; set; } = true;

    /// <summary>关闭窗口行为：`tray` / `quit`；**默认最小化到托盘**（FR-W-SET-6）。</summary>
    [JsonPropertyName("closeBehavior")]
    public string CloseBehavior { get; set; } = "tray";

    /// <summary>全局快捷键（FR-W-SET-7）。</summary>
    [JsonPropertyName("globalShortcut")]
    public string GlobalShortcut { get; set; } = Contracts.ProtocolConstants.DefaultGlobalShortcut;

    /// <summary>全局快捷键是否启用。</summary>
    [JsonPropertyName("globalShortcutEnabled")]
    public bool GlobalShortcutEnabled { get; set; } = true;

    /// <summary>发送键：`enter` / `ctrl+enter`（FR-W-SET-8）。</summary>
    [JsonPropertyName("sendKey")]
    public string SendKey { get; set; } = "enter";

    /// <summary>五类通知分类开关（FR-W-NOTI-7）。</summary>
    [JsonPropertyName("notifications")]
    public NotificationSettings Notifications { get; set; } = new();

    /// <summary>勿扰时段（**必须支持跨午夜** —— FR-W-NOTI-7）。</summary>
    [JsonPropertyName("doNotDisturb")]
    public DoNotDisturbSettings DoNotDisturb { get; set; } = new();

    /// <summary>窗口尺寸与位置（FR-W-UI-6）。</summary>
    [JsonPropertyName("window")]
    public WindowSettings Window { get; set; } = new();

    /// <summary>上次登录的用户名（**不记密码** —— FR-W-AUTH-9）。</summary>
    [JsonPropertyName("lastUsername")]
    public string LastUsername { get; set; } = string.Empty;

    /// <summary>深浅色主题的窗口尺寸等持久化指纹（预留）。</summary>
    [JsonPropertyName("schemaNote")]
    public string SchemaNote { get; set; } = string.Empty;
}

/// <summary>五类通知开关（C-3 语义：聊天 / 问候 / 提醒 / 错误 / 进度）。</summary>
public sealed class NotificationSettings
{
    [JsonPropertyName("chat")]
    public bool Chat { get; set; } = true;

    [JsonPropertyName("greeting")]
    public bool Greeting { get; set; } = true;

    [JsonPropertyName("reminder")]
    public bool Reminder { get; set; } = true;

    [JsonPropertyName("error")]
    public bool Error { get; set; } = true;

    [JsonPropertyName("progress")]
    public bool Progress { get; set; } = true;
}

/// <summary>勿扰时段。跨午夜（如 23:00–08:00）必须正确判定。</summary>
public sealed class DoNotDisturbSettings
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    /// <summary>`HH:MM`。</summary>
    [JsonPropertyName("start")]
    public string Start { get; set; } = "23:00";

    /// <summary>`HH:MM`。</summary>
    [JsonPropertyName("end")]
    public string End { get; set; } = "08:00";

    /// <summary>
    /// 判断给定本地时间是否落在勿扰时段内。
    /// ⚠️ 必须支持跨午夜：当 <see cref="Start"/> &gt; <see cref="End"/> 时区间跨越 00:00。
    /// </summary>
    /// <remarks>方法不参与序列化（System.Text.Json 只序列化属性/字段），无需标注。</remarks>
    public bool ContainsLocal(TimeOnly now)
    {
        if (!Enabled)
        {
            return false;
        }

        if (!TryParseTime(Start, out var start) || !TryParseTime(End, out var end))
        {
            return false;
        }

        if (start == end)
        {
            // 起止相同：视为全天勿扰（跨午夜退化为整段）。
            return true;
        }

        return start < end
            ? now >= start && now < end
            : now >= start || now < end;
    }

    private static bool TryParseTime(string? value, out TimeOnly time)
        => TimeOnly.TryParseExact(
            value,
            "HH:mm",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None,
            out time);
}

/// <summary>窗口几何。</summary>
public sealed class WindowSettings
{
    [JsonPropertyName("width")]
    public double Width { get; set; } = 900;

    [JsonPropertyName("height")]
    public double Height { get; set; } = 720;

    /// <summary>为 <c>null</c> 表示未记录（首次启动居中）。</summary>
    [JsonPropertyName("x")]
    public double? X { get; set; }

    [JsonPropertyName("y")]
    public double? Y { get; set; }
}
