using System.Text.Json.Serialization;

namespace TKSDesktop.Contracts.Dtos;

/* ==========================================================================
 * 积分 · 等级 · 互动 · 补签卡 DTO（PRD §7.0）
 *
 * ⚠️ 字段名风格契约（§5.5 **C-8**，不可漂移）——**三处 snake_case 例外**：
 *    ① `/level/config` 的 `levels[]`
 *    ② `/points/history` 的 `items[]`
 *    ③ `/points/makeup-card*` 的卡记录
 *    其余**全部 camelCase**。
 * ⚠️ FR-W-PROTO-2：**逐字段手写 `[JsonPropertyName]`**；禁止全局命名策略转换
 *    （写错字段名不报错，只会静默取到默认值/空串 —— 另一端曾因此崩溃）。
 * ⚠️ 这些接口挂在 `ws_api` 上（与 WS 同进程），业务失败为 **HTTP 200 + `code != 0`**。
 * ========================================================================== */

/* -------------------------------------------------------------------------- */
/* 互动（camelCase）                                                             */
/* -------------------------------------------------------------------------- */

/// <summary>`GET /interaction/items` 的 `items[]`（**camelCase**）。</summary>
public sealed class InteractionItemDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>emoji 字符，**当前即有值**（FR-W-INT-9）。</summary>
    [JsonPropertyName("icon")]
    public string? Icon { get; set; }

    /// <summary>图片地址，**当前恒为空串**（美术资源未交付）→ 必须回退 emoji（FR-W-INT-9 / EDGE-W-23）。</summary>
    [JsonPropertyName("iconUrl")]
    public string? IconUrl { get; set; }

    [JsonPropertyName("costPoints")]
    public int CostPoints { get; set; }

    [JsonPropertyName("sortOrder")]
    public int SortOrder { get; set; }

    /// <summary>
    /// 是否买得起。**置灰依据必须用该字段**，不得用本地余额自算
    /// （本地缓存过期会导致误判 —— FR-W-INT-4）。
    /// </summary>
    [JsonPropertyName("affordable")]
    public bool Affordable { get; set; }
}

/// <summary>`GET /interaction/items` 响应 `data`。</summary>
public sealed class InteractionItemsDataDto
{
    [JsonPropertyName("balance")]
    public int Balance { get; set; }

    /// <summary>服务端不设每日上限，恒为 `null`（FR-W-INT-10）。</summary>
    [JsonPropertyName("dailyLimit")]
    public int? DailyLimit { get; set; }

    [JsonPropertyName("items")]
    public List<InteractionItemDto> Items { get; set; } = [];
}

/// <summary>`POST /interaction/send` 请求体。</summary>
public sealed class InteractionSendRequestDto
{
    [JsonPropertyName("itemId")]
    public string ItemId { get; set; } = string.Empty;

    /// <summary>幂等键；**重试必须复用**同一值（FR-W-INT-11）。</summary>
    [JsonPropertyName("requestId")]
    public string RequestId { get; set; } = string.Empty;

    /// <summary>可选附言（§7.1a）：输入框有文字时随送礼一并提交，服务端只回一条。</summary>
    [JsonPropertyName("text")]
    public string? Text { get; set; }
}

/// <summary>`POST /interaction/send` 成功/失败响应 `data`（失败同样 HTTP 200）。</summary>
public sealed class InteractionSendDataDto
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("itemId")]
    public string? ItemId { get; set; }

    [JsonPropertyName("requestId")]
    public string? RequestId { get; set; }

    [JsonPropertyName("balance")]
    public int? Balance { get; set; }

    [JsonPropertyName("charged")]
    public int? Charged { get; set; }

    [JsonPropertyName("refunded")]
    public bool? Refunded { get; set; }

    [JsonPropertyName("duplicate")]
    public bool? Duplicate { get; set; }

    [JsonPropertyName("item")]
    public InteractionSendItemDto? Item { get; set; }

    /// <summary>兜底通道文案；主通道是 WS `chat.reply.stream`。</summary>
    [JsonPropertyName("reply")]
    public string? Reply { get; set; }

    [JsonPropertyName("messageId")]
    public string? MessageId { get; set; }

    [JsonPropertyName("timerInstruction")]
    public TimerInstructionDto? TimerInstruction { get; set; }

    /// <summary>被服务端摘走并合进本次回复的防抖缓冲消息数（§7.1a）。</summary>
    [JsonPropertyName("mergedCount")]
    public int? MergedCount { get; set; }

    /// <summary>
    /// ⚠️ EDGE-W-22：这些用户消息**不会**收到常规 `done.requestIds` 送达确认，
    /// 必须用本字段批量把它们从「发送中」置为「已送达」。
    /// </summary>
    [JsonPropertyName("mergedRequestIds")]
    public List<string>? MergedRequestIds { get; set; }

    /// <summary>`40204` 时存在的兜底文案（已退款）。</summary>
    [JsonPropertyName("fallbackText")]
    public string? FallbackText { get; set; }

    /// <summary>失败响应可能附带（宽松读取，缺失不报错）。</summary>
    [JsonPropertyName("errorCode")]
    public int? ErrorCode { get; set; }

    [JsonPropertyName("messageText")]
    public string? MessageText { get; set; }
}

/// <summary>互动响应里内嵌的物品摘要。</summary>
public sealed class InteractionSendItemDto
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("icon")]
    public string? Icon { get; set; }

    [JsonPropertyName("costPoints")]
    public int? CostPoints { get; set; }
}

/* -------------------------------------------------------------------------- */
/* 积分（camelCase）                                                             */
/* -------------------------------------------------------------------------- */

/// <summary>`GET /points/balance` 响应 `data`。</summary>
public sealed class PointsBalanceDataDto
{
    [JsonPropertyName("balance")]
    public int Balance { get; set; }

    [JsonPropertyName("updatedAt")]
    public long UpdatedAt { get; set; }
}

/// <summary>补签卡汇总（**camelCase**）。</summary>
public sealed class MakeupCardSummaryDto
{
    [JsonPropertyName("available")]
    public int Available { get; set; }

    [JsonPropertyName("used")]
    public int Used { get; set; }

    [JsonPropertyName("totalGranted")]
    public int TotalGranted { get; set; }

    /// <summary>上限，**由服务端下发，不得硬编码**（FR-W-MC-1）。</summary>
    [JsonPropertyName("maxAvailable")]
    public int MaxAvailable { get; set; }

    /// <summary>每月发放张数，**由服务端下发，不得硬编码**（FR-W-MC-1）。</summary>
    [JsonPropertyName("monthlyGrant")]
    public int MonthlyGrant { get; set; }

    [JsonPropertyName("lastGrantedMonth")]
    public string? LastGrantedMonth { get; set; }

    [JsonPropertyName("currentMonthGranted")]
    public bool CurrentMonthGranted { get; set; }

    [JsonPropertyName("atLimit")]
    public bool AtLimit { get; set; }
}

/// <summary>等级状态对象（`/points/overview` 的 `level`、`/level/status`；**camelCase**）。</summary>
public sealed class LevelStatusDto
{
    [JsonPropertyName("levelCode")]
    public string? LevelCode { get; set; }

    /// <summary>`levelCode = NONE` 时为**空字符串** + `isDefaultLevel = true` → 必须中性兜底（FR-W-LV-3）。</summary>
    [JsonPropertyName("levelName")]
    public string? LevelName { get; set; }

    [JsonPropertyName("prevLevelCode")]
    public string? PrevLevelCode { get; set; }

    [JsonPropertyName("continuousDays")]
    public int ContinuousDays { get; set; }

    /// <summary>`UPGRADE` / `RESTORE` / `RESET`（三态，决定反馈样式强度）。</summary>
    [JsonPropertyName("changeType")]
    public string? ChangeType { get; set; }

    [JsonPropertyName("changeSource")]
    public string? ChangeSource { get; set; }

    [JsonPropertyName("highestLevelCode")]
    public string? HighestLevelCode { get; set; }

    /// <summary>服务端业务时区的 `YYYY-MM-DD`；**不得用本地日期推算**（FR-W-PROG-3）。</summary>
    [JsonPropertyName("lastValidDate")]
    public string? LastValidDate { get; set; }

    [JsonPropertyName("gapDays")]
    public int GapDays { get; set; }

    [JsonPropertyName("breakDeadlineDate")]
    public string? BreakDeadlineDate { get; set; }

    [JsonPropertyName("nextLevelCode")]
    public string? NextLevelCode { get; set; }

    [JsonPropertyName("nextLevelName")]
    public string? NextLevelName { get; set; }

    [JsonPropertyName("nextLevelThresholdDays")]
    public int? NextLevelThresholdDays { get; set; }

    [JsonPropertyName("daysToNextLevel")]
    public int? DaysToNextLevel { get; set; }

    [JsonPropertyName("levelUpdatedAt")]
    public long? LevelUpdatedAt { get; set; }

    [JsonPropertyName("isDefaultLevel")]
    public bool IsDefaultLevel { get; set; }

    [JsonPropertyName("availableMakeupCards")]
    public int? AvailableMakeupCards { get; set; }
}

/// <summary>`GET /points/overview` 响应 `data`（首屏聚合，个人中心首选 —— FR-W-PROG-5）。</summary>
public sealed class PointsOverviewDataDto
{
    [JsonPropertyName("balance")]
    public int Balance { get; set; }

    [JsonPropertyName("balanceUpdatedAt")]
    public long BalanceUpdatedAt { get; set; }

    [JsonPropertyName("level")]
    public LevelStatusDto? Level { get; set; }

    [JsonPropertyName("makeupCard")]
    public MakeupCardSummaryDto? MakeupCard { get; set; }
}

/* -------------------------------------------------------------------------- */
/* §7.0 三处 snake_case 例外                                                     */
/* -------------------------------------------------------------------------- */

/// <summary>
/// `GET /level/config` 的 `levels[]` —— **snake_case 例外 ①**（V-W-S3 逐字断言）。
/// </summary>
public sealed class LevelConfigEntryDto
{
    [JsonPropertyName("level_code")]
    public string? LevelCode { get; set; }

    [JsonPropertyName("level_name")]
    public string? LevelName { get; set; }

    [JsonPropertyName("threshold_days")]
    public int ThresholdDays { get; set; }

    [JsonPropertyName("sort_order")]
    public int SortOrder { get; set; }
}

/// <summary>`GET /level/config` 响应 `data`（外层字段为 camelCase，仅 `levels[]` 是 snake_case）。</summary>
public sealed class LevelConfigDataDto
{
    [JsonPropertyName("levels")]
    public List<LevelConfigEntryDto> Levels { get; set; } = [];

    [JsonPropertyName("defaultLevelCode")]
    public string? DefaultLevelCode { get; set; }

    [JsonPropertyName("defaultLevelName")]
    public string? DefaultLevelName { get; set; }

    [JsonPropertyName("makeupCardMax")]
    public int? MakeupCardMax { get; set; }

    [JsonPropertyName("breakGapDays")]
    public int? BreakGapDays { get; set; }

    [JsonPropertyName("warningGapDays")]
    public List<int>? WarningGapDays { get; set; }
}

/// <summary>
/// `GET /points/history` 的 `items[]` —— **snake_case 例外 ②**（V-W-S3 逐字断言）。
/// </summary>
public sealed class PointsLedgerItemDto
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("user_id")]
    public string? UserId { get; set; }

    [JsonPropertyName("change_amount")]
    public int ChangeAmount { get; set; }

    [JsonPropertyName("reason_code")]
    public string? ReasonCode { get; set; }

    [JsonPropertyName("balance_after")]
    public int BalanceAfter { get; set; }

    [JsonPropertyName("related_item_id")]
    public string? RelatedItemId { get; set; }

    [JsonPropertyName("idempotency_key")]
    public string? IdempotencyKey { get; set; }

    [JsonPropertyName("created_at")]
    public long CreatedAt { get; set; }

    [JsonPropertyName("business_date")]
    public string? BusinessDate { get; set; }
}

/// <summary>分页响应 `data`（外层 camelCase）。</summary>
public sealed class PagedDataDto<T>
{
    [JsonPropertyName("items")]
    public List<T> Items { get; set; } = [];

    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("page")]
    public int Page { get; set; }

    [JsonPropertyName("pageSize")]
    public int PageSize { get; set; }

    [JsonPropertyName("hasMore")]
    public bool HasMore { get; set; }
}

/// <summary>
/// `/points/makeup-card*` 的卡记录 —— **snake_case 例外 ③**（V-W-S3 逐字断言）。
/// </summary>
public sealed class MakeupCardRecordDto
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("user_id")]
    public string? UserId { get; set; }

    [JsonPropertyName("granted_month")]
    public string? GrantedMonth { get; set; }

    /// <summary>`AVAILABLE` / `USED`。</summary>
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("used_for_date")]
    public string? UsedForDate { get; set; }

    [JsonPropertyName("used_at")]
    public long? UsedAt { get; set; }

    [JsonPropertyName("created_at")]
    public long CreatedAt { get; set; }
}

/// <summary>补签候选日期（`GET /points/makeup-card/candidates` 的 `items[]`；camelCase）。</summary>
public sealed class MakeupCandidateDto
{
    /// <summary>`YYYY-MM-DD`，**服务端业务时区**；日历必须以此为准，不得本地推算（FR-W-MC-2 / FR-W-PROG-3）。</summary>
    [JsonPropertyName("date")]
    public string Date { get; set; } = string.Empty;

    [JsonPropertyName("daysAgo")]
    public int DaysAgo { get; set; }
}

/// <summary>`GET /points/makeup-card/candidates` 响应 `data`。</summary>
public sealed class MakeupCandidatesDataDto
{
    [JsonPropertyName("items")]
    public List<MakeupCandidateDto> Items { get; set; } = [];

    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("firstActivityDate")]
    public string? FirstActivityDate { get; set; }

    [JsonPropertyName("available")]
    public int Available { get; set; }
}

/// <summary>`POST /points/makeup-card/use` 请求体。</summary>
public sealed class MakeupCardUseRequestDto
{
    [JsonPropertyName("targetDate")]
    public string TargetDate { get; set; } = string.Empty;
}

/// <summary>`POST /points/makeup-card/use` 响应 `data`。</summary>
public sealed class MakeupCardUseDataDto
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("availableCards")]
    public int? AvailableCards { get; set; }

    [JsonPropertyName("targetDate")]
    public string? TargetDate { get; set; }

    [JsonPropertyName("card")]
    public MakeupCardRecordDto? Card { get; set; }

    /// <summary>依据其 `changeType` 决定反馈样式：`RESTORE` 克制、`UPGRADE` 才庆祝（FR-W-MC-5）。</summary>
    [JsonPropertyName("level")]
    public LevelStatusDto? Level { get; set; }
}
