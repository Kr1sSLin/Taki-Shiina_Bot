using Microsoft.Extensions.Logging;
using TKSDesktop.App;
using TKSDesktop.Contracts;
using TKSDesktop.Core.Platform;

namespace TKSDesktop.Core.Services;

/// <summary>
/// 通知服务（PRD §6.7 FR-W-NOTI-1..9 / FR-W-DSK-10）。
///
/// 职责：把「通知分类」翻译为**语义 ID（C-3 的 1002–1008）**并交给
/// <see cref="INotificationPresenter"/>，同时集中执行全部抑制规则：
/// <list type="number">
///   <item><b>窗口前台聚焦时不弹聊天类通知</b>（FR-W-NOTI-2）—— 提醒 / 错误类不受此限制；</item>
///   <item><b>分类开关</b>（FR-W-NOTI-7）；</item>
///   <item><b>勿扰时段</b>（跨午夜必须正确，FR-W-NOTI-7）；</item>
///   <item><b>正文按 160 字符截断</b>（FR-W-NOTI-6）；</item>
///   <item><b>系统「专注助手 / 勿扰」时抑制 Toast 但仍正常收消息</b>（FR-W-DSK-10）；</item>
///   <item>同类通知**替换而非堆叠**（FR-W-NOTI-3，由语义 ID → ToastTag 实现）。</item>
/// </list>
///
/// ⚠️ 本类**不做**任何网络或落库副作用；只负责「是否弹、弹什么」。
/// </summary>
public sealed class UserNotificationService : IUserNotificationService
{
    private readonly INotificationPresenter _presenter;
    private readonly SettingsStore _settings;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<UserNotificationService> _logger;

    public UserNotificationService(
        INotificationPresenter presenter,
        SettingsStore settings,
        TimeProvider timeProvider,
        ILogger<UserNotificationService> logger)
    {
        _presenter = presenter;
        _settings = settings;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool IsWindowFocused { get; set; }

    /// <inheritdoc />
    public void Notify(NotificationCategory category, string title, string body, string? messageId = null)
    {
        var settings = _settings.Current;

        // ① 窗口前台聚焦时**不弹聊天类**通知（FR-W-NOTI-2）。
        //    提醒 / 错误类在窗口聚焦时也要弹（它们在通知中心有独立价值）。
        if (IsWindowFocused && category is NotificationCategory.Chat or NotificationCategory.Greeting)
        {
            return;
        }

        // ② 分类开关（FR-W-NOTI-7）。
        if (!IsCategoryEnabled(category, settings))
        {
            return;
        }

        // ③ 勿扰时段（支持跨午夜 —— FR-W-NOTI-7）。勿扰期间**仍正常收消息**，只是不弹。
        var now = TimeOnly.FromDateTime(_timeProvider.GetLocalNow().DateTime);
        if (settings.DoNotDisturb.ContainsLocal(now))
        {
            return;
        }

        // ④ 系统勿扰（专注助手）：抑制 Toast，但仍正常收消息（FR-W-DSK-10）。
        if (_presenter.IsSystemDoNotDisturbActive())
        {
            return;
        }

        var semanticId = ToSemanticId(category);

        // ⑤ 正文截断 160 字符（FR-W-NOTI-6）。
        var truncated = Truncate(body, ProtocolConstants.NotificationBodyMaxChars);

        try
        {
            _presenter.Present(semanticId, title, truncated, messageId);
        }
        catch (Exception ex)
        {
            // 通知失败绝不影响消息接收与落库（NFR-W-8：能力不可用必须降级而非崩溃）。
            _logger.LogWarning(ex, "通知呈现失败（分类={Category}）", category);
        }
    }

    /// <inheritdoc />
    public void ClearMessageNotifications()
    {
        // FR-W-NOTI-5：清除消息类（Chat / Greeting）；提醒 / 错误类**不清**（通知中心有价值）。
        try
        {
            _presenter.ClearMessageNotifications();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "清除消息类通知失败");
        }
    }

    /// <summary>
    /// 通知分类 → 语义 ID（C-3）。**基准是 Android 端**。
    /// </summary>
    public static SemanticNotificationId ToSemanticId(NotificationCategory category) => category switch
    {
        NotificationCategory.Chat => SemanticNotificationId.Chat,
        NotificationCategory.Greeting => SemanticNotificationId.Greeting,
        NotificationCategory.Reminder => SemanticNotificationId.Reminder,
        NotificationCategory.Error => SemanticNotificationId.Error,
        NotificationCategory.Progress => SemanticNotificationId.Progress,
        _ => SemanticNotificationId.Progress,
    };

    private static bool IsCategoryEnabled(NotificationCategory category, AppSettings settings) => category switch
    {
        NotificationCategory.Chat => settings.Notifications.Chat,
        NotificationCategory.Greeting => settings.Notifications.Greeting,
        NotificationCategory.Reminder => settings.Notifications.Reminder,
        NotificationCategory.Error => settings.Notifications.Error,
        NotificationCategory.Progress => settings.Notifications.Progress,
        _ => true,
    };

    /// <summary>按字符截断（FR-W-NOTI-6）；完整内容在应用内可见。</summary>
    public static string Truncate(string? body, int maxChars)
    {
        if (string.IsNullOrEmpty(body))
        {
            return string.Empty;
        }

        return body.Length <= maxChars ? body : body[..maxChars];
    }
}
