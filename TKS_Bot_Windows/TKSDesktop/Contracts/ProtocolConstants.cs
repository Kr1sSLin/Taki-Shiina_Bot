namespace TKSDesktop.Contracts;

/// <summary>
/// 协议与后端行为常量（PRD §5.3.1 / §5.4 / §5.5 C-2、C-5）。
///
/// ⚠️ 契约冻结：本文件是 Windows 端常量落点，**数值必须与后端行为及另外两端一致**（V-W-C5
/// 要求「故意改一个常量必须让门禁失败」）。注释只引用**符号名**，不得引用其他端的具体行号
/// （PRD §5.5 C-2 的 V-S3 要求）。
/// </summary>
public static class ProtocolConstants
{
    /* ---- 连接与心跳（FR-W-CONN-2 / FR-W-CONN-3） ---- */

    /// <summary>心跳间隔 25 秒，与服务端保活约定一致（PRD §5.3.1）。</summary>
    public const int HeartbeatIntervalMs = 25_000;

    /// <summary>重连退避基数（`2^n` 秒）。</summary>
    public const int ReconnectBaseMs = 1_000;

    /// <summary>重连退避上限 60 秒。</summary>
    public const int ReconnectMaxMs = 60_000;

    /// <summary>重连最大尝试次数 15；达到后进入 Degraded 态。</summary>
    public const int ReconnectMaxAttempts = 15;

    /// <summary>WS 关闭码：鉴权失败（后端先 accept 再下发 auth.expired，随后以 4001 关闭）。</summary>
    public const int WsCloseInvalidToken = 4001;

    /* ---- 超时分级（PRD FR-W-NET-4，必须分档，不得统一成一个值） ---- */

    /// <summary>常规 REST 超时 30 秒。</summary>
    public const int RestTimeoutMs = 30_000;

    /// <summary>
    /// 互动 POST /interaction/send 超时 90 秒。
    /// ⚠️ **必须 &gt; 60 秒**：nginx `proxy_read_timeout` 为 60s，而服务端最长约 40s
    /// （25s 合并等待 + 15s AI 兜底）。取小于 60s 会在服务端仍在工作时误判失败。
    /// </summary>
    public const int InteractionTimeoutMs = 90_000;

    /// <summary>流式回复整轮超时 150 秒；**每收到一个 delta 重置计时**（PRD FR-W-CHAT-8）。</summary>
    public const int StreamingTimeoutMs = 150_000;

    /// <summary>连通性测试超时 10 秒（/healthz，仅快速反馈，不得阻塞 UI）。</summary>
    public const int ConnectivityTimeoutMs = 10_000;

    /// <summary>主动续签阈值：Access Token 剩余有效期 &lt; 60 秒时先续签（FR-W-AUTH-11）。</summary>
    public const int ProactiveRefreshThresholdMs = 60_000;

    /// <summary>免登录空闲上限 7 天（FR-W-AUTH-13，客户端本地强制，服务端 Refresh TTL 为 30 天）。</summary>
    public const long OfflineCredentialMaxIdleMs = 7L * 24 * 60 * 60 * 1000;

    /* ---- 服务端历史与同步（FR-W-SYNC-3 / FR-W-SYNC-10） ---- */

    /// <summary>历史同步单次 limit。</summary>
    public const int SyncLimit = 300;

    /// <summary>服务端每用户聊天历史上限，**仅作提示，客户端本地不截断**（FR-W-SYNC-10）。</summary>
    public const int ServerHistoryLimit = 300;

    /// <summary>首屏本地加载条数（FR-W-CHAT-16）。</summary>
    public const int LocalFirstPageSize = 50;

    /* ---- 图片约束（FR-W-IMG-2 / FR-W-IMG-11，与服务端 VISION_* 一致） ---- */

    /// <summary>单条消息最多 3 张图。</summary>
    public const int MaxImageCount = 3;

    /// <summary>单张图片最大 20MB。</summary>
    public const long MaxImageBytes = 20L * 1024 * 1024;

    /// <summary>
    /// 单次发送附件总量上限 24MB（FR-W-IMG-11 的 413 防御）。
    /// 依据：3×20MB base64 后约 80MB，超过 nginx 默认 `client_max_body_size 32m`；
    /// 24MB 为「base64 膨胀 + JSON 包装」预留余量后的阈值。
    /// </summary>
    public const long MaxTotalAttachmentBytes = 24L * 1024 * 1024;

    /// <summary>允许的图片 MIME（服务端 VISION_INVALID_MIME 判定集合）。</summary>
    public static readonly string[] AllowedImageMime = ["image/jpeg", "image/png"];

    /* ---- 提醒（FR-W-REM-4） ---- */

    /// <summary>启动时过期提醒处理阈值：超过 30 分钟丢弃，否则立即补发。</summary>
    public const long ReminderStaleMs = 30L * 60 * 1000;

    /* ---- 通知（FR-W-NOTI-6） ---- */

    /// <summary>通知正文截断长度（与 Linux 端一致）。</summary>
    public const int NotificationBodyMaxChars = 160;

    /* ---- 日志（FR-W-LOG-2 / FR-W-LOG-3） ---- */

    /// <summary>日志字符串截断阈值：超过该长度截断为前 64 字符。</summary>
    public const int LogRedactMaxChars = 400;

    /// <summary>日志截断后保留的前缀字符数。</summary>
    public const int LogRedactKeepChars = 64;

    /// <summary>日志按天滚动保留天数。</summary>
    public const int LogRetentionDays = 7;

    /// <summary>单个日志文件上限（字节）。</summary>
    public const long LogMaxFileBytes = 10L * 1024 * 1024;

    /// <summary>附件写入前要求的最小可用磁盘余量（EDGE-W-15）。</summary>
    public const long AttachmentFreeSpaceReserveBytes = 64L * 1024 * 1024;

    /* ---- 服务地址默认值（PRD §5.1） ---- */

    /// <summary>REST 基址默认值。</summary>
    public const string DefaultApiBaseUrl = "https://takishiinabot.top/api/v1/";

    /// <summary>WS 基址默认值，实际连接 `{wsBaseUrl}/ws/chat`。</summary>
    public const string DefaultWsBaseUrl = "wss://takishiinabot.top";

    /// <summary>WS 路径。</summary>
    public const string WsChatPath = "/ws/chat";

    /* ---- 单会话（后端 user_chat_history 为单一列表） ---- */

    /// <summary>固定会话 ID。</summary>
    public const string DefaultSessionId = "default_session";

    /* ---- C-5：历史分隔符 ---- */

    /// <summary>
    /// 服务端每日 06:00 插入的分隔标记（后端常量）。
    /// **必须**经 <see cref="IsHistorySeparator"/> 识别并渲染为日期分隔线，不得作为普通气泡（C-5 / FR-W-SYNC-8）。
    /// </summary>
    public const string HistorySeparator = "──── 新的一天 ────";

    /// <summary>
    /// 判断一条内容是否为「新的一天」分隔标记（C-5）。
    /// 支持前后空白容错：服务端写入为定值，但同步/清洗链路可能引入空白。
    /// </summary>
    public static bool IsHistorySeparator(string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return false;
        }

        return content.Trim() == HistorySeparator;
    }
    /* ---- FR-W-SYNC-9：历史行前缀 `【MM-DD HH:MM】` ---- */

    /// <summary>
    /// 服务端 <c>with_history_timestamp</c> 给模型的 `<c>【MM-DD HH:MM】</c>` 前缀模式；
    /// 渲染时必须剥离（FR-W-SYNC-9）。
    ///
    /// ⚠️ 与 <see cref="HistorySeparator"/> 同属**服务端协议字面量**（不是界面文案），
    /// 因此集中在本契约常量文件内 —— NFR-W-10 只管界面文案，此处是跨端契约值。
    /// </summary>
    public const string HistoryTimestampPrefixPattern = @"^\s*【\d{2}-\d{2}\s+\d{2}:\d{2}】\s*";

    /* ---- 默认全局快捷键（FR-W-DSK-2） ---- */

    /// <summary>默认全局快捷键 `Ctrl+Alt+T`。</summary>
    public const string DefaultGlobalShortcut = "Control+Alt+T";

    /* ---- AUMID（PRD §14.2 / §14.3） ---- */

    /// <summary>
    /// Application User Model ID。
    /// ⚠️ 必须与开始菜单快捷方式的 `AppUserModelID` 属性逐字一致，否则 Toast 弹不出来。
    /// 不使用带点的 appId 形式（避免与 Toast 注册不匹配）。
    /// </summary>
    public const string Aumid = "TKSDesktop";

    /* ---- 客户端自有错误码（PRD §5.3.3） ---- */

    public const string ClientErrorSendFailed = "SEND_FAILED";
    public const string ClientErrorTimeout = "TIMEOUT";
    public const string ClientErrorConnectionLost = "CONNECTION_LOST";

    /* ---- 消息状态（PRD §9.1） ---- */

    public const string StatusSending = "sending";
    public const string StatusSent = "sent";
    public const string StatusReceived = "received";
    public const string StatusStreaming = "streaming";
    public const string StatusError = "error";

    /* ---- 同步游标 key（FR-W-SYNC-3 / FR-W-SYNC-7：必须独立） ---- */

    public const string CursorKeyChat = "chat";
    public const string CursorKeyMemory = "memory";
}
