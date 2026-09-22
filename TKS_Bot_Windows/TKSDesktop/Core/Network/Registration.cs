using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TKSDesktop.Core.Auth;
using TKSDesktop.Core.Network.Ws;
using TKSDesktop.Core.Platform;
using TKSDesktop.Core.Services;
using TKSDesktop.Core.Services.Ports;

namespace TKSDesktop.Core.Network;

/// <summary>
/// 网络与鉴权注册入口（PRD §5 / §6.1 / §6.2）。
///
/// ⚠️ **跨模块契约点**：`App/AppBootstrap.cs` 按此签名调用，**不得改名或改签名**。
/// </summary>
public static class Registration
{
    /// <summary>注册 HttpClient / 网络层 / 鉴权层。</summary>
    public static IServiceCollection AddNetworking(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // REST 超时由每次调用经 CancellationTokenSource 控制（FR-W-NET-4 分级），
        // 因此这里的 HttpClient.Timeout 必须为无限，否则会覆盖分级超时。
        services.AddSingleton(_ => new HttpClient { Timeout = Timeout.InfiniteTimeSpan });
        services.AddSingleton(sp => new HealthProbe(
            sp.GetRequiredService<HttpClient>(),
            () => sp.GetRequiredService<App.SettingsStore>().Current.ApiBaseUrl,
            sp.GetRequiredService<ILogger<HealthProbe>>()));

        // ---- 鉴权（FR-W-AUTH-*）：必须先于依赖 IAccessTokenProvider 的 WS/REST 注册 ----
        services.AddSingleton<Core.Auth.DeviceIdProvider>(sp => new Core.Auth.DeviceIdProvider(
            sp.GetRequiredService<IPaths>(),
            sp.GetRequiredService<ILogger<Core.Auth.DeviceIdProvider>>()));
        // ⚠️ 凭据落盘只有**一条**实现路径：ISecretStore → ThemeAwareSecretStore（DPAPI + 原子写）。
        //    CredentialStore 只是强类型载荷/状态层，**不再**自带第二份 DPAPI 实现
        //    （两份实现会写出不同格式却指向同一个 credentials.bin，且导致运行时与 --selftest
        //     验证不同代码路径 —— 见 CredentialStore 类注释与 V-W-S4）。
        services.AddSingleton<Core.Auth.CredentialStore>(sp => new Core.Auth.CredentialStore(
            sp.GetRequiredService<ISecretStore>(),
            sp.GetRequiredService<IPaths>(),
            sp.GetRequiredService<ILogger<Core.Auth.CredentialStore>>()));
        services.AddSingleton<Core.Auth.TokenManager>();
        services.AddSingleton<Core.Auth.AuthService>();

        // RestClient 是 AuthService 的依赖，因此 Token 提供者必须直接指向 TokenManager。
        // 若指回 AuthService，会形成 AuthService -> RestClient -> IAccessTokenProvider -> AuthService
        // 的运行时循环依赖（工厂注册不会被 ValidateOnBuild 提前发现）。
        services.AddSingleton<IAccessTokenProvider>(sp => sp.GetRequiredService<Core.Auth.TokenManager>());

        // ---- WS ----
        services.AddSingleton(sp => new WsClient(sp.GetRequiredService<ILogger<WsClient>>()));
        services.AddSingleton(sp => new WsConnectionManager(
            sp.GetRequiredService<WsClient>(),
            sp.GetRequiredService<IAccessTokenProvider>(),
            () => sp.GetRequiredService<App.SettingsStore>().Current.WsBaseUrl,
            sp.GetRequiredService<ILogger<WsConnectionManager>>()));
        services.AddSingleton<WssPortAdapter>();
        services.AddSingleton<IWssPort>(sp => sp.GetRequiredService<WssPortAdapter>());

        // ---- REST ----
        services.AddSingleton(sp => new RestClient(
            sp.GetRequiredService<HttpClient>(),
            sp.GetRequiredService<IAccessTokenProvider>(),
            () => sp.GetRequiredService<App.SettingsStore>().Current.ApiBaseUrl,
            sp.GetRequiredService<ILogger<RestClient>>()));
        services.AddSingleton<RestPortAdapter>();
        services.AddSingleton<IRestPort>(sp => sp.GetRequiredService<RestPortAdapter>());

        services.AddSingleton<IAuthService>(sp => new Core.Services.AuthServiceAdapter(
            sp.GetRequiredService<Core.Auth.AuthService>(),
            sp.GetRequiredService<Core.Auth.TokenManager>(),
            sp.GetRequiredService<IPaths>(),
            cancellationToken => sp.GetRequiredService<WsConnectionManager>()
                .DisconnectAsync("logout", cancellationToken),
            sp.GetRequiredService<ILogger<Core.Services.AuthServiceAdapter>>()));

        return services;
    }
}
