using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TKSDesktop.App;
using TKSDesktop.Core.Auth;
using TKSDesktop.Core.Platform;
using TKSDesktop.Core.Services;

namespace TKSDesktop.App;

/// <summary>
/// 启动协调器（PRD FR-W-AUTH-12 / §8.4 / FR-W-DSK-3 / FR-W-DSK-9 / FR-W-ARCH-1）。
///
/// **这是「免输入自动登录」与「开机静默启动到托盘」的唯一落点**：
/// <list type="number">
///   <item>尝试恢复登录态（凭据经 DPAPI 解密）；</item>
///   <item>成功 → **直接进入聊天主界面并自动建连，不显示登录页**；</item>
///   <item>失败 → 按恢复状态给对应提示后显示登录页；</item>
///   <item>`--hidden` 且凭据有效 → 静默到托盘（不显示窗口）并完成建连；
///         无凭据时**忽略该参数**并显示登录页（FR-W-DSK-9）。</item>
/// </list>
///
/// ⚠️ 后台服务（WS / 同步 / 提醒）在**宿主**中启动，与窗口生命周期无关（FR-W-ARCH-1 / G2）。
/// </summary>
public static class StartupCoordinator
{
    /// <summary>启动应用；返回进程退出码。</summary>
    public static int Start(
        System.Windows.Application app,
        IServiceProvider provider,
        CliOptions options)
    {
        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("App.Startup");
        var auth = provider.GetRequiredService<IAuthService>();
        WindowFactory.PendingMessageId = options.Notification?.MessageId;

        // ---- 1. 凭据恢复（FR-W-AUTH-12 / 13 / 3b / EDGE-W-11）----
        CredentialRestoreResult restore;
        try
        {
            restore = auth.TryRestore();
        }
        catch (Exception ex)
        {
            // 恢复流程自身出错不得让应用起不来（EDGE-W-27 精神）。
            logger.LogError(ex, "凭据恢复失败，回退到登录页");
            restore = new CredentialRestoreResult(CredentialRestoreStatus.NoCredentials, false, null, null);
        }

        logger.LogInformation("凭据恢复结果：{Status}（Authenticated={Has}）", restore.Status, restore.Authenticated);

        if (restore.Authenticated)
        {
            // 直接进入聊天主界面并自动建连，**不显示登录页**（FR-W-AUTH-12）。
            // `--hidden` 时静默到托盘：仍建连，但不显示窗口（FR-W-DSK-3 / §8.4）。
            WindowFactory.ShowMain(provider, startHidden: options.Hidden && options.Notification is null);
            _ = StartBackgroundAsync(provider);

            if (options.Hidden)
            {
                logger.LogInformation("--hidden：已静默启动到托盘并开始建连");
            }

            app.Run();
            return 0;
        }

        // 无可用凭据：`--hidden` 被忽略，显示登录页（FR-W-DSK-9）。
        if (options.Hidden)
        {
            logger.LogInformation("--hidden 被忽略：无可用凭据");
        }

        WindowFactory.ShowLogin(provider, NoticeKeyFor(restore.Status));
        logger.LogInformation("Login window shown and initial layout completed");
        app.Run();
        return 0;
    }

    /// <summary>按恢复状态给出登录页提示文案 key（FR-W-AUTH-12 / 13 / 3b / EDGE-W-11）。</summary>
    private static string? NoticeKeyFor(CredentialRestoreStatus status) => status switch
    {
        CredentialRestoreStatus.Corrupted => "login.credentialCorrupted",
        CredentialRestoreStatus.IdleExpired => "login.idleExpired",
        _ => null,
    };

    /// <summary>
    /// 启动后台服务：建连 → 全量同步 → 记忆同步 → 装载提醒。
    /// **与窗口生命周期无关**（FR-W-ARCH-1 / G2：关窗/隐藏到托盘后仍常驻在线）。
    /// </summary>
    public static async Task StartBackgroundAsync(IServiceProvider provider)
    {
        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("App.Background");
        try
        {
            await provider.GetRequiredService<DesktopRuntime>().StartAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "后台服务启动失败（不影响 UI 可用）");
        }
    }
}
