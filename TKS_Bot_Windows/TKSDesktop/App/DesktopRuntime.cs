using Microsoft.Extensions.Logging;
using TKSDesktop.Core.Platform;
using TKSDesktop.Core.Services;

namespace TKSDesktop.App;

/// <summary>窗口之外的常驻运行时：连接、同步、提醒以及休眠/网络恢复。</summary>
public sealed class DesktopRuntime : IDisposable
{
    private readonly IChatService _chat;
    private readonly ISyncService _sync;
    private readonly IReminderScheduler _reminders;
    private readonly IPowerEvents _power;
    private readonly ILogger<DesktopRuntime> _logger;
    private readonly IAuthService _auth;
    private readonly SemaphoreSlim _recoveryGate = new(1, 1);
    private int _started;
    private volatile bool _disposed;

    public DesktopRuntime(
        IChatService chat,
        ISyncService sync,
        IReminderScheduler reminders,
        IPowerEvents power,
        ILogger<DesktopRuntime> logger,
        IAuthService auth)
    {
        _chat = chat;
        _sync = sync;
        _reminders = reminders;
        _power = power;
        _logger = logger;
        _auth = auth;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            // 同一进程退出登录后再次登录：宿主仍存活，但 WS 已主动断开。
            await RecoverAsync(includeOverdueReminders: false, cancellationToken).ConfigureAwait(false);
            return;
        }

        _power.Resumed += OnResumed;
        _power.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        _power.Start();

        await RecoverAsync(includeOverdueReminders: false, cancellationToken).ConfigureAwait(false);
        await _reminders.RestoreAsync(cancellationToken).ConfigureAwait(false);
        _chat.ConnectionChanged += OnConnectionChanged;
    }

    private void OnResumed(object? sender, EventArgs e) => _ = RecoverAsync(includeOverdueReminders: true, CancellationToken.None);

    private void OnNetworkAvailabilityChanged(object? sender, bool online)
    {
        if (online)
        {
            _ = RecoverAsync(includeOverdueReminders: true, CancellationToken.None);
        }
    }

    private void OnConnectionChanged(object? sender, ConnectionSnapshot snapshot)
    {
        // 自动重连成功也必须追平离线期间的数据；首次启动由 RecoverAsync 完成，
        // 因此本事件在首次恢复后才订阅，避免启动时重复拉取。
        if (snapshot.Status == ConnectionStatus.Connected)
        {
            _ = SyncAfterConnectionAsync(CancellationToken.None);
        }
    }

    private async Task SyncAfterConnectionAsync(CancellationToken cancellationToken)
    {
        await _recoveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed || _auth.State != AuthState.Authenticated)
            {
                return;
            }

            await _sync.SyncFullAsync(cancellationToken).ConfigureAwait(false);
            await _sync.SyncFactsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "连接恢复后的全量同步失败（将在下次重连重试）");
        }
        finally
        {
            _recoveryGate.Release();
        }
    }

    private async Task RecoverAsync(bool includeOverdueReminders, CancellationToken cancellationToken)
    {
        await _recoveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed || _auth.State != AuthState.Authenticated)
            {
                return;
            }

            await _chat.ReconnectAsync().ConfigureAwait(false);
            await _sync.SyncFullAsync(cancellationToken).ConfigureAwait(false);
            await _sync.SyncFactsAsync(cancellationToken).ConfigureAwait(false);
            if (includeOverdueReminders)
            {
                await _reminders.ResendOverdueAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "桌面后台恢复失败（界面保持可用）");
        }
        finally
        {
            _recoveryGate.Release();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _power.Resumed -= OnResumed;
        _power.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        _chat.ConnectionChanged -= OnConnectionChanged;
        if (_power is IDisposable disposable)
        {
            disposable.Dispose();
        }

        // 已排队的恢复回调仍可能进入 finally.Release；不提前释放信号量。
    }
}
