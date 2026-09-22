using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TKSDesktop.App;
using TKSDesktop.Core.Platform;
using TKSDesktop.Core.Services.Ports;
using TKSDesktop.ViewModels;
using TKSDesktop.Platform.Windows;

namespace TKSDesktop.App;

/// <summary>
/// 组合根（PRD §3.2 / §3.3 依赖方向：`Views → ViewModels → Core → Platform/Data`）。
///
/// 职责：
/// <list type="number">
///   <item>建立 <see cref="AppPaths"/> 并创建全部目录（FR-W-DSK-14）；</item>
///   <item>装配日志（JSON 行 + 脱敏 + `deviceId` 上下文字段）；</item>
///   <item>装配设置存储与设置服务；</item>
///   <item>装配平台层实现（托盘 / Toast / 自启 / 快捷键 / 剪贴板 / 窗口 / 电源 / DPAPI）；</item>
///   <item>装配 Core 层与 ViewModel；</item>
///   <item>执行启动流程（凭据恢复 → 自动登录 → 建连；`--hidden` 静默到托盘）。</item>
/// </list>
/// </summary>
public static class AppBootstrap
{
    /// <summary>进程级组合根（供自检与启动流程共用）。</summary>
    public static ServiceProvider BuildServiceProvider(CliOptions options, AppPaths paths)
    {
        var services = new ServiceCollection();

        // ---- 基础设施 ----
        services.AddSingleton(paths);
        services.AddSingleton<IPaths>(sp => sp.GetRequiredService<AppPaths>());
        services.AddSingleton(options);

        // 日志：JSON 行 + 脱敏（FR-W-LOG-1/2/3）
        services.AddSingleton<JsonLineFileLoggerProvider>(_ => new JsonLineFileLoggerProvider(paths.LogsDir));
        // 让 ILoggerFactory 使用同一个 DI-owned provider，确保应用/测试释放服务图时
        // 一定关闭当天的文件句柄（Windows 不允许删除仍被打开的日志文件）。
        services.AddSingleton<ILoggerProvider>(sp => sp.GetRequiredService<JsonLineFileLoggerProvider>());
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
        });

        // ---- 平台层（W-P5：唯一允许出现 Windows API 的地方）----
        services.AddSingleton<ISecretStore, ThemeAwareSecretStore>();
        services.AddSingleton<IAutoStartManager, AutoStartManager>();
        services.AddSingleton<IGlobalHotkey, GlobalHotkeyManager>();
        services.AddSingleton<IClipboardImage, ClipboardImageReader>();
        services.AddSingleton<IWindowChrome, WindowChromeService>();
        services.AddSingleton<IPowerEvents, PowerEventWatcher>();
        services.AddSingleton<ITrayIcon>(sp => new TrayIconManager(
            new TrayIconManager.TrayMenuTexts(
                I18n.T("tray.menu.show"),
                I18n.T("tray.menu.status"),
                I18n.T("tray.menu.profile"),
                I18n.T("tray.menu.reminders"),
                I18n.T("tray.menu.reconnect"),
                I18n.T("tray.menu.settings"),
                I18n.T("tray.menu.exit")),
            new TrayIconManager.TrayMenuActions(
                ToggleWindow: () => sp.GetRequiredService<IUiDispatcher>().Invoke(
                    () => WindowFactory.MainHandle?.ToggleVisibility()),
                OpenProfile: () => sp.GetRequiredService<IUiDispatcher>().Invoke(
                    () => sp.GetRequiredService<MainViewModel>().OpenProfileCommand.Execute(null)),
                OpenReminders: () => sp.GetRequiredService<IUiDispatcher>().Invoke(
                    () => sp.GetRequiredService<MainViewModel>().OpenRemindersCommand.Execute(null)),
                Reconnect: () => sp.GetRequiredService<IUiDispatcher>().Invoke(
                    () => sp.GetRequiredService<MainViewModel>().ManualReconnectCommand.Execute(null)),
                OpenSettings: () => sp.GetRequiredService<IUiDispatcher>().Invoke(
                    () => sp.GetRequiredService<MainViewModel>().OpenSettingsCommand.Execute(null)),
                Exit: () => sp.GetRequiredService<IUiDispatcher>().Invoke(
                    () => System.Windows.Application.Current?.Shutdown()),
                StatusTextProvider: () => sp.GetRequiredService<MainViewModel>().ConnectionTooltip)));
        services.AddSingleton<INotificationPresenter, ToastNotificationPresenter>();
        services.AddSingleton<IUiDispatcher, WpfUiDispatcher>();
        services.AddSingleton<IFileStoragePort, WindowsFileStorage>();
        services.AddSingleton<IImageProbe, WpfImageProbe>();

        // ---- 设置 ----
        services.AddSingleton(sp => new SettingsStore(
            paths.SettingsFile,
            sp.GetRequiredService<ILogger<SettingsStore>>()));

        // ---- Core 层与 ViewModel：由各自实现方通过扩展方法注册，保持本文件稳定 ----
        // （CoreDataRegistration / CoreNetworkRegistration / CoreServicesRegistration /
        // ---- Core 层与 ViewModel 接线（跨模块契约点，见下）----
        // 契约点（各模块的**唯一**对外注册入口，不得改名）：
        //   TKSDesktop.Core.Data.Registration      → AddLocalDb(services)
        //   TKSDesktop.Core.Network.Registration   → AddNetworking(services)
        //   TKSDesktop.Core.Services.Registration  → AddDomainServices(services)
        //   TKSDesktop.ViewModels.Registration     → AddViewModels(services)
        TKSDesktop.Core.Data.Registration.AddLocalDb(services);
        TKSDesktop.Core.Network.Registration.AddNetworking(services);
        TKSDesktop.Core.Services.Registration.AddDomainServices(services);
        TKSDesktop.ViewModels.Registration.AddViewModels(services);
        services.AddSingleton<DesktopRuntime>();
        services.AddSingleton<SingleInstanceGuard>();

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }

    /// <summary>
    /// 启动流程（PRD FR-W-AUTH-12 / FR-W-DSK-3 / FR-W-DSK-9）。
    /// 返回进程退出码。
    /// </summary>
    public static int Run(System.Windows.Application app, CliOptions options)
    {
        var paths = new AppPaths();
        // 便携版不可写时必须明确报错，不得静默回退到 %APPDATA%（§14.4）
        if (paths.ValidatePortableWritable() is { } portableError)
        {
            System.Windows.MessageBox.Show(
                I18n.T("settings.portable.notWritable") + "\n" + portableError,
                AppVersion.ProductName,
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
            return 2;
        }

        paths.EnsureDirectories();

        var provider = BuildServiceProvider(options, paths);
        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("App.Bootstrap");
        logger.LogInformation("启动 {Product} {Version}（portable={Portable}, hidden={Hidden}）",
            AppVersion.ProductName, AppVersion.Informational, paths.IsPortable, options.Hidden);

        if (options.ResetConfig)
        {
            provider.GetRequiredService<SettingsStore>().Reset();
            logger.LogInformation("已按 --reset-config 重置设置");
        }

        provider.GetRequiredService<SettingsStore>().Load();

        var singleInstance = provider.GetRequiredService<SingleInstanceGuard>();
        if (!singleInstance.TryAcquire())
        {
            var activation = options.Notification;
            _ = singleInstance.NotifyExistingInstanceAsync(new ActivateRequest(
                ActivateRequest.ActionActivate, activation?.Action, activation?.MessageId)).GetAwaiter().GetResult();
            provider.DisposeAsync().AsTask().GetAwaiter().GetResult();
            return 0;
        }

        singleInstance.ActivateRequested += (_, request) =>
            provider.GetRequiredService<IUiDispatcher>().Invoke(() =>
            {
                if (provider.GetRequiredService<Core.Services.IAuthService>().State == Core.Services.AuthState.Authenticated
                    && WindowFactory.MainHandle is { } main)
                {
                    if (!string.IsNullOrWhiteSpace(request.MessageId))
                    {
                        main.ScrollToMessage(request.MessageId);
                    }
                    else
                    {
                        main.ActivateAndFocus();
                    }
                }
                else
                {
                    WindowFactory.PendingMessageId = request.MessageId;
                    WindowFactory.LoginHandle?.Show(null);
                }
            });
        singleInstance.StartListening();

        var runtimeAuth = provider.GetRequiredService<Core.Auth.AuthService>();
        runtimeAuth.AuthExpired += (_, expired) =>
            provider.GetRequiredService<IUiDispatcher>().Invoke(() =>
                WindowFactory.ReturnToLogin(
                    provider,
                    string.Equals(expired.Reason, "kicked", StringComparison.Ordinal)
                        ? "login.kickedOut"
                        : "login.sessionExpired"));

        try
        {
            return StartupCoordinator.Start(app, provider, options);
        }
        finally
        {
            // LocalDb 等组件只实现 IAsyncDisposable；同步 Dispose 会在正常退出时抛出。
            provider.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}
