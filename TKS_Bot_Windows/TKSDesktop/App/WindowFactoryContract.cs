namespace TKSDesktop.App;

/// <summary>
/// 窗口创建契约点（PRD §3.2 UI 层 / FR-W-DSK-13）。
///
/// ⚠️ **契约冻结**：`App` 层（启动协调器）只经此静态入口创建窗口，
///    **不**直接 `new MainWindow()`，以免把 UI 层类型泄漏到启动流程。
/// ⚠️ 实现位置：`Views/WindowFactory.cs`（UI 层负责实现，`App` 层只消费）。
/// ⚠️ 两个方法都**不得**自行调用 `app.Run()`（消息循环由启动协调器统一管理）。
/// </summary>
public static class WindowFactoryContract
{
    /// <summary>主窗口契约（由 UI 层实现 `Views/MainWindow.xaml`）。</summary>
    public interface IMainWindowHandle
    {
        /// <summary>显示窗口；<paramref name="startHidden"/> 为 <c>true</c> 时不显示（静默到托盘 —— FR-W-DSK-3）。</summary>
        void Show(bool startHidden);

        /// <summary>隐藏到托盘（FR-W-DSK-13 ②）。</summary>
        void HideToTray();

        /// <summary>唤起并聚焦（全局快捷键 / 托盘双击 / 单实例唤起）。</summary>
        void ActivateAndFocus();

        /// <summary>在显示与隐藏之间切换（托盘双击 / 全局快捷键）。</summary>
        void ToggleVisibility();

        /// <summary>滚动定位到指定消息（点击通知后 —— FR-W-NOTI-4）。</summary>
        void ScrollToMessage(string messageId);
    }

    /// <summary>登录窗口契约（由 UI 层实现 `Views/LoginWindow.xaml`）。</summary>
    public interface ILoginWindowHandle
    {
        /// <summary>显示登录页；<paramref name="noticeKey"/> 非空时展示对应 i18n 文案（登录过期 / 凭据损坏等）。</summary>
        void Show(string? noticeKey);

        /// <summary>关闭登录页（登录成功、切换到主界面时调用）。</summary>
        void Close();
    }
}
