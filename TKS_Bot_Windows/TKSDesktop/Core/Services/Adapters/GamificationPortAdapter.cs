using TKSDesktop.Contracts;
using TKSDesktop.Contracts.Dtos;
using TKSDesktop.Core.Network;
using TKSDesktop.Core.Services.Ports;

namespace TKSDesktop.Core.Services.Adapters;

/// <summary>
/// 养成 REST 端口的适配器：把窄端口 <see cref="IGamificationPort"/> 接到
/// <see cref="RestClient"/>（PRD §7.0 / §3.3 依赖方向：Core.Services → Core.Network）。
///
/// <para>本适配器只做「路径 + 查询参数 + 超时」的形状转换，**不做业务判断**：
/// 信封解包、业务失败（HTTP 200 + `code != 0`）、401 互斥续签、traceId 全部由
/// <see cref="RestClient"/> 负责（陷阱 3 / 4 / FR-W-AUTH-4）。</para>
///
/// ⚠️ 所有路径**不含** `apiBaseUrl` 前缀（`RestClient` 已持有基址），
///    因此这里用 `/points/overview` 这类**相对路径**，与 §7.0 的接口清单一一对应。
/// </summary>
public sealed class GamificationPortAdapter : IGamificationPort
{
    private readonly RestClient _rest;

    public GamificationPortAdapter(RestClient rest)
    {
        ArgumentNullException.ThrowIfNull(rest);
        _rest = rest;
    }

    /// <inheritdoc />
    public Task<PortCall<PointsOverviewDataDto>> GetPointsOverviewAsync(
        int timeoutMs,
        CancellationToken cancellationToken = default)
        => WrapAsync(_rest.GetAsync<PointsOverviewDataDto>(
            "/points/overview", query: null, timeoutMs: timeoutMs, cancellationToken: cancellationToken));

    /// <inheritdoc />
    public Task<PortCall<PointsBalanceDataDto>> GetPointsBalanceAsync(
        int timeoutMs,
        CancellationToken cancellationToken = default)
        => WrapAsync(_rest.GetAsync<PointsBalanceDataDto>(
            "/points/balance", query: null, timeoutMs: timeoutMs, cancellationToken: cancellationToken));

    /// <inheritdoc />
    public Task<PortCall<LevelStatusDto>> GetLevelStatusAsync(
        int timeoutMs,
        CancellationToken cancellationToken = default)
        => WrapAsync(_rest.GetAsync<LevelStatusDto>(
            "/level/status", query: null, timeoutMs: timeoutMs, cancellationToken: cancellationToken));

    /// <inheritdoc />
    public Task<PortCall<LevelConfigDataDto>> GetLevelConfigAsync(
        int timeoutMs,
        CancellationToken cancellationToken = default)
        => WrapAsync(_rest.GetAsync<LevelConfigDataDto>(
            "/level/config", query: null, timeoutMs: timeoutMs, cancellationToken: cancellationToken));

    /// <inheritdoc />
    public Task<PortCall<PagedDataDto<PointsLedgerItemDto>>> GetPointsHistoryAsync(
        int page,
        int pageSize,
        string? reasonCode,
        int timeoutMs,
        CancellationToken cancellationToken = default)
    {
        var query = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["page"] = page.ToString(System.Globalization.CultureInfo.InvariantCulture),
            // 后端 pageSize 上限 100（§7.0）—— 客户端在此钳制，避免无谓的 400。
            ["pageSize"] = Math.Clamp(pageSize, 1, 100).ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        if (!string.IsNullOrWhiteSpace(reasonCode))
        {
            query["reasonCode"] = reasonCode;
        }

        return WrapAsync(_rest.GetAsync<PagedDataDto<PointsLedgerItemDto>>(
            "/points/history", query, timeoutMs, cancellationToken: cancellationToken));
    }

    /// <inheritdoc />
    public Task<PortCall<InteractionItemsDataDto>> GetInteractionItemsAsync(
        int timeoutMs,
        CancellationToken cancellationToken = default)
        => WrapAsync(_rest.GetAsync<InteractionItemsDataDto>(
            "/interaction/items", query: null, timeoutMs: timeoutMs, cancellationToken: cancellationToken));

    /// <inheritdoc />
    public Task<PortCall<InteractionSendDataDto>> SendInteractionAsync(
        string itemId,
        string requestId,
        string? text,
        int timeoutMs,
        CancellationToken cancellationToken = default)
    {
        // ⚠️ `requestId` 即幂等键；重试必须复用同一值（FR-W-INT-11）。
        var body = new InteractionSendRequestDto
        {
            ItemId = itemId,
            RequestId = requestId,
            Text = string.IsNullOrWhiteSpace(text) ? null : text,
        };

        // ⚠️ 超时由调用方传 InteractionTimeoutMs（90s）—— 必须 > nginx 的 60s（陷阱 7）。
        return WrapAsync(_rest.PostAsync<InteractionSendDataDto>(
            "/interaction/send", body, timeoutMs, cancellationToken: cancellationToken));
    }

    /// <inheritdoc />
    public Task<PortCall<MakeupCardSummaryDto>> GetMakeupCardAsync(
        int timeoutMs,
        CancellationToken cancellationToken = default)
        => WrapAsync(_rest.GetAsync<MakeupCardSummaryDto>(
            "/points/makeup-card", query: null, timeoutMs: timeoutMs, cancellationToken: cancellationToken));

    /// <inheritdoc />
    public Task<PortCall<MakeupCandidatesDataDto>> GetMakeupCandidatesAsync(
        int limit,
        int timeoutMs,
        CancellationToken cancellationToken = default)
    {
        var query = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["limit"] = Math.Max(1, limit).ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        return WrapAsync(_rest.GetAsync<MakeupCandidatesDataDto>(
            "/points/makeup-card/candidates", query, timeoutMs, cancellationToken: cancellationToken));
    }

    /// <inheritdoc />
    public Task<PortCall<MakeupCardUseDataDto>> UseMakeupCardAsync(
        string targetDate,
        int timeoutMs,
        CancellationToken cancellationToken = default)
    {
        var body = new MakeupCardUseRequestDto { TargetDate = targetDate };
        return WrapAsync(_rest.PostAsync<MakeupCardUseDataDto>(
            "/points/makeup-card/use", body, timeoutMs, cancellationToken: cancellationToken));
    }

    /// <inheritdoc />
    public Task<PortCall<PagedDataDto<MakeupCardRecordDto>>> GetMakeupHistoryAsync(
        int page,
        int pageSize,
        int timeoutMs,
        CancellationToken cancellationToken = default)
    {
        var query = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["page"] = page.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["pageSize"] = Math.Clamp(pageSize, 1, 100).ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        return WrapAsync(_rest.GetAsync<PagedDataDto<MakeupCardRecordDto>>(
            "/points/makeup-card/history", query, timeoutMs, cancellationToken: cancellationToken));
    }

    /// <summary>
    /// 把 <see cref="ApiResult{T}"/> 转成端口结果 <see cref="PortCall{T}"/>。
    /// ⚠️ **业务失败（HTTP 200 + `code != 0`）也必须算失败** —— 这里依据
    /// <c>ApiResult.IsSuccess</c>（由 `RestClient` 按信封 `code` 判定），而**不是** HTTP 状态码（陷阱 4）。
    /// </summary>
    private static async Task<PortCall<T>> WrapAsync<T>(Task<ApiResult<T>> call)
    {
        try
        {
            var result = await call.ConfigureAwait(false);

            if (result.IsSuccess && result.Value is { } value)
            {
                return PortCall<T>.Ok(value);
            }

            // 未知码 / 无码 → 中性兜底文案（不得显示原始数字，V-W-S6）。
            var code = result.Error?.Code;
            return new PortCall<T>(false, result.FailureValue,
                code?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? result.Error?.ClientCode,
                result.ErrorKey);
        }
        catch (OperationCanceledException)
        {
            // 超时 / 主动取消：映射为客户端 TIMEOUT 文案，**不抛错**（调用方需要给出提示）。
            return PortCall<T>.Fail(
                ProtocolConstants.ClientErrorTimeout,
                ErrorCatalog.I18nKeyOf(ProtocolConstants.ClientErrorTimeout));
        }
    }
}
