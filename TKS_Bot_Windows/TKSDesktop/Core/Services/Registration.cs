using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TKSDesktop.App;
using TKSDesktop.Core.Platform;
using TKSDesktop.Core.Services.Ports;
using TKSDesktop.Core.Services.Reminders;

namespace TKSDesktop.Core.Services;

/// <summary>
/// 领域服务注册入口（PRD §3.3 服务层 / §6.3–§6.7）。
///
/// ⚠️ **跨模块契约点**：`App/AppBootstrap.cs` 按此签名调用，**不得改名或改签名**。
/// </summary>
public static class Registration
{
    /// <summary>注册对话 / 同步 / 提醒 / 媒体 / 养成 / 通知等领域服务。</summary>
    public static IServiceCollection AddDomainServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // 时间源可注入：自检需要注入测试时钟（V-W-B22 的 7 天/30 分钟语义断言）。
        services.AddSingleton(TimeProvider.System);

        // 养成 REST 端口（§7.0 的 9 个端点 + 超时可注入）—— 由 GamificationService 消费。
        services.AddSingleton<IGamificationPort>(sp => new Adapters.GamificationPortAdapter(
            sp.GetRequiredService<Core.Network.RestClient>()));

        // ---- 提醒（FR-W-REM-1..7）----
        services.AddSingleton<IUserNotificationService, UserNotificationService>();
        services.AddSingleton<IReminderPort>(sp => new ReminderRepositoryAdapter(
            sp.GetRequiredService<Data.Repositories.ReminderRepository>()));
        services.AddSingleton<IReminderScheduler>(sp => new ReminderScheduler(
            sp.GetRequiredService<IReminderPort>(),
            sp.GetRequiredService<IUserNotificationService>(),
            sp.GetRequiredService<IUiDispatcher>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<ReminderScheduler>>()));

        // ---- 设置服务（落盘 + 平台联动）----
        services.AddSingleton<ISettingsService, SettingsService>();

        // ---- M1/M2 真实运行链路（不能只让纯逻辑测试通过）----
        services.AddSingleton<IChatService, Chat.ChatService>();
        services.AddSingleton<ISyncService, Sync.SyncService>();
        services.AddSingleton<IMediaService, Media.MediaService>();
        services.AddSingleton<IGamificationService, GamificationService>();

        return services;
    }
}
