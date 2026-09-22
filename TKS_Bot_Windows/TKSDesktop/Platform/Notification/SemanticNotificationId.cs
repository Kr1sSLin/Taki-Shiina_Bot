namespace TKSDesktop.Contracts;

/// <summary>
/// 通知语义 ID（PRD §5.5 **C-3**）。
///
/// ⚠️ **基准是 Android 端**（`AppNotifier` 的渠道常量），**不是** Linux 端现值
///    （Linux 端 1004–1006 的语义与 Android 互换，属待修正项，本端**不得照抄**）。
/// ⚠️ Windows Toast 没有「渠道」概念，但**语义 ID 仍必须按本表分配**，
///    并使用「相同 ToastTag + 覆盖组」实现「同类通知替换而非堆叠」（FR-W-NOTI-3）。
/// ⚠️ 门禁 V-W-S5 会机械断言这 7 个值，改动即失败。
/// </summary>
public enum SemanticNotificationId
{
    /// <summary>聊天消息（立希的普通回复）。</summary>
    Chat = 1002,

    /// <summary>问候（`greetingScenario` ∈ morning / night）。</summary>
    Greeting = 1003,

    /// <summary>Bot 错误（`bot.error`）。</summary>
    Error = 1004,

    /// <summary>提醒（`timerInstruction` 本地排程触发）。</summary>
    Reminder = 1005,

    /// <summary>等级变动（升级 / 等级恢复）。</summary>
    Level = 1006,

    /// <summary>断签预警（`streak.warning`）。</summary>
    Streak = 1007,

    /// <summary>进度类（积分 / 补签卡等其他养成进度提示）。</summary>
    Progress = 1008,
}

public static class SemanticNotificationIds
{
    /// <summary>全部语义 ID（供机械校验与遍历，避免遗漏）。</summary>
    public static readonly IReadOnlyList<SemanticNotificationId> All =
    [
        SemanticNotificationId.Chat,
        SemanticNotificationId.Greeting,
        SemanticNotificationId.Error,
        SemanticNotificationId.Reminder,
        SemanticNotificationId.Level,
        SemanticNotificationId.Streak,
        SemanticNotificationId.Progress,
    ];

    /// <summary>Toast 覆盖组：同一语义 ID 的通知互相替换而非堆叠（FR-W-NOTI-3）。</summary>
    public static string ToastTag(this SemanticNotificationId id) => $"tks-{id.ToString().ToLowerInvariant()}";

    /// <summary>Toast 覆盖组名（同组内新通知替换旧通知）。</summary>
    public const string ToastGroup = "tks-notifications";
}
