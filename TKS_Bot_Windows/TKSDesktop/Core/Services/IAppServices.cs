using TKSDesktop.Contracts;
using TKSDesktop.Contracts.Dtos;

namespace TKSDesktop.Core.Services;

/// <summary>
/// 服务层接口冻结（PRD §15「Contracts 必须先冻结再并行开发」）。
///
/// ⚠️ 本文件是**唯一写者**产物：并行实现方**只实现**这些接口，**不得**修改签名。
///    所有接口位于 `Core` 层，**不得**引用 `System.Windows.*`（NFR-W-14 / V-W-S1）。
/// </summary>

/// <summary>连接状态（PRD §10.1 状态机）。</summary>
public enum ConnectionStatus
{
    Unauthenticated,
    Connecting,
    Connected,
    Reconnecting,
    Refreshing,
    Degraded,
    Disconnected,
}

/// <summary>连接状态快照。</summary>
public sealed record ConnectionSnapshot(
    ConnectionStatus Status,
    int Attempt,
    string? LastError,
    bool Degraded);

/// <summary>聊天界面的输入/思考状态（FR-W-CHAT-14）。</summary>
public sealed record ChatTypingState(bool Typing, string? Stage, double? DebounceWindowSec);

/// <summary>提醒记录（§9.6 `reminders`）。</summary>
public sealed record ReminderInfo(
    string ReminderId,
    string TargetTime,
    long FireAt,
    string Text,
    string Status,
    long CreatedAt);

/// <summary>一条待展示的消息（供 UI 绑定；`Core` 不得引用 `System.Windows.*`）。</summary>
public sealed record ChatMessageView(
    string MessageId,
    string Role,
    string ContentType,
    string Content,
    string Status,
    long Timestamp,
    string? ErrorCode,
    string? InteractionItemName,
    string? InteractionItemIcon,
    bool InteractionFailed,
    bool IsHistorySeparator,
    IReadOnlyList<string> AttachmentPaths);

/// <summary>附件草稿（已复制进私有目录、尚未绑定消息）。</summary>
public sealed record AttachmentDraft(
    string AttachmentId,
    string MimeType,
    string LocalPath,
    long FileSize,
    int? Width,
    int? Height);

/// <summary>
/// 对话核心（FR-W-CHAT-*）。
/// </summary>
public interface IChatService
{
    /// <summary>连接状态变化。</summary>
    event EventHandler<ConnectionSnapshot>? ConnectionChanged;

    /// <summary>输入/思考状态变化。</summary>
    event EventHandler<ChatTypingState>? TypingChanged;

    /// <summary>消息列表发生变化（新增 / 更新；实现方须保证在 UI 线程投递）。</summary>
    event EventHandler<ChatMessageView>? MessageChanged;

    /// <summary>消息被移除（流式占位被最终消息替换等）。</summary>
    event EventHandler<string>? MessageRemoved;

    /// <summary>本地会话已清空（设置页清除数据后同步清理当前界面）。</summary>
    event EventHandler? ConversationCleared;

    /// <summary>需要 UI 提示一条轻量信息（已排队 / 超时 / 未连接等），值为 i18n key。</summary>
    event EventHandler<string>? NoticeRaised;

    /// <summary>请求滚动定位到某条消息（点击通知后 —— FR-W-NOTI-4）。</summary>
    event EventHandler<string>? ScrollToMessageRequested;

    /// <summary>当前连接状态。</summary>
    ConnectionSnapshot State { get; }

    /// <summary>
    /// 发送一条消息（FR-W-CHAT-2/3/4）。
    /// <paramref name="requestId"/> 同时作为本地主键、WS 顶层 requestId 与幂等键。
    /// 未连接时**必须拒绝**并返回失败（不做离线队列 —— EDGE-W-6）。
    /// </summary>
    Task<SendResult> SendAsync(string requestId, string? text, IReadOnlyList<AttachmentDraft> attachments);

    /// <summary>用户主动选择的单次 REST 文本发送，仅在 WS 达重连上限时开放。</summary>
    Task<SendResult> SendFallbackAsync(string requestId, string text);

    /// <summary>重发一条失败的用户消息（**复用同一 requestId** —— FR-W-CHAT-11）。</summary>
    Task<SendResult> RetryAsync(string messageId);

    /// <summary>手动重连（置 full-sync 标志 —— FR-W-CONN-6）。</summary>
    Task ReconnectAsync();

    /// <summary>删除本地消息（右键菜单「删除」）。</summary>
    Task DeleteLocalAsync(string messageId);

    /// <summary>清空本地会话（FR-W-CHAT-12：必须与设置页共用同一实现，并推进游标）。</summary>
    Task ClearLocalConversationAsync();

    /// <summary>搜索本地消息（FR-W-CHAT-17）。</summary>
    Task<IReadOnlyList<ChatMessageView>> SearchAsync(string term, int limit);
}

/// <summary>发送结果。</summary>
public sealed record SendResult(bool Success, string? ErrorCode, string? I18nKey);

/// <summary>
/// 同步（FR-W-SYNC-*）。
/// </summary>
public interface ISyncService
{
    /// <summary>历史已落库，界面应重新合并本地消息。</summary>
    event EventHandler? ChatSynchronized;

    /// <summary>同步失败事件（FR-W-SYNC-6：失败必须**在 UI 可见**）。</summary>
    event EventHandler<SyncFailure>? SyncFailed;

    /// <summary>增量同步（`since = max(本地最大 timestamp, 游标)`，limit=300 —— FR-W-SYNC-3 / 陷阱 10）。</summary>
    Task<int> SyncIncrementalAsync(CancellationToken ct = default);

    /// <summary>全量同步（`since=0`；手动重连 / 首次登录 / 上次失败时 —— FR-W-SYNC-4）。</summary>
    Task<int> SyncFullAsync(CancellationToken ct = default);

    /// <summary>用户事实（记忆）独立游标同步（FR-W-SYNC-7）。</summary>
    Task<int> SyncFactsAsync(CancellationToken ct = default);
}

/// <summary>同步失败信息。</summary>
public sealed record SyncFailure(string Stage, string? ErrorCode, string I18nKey);

/// <summary>积分/等级/补签卡状态（首屏聚合，FR-W-PROG-5）。</summary>
public sealed record ProgressSnapshot(
    int Balance,
    string LevelCode,
    string LevelName,
    int ContinuousDays,
    int? DaysToNextLevel,
    int? NextLevelThresholdDays,
    string? NextLevelName,
    string? BreakDeadlineDate,
    int GapDays,
    bool IsDefaultLevel,
    int AvailableMakeupCards,
    long SyncedAt,
    bool IsOfflineCache);

/// <summary>养成层（FR-W-PT/LV/MC/INT/PROG）。</summary>
public interface IGamificationService
{
    /// <summary>进度变化（积分 / 等级 / 补签卡）—— UI 据此刷新个人中心。</summary>
    event EventHandler<ProgressSnapshot>? ProgressChanged;

    /// <summary>升级 / 恢复 / 回落反馈（FR-W-LV-4/5/5a）。</summary>
    event EventHandler<LevelChangeFeedback>? LevelChanged;

    /// <summary>断签预警（FR-W-LV-6）。</summary>
    event EventHandler<StreakWarningPayloadDto>? StreakWarning;

    /// <summary>当前进度快照（离线时返回缓存并标记 <c>IsOfflineCache</c> —— FR-W-PROG-4）。</summary>
    ProgressSnapshot? Current { get; }

    /// <summary>首屏拉取（`GET /points/overview` 一次拿全量）。</summary>
    Task<ProgressSnapshot> RefreshOverviewAsync(CancellationToken ct = default);

    /// <summary>互动菜单（`GET /interaction/items`；置灰用服务端 `affordable` —— FR-W-INT-4）。</summary>
    Task<InteractionItemsDataDto> GetInteractionItemsAsync(CancellationToken ct = default);

    /// <summary>送出互动（幂等 `requestId`；超时 90s —— FR-W-INT-8/11）。</summary>
    Task<InteractionSendOutcome> SendInteractionAsync(
        string itemId,
        string requestId,
        string? text,
        CancellationToken ct = default);

    /// <summary>积分流水分页。</summary>
    Task<PagedDataDto<PointsLedgerItemDto>> GetPointsHistoryAsync(
        int page,
        int pageSize,
        string? reasonCode = null,
        CancellationToken ct = default);

    /// <summary>等级配置（驱动等级说明页；`snake_case` 例外 ①）。</summary>
    Task<LevelConfigDataDto> GetLevelConfigAsync(CancellationToken ct = default);

    /// <summary>补签卡汇总。</summary>
    Task<MakeupCardSummaryDto> GetMakeupCardAsync(CancellationToken ct = default);

    /// <summary>补签候选日期（**补签日历唯一数据源**，不得本地推算 —— FR-W-MC-2）。</summary>
    Task<MakeupCandidatesDataDto> GetMakeupCandidatesAsync(int limit = 120, CancellationToken ct = default);

    /// <summary>使用补签卡（**必须用户主动点击** + 二次确认 —— FR-W-MC-3）。</summary>
    Task<MakeupCardUseOutcome> UseMakeupCardAsync(string targetDate, CancellationToken ct = default);

    /// <summary>补签卡流水。</summary>
    Task<PagedDataDto<MakeupCardRecordDto>> GetMakeupHistoryAsync(
        int page,
        int pageSize,
        CancellationToken ct = default);
}

/// <summary>互动送出结果。</summary>
public sealed record InteractionSendOutcome(
    bool Success,
    int? ErrorCode,
    string? I18nKey,
    int? Balance,
    bool Refunded,
    string? FallbackText,
    IReadOnlyList<string> MergedRequestIds,
    IReadOnlyList<string> RequestIdsToMarkDelivered);

/// <summary>等级变更反馈强度（决定 UI 样式）。</summary>
public sealed record LevelChangeFeedback(
    string ChangeType,
    string LevelCode,
    string LevelName,
    int ContinuousDays,
    string? NextLevelName,
    int? DaysToNextLevel,
    bool PlayCelebration,
    bool AffectsPointsBalance);

/// <summary>补签结果。</summary>
public sealed record MakeupCardUseOutcome(
    bool Success,
    int? ErrorCode,
    string? I18nKey,
    int? AvailableCards,
    LevelChangeFeedback? LevelFeedback);

/// <summary>提醒调度（FR-W-REM-*）。</summary>
public interface IReminderScheduler
{
    /// <summary>由 `timerInstruction` 排程（去重键 KEEP 语义 —— FR-W-REM-3）。</summary>
    Task ScheduleAsync(
        IReadOnlyList<string> requestIds,
        TimerInstructionDto instruction,
        CancellationToken ct = default);

    /// <summary>启动时装载未触发提醒；过期 &gt;30 分钟丢弃、≤30 分钟立即补发（FR-W-REM-4）。</summary>
    Task RestoreAsync(CancellationToken ct = default);

    /// <summary>唤醒后重新校验并补发（FR-W-REM-7 / EDGE-W-12）。</summary>
    Task ResendOverdueAsync(CancellationToken ct = default);

    /// <summary>已排程提醒列表（设置页 —— FR-W-REM-6）。</summary>
    Task<IReadOnlyList<ReminderInfo>> ListPendingAsync(CancellationToken ct = default);

    /// <summary>取消一条提醒（FR-W-REM-6）。</summary>
    Task CancelAsync(string reminderId, CancellationToken ct = default);
}

/// <summary>通知分类（对应 C-3 的语义 ID）。</summary>
public enum NotificationCategory
{
    Chat,
    Greeting,
    Reminder,
    Error,
    Progress,
}

/// <summary>通知服务（FR-W-NOTI-*）。</summary>
public interface IUserNotificationService
{
    /// <summary>
    /// 按分类推送通知。
    /// 实现必须处理：① 窗口前台聚焦时**不弹**聊天类通知（FR-W-NOTI-2）；
    /// ② 分类开关（FR-W-NOTI-7）；③ 勿扰时段（**支持跨午夜**）；
    /// ④ 正文按 160 字符截断（FR-W-NOTI-6）；⑤ 系统勿扰时抑制但**仍收消息**（FR-W-DSK-10）。
    /// </summary>
    void Notify(NotificationCategory category, string title, string body, string? messageId = null);

    /// <summary>当前窗口是否处于前台且聚焦（由 UI 层更新）。</summary>
    bool IsWindowFocused { get; set; }

    /// <summary>应用回到前台：清除消息类通知（FR-W-NOTI-5）。</summary>
    void ClearMessageNotifications();
}

/// <summary>图片/附件服务（FR-W-IMG-*）。</summary>
public interface IMediaService
{
    /// <summary>校验并复制图片到私有目录，返回草稿附件（FR-W-IMG-2/5）。</summary>
    Task<AttachmentDraftResult> AddFromFileAsync(string sourcePath, CancellationToken ct = default);

    /// <summary>由剪贴板 PNG 字节添加附件（FR-W-IMG-8）。</summary>
    Task<AttachmentDraftResult> AddFromClipboardAsync(CancellationToken ct = default);

    /// <summary>当前草稿附件。</summary>
    IReadOnlyList<AttachmentDraft> Drafts { get; }

    /// <summary>移除一张草稿附件（同时删除私有目录文件）。</summary>
    Task RemoveDraftAsync(string attachmentId, CancellationToken ct = default);

    /// <summary>清空草稿（发送成功后）。</summary>
    Task ClearDraftsAsync(CancellationToken ct = default);

    /// <summary>启动时恢复未发送的草稿附件（§9.2：`message_id` 允许 NULL）。</summary>
    Task RestoreDraftsAsync(CancellationToken ct = default);

    /// <summary>附件目录占用统计（FR-W-SET-11）。</summary>
    Task<long> GetAttachmentsSizeAsync(CancellationToken ct = default);

    /// <summary>清理超过 N 天的本地附件（FR-W-SET-11）。</summary>
    Task<int> CleanupAttachmentsAsync(int olderThanDays, CancellationToken ct = default);
}

/// <summary>草稿附件添加结果。</summary>
public sealed record AttachmentDraftResult(bool Success, AttachmentDraft? Draft, string? ErrorI18nKey);

/// <summary>设置应用服务（供 ViewModel 使用；实现位于 UI 层，负责落盘与平台联动）。</summary>
public interface ISettingsService
{
    event EventHandler? SettingsChanged;

    App.AppSettings Current { get; }

    /// <summary>保存设置并应用联动（自启 / 快捷键 / 主题）。</summary>
    Task SaveAsync(App.AppSettings settings, CancellationToken ct = default);

    /// <summary>主题是否生效为深色（含「跟随系统」解析结果）。</summary>
    bool IsDarkThemeEffective { get; }
}
