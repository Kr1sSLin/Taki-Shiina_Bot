using TKSDesktop.Contracts;
using TKSDesktop.Contracts.Dtos;
using TKSDesktop.Core.Services.Ports;

namespace TKSDesktop.Core.Services;

/// <summary>
/// 养成 REST 门面。M0-M3 的聊天外壳依赖其只读快照；具体 M4 页面复用同一实现。
/// 服务端失败转换为稳定的空数据/失败结果，不让个人中心依赖阻断聊天主界面启动。
/// </summary>
public sealed class GamificationService : IGamificationService
{
    private readonly IGamificationPort _port;
    private readonly TimeProvider _timeProvider;

    public GamificationService(IGamificationPort port, TimeProvider timeProvider)
    {
        _port = port;
        _timeProvider = timeProvider;
    }

    public event EventHandler<ProgressSnapshot>? ProgressChanged;

    public event EventHandler<LevelChangeFeedback>? LevelChanged;

    public event EventHandler<StreakWarningPayloadDto>? StreakWarning
    {
        add => _ = value;
        remove => _ = value;
    }

    public ProgressSnapshot? Current { get; private set; }

    public async Task<ProgressSnapshot> RefreshOverviewAsync(CancellationToken ct = default)
    {
        var call = await _port.GetPointsOverviewAsync(ProtocolConstants.RestTimeoutMs, ct).ConfigureAwait(false);
        if (!call.Success || call.Value is null)
        {
            return Current ?? EmptySnapshot(isOfflineCache: true);
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

        Current = next;
        ProgressChanged?.Invoke(this, next);
        return next;
    }

    public async Task<InteractionItemsDataDto> GetInteractionItemsAsync(CancellationToken ct = default)
        => (await _port.GetInteractionItemsAsync(ProtocolConstants.RestTimeoutMs, ct).ConfigureAwait(false)).Value
            ?? new InteractionItemsDataDto();

    public async Task<InteractionSendOutcome> SendInteractionAsync(string itemId, string requestId, string? text, CancellationToken ct = default)
    {
        var call = await _port.SendInteractionAsync(itemId, requestId, text, ProtocolConstants.InteractionTimeoutMs, ct).ConfigureAwait(false);
        if (!call.Success || call.Value is null)
        {
            var errorCode = int.TryParse(call.ErrorCode, CultureInfo.InvariantCulture, out var parsed) ? (int?)parsed : null;
            return new(false, errorCode, call.I18nKey, null, false, null, [], []);
        }

        var data = call.Value;
        return new(
            data.Success,
            data.ErrorCode,
            data.ErrorCode is { } code ? ErrorCatalog.I18nKeyOf(code) : null,
            data.Balance,
            data.Refunded ?? false,
            data.FallbackText,
            data.MergedRequestIds ?? [],
            data.MergedRequestIds ?? []);
    }

    public async Task<PagedDataDto<PointsLedgerItemDto>> GetPointsHistoryAsync(int page, int pageSize, string? reasonCode = null, CancellationToken ct = default)
        => (await _port.GetPointsHistoryAsync(page, pageSize, reasonCode, ProtocolConstants.RestTimeoutMs, ct).ConfigureAwait(false)).Value
            ?? new PagedDataDto<PointsLedgerItemDto>();

    public async Task<LevelConfigDataDto> GetLevelConfigAsync(CancellationToken ct = default)
        => (await _port.GetLevelConfigAsync(ProtocolConstants.RestTimeoutMs, ct).ConfigureAwait(false)).Value
            ?? new LevelConfigDataDto();

    public async Task<MakeupCardSummaryDto> GetMakeupCardAsync(CancellationToken ct = default)
        => (await _port.GetMakeupCardAsync(ProtocolConstants.RestTimeoutMs, ct).ConfigureAwait(false)).Value
            ?? new MakeupCardSummaryDto();

    public async Task<MakeupCandidatesDataDto> GetMakeupCandidatesAsync(int limit = 120, CancellationToken ct = default)
        => (await _port.GetMakeupCandidatesAsync(limit, ProtocolConstants.RestTimeoutMs, ct).ConfigureAwait(false)).Value
            ?? new MakeupCandidatesDataDto();

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
        if (feedback is not null)
        {
            LevelChanged?.Invoke(this, feedback);
        }

        return new(data.Success, null, null, data.AvailableCards, feedback);
    }

    public async Task<PagedDataDto<MakeupCardRecordDto>> GetMakeupHistoryAsync(int page, int pageSize, CancellationToken ct = default)
        => (await _port.GetMakeupHistoryAsync(page, pageSize, ProtocolConstants.RestTimeoutMs, ct).ConfigureAwait(false)).Value
            ?? new PagedDataDto<MakeupCardRecordDto>();

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
