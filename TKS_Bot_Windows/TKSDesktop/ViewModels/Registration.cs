using Microsoft.Extensions.DependencyInjection;
using TKSDesktop.Contracts;
using TKSDesktop.Core.Data.Repositories;
using TKSDesktop.Core.Network;
using TKSDesktop.Core.Services;

namespace TKSDesktop.ViewModels;

/* ==========================================================================
 * 视图模型注册入口 + 视图模型层自有的窄读端口
 *
 * ⚠️ 本文件是**跨模块契约点**：`App/AppBootstrap.cs` 以全限定名
 *    `TKSDesktop.ViewModels.Registration.AddViewModels(services)` 调用，
 *    签名与命名空间**不得**改动。
 *
 * ⚠️ 依赖方向（§3.3）：ViewModels → 服务接口 / 平台接口。
 *    `ChatViewModel` / `ProfileViewModel` / `HistoryViewModel` / `SettingsViewModel`
 *    等只依赖 `Core/Services`、`Core/Platform` 的**接口**。
 *
 * ⚠️ 本文件是 UI 层的**组合根**（唯一把具体实现接到接口上的地方），与
 *    `App/AppBootstrap.cs` 同一角色，因此只有这里出现下列具体类型：
 *      · 本地只读查询：`Core.Data.Repositories.*`（消息 / 通知 / 事实）
 *      · 连通性探测：`Core.Network.HealthProbe`
 *    理由：`Core/Services` 当前**没有**只读历史/记忆端口，也没有连通性探测端口，
 *    而界面必须能读到这些数据。缺口与建议（新增 `Core.Services.IHistoryService`
 *    与 `IConnectivityProbe`，由服务层写者落地）已在交付报告中列出；
 *    届时只需替换本文件底部的两个适配器，各 ViewModel 无需改动。
 * ========================================================================== */

/// <summary>本地 Bot 通知（<c>bot_notifications</c> 的只读投影，供历史页展示）。</summary>
public sealed record LocalNotificationItem(
    string NotificationId,
    string ErrorCode,
    string Message,
    bool IsRead,
    long Timestamp);

/// <summary>本地用户事实（<c>user_facts</c> 的只读投影，供记忆档案展示）。</summary>
public sealed record LocalFactItem(string FactId, string Fact, long Timestamp);

/// <summary>
/// 本地只读历史端口（消息首屏分页 / 历史通知 / 记忆事实）。
/// 全部方法**不得抛错**：失败返回空集合或 0（NFR-W-12）。
/// </summary>
public interface ILocalHistoryReader
{
    /// <summary>
    /// 按时间**升序**取最近 <paramref name="limit"/> 条消息，再向更早方向跳过
    /// <paramref name="skipFromEnd"/> 条（FR-W-CHAT-16 首屏 50 条 + 向上分页）。
    /// </summary>
    Task<IReadOnlyList<ChatMessageView>> ListRecentMessagesAsync(int limit, int skipFromEnd, CancellationToken ct = default);

    /// <summary>按时间**倒序**取通知（FR-W-HIS-1）。</summary>
    Task<IReadOnlyList<LocalNotificationItem>> ListNotificationsAsync(int limit, CancellationToken ct = default);

    /// <summary>未读数（FR-W-HIS-1）。</summary>
    Task<int> CountUnreadNotificationsAsync(CancellationToken ct = default);

    /// <summary>把给定通知标为已读（FR-W-HIS-2）。</summary>
    Task<int> MarkNotificationsReadAsync(IReadOnlyList<string> notificationIds, CancellationToken ct = default);

    /// <summary>全部标为已读（FR-W-HIS-2）。</summary>
    Task<int> MarkAllNotificationsReadAsync(CancellationToken ct = default);

    /// <summary>事实列表（**倒序**、只读；<paramref name="term"/> 为空时等价于列表）（FR-W-HIS-3/5）。</summary>
    Task<IReadOnlyList<LocalFactItem>> ListFactsAsync(string? term, int limit, CancellationToken ct = default);
}

/// <summary>连通性三态（EDGE-W-28：404 中间态必须与「不可达」区分文案）。</summary>
public enum ConnectivityState
{
    /// <summary>HTTP 200，服务可用。</summary>
    Reachable,

    /// <summary>HTTP 404：服务可达但健康检查路径不可用。</summary>
    HealthEndpointUnavailable,

    /// <summary>完全不可达。</summary>
    Unreachable,
}

/// <summary>连通性测试端口（FR-W-SET-4）。**不得抛错**：所有失败收敛为 <see cref="ConnectivityState.Unreachable"/>。</summary>
public interface IConnectivityTest
{
    /// <summary>探测指定 REST 基址。</summary>
    Task<ConnectivityState> TestAsync(string apiBaseUrl, CancellationToken ct = default);
}

/// <summary>
/// 用户确认（二次确认框）。由 View 层用 <c>MessageBox</c> 实现，
/// 使 ViewModel 不引用任何 WPF 类型（NFR-W-14 精神）。
/// </summary>
public interface IUserPrompt
{
    /// <summary>弹出确认框；返回用户是否确认。</summary>
    bool Confirm(string message);
}

/// <summary>
/// 外壳动作（打开目录等）。⚠️ FR-W-SEC-5：实现方**只允许**
/// <c>explorer.exe &lt;已校验存在的目录&gt;</c>，**禁止**把用户输入拼进 <c>Process.Start</c>。
/// </summary>
public interface IShellLauncher
{
    /// <summary>在资源管理器中打开目录；不存在时静默返回。</summary>
    void OpenDirectory(string directory);
}

/// <summary>视图模型注册入口。</summary>
public static class Registration
{
    /// <summary>
    /// 注册全部 ViewModel 与 UI 层内部端口。
    /// ⚠️ 由 `App/AppBootstrap.cs` 以全限定名调用，签名冻结。
    /// </summary>
    public static void AddViewModels(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // ---- UI 层内部端口（适配器，见文件头说明）----
        services.AddSingleton<ILocalHistoryReader, RepositoryBackedHistoryReader>();
        services.AddSingleton<IConnectivityTest, HealthProbeConnectivityTest>();

        // ---- View 层提供的外壳能力实现 ----
        services.AddSingleton<IUserPrompt, global::TKSDesktop.Views.UiPrompt>();
        services.AddSingleton<IShellLauncher, global::TKSDesktop.Views.ShellLauncher>();

        // ---- ViewModel（单例：主窗口 / 弹出窗口共享同一宿主服务，FR-W-DSK-8）----
        services.AddSingleton<LoginViewModel>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<ChatViewModel>();
        services.AddSingleton<ProfileViewModel>();
        services.AddSingleton<InteractionMenuViewModel>();
        services.AddSingleton<HistoryViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<RemindersViewModel>();
    }
}

/// <summary>
/// 组合根适配器：把 <c>Core.Data.Repositories.*</c> 接到 <see cref="ILocalHistoryReader"/>。
/// ⚠️ 每方法内部惰性解析仓储，避免窗口构造期同步打开 SQLite。
/// </summary>
internal sealed class RepositoryBackedHistoryReader : ILocalHistoryReader
{
    private const int NotificationLimit = NotificationRepository.DefaultLimit;

    private readonly IServiceProvider _provider;

    public RepositoryBackedHistoryReader(IServiceProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _provider = provider;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ChatMessageView>> ListRecentMessagesAsync(int limit, int skipFromEnd, CancellationToken ct = default)
    {
        try
        {
            var repository = _provider.GetRequiredService<MessageRepository>();
            var take = Math.Clamp(limit + Math.Max(0, skipFromEnd), 1, 1000);
            var rows = await repository.ListRecentAsync(take, null, ct).ConfigureAwait(false);

            // ListRecentAsync 返回**升序**的最近 take 条：从尾部（最新端）往前跳 skipFromEnd 条即更早一页。
            var page = skipFromEnd <= 0
                ? rows
                : rows.Take(Math.Max(0, rows.Count - skipFromEnd)).ToList();

            var views = new List<ChatMessageView>(page.Count);

            foreach (var row in page)
            {
                var attachments = await LoadAttachmentsAsync(repository, row, ct).ConfigureAwait(false);
                views.Add(new ChatMessageView(
                    row.MessageId,
                    row.Role,
                    row.ContentType,
                    row.Content,
                    row.Status,
                    row.Timestamp,
                    row.ErrorCode,
                    null,
                    null,
                    false,
                    ProtocolConstants.IsHistorySeparator(row.Content),
                    attachments));
            }

            return views;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 本地读取失败不得让聊天页起不来：返回空集合（NFR-W-12）。
            return [];
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LocalNotificationItem>> ListNotificationsAsync(int limit, CancellationToken ct = default)
    {
        try
        {
            var repository = _provider.GetRequiredService<NotificationRepository>();
            var rows = await repository
                .ListAsync(Math.Clamp(limit, 1, NotificationLimit), ct)
                .ConfigureAwait(false);

            return rows
                .Select(row => new LocalNotificationItem(
                    row.NotificationId,
                    row.ErrorCode,
                    row.Message,
                    row.IsRead,
                    row.Timestamp))
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return [];
        }
    }

    /// <inheritdoc />
    public async Task<int> CountUnreadNotificationsAsync(CancellationToken ct = default)
    {
        try
        {
            var repository = _provider.GetRequiredService<NotificationRepository>();
            return await repository.UnreadCountAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return 0;
        }
    }

    /// <inheritdoc />
    public async Task<int> MarkNotificationsReadAsync(IReadOnlyList<string> notificationIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(notificationIds);
        if (notificationIds.Count == 0)
        {
            return 0;
        }

        try
        {
            var repository = _provider.GetRequiredService<NotificationRepository>();
            return await repository.MarkReadAsync(notificationIds, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return 0;
        }
    }

    /// <inheritdoc />
    public async Task<int> MarkAllNotificationsReadAsync(CancellationToken ct = default)
    {
        try
        {
            var repository = _provider.GetRequiredService<NotificationRepository>();
            return await repository.MarkAllReadAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return 0;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LocalFactItem>> ListFactsAsync(string? term, int limit, CancellationToken ct = default)
    {
        try
        {
            var repository = _provider.GetRequiredService<FactRepository>();
            var safeLimit = Math.Clamp(limit, 1, FactRepository.DefaultLimit);
            var rows = string.IsNullOrWhiteSpace(term)
                ? await repository.ListAsync(safeLimit, ct).ConfigureAwait(false)
                : await repository.SearchAsync(term, safeLimit, ct).ConfigureAwait(false);

            return rows
                .Select(row => new LocalFactItem(row.FactId, row.Fact, row.Timestamp))
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return [];
        }
    }

    /// <summary>
    /// 只为「带图」的消息补附件路径（避免为纯文本行引入 N+1 次查询 —— FR-W-SYNC-* 性能）。
    /// </summary>
    private static async Task<IReadOnlyList<string>> LoadAttachmentsAsync(
        MessageRepository repository,
        Core.Data.Records.MessageRecord row,
        CancellationToken ct)
    {
        if (row.ContentType is not ("image" or "mixed"))
        {
            return [];
        }

        try
        {
            var attachments = await repository.ListByMessageAsync(row.MessageId, ct).ConfigureAwait(false);
            return attachments.Select(static a => a.LocalPath).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return [];
        }
    }
}

/// <summary>组合根适配器：把 <c>Core.Network.HealthProbe</c> 接到 <see cref="IConnectivityTest"/>。</summary>
internal sealed class HealthProbeConnectivityTest : IConnectivityTest
{
    private readonly IServiceProvider _provider;

    public HealthProbeConnectivityTest(IServiceProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _provider = provider;
    }

    /// <inheritdoc />
    public async Task<ConnectivityState> TestAsync(string apiBaseUrl, CancellationToken ct = default)
    {
        try
        {
            var probe = _provider.GetRequiredService<HealthProbe>();
            var result = await probe
                .ProbeAsync(apiBaseUrl, ProtocolConstants.ConnectivityTimeoutMs, ct)
                .ConfigureAwait(false);

            return result.Status switch
            {
                HealthStatus.Reachable => ConnectivityState.Reachable,
                HealthStatus.HealthEndpointUnavailable => ConnectivityState.HealthEndpointUnavailable,
                _ => ConnectivityState.Unreachable,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ConnectivityState.Unreachable;
        }
    }
}
