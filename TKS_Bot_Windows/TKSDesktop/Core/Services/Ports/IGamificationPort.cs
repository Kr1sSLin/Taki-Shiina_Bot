using TKSDesktop.Contracts.Dtos;

namespace TKSDesktop.Core.Services.Ports;

/// <summary>
/// 养成（积分 / 等级 / 互动 / 补签卡）REST 端口（窄接口，PRD §7.0）。
///
/// 存在的理由：这 9 个端点挂在 `ws_api` 进程（`:8001`），业务失败是 **HTTP 200 + `code != 0`**，
/// 且 **互动端点的超时必须 > 60s**（陷阱 7 / FR-W-NET-4）。把它们收进专用端口，
/// 使领域层无需依赖 `Core.Network.RestClient` 具体类，也让超时可被注入与断言。
///
/// ⚠️ **超时语义**：所有方法**必须**接受 <paramref name="timeoutMs"/>，并透传给底层 HTTP 调用。
///    `POST /interaction/send` 传 <c>ProtocolConstants.InteractionTimeoutMs</c>（90 000），
///    其余传 <c>ProtocolConstants.RestTimeoutMs</c>（30 000）。
/// ⚠️ 实现方**不得**只判断 HTTP 状态码：必须解包信封并按 `code` 判定成败（陷阱 4）。
/// ⚠️ 未知字段与未知结构必须容忍（NFR-W-12）。
/// </summary>
public interface IGamificationPort
{
    /// <summary>`GET /points/overview` —— 首屏聚合（余额 + 等级 + 补签卡一次取回，FR-W-PROG-5）。</summary>
    Task<PortCall<PointsOverviewDataDto>> GetPointsOverviewAsync(
        int timeoutMs,
        CancellationToken cancellationToken = default);

    /// <summary>`GET /points/balance` —— 仅需余额时使用。</summary>
    Task<PortCall<PointsBalanceDataDto>> GetPointsBalanceAsync(
        int timeoutMs,
        CancellationToken cancellationToken = default);

    /// <summary>`GET /level/status` —— 仅需等级时可代替 `/points/overview`。</summary>
    Task<PortCall<LevelStatusDto>> GetLevelStatusAsync(
        int timeoutMs,
        CancellationToken cancellationToken = default);

    /// <summary>`GET /level/config` —— 等级阈值表（`levels[]` 为 **snake_case**，C-8 例外 ①）。</summary>
    Task<PortCall<LevelConfigDataDto>> GetLevelConfigAsync(
        int timeoutMs,
        CancellationToken cancellationToken = default);

    /// <summary>`GET /points/history?page&amp;pageSize&amp;reasonCode` —— 积分流水（`items[]` 为 snake_case，例外 ②）。</summary>
    Task<PortCall<PagedDataDto<PointsLedgerItemDto>>> GetPointsHistoryAsync(
        int page,
        int pageSize,
        string? reasonCode,
        int timeoutMs,
        CancellationToken cancellationToken = default);

    /// <summary>`GET /interaction/items` —— 互动菜单；置灰依据用服务端 `affordable`（FR-W-INT-4）。</summary>
    Task<PortCall<InteractionItemsDataDto>> GetInteractionItemsAsync(
        int timeoutMs,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// `POST /interaction/send` —— 送出互动。
    /// ⚠️ <paramref name="requestId"/> 是**幂等键**，重试必须复用同一值（FR-W-INT-11）。
    /// ⚠️ 超时**必须**传 <c>ProtocolConstants.InteractionTimeoutMs</c>（90s &gt; nginx 的 60s，陷阱 7）。
    /// </summary>
    Task<PortCall<InteractionSendDataDto>> SendInteractionAsync(
        string itemId,
        string requestId,
        string? text,
        int timeoutMs,
        CancellationToken cancellationToken = default);

    /// <summary>`GET /points/makeup-card` —— 补签卡汇总（`monthlyGrant` / `maxAvailable` 由服务端下发）。</summary>
    Task<PortCall<MakeupCardSummaryDto>> GetMakeupCardAsync(
        int timeoutMs,
        CancellationToken cancellationToken = default);

    /// <summary>`GET /points/makeup-card/candidates?limit` —— **补签日历唯一数据源**（FR-W-MC-2）。</summary>
    Task<PortCall<MakeupCandidatesDataDto>> GetMakeupCandidatesAsync(
        int limit,
        int timeoutMs,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// `POST /points/makeup-card/use` —— 使用补签卡。
    /// ⚠️ **必须由用户主动点击**并二次确认（FR-W-MC-3）；失败不消耗卡片。
    /// </summary>
    Task<PortCall<MakeupCardUseDataDto>> UseMakeupCardAsync(
        string targetDate,
        int timeoutMs,
        CancellationToken cancellationToken = default);

    /// <summary>`GET /points/makeup-card/history?page&amp;pageSize` —— 补签卡流水（卡记录 snake_case，例外 ③）。</summary>
    Task<PortCall<PagedDataDto<MakeupCardRecordDto>>> GetMakeupHistoryAsync(
        int page,
        int pageSize,
        int timeoutMs,
        CancellationToken cancellationToken = default);
}
