using TKSDesktop.Contracts;
using TKSDesktop.Contracts.Dtos;
using TKSDesktop.Core.Services.Ports;
using TKSDesktop.App;
using TKSDesktop.Core.Data.Repositories;
using TKSDesktop.Core.Platform;
using Microsoft.Extensions.Logging;
using TKSDesktop.Core.Data.Records;

namespace TKSDesktop.Core.Services;

/// <summary>
/// Server-authoritative gamification state, persistent cache and live event delivery.
/// </summary>
public sealed class GamificationService : IGamificationService, IDisposable
{
    private readonly IGamificationPort _port;
    private readonly TimeProvider _timeProvider;
    private readonly IWssPort? _wss;
    private readonly IInteractionDelivery? _delivery;
    private readonly ProgressCacheRepository? _cache;
    private readonly IAuthService? _auth;
    private readonly IUiDispatcher? _dispatcher;
    private readonly IUserNotificationService? _notifications;
    private readonly ILogger<GamificationService>? _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _eventGate = new(1, 1);
    private readonly Dictionary<string, long> _feedbackKeys = new(StringComparer.Ordinal);
    private bool _disposed;

    public GamificationService(IGamificationPort port, TimeProvider timeProvider,
        IWssPort? wss = null, IInteractionDelivery? delivery = null,
        ProgressCacheRepository? cache = null, IAuthService? auth = null,
        IUiDispatcher? dispatcher = null, IUserNotificationService? notifications = null,
        ILogger<GamificationService>? logger = null)
    {
        _port = port;
        _timeProvider = timeProvider;
        _wss = wss;
        _delivery = delivery;
        _cache = cache;
        _auth = auth;
        _dispatcher = dispatcher;
        _notifications = notifications;
        _logger = logger;
        if (_wss is not null)
        {
            _wss.FrameReceived += OnFrame;
        }
    }

    public event EventHandler<ProgressSnapshot>? ProgressChanged;

    public event EventHandler<LevelChangeFeedback>? LevelChanged;

    public event EventHandler<StreakWarningPayloadDto>? StreakWarning;

    public ProgressSnapshot? Current { get; private set; }

    public async Task<ProgressSnapshot> RefreshOverviewAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await RefreshCoreAsync(ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task<ProgressSnapshot> RefreshCoreAsync(CancellationToken ct)
    {
        var call = await _port.GetPointsOverviewAsync(ProtocolConstants.RestTimeoutMs, ct).ConfigureAwait(false);
        if (!call.Success || call.Value is null)
        {
            if (Current is null && _cache is not null && _auth?.UserId is { } user)
            {
                var cached = await _cache.GetProgressAsync(user, ct).ConfigureAwait(false);
                if (cached is not null)
                    Current = new(cached.Balance, cached.LevelCode, cached.LevelName, cached.ContinuousDays,
                        cached.DaysToNextLevel, cached.NextLevelThresholdDays, cached.NextLevelName,
                        cached.BreakDeadlineDate, cached.GapDays, cached.IsDefaultLevel,
                        cached.AvailableMakeupCards, cached.SyncedAt, true);
            }
            Publish((Current ?? EmptySnapshot(true)) with { IsOfflineCache = true });
            return Current!;
        }

        var level = call.Value.Level ?? new LevelStatusDto { LevelCode = LevelVisuals.DefaultLevelCode, IsDefaultLevel = true };
        var next = new ProgressSnapshot(
            call.Value.Balance,
            string.IsNullOrWhiteSpace(level.LevelCode) ? LevelVisuals.DefaultLevelCode : level.LevelCode,
            LevelVisuals.ResolveDisplayName(level.LevelCode, level.LevelName),
            level.ContinuousDays,
            level.DaysToNextLevel,
            level.NextLevelThresholdDays,
            level.NextLevelName,
            level.BreakDeadlineDate,
            level.GapDays,
            level.IsDefaultLevel,
            call.Value.MakeupCard?.Available ?? level.AvailableMakeupCards ?? 0,
            _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            IsOfflineCache: false);

        if (_cache is not null && _auth?.UserId is { } userId)
            await _cache.SaveProgressAsync(userId, call.Value, ct).ConfigureAwait(false);
        Publish(next);
        return next;
    }

    public async Task<InteractionItemsDataDto> GetInteractionItemsAsync(CancellationToken ct = default)
    {
        var call = await _port.GetInteractionItemsAsync(ProtocolConstants.RestTimeoutMs, ct).ConfigureAwait(false);
        var data = Require(call);
        await SetBalanceAsync(data.Balance, ct).ConfigureAwait(false);
        return data;
    }

    public async Task<InteractionSendOutcome> SendInteractionAsync(string itemId, string requestId, string? text, CancellationToken ct = default)
    {
        if (_delivery is not null) await _delivery.BeginAsync(requestId, itemId, text).ConfigureAwait(false);
        PortCall<InteractionSendDataDto> call;
        try
        {
            call = await _port.SendInteractionAsync(itemId, requestId, text, ProtocolConstants.InteractionTimeoutMs, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (_delivery is not null) await _delivery.CompleteAsync(requestId, null, false, ProtocolConstants.ClientErrorTimeout).ConfigureAwait(false);
            await RefreshOverviewAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        var data = call.Value;
        var success = call.Success && data?.Success == true;
        var code = int.TryParse(call.ErrorCode, out var parsed) ? parsed : data?.ErrorCode;
        if (data?.Balance is { } balance) await SetBalanceAsync(balance, ct).ConfigureAwait(false);
        if (_delivery is not null) await _delivery.CompleteAsync(requestId, data, success, call.ErrorCode).ConfigureAwait(false);
        if (data?.Balance is null || data.Refunded == true)
            await RefreshOverviewAsync(ct).ConfigureAwait(false);
        return new(success, code, success ? null : call.ErrorCode == ProtocolConstants.ClientErrorTimeout
                ? "interaction.timeout" : code.HasValue ? ErrorCatalog.I18nKeyOf(code) : call.I18nKey,
            data?.Balance, data?.Refunded ?? false, data?.FallbackText,
            data?.MergedRequestIds ?? [], success ? (data?.MergedRequestIds ?? []).Append(requestId).ToArray() : []);
    }

    public async Task<PagedDataDto<PointsLedgerItemDto>> GetPointsHistoryAsync(int page, int pageSize, string? reasonCode = null, CancellationToken ct = default)
    {
        var call = await _port.GetPointsHistoryAsync(page, pageSize, reasonCode, ProtocolConstants.RestTimeoutMs, ct).ConfigureAwait(false);
        if (call.Success && call.Value is { } data)
        {
            if (_cache is not null && _auth?.UserId is { } user)
                await _cache.SaveLedgerAsync(data.Items.Select(i => new LedgerCacheRecord(i.Id, user, i.ChangeAmount,
                    i.ReasonCode ?? string.Empty, i.BalanceAfter, i.RelatedItemId, i.IdempotencyKey,
                    i.CreatedAt, i.BusinessDate ?? string.Empty)).ToArray(), ct).ConfigureAwait(false);
            return data;
        }
        if (_cache is null || _auth?.UserId is not { } account) return Require(call);
        MarkOffline();
        var rows = (await _cache.GetLedgerAsync(2000, ct).ConfigureAwait(false))
            .Where(i => i.UserId == account && (string.IsNullOrWhiteSpace(reasonCode) || i.ReasonCode == reasonCode))
            .Select(i => new PointsLedgerItemDto
            {
                Id = i.Id,
                UserId = i.UserId,
                ChangeAmount = i.ChangeAmount,
                ReasonCode = i.ReasonCode,
                BalanceAfter = i.BalanceAfter,
                RelatedItemId = i.RelatedItemId,
                CreatedAt = i.CreatedAt,
                BusinessDate = i.BusinessDate
            }).ToList();
        return Page(rows, page, pageSize);
    }

    public async Task<LevelConfigDataDto> GetLevelConfigAsync(CancellationToken ct = default)
    {
        var call = await _port.GetLevelConfigAsync(ProtocolConstants.RestTimeoutMs, ct).ConfigureAwait(false);
        if (call.Success && call.Value is { } data)
        {
            data.Levels = data.Levels.Where(i => !string.IsNullOrWhiteSpace(i.LevelCode))
                .DistinctBy(i => i.LevelCode).ToList();
            if (_cache is not null) await _cache.ReplaceLevelConfigAsync(data.Levels, ct).ConfigureAwait(false);
            return data;
        }
        if (_cache is null) return Require(call);
        return new()
        {
            Levels = (await _cache.GetLevelConfigAsync(ct).ConfigureAwait(false))
            .Select(i => new LevelConfigEntryDto
            {
                LevelCode = i.LevelCode,
                LevelName = i.LevelName,
                ThresholdDays = i.ThresholdDays,
                SortOrder = i.SortOrder
            }).ToList()
        };
    }

    public async Task<MakeupCardSummaryDto> GetMakeupCardAsync(CancellationToken ct = default)
        => Require(await _port.GetMakeupCardAsync(ProtocolConstants.RestTimeoutMs, ct).ConfigureAwait(false));

    public async Task<MakeupCandidatesDataDto> GetMakeupCandidatesAsync(int limit = 120, CancellationToken ct = default)
    {
        var call = await _port.GetMakeupCandidatesAsync(limit, ProtocolConstants.RestTimeoutMs, ct).ConfigureAwait(false);
        var data = Require(call);
        data.Items = data.Items.Where(i => !string.IsNullOrWhiteSpace(i.Date)).DistinctBy(i => i.Date).ToList();
        if (_cache is not null) await _cache.ReplaceCandidatesAsync(data.Items, ct).ConfigureAwait(false);
        return data;
    }

    public async Task<MakeupCardUseOutcome> UseMakeupCardAsync(string targetDate, CancellationToken ct = default)
    {
        var call = await _port.UseMakeupCardAsync(targetDate, ProtocolConstants.RestTimeoutMs, ct).ConfigureAwait(false);
        if (!call.Success || call.Value is null)
        {
            var errorCode = int.TryParse(call.ErrorCode, CultureInfo.InvariantCulture, out var parsed) ? (int?)parsed : null;
            return new(false, errorCode, call.I18nKey, null, null);
        }

        var data = call.Value;
        var feedback = data.Level is null ? null : ToFeedback(data.Level);
        if (feedback is not null && data.Success)
        {
            EmitFeedback(feedback, data.Level?.LevelUpdatedAt ?? 0);
        }
        await RefreshOverviewAsync(ct).ConfigureAwait(false);
        return new(data.Success, null, null, data.AvailableCards, feedback);
    }

    public async Task<PagedDataDto<MakeupCardRecordDto>> GetMakeupHistoryAsync(int page, int pageSize, CancellationToken ct = default)
    {
        var call = await _port.GetMakeupHistoryAsync(page, pageSize, ProtocolConstants.RestTimeoutMs, ct).ConfigureAwait(false);
        if (call.Success && call.Value is { } data)
        {
            if (_cache is not null && _auth?.UserId is { } user)
                await _cache.SaveMakeupCardsAsync(data.Items.Select(i => new MakeupCardCacheRecord(i.Id, user,
                    i.GrantedMonth ?? string.Empty, i.Status ?? string.Empty, i.UsedForDate, i.UsedAt, i.CreatedAt)).ToArray(), ct).ConfigureAwait(false);
            return data;
        }
        if (_cache is null || _auth?.UserId is not { } account) return Require(call);
        MarkOffline();
        var rows = (await _cache.GetMakeupCardsAsync(2000, ct).ConfigureAwait(false)).Where(i => i.UserId == account)
            .Select(i => new MakeupCardRecordDto
            {
                Id = i.Id,
                UserId = i.UserId,
                GrantedMonth = i.GrantedMonth,
                Status = i.Status,
                UsedForDate = i.UsedForDate,
                UsedAt = i.UsedAt,
                CreatedAt = i.CreatedAt
            }).ToList();
        return Page(rows, page, pageSize);
    }

    private void MarkOffline() => Publish((Current ?? EmptySnapshot(true)) with { IsOfflineCache = true });

    private static PagedDataDto<T> Page<T>(List<T> rows, int page, int size)
    {
        page = Math.Max(1, page);
        size = Math.Clamp(size, 1, 100);
        return new()
        {
            Items = rows.Skip((page - 1) * size).Take(size).ToList(),
            Total = rows.Count,
            Page = page,
            PageSize = size,
            HasMore = page * size < rows.Count
        };
    }

    private static T Require<T>(PortCall<T> call) => call.Success && call.Value is not null
        ? call.Value : throw new Network.TksApiException(Network.ApiFailureKind.Network,
            "Gamification request failed", i18nKey: call.I18nKey);

    private void Dispatch(Action action)
    {
        if (_dispatcher is null) action(); else _dispatcher.Invoke(action);
    }

    private void Publish(ProgressSnapshot snapshot)
    {
        Current = snapshot;
        Dispatch(() => ProgressChanged?.Invoke(this, snapshot));
    }

    private async Task SetBalanceAsync(int balance, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cache is not null && _auth?.UserId is { } user)
                await _cache.PatchBalanceAsync(user, balance, _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(), ct).ConfigureAwait(false);
            Publish((Current ?? EmptySnapshot(false)) with { Balance = balance, IsOfflineCache = false });
        }
        finally { _gate.Release(); }
    }

    private void EmitFeedback(LevelChangeFeedback feedback, long timestamp)
    {
        var key = $"{feedback.ChangeType}:{feedback.LevelCode}:{feedback.ContinuousDays}";
        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        lock (_feedbackKeys)
        {
            if (_feedbackKeys.TryGetValue(key, out var seen) && now - seen < 10000) return;
            if (_feedbackKeys.Count > 256) _feedbackKeys.Clear();
            _feedbackKeys[key] = now;
        }
        Dispatch(() =>
        {
            LevelChanged?.Invoke(this, feedback);
            var text = feedback.ChangeType switch
            {
                "UPGRADE" => I18n.T("level.upgrade.body", feedback.LevelName),
                "RESTORE" => I18n.T("level.restore.body", feedback.LevelName),
                "RESET" => I18n.T("level.reset.body") + " " + I18n.T("profile.pointsUnaffected"),
                _ => string.Empty,
            };
            if (text.Length > 0) _notifications?.NotifyProgress(SemanticNotificationId.Level, I18n.T("notification.level.title"), text);
        });
    }

    private void OnFrame(object? sender, WsServerFrame frame) => _ = HandleFrameAsync(frame);

    public async Task HandleFrameAsync(WsServerFrame frame)
    {
        if (_disposed) return;
        await _eventGate.WaitAsync().ConfigureAwait(false);
        try
        {
            switch (frame)
            {
                case WsPointsChangedFrame points:
                    await SetBalanceAsync(points.Payload.BalanceAfter, CancellationToken.None).ConfigureAwait(false);
                    if (points.Payload.ReasonCode == "DAILY_FIRST_CHAT")
                    {
                        Dispatch(() => StreakWarning?.Invoke(this, new StreakWarningPayloadDto()));
                        await RefreshOverviewAsync().ConfigureAwait(false);
                    }
                    break;
                case WsLevelChangedFrame changed:
                    var p = changed.Payload;
                    await RefreshOverviewAsync().ConfigureAwait(false);
                    EmitFeedback(new(p.ChangeType ?? string.Empty, p.LevelCode ?? LevelVisuals.DefaultLevelCode,
                        LevelVisuals.ResolveDisplayName(p.LevelCode, p.LevelName), p.ContinuousDays,
                        p.NextLevelName, p.DaysToNextLevel, p.ChangeType == "UPGRADE", false), p.Timestamp);
                    break;
                case WsStreakWarningFrame warning:
                    Dispatch(() =>
                    {
                        StreakWarning?.Invoke(this, warning.Payload);
                        _notifications?.NotifyProgress(SemanticNotificationId.Streak, I18n.T("streak.warning.title"),
                            I18n.T("streak.warning.body", warning.Payload.GapDays, warning.Payload.RemainingDays, warning.Payload.DeadlineDate ?? string.Empty));
                    });
                    break;
                case WsMakeupCardChangedFrame cards:
                    await RefreshOverviewAsync().ConfigureAwait(false);
                    if (Current is { } snapshot) Publish(snapshot with { AvailableMakeupCards = cards.Payload.Available });
                    break;
            }
        }
        catch (Exception ex) { _logger?.LogWarning(ex, "Gamification event processing failed"); }
        finally { _eventGate.Release(); }
    }

    public void Dispose()
    {
        _disposed = true;
        if (_wss is not null) _wss.FrameReceived -= OnFrame;
    }

    private ProgressSnapshot EmptySnapshot(bool isOfflineCache) => new(
        0,
        LevelVisuals.DefaultLevelCode,
        LevelVisuals.Default.FallbackName,
        0,
        null,
        null,
        null,
        null,
        0,
        true,
        0,
        _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
        isOfflineCache);

    private static LevelChangeFeedback ToFeedback(LevelStatusDto level) => new(
        level.ChangeType ?? string.Empty,
        level.LevelCode ?? LevelVisuals.DefaultLevelCode,
        LevelVisuals.ResolveDisplayName(level.LevelCode, level.LevelName),
        level.ContinuousDays,
        level.NextLevelName,
        level.DaysToNextLevel,
        string.Equals(level.ChangeType, "UPGRADE", StringComparison.Ordinal),
        AffectsPointsBalance: false);
}
