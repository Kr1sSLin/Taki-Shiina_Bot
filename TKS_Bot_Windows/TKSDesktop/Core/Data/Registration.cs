using Microsoft.Extensions.DependencyInjection;
using TKSDesktop.Core.Data.Repositories;
using TKSDesktop.Core.Platform;
using TKSDesktop.Core.Services.Ports;

namespace TKSDesktop.Core.Data;

/// <summary>
/// 数据层注册入口（PRD §3.3 / §9）。
///
/// ⚠️ **跨模块契约点**：`App/AppBootstrap.cs` 按此签名调用，**不得改名或改签名**。
/// </summary>
public static class Registration
{
    /// <summary>
    /// 注册本地库与全部仓储。
    /// ⚠️ <see cref="LocalDb"/> 的打开是异步的（含 `integrity_check` 与迁移），
    ///    因此这里注册为**延迟初始化**的单例：首次解析时同步等待打开完成。
    ///    启动流程在 `StartupCoordinator` 中已先解析它，避免首次请求时阻塞 UI。
    /// </summary>
    public static IServiceCollection AddLocalDb(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(sp =>
        {
            var paths = sp.GetRequiredService<IPaths>();
            return LocalDb.OpenAsync(paths.DatabaseFile).GetAwaiter().GetResult();
        });

        services.AddSingleton<MessageRepository>();
        services.AddSingleton<NotificationRepository>();
        services.AddSingleton<FactRepository>();
        services.AddSingleton<ReminderRepository>();
        services.AddSingleton<CursorRepository>();
        services.AddSingleton<ProgressCacheRepository>();

        services.AddSingleton<ChatRepositoryPortAdapter>();
        services.AddSingleton<IChatRepositoryPort>(sp => sp.GetRequiredService<ChatRepositoryPortAdapter>());
        services.AddSingleton<IAttachmentPort>(sp => sp.GetRequiredService<ChatRepositoryPortAdapter>());

        return services;
    }
}
