namespace TKSDesktop.Core.Data.Records;

/// <summary><c>chat_attachments</c> 行（§9.2）。</summary>
/// <param name="AttachmentId">主键（UUID）。</param>
/// <param name="MessageId">
/// 所属消息；<see langword="null"/> = **草稿附件**（已选中但尚未发送，FR-W-IMG-3 / FR-W-IMG-5）。
/// 草稿必须持久化，否则应用在发送前退出会丢图。
/// </param>
/// <param name="SessionId">会话 ID。</param>
/// <param name="MimeType"><c>image/jpeg</c> / <c>image/png</c>。</param>
/// <param name="LocalPath">应用私有目录内的绝对路径（FR-W-SEC-6 前缀校验基准）。</param>
/// <param name="FileSize">字节数。</param>
/// <param name="Width">像素宽（可能未知）。</param>
/// <param name="Height">像素高（可能未知）。</param>
/// <param name="UploadState"><c>local</c> / 上传态。</param>
/// <param name="Timestamp">Unix 毫秒。</param>
public sealed record AttachmentRecord(
    string AttachmentId,
    string? MessageId,
    string SessionId,
    string MimeType,
    string LocalPath,
    long FileSize,
    int? Width,
    int? Height,
    string UploadState,
    long Timestamp);

/// <summary>附件占用统计（磁盘清理与「本地图片不可恢复」提示用）。</summary>
/// <param name="Count">附件行数。</param>
/// <param name="Bytes">占用字节总数。</param>
/// <param name="DraftCount">其中草稿附件（<c>message_id IS NULL</c>）行数。</param>
public sealed record AttachmentStats(int Count, long Bytes, int DraftCount);

/// <summary><c>bot_notifications</c> 行（§9.3）。</summary>
/// <param name="NotificationId">主键（错误码 + 时间戳 + 正文派生，天然幂等）。</param>
/// <param name="ErrorCode">服务端错误码。</param>
/// <param name="Message">正文（展示前须按 <c>ProtocolConstants.NotificationBodyMaxChars</c> 截断）。</param>
/// <param name="IsRead">是否已读。</param>
/// <param name="Timestamp">Unix 毫秒。</param>
public sealed record NotificationRecord(
    string NotificationId,
    string ErrorCode,
    string Message,
    bool IsRead,
    long Timestamp);

/// <summary><c>user_facts</c> 行（§9.4）。</summary>
/// <param name="FactId">主键。</param>
/// <param name="UserId">用户 ID。</param>
/// <param name="Fact">事实正文。</param>
/// <param name="Timestamp">Unix 毫秒。</param>
public sealed record FactRecord(string FactId, string UserId, string Fact, long Timestamp)
{
    public long Revision { get; init; }
    public string? RevisionId { get; init; }
    public long PermissionVersion { get; init; }
    public bool Enabled { get; init; } = true;
    public string Stage { get; init; } = "candidate";
    public string TruthStatus { get; init; } = "pending";
    public double Score { get; init; }
    public string? SourceMessageId { get; init; }
    public string? TemporalValidity { get; init; }
}

/// <summary><c>reminders</c> 行（§9.5）。</summary>
/// <param name="ReminderId">主键（去重键，FR-W-REM-3）。</param>
/// <param name="TargetTime"><c>HH:MM</c>（展示用，由服务端下发）。</param>
/// <param name="FireAt">触发时刻（Unix 毫秒，本地排程基准）。</param>
/// <param name="Text">提醒文本。</param>
/// <param name="Status"><c>pending</c> / <c>fired</c> / <c>cancelled</c>。</param>
/// <param name="CreatedAt">创建时刻（Unix 毫秒）。</param>
public sealed record ReminderRecord(
    string ReminderId,
    string TargetTime,
    long FireAt,
    string Text,
    string Status,
    long CreatedAt);

/// <summary>提醒写入结果。</summary>
/// <param name="Created">
/// <see langword="true"/> = 新建；<see langword="false"/> = 已存在，**保留原计划未覆盖**
/// （Android <c>ExistingWorkPolicy.KEEP</c> 语义，FR-W-REM-3）。
/// </param>
/// <param name="Record">库中当前行（新建时为刚插入的行）。</param>
public sealed record ReminderUpsertResult(bool Created, ReminderRecord Record);

/// <summary><c>user_progress_cache</c> 行（§9.6，camelCase 语义）。**仅为缓存**，非业务事实来源（FR-W-PROG-1）。</summary>
/// <param name="UserId">用户 ID。</param>
/// <param name="Balance">当前积分余额。</param>
/// <param name="BalanceUpdatedAt">余额更新时刻（Unix 毫秒）。</param>
/// <param name="LevelCode">等级码；默认等级为 <c>NONE</c> 或空串。</param>
/// <param name="LevelName">等级名（默认等级为空串 → UI 须中性兜底，FR-W-LV-3）。</param>
/// <param name="PrevLevelCode">上一等级码。</param>
/// <param name="HighestLevelCode">历史最高等级码。</param>
/// <param name="ContinuousDays">连续天数。</param>
/// <param name="NextLevelCode">下一等级码。</param>
/// <param name="NextLevelName">下一等级名。</param>
/// <param name="NextLevelThresholdDays">下一等级所需天数。</param>
/// <param name="DaysToNextLevel">距下一等级剩余天数。</param>
/// <param name="LastValidDate">服务端业务时区的 <c>YYYY-MM-DD</c>（**不得**用本地日期推算，FR-W-PROG-3）。</param>
/// <param name="GapDays">断签间隔天数。</param>
/// <param name="BreakDeadlineDate">断签截止日。</param>
/// <param name="IsDefaultLevel">是否为默认等级。</param>
/// <param name="LevelUpdatedAt">等级更新时刻。</param>
/// <param name="AvailableMakeupCards">可用补签卡数。</param>
/// <param name="SyncedAt">本地写入时刻（Unix 毫秒），用于判断缓存新鲜度。</param>
public sealed record ProgressCacheRecord(
    string UserId,
    int Balance,
    long BalanceUpdatedAt,
    string LevelCode,
    string LevelName,
    string? PrevLevelCode,
    string? HighestLevelCode,
    int ContinuousDays,
    string? NextLevelCode,
    string? NextLevelName,
    int? NextLevelThresholdDays,
    int? DaysToNextLevel,
    string? LastValidDate,
    int GapDays,
    string? BreakDeadlineDate,
    bool IsDefaultLevel,
    long? LevelUpdatedAt,
    int AvailableMakeupCards,
    long SyncedAt);

/// <summary><c>points_ledger_cache</c> 行（**snake_case**，直接映射服务端 DTO，FR-W-DB-4）。</summary>
/// <param name="Id">服务端流水 ID（**主键由服务端给定**，不能自增替换，否则分页会错位）。</param>
/// <param name="UserId">用户 ID。</param>
/// <param name="ChangeAmount">变动值（可负）。</param>
/// <param name="ReasonCode">原因码。</param>
/// <param name="BalanceAfter">变动后余额。</param>
/// <param name="RelatedItemId">关联物品 ID。</param>
/// <param name="IdempotencyKey">幂等键。</param>
/// <param name="CreatedAt">创建时刻（Unix 毫秒）。</param>
/// <param name="BusinessDate">业务日期 <c>YYYY-MM-DD</c>。</param>
public sealed record LedgerCacheRecord(
    long Id,
    string UserId,
    int ChangeAmount,
    string ReasonCode,
    int BalanceAfter,
    string? RelatedItemId,
    string? IdempotencyKey,
    long CreatedAt,
    string BusinessDate);

/// <summary><c>makeup_cards_cache</c> 行（**snake_case**，FR-W-DB-4）。</summary>
/// <param name="Id">服务端卡 ID。</param>
/// <param name="UserId">用户 ID。</param>
/// <param name="GrantedMonth">发放月份 <c>YYYY-MM</c>。</param>
/// <param name="Status"><c>AVAILABLE</c> / <c>USED</c>。</param>
/// <param name="UsedForDate">用于补签的日期。</param>
/// <param name="UsedAt">使用时刻。</param>
/// <param name="CreatedAt">创建时刻。</param>
public sealed record MakeupCardCacheRecord(
    long Id,
    string UserId,
    string GrantedMonth,
    string Status,
    string? UsedForDate,
    long? UsedAt,
    long CreatedAt);

/// <summary><c>makeup_candidates_cache</c> 行（camelCase 语义）。</summary>
/// <param name="Date">候选日期 <c>YYYY-MM-DD</c>（服务端业务时区）。</param>
/// <param name="DaysAgo">距今天数。</param>
/// <param name="SyncedAt">本地写入时刻。</param>
public sealed record MakeupCandidateCacheRecord(string Date, int DaysAgo, long SyncedAt);

/// <summary><c>level_config_cache</c> 行（snake_case 字段，EDGE-W-24 防御）。</summary>
/// <param name="LevelCode">等级码（空值必须被拒，不得入库）。</param>
/// <param name="LevelName">等级名。</param>
/// <param name="ThresholdDays">所需天数。</param>
/// <param name="SortOrder">展示顺序。</param>
/// <param name="SyncedAt">本地写入时刻。</param>
public sealed record LevelConfigCacheRecord(
    string LevelCode,
    string LevelName,
    int ThresholdDays,
    int SortOrder,
    long SyncedAt);
