using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TKSDesktop.Contracts;

namespace TKSDesktop.Core.Network;

/// <summary>
/// Access Token 存取与续签的提供者契约（实现者：<c>TKSDesktop.Core.Auth.AuthService</c>）。
/// RestClient **只**通过本接口触碰凭据，不直接读写文件。
/// </summary>
public interface IAccessTokenProvider
{
    /// <summary>当前 Access Token；未登录时为 <c>null</c>。</summary>
    string? AccessToken { get; }

    /// <summary>是否具备续签条件（本地有 Refresh Token）。</summary>
    bool CanRefresh { get; }

    /// <summary>
    /// 续签（**实现方必须自带互斥**：同一时刻只允许一个 refresh 在途，FR-W-AUTH-5）。
    /// **不得抛错**：网络故障返回失败结果而不抛异常（保留凭据）。
    /// </summary>
    Task<AccessTokenRefreshOutcome> RefreshAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 上报一次未被成功续签的 401/403。
    /// ⚠️ 仅在 <see cref="ErrorCatalog.ShouldClearCredentials(int?)"/> 为真（401/403）时才会被调用；
    /// 网络故障 / 超时**不会**调用（凭据必须保留）。
    /// </summary>
    /// <param name="reason">原因（取值见 <see cref="AuthFailureReasons"/>）。</param>
    /// <param name="httpStatus">触发该判定的 HTTP 状态码（应为 401/403）。</param>
    void OnUnauthorized(string reason, int? httpStatus);
}

/// <summary>续签结果（强类型，**不把错误编码进 message**）。</summary>
public sealed record AccessTokenRefreshOutcome(
    bool Success,
    string? AccessToken = null,
    ApiError? Error = null,
    bool CredentialsInvalidated = false)
{
    public static AccessTokenRefreshOutcome Ok(string accessToken) => new(true, accessToken);

    public static AccessTokenRefreshOutcome Failed(ApiError error, bool credentialsInvalidated)
        => new(false, null, error, credentialsInvalidated);
}

/// <summary><see cref="IAccessTokenProvider.OnUnauthorized"/> 的原因取值。</summary>
public static class AuthFailureReasons
{
    /// <summary>续签请求本身失败。</summary>
    public const string RefreshFailed = "refresh-failed";

    /// <summary>需要鉴权但本地无 Token。</summary>
    public const string NoToken = "no-token";

    /// <summary>服务端显式下发 <c>auth.expired</c>（WS 通道）。</summary>
    public const string AuthExpired = "auth-expired";
}

/// <summary>
/// REST 客户端（FR-W-NET-1 / FR-W-NET-4 / FR-W-AUTH-4 / FR-W-AUTH-5 / §5.2）。
///
/// <list type="bullet">
///   <item>每个请求生成 <c>trace_{Guid:N}</c>，写入请求头 <c>x-trace-id</c> 并带进日志字段
///         （FR-W-NET-1）。⚠️ **不读响应头**判定 trace：<c>ws_api</c> 不回传响应头，traceId 只在响应体。</item>
///   <item>**业务失败是 HTTP 200 + <c>code != 0</c>**（陷阱 4）：统一流程先读 body，再判 <c>code</c>，
///         **绝不**只看状态码。</item>
///   <item>超时**分级**：默认 <see cref="ProtocolConstants.RestTimeoutMs"/>（30s）、
///         <c>POST /interaction/send</c> 显式传 <see cref="ProtocolConstants.InteractionTimeoutMs"/>（90s，
///         必须 &gt; nginx <c>proxy_read_timeout=60s</c>）。</item>
///   <item>401 → 续签 → **重放原请求一次**（仅一次，避免死循环）；续签在途时其余调用排队复用结果。</item>
///   <item>全程 <c>async</c>，**不使用</b> <c>.Result</c> / <c>.Wait()</c>。</item>
/// </list>
/// </summary>
public sealed class RestClient : IDisposable
{
    /// <summary>trace 头名（FR-W-NET-1）。</summary>
    public const string TraceHeaderName = "x-trace-id";

    /// <summary>trace id 前缀。</summary>
    public const string TracePrefix = "trace_";

    private readonly HttpClient _http;
    private readonly IAccessTokenProvider _tokens;
    private readonly ILogger<RestClient> _logger;
    private readonly Func<string> _apiBaseUrlProvider;

    /// <summary>FR-W-AUTH-5：续签互斥。信号量在整个续签网络往返期间持有 → 同一时刻只允许一个 refresh 在途。</summary>
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    private bool _disposed;

    /// <param name="http">HTTP 客户端（**不修改其 Timeout**：每请求超时由调用方 CTS 控制）。</param>
    /// <param name="tokens">Token 提供者（续签实现方自带互斥）。</param>
    /// <param name="apiBaseUrlProvider">REST 基址（含 <c>/api/v1/</c> 后缀），运行时读取以便设置页即时生效。</param>
    /// <param name="logger">日志。</param>
    public RestClient(
        HttpClient http,
        IAccessTokenProvider tokens,
        Func<string> apiBaseUrlProvider,
        ILogger<RestClient> logger)
    {
        _http = http;
        _tokens = tokens;
        _apiBaseUrlProvider = apiBaseUrlProvider;
        _logger = logger;
    }

    /// <summary>
    /// 生成一个新的 trace id（<c>trace_{guid:N}</c>）。
    /// </summary>
    public static string NewTraceId() => string.Concat(TracePrefix, Guid.NewGuid().ToString("N"));

    /* ---------------------------------------------------------------------- */
    /* 公开请求入口（全部返回强类型结果，不抛异常）                                 */
    /* ---------------------------------------------------------------------- */

    /// <summary>GET（信封解包）。</summary>
    public Task<ApiResult<T>> GetAsync<T>(
        string path,
        IReadOnlyDictionary<string, string?>? query = null,
        int timeoutMs = ProtocolConstants.RestTimeoutMs,
        bool authenticated = true,
        CancellationToken cancellationToken = default)
        => SendAsync<T>(HttpMethod.Get, path, body: null, envelope: true, authenticated, allowRefresh: true,
            query, timeoutMs, cancellationToken);

    /// <summary>POST（信封解包）。</summary>
    public Task<ApiResult<T>> PostAsync<T>(
        string path,
        object? body = null,
        int timeoutMs = ProtocolConstants.RestTimeoutMs,
        bool authenticated = true,
        bool allowRefresh = true,
        CancellationToken cancellationToken = default)
        => SendAsync<T>(HttpMethod.Post, path, body, envelope: true, authenticated, allowRefresh,
            query: null, timeoutMs, cancellationToken);

    /// <summary>PUT（信封解包）。</summary>
    public Task<ApiResult<T>> PutAsync<T>(
        string path,
        object? body = null,
        int timeoutMs = ProtocolConstants.RestTimeoutMs,
        bool authenticated = true,
        CancellationToken cancellationToken = default)
        => SendAsync<T>(HttpMethod.Put, path, body, envelope: true, authenticated, allowRefresh: true,
            query: null, timeoutMs, cancellationToken);

    /// <summary>DELETE（信封解包，无返回体语义）。</summary>
    public Task<ApiResult> DeleteAsync(
        string path,
        int timeoutMs = ProtocolConstants.RestTimeoutMs,
        bool authenticated = true,
        CancellationToken cancellationToken = default)
        => SendNoContentAsync(HttpMethod.Delete, path, body: null, allowRefresh: true, authenticated, timeoutMs, cancellationToken);

    /// <summary>POST，不关心返回体（成功判定同样走 <c>code == 0</c>）。</summary>
    public Task<ApiResult> PostNoContentAsync(
        string path,
        object? body = null,
        int timeoutMs = ProtocolConstants.RestTimeoutMs,
        bool authenticated = true,
        CancellationToken cancellationToken = default)
        => SendNoContentAsync(HttpMethod.Post, path, body, allowRefresh: true, authenticated, timeoutMs, cancellationToken);

    /// <summary>
    /// `/auth/login` / `/auth/refresh` 专用：**无信封**，直接返回对象（§5.2）。
    /// </summary>
    public Task<ApiResult<T>> PostEnvelopeLessAsync<T>(
        string path,
        object? body,
        bool authenticated,
        bool allowRefresh,
        int timeoutMs = ProtocolConstants.RestTimeoutMs,
        CancellationToken cancellationToken = default)
        => SendAsync<T>(HttpMethod.Post, path, body, envelope: false, authenticated, allowRefresh,
            query: null, timeoutMs, cancellationToken);

    /* ---------------------------------------------------------------------- */
    /* 核心流程                                                                */
    /* ---------------------------------------------------------------------- */

    /// <summary>
    /// 发一个请求并解包。**永不抛异常**：所有失败（网络 / 超时 / HTTP / 业务码）都收敛为 <see cref="ApiResult{T}"/>。
    /// </summary>
    public async Task<ApiResult<T>> SendAsync<T>(
        HttpMethod method,
        string path,
        object? body,
        bool envelope,
        bool authenticated,
        bool allowRefresh,
        IReadOnlyDictionary<string, string?>? query,
        int timeoutMs,
        CancellationToken cancellationToken = default)
    {
        var traceId = NewTraceId();

        if (!TryBuildUri(path, query, out var uri, out var configurationError))
        {
            _logger.LogWarning(
                "REST 基址不可用，请求未发出 method={Method} path={Path} traceId={TraceId} reason={Reason}",
                method.Method, path, traceId, configurationError);
            return ApiResult.Fail<T>(TksApiException.Configuration(configurationError!));
        }

        // 最多两次尝试：首次 + 401 后续签重放一次（FR-W-AUTH-4）。
        var attemptedRefresh = false;

        while (true)
        {
            var attempt = await SendOnceAsync(method, uri, body, authenticated, timeoutMs, traceId, cancellationToken)
                .ConfigureAwait(false);

            if (attempt.Sent && attempt.HttpStatus == (int)HttpStatusCode.Unauthorized
                && authenticated && allowRefresh && !attemptedRefresh)
            {
                // ── 401：续签后重放原请求一次
                var refreshed = await RefreshOnceAsync(attempt.StaleToken, traceId, cancellationToken)
                    .ConfigureAwait(false);
                if (refreshed.Success && !string.IsNullOrEmpty(refreshed.AccessToken))
                {
                    attemptedRefresh = true;
                    _logger.LogInformation(
                        "Token 续签成功，重放原请求 method={Method} path={Path} traceId={TraceId}",
                        method.Method, path, traceId);
                    continue;
                }

                // 续签未成功：只有 401/403 才算凭据失效（网络故障保留凭据、不踢人 —— EDGE-W-2）。
                var detail = ApiErrorExtractor.Extract(attempt.Body);
                var unauthorized = TksApiException.Unauthorized(
                    detail.Code,
                    detail.Message ?? "unauthorized",
                    detail.TraceId ?? traceId);

                // ⚠️ EDGE-W-2：是否清凭据**由续签结果决定**，不能因为「原请求返回 401」就清。
                //    续签遇到网络故障时（HttpStatus 为 null）根本无法判断 Token 是否真失效 → 保留凭据、不踢人。
                var refreshRejected =
                    refreshed.Error is { HttpStatus: not null } refreshError
                    && (refreshError.ShouldClearCredentials || ErrorCatalog.IsAuthFailure(refreshError.Code));

                if (refreshRejected && !refreshed.CredentialsInvalidated)
                {
                    _tokens.OnUnauthorized(AuthFailureReasons.RefreshFailed, refreshed.Error?.HttpStatus);
                }

                _logger.LogWarning(
                    "401 且续签未成功，放弃重放 method={Method} path={Path} traceId={TraceId} code={Code}",
                    method.Method, path, traceId, detail.Code);
                return ApiResult.Fail<T>(unauthorized);
            }

            return Unwrap<T>(attempt, path, method, traceId, envelope);
        }
    }

    /// <summary>不关心返回体的请求：成功仅判 <c>code == 0</c>。</summary>
    public async Task<ApiResult> SendNoContentAsync(
        HttpMethod method,
        string path,
        object? body,
        bool allowRefresh,
        bool authenticated,
        int timeoutMs,
        CancellationToken cancellationToken = default)
    {
        var result = await SendAsync<JsonElement>(method, path, body, envelope: true, authenticated, allowRefresh,
            query: null, timeoutMs, cancellationToken).ConfigureAwait(false);

        return result.IsSuccess
            ? ApiResult.Ok(result.TraceId)
            : ApiResult.Fail(result.Error!);
    }

    /// <summary>
    /// 带异常语义的包装：失败时抛 <see cref="TksApiException"/>（供偏好异常风格的上层使用）。
    /// 内部仍复用同一套流程，因此**不会**丢失 code / HttpStatus / traceId。
    /// </summary>
    public async Task<T> SendOrThrowAsync<T>(Func<Task<ApiResult<T>>> call)
    {
        var result = await call().ConfigureAwait(false);
        if (result.IsSuccess)
        {
            if (result.TryGetValue(out var value))
            {
                return value;
            }

            throw TksApiException.InvalidResponse(
                "response envelope had code == 0 but no data",
                result.TraceId);
        }

        throw ToException(result.Error!);
    }

    /// <summary>把强类型错误载荷还原为异常。</summary>
    public static TksApiException ToException(ApiError error) => new(
        error.Kind,
        error.Message,
        error.Code,
        error.HttpStatus,
        error.TraceId,
        error.ClientCode,
        error.I18nKey,
        error.CredentialsCleared);

    /* ---------------------------------------------------------------------- */
    /* 单次尝试                                                                */
    /* ---------------------------------------------------------------------- */

    private async Task<RawAttempt> SendOnceAsync(
        HttpMethod method,
        Uri uri,
        object? body,
        bool authenticated,
        int timeoutMs,
        string traceId,
        CancellationToken cancellationToken)
    {
        var staleToken = authenticated ? _tokens.AccessToken : null;

        using var request = new HttpRequestMessage(method, uri);
        request.Headers.TryAddWithoutValidation(TraceHeaderName, traceId);
        if (authenticated && !string.IsNullOrEmpty(staleToken))
        {
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + staleToken);
        }
        else if (authenticated)
        {
            _logger.LogDebug("请求需要鉴权但本地无 Token path={Path} traceId={TraceId}", uri.AbsolutePath, traceId);
        }

        if (body is not null)
        {
            var json = JsonSerializer.Serialize(body, WsFrameParser.Options);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeoutMs);
        var startedAt = Environment.TickCount64;

        _logger.LogDebug(
            "REST 请求 method={Method} path={Path} traceId={TraceId} timeoutMs={TimeoutMs}",
            method.Method, uri.AbsolutePath, traceId, timeoutMs);

        try
        {
            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutCts.Token)
                .ConfigureAwait(false);

            var text = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);

            _logger.LogDebug(
                "REST 响应 path={Path} traceId={TraceId} status={Status} ms={Elapsed}",
                uri.AbsolutePath, traceId, (int)response.StatusCode, Environment.TickCount64 - startedAt);

            return new RawAttempt(
                Sent: true,
                HttpStatus: (int)response.StatusCode,
                Body: text,
                Failure: null,
                StaleToken: staleToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 链接 CTS 到期 = 本地超时（**保留凭据**，不得当鉴权失败）。
            _logger.LogWarning(
                "REST 请求超时 path={Path} traceId={TraceId} timeoutMs={TimeoutMs}",
                uri.AbsolutePath, traceId, timeoutMs);
            return RawAttempt.Failed(TksApiException.Timeout(traceId, timeoutMs), staleToken);
        }
        catch (OperationCanceledException)
        {
            // 调用方主动取消。
            return RawAttempt.Failed(TksApiException.Cancelled(traceId), staleToken);
        }
        catch (HttpRequestException ex)
        {
            // 连接层失败（DNS / TLS / 连接被拒 / 传输中断）：**保留凭据**。
            _logger.LogWarning(
                "REST 网络故障 path={Path} traceId={TraceId} error={Error}",
                uri.AbsolutePath, traceId, ex.Message);
            return RawAttempt.Failed(TksApiException.Network(ex.Message, traceId, ex), staleToken);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            _logger.LogWarning(
                "REST 传输失败 path={Path} traceId={TraceId} error={Error}",
                uri.AbsolutePath, traceId, ex.Message);
            return RawAttempt.Failed(TksApiException.Network(ex.Message, traceId, ex), staleToken);
        }
    }

    /* ---------------------------------------------------------------------- */
    /* 401 互斥续签                                                            */
    /* ---------------------------------------------------------------------- */

    /// <summary>
    /// FR-W-AUTH-5：拿到续签信号量后**先复查 Token 是否已被别人换掉** —— 若是，直接复用（不再发起轮转）；
    /// 否则发起一次续签。信号量在整个网络往返期间持有，因此同一时刻只有一个 refresh 在途，
    /// 排队等待者复用同一结果。
    /// </summary>
    private async Task<AccessTokenRefreshOutcome> RefreshOnceAsync(
        string? staleToken,
        string traceId,
        CancellationToken cancellationToken)
    {
        if (!_tokens.CanRefresh)
        {
            _logger.LogWarning("无 Refresh Token，无法续签 traceId={TraceId}", traceId);
            return AccessTokenRefreshOutcome.Failed(
                new ApiError(ApiFailureKind.Unauthorized, "no refresh token available", ErrorCatalog.TokenInvalid, 401, traceId),
                credentialsInvalidated: false);
        }

        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = _tokens.AccessToken;
            if (!string.IsNullOrEmpty(current) && !string.Equals(current, staleToken, StringComparison.Ordinal))
            {
                // 另一个并发 401 已在本信号量保护下完成轮转 → 复用新 Token，避免二次轮转使 Refresh Token 互相作废。
                _logger.LogDebug("续签已完成，复用其他请求换来的新 Token traceId={TraceId}", traceId);
                return AccessTokenRefreshOutcome.Ok(current);
            }

            _logger.LogInformation("发起 Token 续签（互斥在途唯一）traceId={TraceId}", traceId);
            return await _tokens.RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return AccessTokenRefreshOutcome.Failed(
                new ApiError(ApiFailureKind.Cancelled, "refresh cancelled", TraceId: traceId),
                credentialsInvalidated: false);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    /* ---------------------------------------------------------------------- */
    /* 解包：业务失败（HTTP 200 + code != 0）与错误状态                            */
    /* ---------------------------------------------------------------------- */

    private ApiResult<T> Unwrap<T>(
        RawAttempt attempt,
        string path,
        HttpMethod method,
        string traceId,
        bool envelope)
    {
        if (attempt.Failure is not null)
        {
            return ApiResult.Fail<T>(attempt.Failure);
        }

        var info = ApiErrorExtractor.Extract(attempt.Body);
        var status = attempt.HttpStatus;

        // ── 非 2xx：错误码可能在顶层，也可能在 detail.code（陷阱 3）
        if (status is null || status < 200 || status >= 300)
        {
            var httpStatus = status ?? 0;
            var message = info.Message
                ?? string.Create(CultureInfo.InvariantCulture, $"HTTP {httpStatus}");
            _logger.LogWarning(
                "REST 返回错误状态 method={Method} path={Path} traceId={TraceId} status={Status} code={Code}",
                method.Method, path, traceId, httpStatus, info.Code);
            return ApiResult.Fail<T>(TksApiException.Http(httpStatus, info.Code, message, info.TraceId ?? traceId));
        }

        // ⚠️ 信封语义下 `code` 缺失即**无法判定成功** —— 绝不退化为「状态码 200 就算成功」（陷阱 4）。
        //    注意：这不包括 `/auth/*` 的**无信封**调用（`envelope: false` 走下面的直接反序列化分支）。
        if (envelope && !info.Code.HasValue)
        {
            _logger.LogWarning(
                "信封响应缺少 code 字段，视为形状不可用 path={Path} traceId={TraceId} status={Status}",
                path, traceId, attempt.HttpStatus);
            return ApiResult.Fail<T>(TksApiException.InvalidResponse(
                "response envelope has no code field",
                traceId));
        }

        if (string.IsNullOrWhiteSpace(attempt.Body))
        {
            // 空响应体：`code` 缺失 → 无法判定成功（HTTP 200 也必须判 code）→ 视为形状不可用，不抛错（NFR-W-12）。
            _logger.LogDebug(
                "REST 响应体为空 path={Path} traceId={TraceId} status={Status}",
                path, traceId, attempt.HttpStatus);
            return ApiResult.Fail<T>(TksApiException.InvalidResponse("empty response body", traceId));
        }

        // ── 业务失败判定：HTTP 200 也可能失败（陷阱 4）
        if (envelope && info.Code.HasValue)
        {
            if (info.Code.Value != ErrorCatalog.CodeOk)
            {
                _logger.LogWarning(
                    "业务失败（HTTP 200 + code != 0）method={Method} path={Path} traceId={TraceId} code={Code}",
                    method.Method, path, traceId, info.Code.Value);
                return new ApiResult<T>(TksApiException.Business(
                    info.Code.Value,
                    info.Message,
                    attempt.HttpStatus,
                    info.TraceId ?? traceId).ToError())
                {
                    FailureValue = SuccessFromEnvelope<T>(attempt.Body, info.TraceId ?? traceId).Value,
                };
            }

            return SuccessFromEnvelope<T>(attempt.Body, info.TraceId ?? traceId);
        }

        return Deserialize<T>(attempt.Body, info.TraceId ?? traceId, path, info.Code);
    }

    /// <summary>
    /// <c>code == 0</c> → 取 <c>data</c>。`data` 为 <c>null</c> / 缺失时返回成功但无值
    /// （NFR-W-12：服务端可能返回 <c>data:null</c>，不得当成失败也不得抛错）。
    /// </summary>
    private ApiResult<T> SuccessFromEnvelope<T>(string body, string traceId)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                return ApiResult.Success<T>(default!, traceId);
            }

            var value = data.Deserialize<T>(WsFrameParser.Options);
            return ApiResult.Success(value!, traceId);
        }
        catch (JsonException ex)
        {
            return ApiResult.Fail<T>(TksApiException.InvalidResponse(
                "envelope data could not be deserialized: " + ex.Message,
                traceId));
        }
    }

    private ApiResult<T> Deserialize<T>(string body, string traceId, string path, int? code)
    {
        try
        {
            var value = JsonSerializer.Deserialize<T>(body, WsFrameParser.Options);
            return value is null
                ? ApiResult.Fail<T>(TksApiException.InvalidResponse("response body deserialized to null", traceId))
                : ApiResult.Success(value, traceId);
        }
        catch (JsonException ex)
        {
            // 调试日志只记长度与异常消息，**不记响应体原文**（可能含敏感字段）。
            _logger.LogDebug(
                "响应体无法反序列化为 {Type} path={Path} traceId={TraceId} code={Code} error={Error}",
                typeof(T).Name, path, traceId, code, ex.Message);
            return ApiResult.Fail<T>(TksApiException.InvalidResponse(
                "response body could not be deserialized: " + ex.Message,
                traceId));
        }
    }

    /* ---------------------------------------------------------------------- */
    /* URL 组装                                                                */
    /* ---------------------------------------------------------------------- */

    /// <summary>REST 基址（运行时读取）。</summary>
    public string ApiBaseUrl => _apiBaseUrlProvider();

    private bool TryBuildUri(
        string path,
        IReadOnlyDictionary<string, string?>? query,
        [NotNullWhen(true)] out Uri? uri,
        out string? error)
    {
        uri = null;
        error = null;

        var baseUrl = _apiBaseUrlProvider();
        if (string.IsNullOrWhiteSpace(baseUrl) || !Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var baseUri))
        {
            error = "invalid api base url";
            return false;
        }

        // ⚠️ 只允许访问用户配置的主机（FR-W-SEC-7）：不做任何外部 / 遥测请求。
        var relative = path.TrimStart('/');
        if (!Uri.TryCreate(baseUri, relative, out var combined))
        {
            error = "invalid request path";
            return false;
        }

        if (query is null || query.Count == 0)
        {
            uri = combined;
            return true;
        }

        var builder = new StringBuilder(combined.GetLeftPart(UriPartial.Path));
        var first = true;
        foreach (var (key, value) in query)
        {
            if (value is null)
            {
                continue;
            }

            builder.Append(first ? '?' : '&');
            first = false;
            builder.Append(Uri.EscapeDataString(key)).Append('=').Append(Uri.EscapeDataString(value));
        }

        if (!Uri.TryCreate(builder.ToString(), UriKind.Absolute, out var withQuery))
        {
            error = "invalid query string";
            return false;
        }

        uri = withQuery;
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _refreshGate.Dispose();
    }

    /// <summary>单次尝试的原始结果。</summary>
    private sealed record RawAttempt(
        bool Sent,
        int? HttpStatus,
        string? Body,
        TksApiException? Failure,
        string? StaleToken)
    {
        public static RawAttempt Failed(TksApiException failure, string? staleToken)
            => new(false, null, null, failure, staleToken);
    }
}
