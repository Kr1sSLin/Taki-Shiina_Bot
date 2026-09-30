using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using TKSDesktop.Contracts;
using TKSDesktop.App;
using TKSDesktop.Core.Platform;

/* ==========================================================================
 * 窗口工厂 + 外壳辅助实现（PRD §3.2 / WindowFactoryContract）
 *
 * ⚠️ 命名空间为 `TKSDesktop.App`：`App/StartupCoordinator.cs` 以**无 using** 的
 *    `WindowFactory.ShowMain(...)` / `WindowFactory.ShowLogin(...)` 调用，因此本类
 *    必须与 `StartupCoordinator` 同命名空间（`TKSDesktop.App`）。
 * ⚠️ 两个方法都**不得**调用 `app.Run()`（消息循环由启动协调器统一管理）。
 * ⚠️ 本文件同时承载 `TKSDesktop.Views` 命名空间下的两个外壳实现
 *    （`UiPrompt` / `ShellLauncher` / `ThemeApplier`），以避免新增未授权文件。
 * ========================================================================== */

namespace TKSDesktop.App
{
    /// <summary>窗口创建入口（实现 <see cref="WindowFactoryContract"/> 的两个契约）。</summary>
    public static class WindowFactory
    {
        private static Views.MainWindow? _mainWindow;
        private static Views.LoginWindow? _loginWindow;

        public static string? PendingMessageId { get; set; }

        /// <summary>显示主窗口（<paramref name="startHidden"/> 为真时静默到托盘 —— FR-W-DSK-3）。</summary>
        public static void ShowMain(IServiceProvider provider, bool startHidden)
        {
            ArgumentNullException.ThrowIfNull(provider);

            var window = _mainWindow ??= CreateMainWindow(provider);

            if (window.IsVisible)
            {
                window.Activate();
                return;
            }

            // 装配顺序：先让窗口完成首屏装配与建连联动（FR-W-ARCH-1 / G2），
            // 再决定是否显示（startHidden 时不显示，静默到托盘 —— FR-W-DSK-3）。
            window.Start(startHidden);

            if (!startHidden)
            {
                window.ShowWindow(startHidden: false);
            }

            if (PendingMessageId is { } messageId)
            {
                PendingMessageId = null;
                window.ScrollToMessage(messageId);
            }
        }

        /// <summary>显示登录窗口（<paramref name="noticeKey"/> 非空时展示对应 i18n 文案）。</summary>
        public static void ShowLogin(IServiceProvider provider, string? noticeKey)
        {
            ArgumentNullException.ThrowIfNull(provider);
            var window = _loginWindow ??= CreateLoginWindow(provider);
            window.ShowWindow(noticeKey);
        }

        /// <summary>退出登录或凭据失效：保留常驻宿主，隐藏主界面并回到登录页。</summary>
        public static void ReturnToLogin(IServiceProvider provider, string? noticeKey)
        {
            ArgumentNullException.ThrowIfNull(provider);
            _mainWindow?.PrepareForReauthentication();
            ShowLogin(provider, noticeKey);
        }

        private static Views.MainWindow CreateMainWindow(IServiceProvider provider)
        {
            var window = new Views.MainWindow(provider);
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_mainWindow, window))
                {
                    _mainWindow = null;
                }
            };
            return window;
        }

        private static Views.LoginWindow CreateLoginWindow(IServiceProvider provider)
        {
            var window = new Views.LoginWindow(provider);
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_loginWindow, window))
                {
                    _loginWindow = null;
                }
            };
            return window;
        }

        /// <summary>主窗口句柄（供托盘 / 快捷键 / 单实例唤起使用）。</summary>
        public static WindowFactoryContract.IMainWindowHandle? MainHandle =>
            _mainWindow is null ? null : new Views.MainWindowHandle(_mainWindow);

        /// <summary>登录窗口句柄。</summary>
        public static WindowFactoryContract.ILoginWindowHandle? LoginHandle =>
            _loginWindow is null ? null : new Views.LoginWindowHandle(_loginWindow);
    }
}

namespace TKSDesktop.Views
{
    /// <summary>
    /// 主题应用（FR-W-SET-1 / FR-W-UI-4）。
    ///
    /// ⚠️ 全部画刷来自 `DesignTokens.xaml`（同一份文件内含 light / dark 两套 `Color`），
    ///    本类只按 `Tks.Brush.<Name>` 从 `Tks.Light.<Name>` / `Tks.Dark.<Name>` 重算，
    ///    **不复制任何色值**。
    /// </summary>
    public static class ThemeApplier
    {
        private static readonly string[] Tokens =
        [
            "Background", "Surface", "SurfaceAlt", "Border", "TextPrimary", "TextSecondary",
            "Accent", "AccentSoft", "BubbleUser", "BubbleBot", "BubbleFailed",
            "Danger", "Gain", "Loss", "Focus", "Scrim", "GlassTint", "Doodle",
        ];

        /// <summary>最近一次成功应用的主题（<c>null</c> 表示尚未应用）。</summary>
        public static bool? Current { get; private set; }

        /// <summary>
        /// 把资源字典中的画刷按主题重算。**不抛错**：键缺失时跳过（NFR-W-12）。
        /// ⚠️ 刻意**不按 <c>Current</c> 短路**：本方法会分别作用于窗口级与应用级字典，
        ///    短路会让后一个字典停留在旧主题。
        /// </summary>
        public static void Apply(ResourceDictionary resources, bool dark)
        {
            ArgumentNullException.ThrowIfNull(resources);

            var prefix = dark ? "Tks.Dark." : "Tks.Light.";

            foreach (var token in Tokens)
            {
                if (resources[$"{prefix}{token}"] is not Color color)
                {
                    continue;
                }

                if (resources[$"Tks.Brush.{token}"] is not SolidColorBrush)
                {
                    continue;
                }

                var brush = new SolidColorBrush(color);
                brush.Freeze();
                resources[$"Tks.Brush.{token}"] = brush;
            }

            // 不透明降级渐变的两端色（FR-W-UI-3：系统模糊不可用时必须改为不透明渐变）。
            if (resources[$"{prefix}OpaqueFrom"] is Color from
                && resources[$"{prefix}OpaqueTo"] is Color to
                && resources["Tks.Brush.OpaqueFallback"] is LinearGradientBrush)
            {
                var gradient = new LinearGradientBrush(from, to, new Point(0, 0), new Point(1, 1));
                gradient.Freeze();
                resources["Tks.Brush.OpaqueFallback"] = gradient;
            }

            if (resources[$"{prefix}GlassTint"] is Color tint)
            {
                var surface = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0),
                    EndPoint = new Point(1, 1),
                    GradientStops = new GradientStopCollection
                    {
                        new(Color.FromArgb(dark ? (byte)150 : (byte)166, tint.R, tint.G, tint.B), 0),
                        new(Color.FromArgb(dark ? (byte)68 : (byte)94, tint.R, tint.G, tint.B), 0.12),
                        new(Color.FromArgb(dark ? (byte)112 : (byte)130, tint.R, tint.G, tint.B), 1),
                    },
                };
                surface.Freeze();
                resources["Tks.Brush.GlassSurface"] = surface;

                var border = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0),
                    EndPoint = new Point(1, 1),
                    GradientStops = new GradientStopCollection
                    {
                        new(dark ? Color.FromArgb(120, 255, 255, 255) : Color.FromArgb(205, 255, 255, 255), 0),
                        new(dark ? Color.FromArgb(52, 255, 255, 255) : Color.FromArgb(105, 255, 255, 255), 0.45),
                        new(dark ? Color.FromArgb(28, 255, 255, 255) : Color.FromArgb(48, 255, 255, 255), 1),
                    },
                };
                border.Freeze();
                resources["Tks.Brush.GlassBorder"] = border;

                var button = new SolidColorBrush(Color.FromArgb(
                    dark ? (byte)118 : (byte)106,
                    tint.R,
                    tint.G,
                    tint.B));
                button.Freeze();
                resources["Tks.Brush.GlassButton"] = button;
            }

            Current = dark;
        }
    }


    /// <summary>用户确认框实现（ViewModel 只依赖 <c>IUserPrompt</c> 接口）。</summary>
    public sealed class UiPrompt : ViewModels.IUserPrompt
    {
        /// <inheritdoc />
        public bool Confirm(string message)
            => MessageBox.Show(
                message,
                Views.WindowTexts.ConfirmTitle,
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question,
                MessageBoxResult.Cancel) == MessageBoxResult.OK;
    }

    /// <summary>
    /// 外壳动作实现。
    /// ⚠️ FR-W-SEC-5：**只用** <c>explorer.exe &lt;已校验存在的目录&gt;</c>，
    ///    **禁止**把用户输入拼进 <c>Process.Start</c>（命令行注入面）。
    /// </summary>
    public sealed class ShellLauncher : ViewModels.IShellLauncher
    {
        private const string ExplorerFileName = "explorer.exe";

        /// <inheritdoc />
        public void OpenDirectory(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                return;
            }

            // ⚠️ 只接受**已存在**的目录；不存在时静默返回（不做任何路径拼接）。
            if (!Directory.Exists(directory))
            {
                return;
            }

            try
            {
                var info = new ProcessStartInfo(ExplorerFileName)
                {
                    UseShellExecute = true,
                };
                // 逐参数加入 ArgumentList（.NET 会做必要的引号处理），不拼接命令字符串。
                info.ArgumentList.Add(directory);
                using var process = Process.Start(info);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
            {
                // 打开失败不得让设置页崩溃（NFR-W-12）。
            }
        }
    }

    /// <summary>窗口层静态文案（统一走 I18n，避免在 XAML 硬编码 —— V-W-S8）。</summary>
    internal static class WindowTexts
    {
        /// <summary>二次确认框标题。</summary>
        public static string ConfirmTitle => I18n.T("common.confirm");
    }

    /// <summary>
    /// 主窗口句柄（实现 `WindowFactoryContract.IMainWindowHandle`）。
    /// </summary>
    public sealed class MainWindowHandle : WindowFactoryContract.IMainWindowHandle
    {
        private readonly MainWindow _window;

        /// <summary>构造。</summary>
        public MainWindowHandle(MainWindow window)
        {
            ArgumentNullException.ThrowIfNull(window);
            _window = window;
        }

        /// <inheritdoc />
        public void Show(bool startHidden) => _window.ShowWindow(startHidden);

        /// <inheritdoc />
        public void HideToTray() => _window.HideToTray();

        /// <inheritdoc />
        public void ActivateAndFocus() => _window.ActivateAndFocus();

        /// <inheritdoc />
        public void ToggleVisibility() => _window.ToggleVisibility();

        /// <inheritdoc />
        public void ScrollToMessage(string messageId) => _window.ScrollToMessage(messageId);
    }

    /// <summary>登录窗口句柄（实现 `WindowFactoryContract.ILoginWindowHandle`）。</summary>
    public sealed class LoginWindowHandle : WindowFactoryContract.ILoginWindowHandle
    {
        private readonly LoginWindow _window;

        /// <summary>构造。</summary>
        public LoginWindowHandle(LoginWindow window)
        {
            ArgumentNullException.ThrowIfNull(window);
            _window = window;
        }

        /// <inheritdoc />
        public void Show(string? noticeKey) => _window.ShowWindow(noticeKey);

        /// <inheritdoc />
        public void Close() => _window.CloseWindow();
    }
}
