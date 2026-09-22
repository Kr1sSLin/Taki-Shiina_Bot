using Xunit;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using TKSDesktop.Contracts;
using TKSDesktop.Core.Auth;
using TKSDesktop.Core.Network;

namespace TKSDesktop.Tests;

/// <summary>按预设响应体回复的桩处理器（记录请求次数与请求头，供断言重放 / 续签行为）。</summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<(HttpStatusCode Status, string Body)> _responses;

    public StubHttpMessageHandler(params (HttpStatusCode Status, string Body)[] responses)
    {
        _responses = new Queue<(HttpStatusCode, string)>(responses);
    }

    public int RequestCount { get; private set; }

    public List<string?> TraceHeaders { get; } = [];

    public List<string?> AuthorizationHeaders { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestCount++;
        TraceHeaders.Add(request.Headers.TryGetValues(RestClient.TraceHeaderName, out var trace) ? string.Join(",", trace) : null);
        AuthorizationHeaders.Add(request.Headers.TryGetValues("Authorization", out var auth) ? string.Join(",", auth) : null);

        if (_responses.Count == 0)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"code":0,"message":"ok","data":null}""", Encoding.UTF8, "application/json"),
            });
        }

        var (status, body) = _responses.Dequeue();
        return Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });
    }
}

/// <summary>可编程的 Token 提供者（记录续签次数，模拟网络故障 / 401 两类结果）。</summary>
internal sealed class FakeTokenProvider : IAccessTokenProvider
{
    public string? AccessToken { get; set; } = "access-token-old";

    public bool CanRefresh { get; set; } = true;

    public int RefreshCount { get; private set; }

    public List<string> UnauthorizedReasons { get; } = [];

    /// <summary>续签结果：默认返回新 Token。</summary>
    public AccessTokenRefreshOutcome NextRefreshOutcome { get; set; } =
        AccessTokenRefreshOutcome.Ok("access-token-new");

    public Task<AccessTokenRefreshOutcome> RefreshAsync(CancellationToken cancellationToken)
    {
        RefreshCount++;
        var outcome = NextRefreshOutcome;
        if (outcome.Success)
        {
            AccessToken = outcome.AccessToken;
        }

        return Task.FromResult(outcome);
    }

    public void OnUnauthorized(string reason, int? httpStatus) => UnauthorizedReasons.Add(reason);
}

/// <summary>
/// RestClient 的**业务失败 = HTTP 200 + code != 0**判定测试（PRD §5.6 **陷阱 4**），
/// 以及 traceId 注入、超时分级、401 互斥续签与重放一次的行为。
/// </summary>
public sealed class NetworkRestClientEnvelopeTests
{
    private static RestClient CreateClient(StubHttpMessageHandler handler, FakeTokenProvider tokens)
        => new(
            new HttpClient(handler),
            tokens,
            () => "https://takishiinabot.top/api/v1/",
            NullLogger<RestClient>.Instance);

    /* ---------------- 陷阱 4：HTTP 200 + code != 0 是失败 -------------------- */

    [Fact]
    public async Task GetAsync_Http200WithNonZeroCode_IsFailureWithCode()
    {
        var handler = new StubHttpMessageHandler((HttpStatusCode.OK, """{"code":40201,"message":"积分不足","data":null}"""));
        var tokens = new FakeTokenProvider();
        using var client = CreateClient(handler, tokens);

        var result = await client.GetAsync<object>("points/balance");

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCatalog.PointsInsufficient, result.Error!.Code);
        Assert.Equal(200, result.Error.HttpStatus);
        Assert.Equal(ApiFailureKind.Business, result.Error.Kind);
        // 未知/已知码都必须能映射到文案 key（V-W-S6：不得显示原始数字）。
        Assert.Equal("error.api.40201", result.ErrorKey);
    }

    [Fact]
    public async Task GetAsync_Http200WithZeroCode_IsSuccessAndTakesData()
    {
        var handler = new StubHttpMessageHandler(
            (HttpStatusCode.OK, """{"code":0,"message":"ok","data":{"balance":123},"traceId":"trace_srv"}"""));
        var tokens = new FakeTokenProvider();
        using var client = CreateClient(handler, tokens);

        var result = await client.GetAsync<BalanceProbe>("points/balance");

        Assert.True(result.IsSuccess);
        Assert.True(result.TryGetValue(out var value));
        Assert.Equal(123, value.Balance);
        Assert.Equal("trace_srv", result.TraceId);
    }

    [Fact]
    public async Task GetAsync_Http200WithZeroCodeAndNullData_IsSuccessWithoutValue()
    {
        // data:null 不得当成失败，也不得抛错（NFR-W-12）。
        var handler = new StubHttpMessageHandler((HttpStatusCode.OK, """{"code":0,"message":"ok","data":null}"""));
        var tokens = new FakeTokenProvider();
        using var client = CreateClient(handler, tokens);

        var result = await client.GetAsync<BalanceProbe>("settings/city");

        Assert.True(result.IsSuccess);
        Assert.False(result.HasValue);
        Assert.False(result.TryGetValue(out _));
    }

    [Fact]
    public async Task GetAsync_NonEnvelopeImageOfHealthz_IsToleratedAsBusinessFailureWhenCodeMissing()
    {
        // 无信封形态（如 ws_api 的 /healthz）：code 缺失 → 形状不可用，返回失败而非抛错。
        var handler = new StubHttpMessageHandler((HttpStatusCode.OK, """{"status":"ok","service":"ws_api"}"""));
        var tokens = new FakeTokenProvider();
        using var client = CreateClient(handler, tokens);

        var result = await client.GetAsync<BalanceProbe>("healthz");

        Assert.True(result.IsFailure);
        Assert.Equal(ApiFailureKind.InvalidResponse, result.Error!.Kind);
    }

    /* ---------------- 401 重放 + 双形状错误码 --------------------------------- */

    [Fact]
    public async Task GetAsync_401Then200_RefreshesOnceAndReplaysExactlyOnce()
    {
        var handler = new StubHttpMessageHandler(
            (HttpStatusCode.Unauthorized, """{"detail":{"code":40101,"message":"鉴权失败"}}"""),
            (HttpStatusCode.OK, """{"code":0,"message":"ok","data":{"balance":7}}"""));
        var tokens = new FakeTokenProvider();
        using var client = CreateClient(handler, tokens);

        var result = await client.GetAsync<BalanceProbe>("points/balance");

        Assert.True(result.IsSuccess);
        Assert.Equal(1, tokens.RefreshCount);
        Assert.Equal(2, handler.RequestCount);
        // 重放用的是新 Token。
        Assert.Equal("Bearer access-token-old", handler.AuthorizationHeaders[0]);
        Assert.Equal("Bearer access-token-new", handler.AuthorizationHeaders[1]);
    }

    [Fact]
    public async Task GetAsync_401AndRefreshReturns401_ClearsCredentials()
    {
        var handler = new StubHttpMessageHandler(
            (HttpStatusCode.Unauthorized, """{"detail":{"code":40101,"message":"鉴权失败"}}"""));
        var tokens = new FakeTokenProvider
        {
            NextRefreshOutcome = AccessTokenRefreshOutcome.Failed(
                new ApiError(ApiFailureKind.Unauthorized, "refresh rejected", ErrorCatalog.TokenInvalid, 401),
                credentialsInvalidated: false),
        };
        using var client = CreateClient(handler, tokens);

        var result = await client.GetAsync<BalanceProbe>("points/balance");

        Assert.True(result.IsFailure);
        Assert.Equal(ApiFailureKind.Unauthorized, result.Error!.Kind);
        Assert.Equal(ErrorCatalog.AuthFailed, result.Error.Code);
        Assert.True(result.Error.ShouldClearCredentials);
        Assert.Single(tokens.UnauthorizedReasons);
        Assert.Equal(AuthFailureReasons.RefreshFailed, tokens.UnauthorizedReasons[0]);
    }

    [Fact]
    public async Task GetAsync_NetworkFailureDuringRefresh_DoesNotClearCredentials()
    {
        // ⚠️ EDGE-W-2：网络故障（HttpStatus 为 null）**必须保留凭据、不踢人**。
        var handler = new StubHttpMessageHandler(
            (HttpStatusCode.Unauthorized, """{"code":40101}"""));
        var tokens = new FakeTokenProvider
        {
            NextRefreshOutcome = AccessTokenRefreshOutcome.Failed(
                new ApiError(ApiFailureKind.Network, "connection refused"),
                credentialsInvalidated: false),
        };
        using var client = CreateClient(handler, tokens);

        var result = await client.GetAsync<BalanceProbe>("points/balance");

        Assert.True(result.IsFailure);
        // 网络故障的判定结果仍是 401（原请求状态），但清凭据的调用**不应**发生。
        Assert.False(tokens.UnauthorizedReasons.Count > 0 && tokens.UnauthorizedReasons[0] == AuthFailureReasons.NoToken);
        Assert.Empty(tokens.UnauthorizedReasons);
    }

    [Fact]
    public async Task GetAsync_401OnAuthEndpointWithAllowRefreshFalse_DoesNotRefresh()
    {
        var handler = new StubHttpMessageHandler((HttpStatusCode.Unauthorized, """{"code":40101}"""));
        var tokens = new FakeTokenProvider();
        using var client = CreateClient(handler, tokens);

        // `/auth/refresh` 自身不得触发续签，否则递归。
        var result = await client.PostEnvelopeLessAsync<BalanceProbe>(
            "auth/refresh", new { refreshToken = "r" }, authenticated: false, allowRefresh: false);

        Assert.True(result.IsFailure);
        Assert.Equal(0, tokens.RefreshCount);
        Assert.Equal(1, handler.RequestCount);
    }

    /* ---------------- traceId（FR-W-NET-1） --------------------------------- */

    [Fact]
    public async Task GetAsync_SendsTraceHeaderInRequiredFormat()
    {
        var handler = new StubHttpMessageHandler((HttpStatusCode.OK, """{"code":0,"data":null}"""));
        var tokens = new FakeTokenProvider();
        using var client = CreateClient(handler, tokens);

        var result = await client.GetAsync<BalanceProbe>("points/balance");

        var trace = Assert.Single(handler.TraceHeaders);
        Assert.NotNull(trace);
        Assert.StartsWith(RestClient.TracePrefix, trace, StringComparison.Ordinal);
        // trace_{Guid:N} → 前缀 + 32 位十六进制。
        Assert.Equal(RestClient.TracePrefix.Length + 32, trace!.Length);
        Assert.Equal(trace, result.TraceId);
    }

    [Fact]
    public void NewTraceId_IsUniquePerCall()
    {
        Assert.NotEqual(RestClient.NewTraceId(), RestClient.NewTraceId());
    }

    /* ---------------- 超时分级（FR-W-NET-4） --------------------------------- */

    [Fact]
    public void ProtocolConstants_TimeoutTiers_AreDistinctAndInteractionExceedsNginxReadTimeout()
    {
        // nginx proxy_read_timeout = 60s：互动接口**必须** > 60s。
        Assert.True(
            ProtocolConstants.InteractionTimeoutMs > 60_000,
            "InteractionTimeoutMs 必须 > 60000ms（nginx proxy_read_timeout=60s）");
        Assert.Equal(30_000, ProtocolConstants.RestTimeoutMs);
        Assert.Equal(90_000, ProtocolConstants.InteractionTimeoutMs);
        Assert.Equal(10_000, ProtocolConstants.ConnectivityTimeoutMs);
        Assert.NotEqual(ProtocolConstants.RestTimeoutMs, ProtocolConstants.InteractionTimeoutMs);
    }

    [Fact]
    public async Task GetAsync_Timeout_IsClassifiedAsTimeoutAndPreservesCredentials()
    {
        // 桩处理器永不返回 → 触发本地 CTS 超时；kind 必须是 Timeout 而非 Network（凭据保留）。
        var handler = new HangingHttpMessageHandler();
        var tokens = new FakeTokenProvider();
        using var client = new RestClient(
            new HttpClient(handler),
            tokens,
            () => "https://takishiinabot.top/api/v1/",
            NullLogger<RestClient>.Instance);

        var result = await client.GetAsync<BalanceProbe>("points/balance", timeoutMs: 150);

        Assert.True(result.IsFailure);
        Assert.Equal(ApiFailureKind.Timeout, result.Error!.Kind);
        Assert.Equal(ProtocolConstants.ClientErrorTimeout, result.Error.ClientCode);
        Assert.Null(result.Error.HttpStatus);
        Assert.False(result.Error.ShouldClearCredentials);
        Assert.Empty(tokens.UnauthorizedReasons);
    }

    private sealed class HangingHttpMessageHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    /* ---------------- 基址非法 / 空响应体不抛错 ------------------------------ */

    [Fact]
    public async Task GetAsync_InvalidBaseUrl_ReturnsConfigurationFailureWithoutThrowing()
    {
        var handler = new StubHttpMessageHandler((HttpStatusCode.OK, """{"code":0}"""));
        var tokens = new FakeTokenProvider();
        using var client = new RestClient(
            new HttpClient(handler),
            tokens,
            () => "not-a-url",
            NullLogger<RestClient>.Instance);

        var result = await client.GetAsync<BalanceProbe>("points/balance");

        Assert.True(result.IsFailure);
        Assert.Equal(ApiFailureKind.Configuration, result.Error!.Kind);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task GetAsync_EmptyBody_IsInvalidResponseInsteadOfSuccess()
    {
        // 空响应体无法判定 `code == 0` → 不得当成成功（NFR-W-12：不抛错，返回强类型失败）。
        var handler = new StubHttpMessageHandler((HttpStatusCode.OK, string.Empty));
        var tokens = new FakeTokenProvider();
        using var client = CreateClient(handler, tokens);

        var result = await client.GetAsync<BalanceProbe>("points/balance");

        Assert.True(result.IsFailure);
        Assert.True(result.IsFailure);
        Assert.Equal(ApiFailureKind.InvalidResponse, result.Error!.Kind);
    }

    /// <summary>
    /// 探针 DTO：⚠️ 必须**逐字段**标注 <c>[JsonPropertyName]</c> ——
    /// <see cref="WsFrameParser.Options"/> 关掉了大小写不敏感与全局命名策略（FR-W-PROTO-2），
    /// 少了标注就会静默取到默认值（这正是该契约要防的缺陷）。
    /// </summary>
    private sealed class BalanceProbe
    {
        [System.Text.Json.Serialization.JsonPropertyName("balance")]
        public int Balance { get; set; }
    }
}
