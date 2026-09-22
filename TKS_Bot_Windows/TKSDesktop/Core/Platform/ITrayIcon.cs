namespace TKSDesktop.Core.Platform;

/// <summary>托盘图标状态（FR-W-CONN-9 / FR-W-DSK-1）。</summary>
public enum TrayState
{
    /// <summary>未登录或无凭据。</summary>
    Offline,

    /// <summary>已连接。</summary>
    Online,

    /// <summary>连接中 / 重连中。</summary>
    Connecting,

    /// <summary>已断开。</summary>
    Disconnected,

    /// <summary>有未读消息（在线 + 未读标记）。</summary>
    Unread,
}

/// <summary>
/// 系统托盘（PRD §3.5 / FR-W-DSK-1）。
///
/// ⚠️ EDGE-W-9：注册失败（`Shell_NotifyIcon` 失败）时：
///    ① 捕获失败并记日志；② 把「关闭窗口行为」**临时强制为「直接退出」**；
///    ③ 设置页提示「托盘不可用，已临时改为关闭即退出」。
/// ⚠️ EDGE-W-9：Explorer 重启后**必须重新注册**托盘图标（监听 `TaskbarCreated` 消息）。
/// </summary>
public interface ITrayIcon
{
    /// <summary>托盘是否可用（注册成功）。不可用时 UI 必须如实展示（NFR-W-8）。</summary>
    bool IsAvailable { get; }

    /// <summary>托盘注册失败的原因（供设置页展示）；成功时为 <c>null</c>。</summary>
    string? UnavailableReason { get; }

    /// <summary>创建并注册托盘图标。返回是否成功。</summary>
    bool Initialize();

    /// <summary>更新图标状态（在线/离线/未读三态）。</summary>
    void SetState(TrayState state);

    /// <summary>设置托盘 tooltip（连接状态文案）。</summary>
    void SetTooltip(string text);

    /// <summary>弹出托盘气泡通知（Toast 不可用时的降级通道 —— EDGE-W-10）。</summary>
    void ShowBalloon(string title, string body);

    /// <summary>释放并移除托盘图标。</summary>
    void Dispose();
}

/// <summary>
/// 通知呈现（PRD §3.5 / FR-W-NOTI-*）。
///
/// ⚠️ EDGE-W-10：Toast 不可用（便携版未创建开始菜单快捷方式 / 系统策略禁用 / 专注助手）时
///    **必须降级为托盘气泡**，并在设置页如实展示当前通知能力状态。
/// </summary>
public interface INotificationPresenter
{
    /// <summary>Toast 是否可用（AUMID + 开始菜单快捷方式均已就绪）。</summary>
    bool IsToastAvailable { get; }

    /// <summary>当前通知能力状态（供设置页展示）。</summary>
    string CapabilityDescription { get; }

    /// <summary>
    /// 呈现一条通知。
    /// </summary>
    /// <param name="id">语义 ID（C-3 的 1002–1008），决定 ToastTag 与覆盖组（FR-W-NOTI-3）。</param>
    /// <param name="title">标题。</param>
    /// <param name="body">正文（调用方已按 FR-W-NOTI-6 截断）。</param>
    /// <param name="messageId">可选：点击通知后需滚动定位的消息 id（FR-W-NOTI-4）。</param>
    void Present(Contracts.SemanticNotificationId id, string title, string body, string? messageId = null);

    /// <summary>清除所有消息类通知（应用回到前台时 —— FR-W-NOTI-5；提醒/错误类不清）。</summary>
    void ClearMessageNotifications();

    /// <summary>系统「专注助手 / 勿扰」是否处于抑制状态（FR-W-DSK-10：抑制 Toast 但仍收消息）。</summary>
    bool IsSystemDoNotDisturbActive();
}
