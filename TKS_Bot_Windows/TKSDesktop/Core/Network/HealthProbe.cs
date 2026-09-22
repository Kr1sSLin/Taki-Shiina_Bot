using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Logging;
using TKSDesktop.Contracts;

namespace TKSDesktop.Core.Network;

/// <summary>连通性探测三态（EDGE-W-28）。</summary>
public enum HealthStatus
{
    /// <summary>HTTP 200，服务可用。</summary>
    Reachable,

    /// <summary>
    /// **中间态**：HTTP 404 —— 服务可达，但 `/healthz` 路径不可用。
    /// ⚠️ 文案必须区别于 <see cref="Unreachable"/>（「服务可达但健康检查路径不可用」，
    /// 而不是「无法连接服务」）。
    /// </summary>
    HealthEndpointUnavailable,

    /// <summary>完全不可达（DNS / TLS / 连接被拒 / 超时 / 非 404 的错误状态）。</summary>
    Unreachable,
}

/// <summary>连通性探测结果。</summary>
public sealed record HealthProbeResult(
    HealthStatus Status,
    string Url,
    int? HttpStatus,
    long? LatencyMs,
    ApiError? Error)
{
    /// <summary>是否可直接使用（仅 <see cref="HealthStatus.Reachable"/>）。</summary>
    public bool IsReachable => Status == HealthStatus.Reachable;

    /// <summary>是否为 404 中间态。</summary>
    public bool IsHealthEndpointUnavailable => Status == HealthStatus.HealthEndpointUnavailable;
}

/// <summary>服务地址类别（决定允许的 scheme 集合）。</summary>
public enum ServiceEndpointKind
{
    /// <summary>REST 基址（`https` / `http`）。</summary>
    Rest,

    /// <summary>WS 基址（`wss` / `ws`）。</summary>
    WebSocket,
}

/// <summary>URL scheme 校验结果。</summary>
public sealed record UrlSchemeCheck(
    bool IsValid,
    bool RequiresConfirmation,
    bool IsLoopback,
    string? Scheme,
    string? MessageKey)
{
    /// <summary>
    /// 是否**无需二次确认**即可放行（`https`/`wss`，或回环上的 `http`/`ws`）。
    /// 非回环的 ` http`/`ws` 为 <c>false</c>：仍是合法输入，但必须先向用户明示
    /// 「凭据将以明文传输」并取得确认（FR-W-CFG-2 / FR-W-SEC-4）。
    /// </summary>
    public bool IsAllowedWithoutConfirmation => IsValid && !RequiresConfirmation;
}

/// <summary>
/// 连通性探测与地址策略（EDGE-W-28 / FR-W-CFG-2 / FR-W-CFG-4 / FR-W-SEC-4）。
///
/// <list type="bullet">
///   <item>探测 <c>GET {apiBaseUrl 去掉尾部 api/v1}/healthz</c>，超时
///         <see cref="ProtocolConstants.ConnectivityTimeoutMs"/>（10s）；</item>
///   <item>**只判 HTTP 200，不解析响应体结构** —— 两个进程的 `/healthz` 形态不同
///         （`http_api` 带信封、`ws_api` 是 <c>{status,service}</c> 裸对象）；</item>
///   <item>404 → 中间态 <see cref="HealthStatus.HealthEndpointUnavailable"/>。</item>
/// </list>
/// </summary>
public sealed class HealthProbe
{
    /// <summary>健康检查路径。</summary>
    public const string HealthPath = "/healthz";

    /// <summary>免二次确认的回环主机集合。</summary>
    public static readonly IReadOnlyList<string> LoopbackHosts = ["localhost", "127.0.0.1", "::1"];

    private readonly HttpClient _http;
    private readonly Func<string> _apiBaseUrlProvider;
    private readonly ILogger<HealthProbe> _logger;

    /// <param name="http">HTTP 客户端（**不修改其 Timeout**：超时由 CTS 控制）。</param>
    /// <param name="apiBaseUrlProvider">REST 基址（含 <c>/api/v1/</c> 后缀）。</param>
    /// <param name="logger">日志。</param>
    public HealthProbe(HttpClient http, Func<string> apiBaseUrlProvider, ILogger<HealthProbe> logger)
    {
        _http = http;
        _apiBaseUrlProvider = apiBaseUrlProvider;
        _logger = logger;
    }

    /// <summary>
    /// 探测当前配置的 REST 基址对应的 `/healthz`。
    /// **永不抛错**：所有失败都收敛为 <see cref="HealthStatus.Unreachable"/>。
    /// </summary>
    public Task<HealthProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
        => ProbeAsync(_apiBaseUrlProvider(), ProtocolConstants.ConnectivityTimeoutMs, cancellationToken);

    /// <summary>
    /// 探测指定 REST 基址的 `/healthz`。**永不抛错**。
    /// </summary>
    public async Task<HealthProbeResult> ProbeAsync(
        string apiBaseUrl,
        int timeoutMs = ProtocolConstants.ConnectivityTimeoutMs,
        CancellationToken cancellationToken = default)
    {
        if (!TryBuildHealthUri(apiBaseUrl, out var uri))
        {
            _logger.LogWarning("健康检查地址无法构造（apiBaseUrl 非法）");
            return new HealthProbeResult(
                HealthStatus.Unreachable,
                string.Empty,
                null,
                null,
                new ApiError(ApiFailureKind.Configuration, "invalid api base url"));
        }

        var url = uri.ToString();
        var startedAt = Environment.TickCount64;

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        // ⚠️ 不附加 Authorization：/healthz 是公开探活端点，且此处不读响应头 / 响应体结构。

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeoutMs);

        try
        {
            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                .ConfigureAwait(false);

            var latency = Environment.TickCount64 - startedAt;
            var status = (int)response.StatusCode;

            if (status == (int)HttpStatusCode.OK)
            {
                _logger.LogInformation("连通性探测成功 url={Url} ms={Ms}", url, latency);
                return new HealthProbeResult(HealthStatus.Reachable, url, status, latency, null);
            }

            if (status == (int)HttpStatusCode.NotFound)
            {
                // EDGE-W-28 中间态：服务可达，但健康检查路径不可用（**不得**与「不可达」同文案）。
                _logger.LogWarning("健康检查路径不可用（HTTP 404），服务本身可达 url={Url}", url);
                return new HealthProbeResult(HealthStatus.HealthEndpointUnavailable, url, status, latency, null);
            }

            _logger.LogWarning("连通性探测返回非 200 url={Url} status={Status}", url, status);
            return new HealthProbeResult(
                HealthStatus.Unreachable,
                url,
                status,
                latency,
                new ApiError(ApiFailureKind.Http, string.Create(CultureInfo.InvariantCulture, $"HTTP {status}"), null, status));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("连通性探测超时 url={Url} timeoutMs={TimeoutMs}", url, timeoutMs);
            return new HealthProbeResult(
                HealthStatus.Unreachable,
                url,
                null,
                null,
                new ApiError(ApiFailureKind.Timeout, "health probe timeout"));
        }
        catch (OperationCanceledException)
        {
            return new HealthProbeResult(HealthStatus.Unreachable, url, null, null, new ApiError(ApiFailureKind.Cancelled, "probe cancelled"));
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("连通性探测网络故障 url={Url} error={Error}", url, ex.Message);
            return new HealthProbeResult(HealthStatus.Unreachable, url, null, null, new ApiError(ApiFailureKind.Network, ex.Message));
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            _logger.LogWarning("连通性探测失败 url={Url} error={Error}", url, ex.Message);
            return new HealthProbeResult(HealthStatus.Unreachable, url, null, null, new ApiError(ApiFailureKind.Network, ex.Message));
        }
    }

    /// <summary>
    /// 由 `apiBaseUrl` 构造 `/healthz` 地址：**去掉尾部的 `api/v1` 路径段**后拼接 `healthz`。
    /// </summary>
    public static bool TryBuildHealthUri(string? apiBaseUrl, [NotNullWhen(true)] out Uri? uri)
    {
        uri = null;

        if (string.IsNullOrWhiteSpace(apiBaseUrl) || !Uri.TryCreate(apiBaseUrl.Trim(), UriKind.Absolute, out var baseUri))
        {
            return false;
        }

        var path = baseUri.AbsolutePath.TrimEnd('/');

        // 去掉部署前缀（/api/v1）。大小写不敏感：配置里可能写成 /API/V1/。
        foreach (var suffix in (string[])["/api/v1", "api/v1"])
        {
            if (path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                path = path[..^suffix.Length].TrimEnd('/');
                break;
            }
        }

        var builder = new UriBuilder(baseUri)
        {
            Path = path + HealthPath,
            Query = string.Empty,
            Fragment = string.Empty,
        };

        uri = builder.Uri;
        return true;
    }

    /* ---------------------------------------------------------------------- */
    /* 地址策略（FR-W-CFG-2 / FR-W-CFG-4 / FR-W-SEC-4）                          */
    /* ---------------------------------------------------------------------- */

    /// <summary>
    /// scheme 校验：仅 `https`/`wss` 允许；`http`/`ws` **仅**回环免二次确认；
    /// 其余 scheme 一律非法（FR-W-SEC-4：**不得**引入任何外部 / 遥测请求）。
    /// </summary>
    public static UrlSchemeCheck CheckAllowedScheme(string? value, ServiceEndpointKind kind)
    {
        var secure = kind == ServiceEndpointKind.Rest ? "https" : "wss";
        var insecure = kind == ServiceEndpointKind.Rest ? "http" : "ws";

        if (string.IsNullOrWhiteSpace(value))
        {
            return new UrlSchemeCheck(false, false, false, null, "settings.url.error.empty");
        }

        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
        {
            return new UrlSchemeCheck(false, false, false, null, "settings.url.error.invalid");
        }

        var scheme = uri.Scheme.ToLowerInvariant();

        if (scheme == secure)
        {
            return new UrlSchemeCheck(true, false, IsLoopback(uri.Host), scheme, null);
        }

        if (scheme == insecure)
        {
            // 非安全协议：回环免确认，其他必须二次确认（明示「凭据将以明文传输」）。
            return IsLoopback(uri.Host)
                ? new UrlSchemeCheck(true, false, true, scheme, null)
                : new UrlSchemeCheck(true, true, false, scheme, "settings.url.warning.plaintext");
        }

        return new UrlSchemeCheck(false, false, IsLoopback(uri.Host), scheme, "settings.url.error.scheme");
    }

    /// <summary>主机是否为回环地址（`localhost` / `127.0.0.1` / `::1`）。</summary>
    public static bool IsLoopback(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        var normalized = host.Trim().Trim('[', ']');
        foreach (var loopback in LoopbackHosts)
        {
            if (string.Equals(normalized, loopback, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 由 `apiBaseUrl` 推导 `wsBaseUrl`（FR-W-CFG-4，行为必须与另两端 `deriveWsBaseUrl` 等价）：
    /// `https → wss`、`http → ws`、**清空 path 与 query**、**末尾去 `/`**。
    /// 输入非法时回落到 <see cref="ProtocolConstants.DefaultWsBaseUrl"/>。
    /// </summary>
    public static string DeriveWsBaseUrl(string? apiBaseUrl)
    {
        if (string.IsNullOrWhiteSpace(apiBaseUrl) || !Uri.TryCreate(apiBaseUrl.Trim(), UriKind.Absolute, out var uri))
        {
            return ProtocolConstants.DefaultWsBaseUrl;
        }

        var scheme = uri.Scheme.ToLowerInvariant() switch
        {
            "https" => "wss",
            "http" => "ws",
            "wss" => "wss",
            "ws" => "ws",
            _ => "wss",
        };

        var builder = new UriBuilder(uri)
        {
            Scheme = scheme,
            Path = string.Empty,
            Query = string.Empty,
            Fragment = string.Empty,
        };

        return builder.Uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
    }

    /// <summary>规范化 REST 基址：确保末尾有 `/`（`apiBaseUrl` 语义为「基址」）。</summary>
    public static string NormalizeApiBaseUrl(string value)
    {
        var trimmed = value.Trim();
        return trimmed.EndsWith('/') ? trimmed : trimmed + "/";
    }
}
