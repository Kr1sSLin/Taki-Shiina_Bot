namespace TKSDesktop.App;

/// <summary>URL 校验结果。</summary>
public sealed record UrlValidationResult(bool IsValid, bool RequiresPlaintextConfirmation, string? MessageKey)
{
    public static UrlValidationResult Valid() => new(true, false, null);

    public static UrlValidationResult Invalid(string messageKey) => new(false, false, messageKey);

    public static UrlValidationResult NeedsConfirmation(string messageKey) => new(true, true, messageKey);
}

/// <summary>
/// 服务地址校验与派生（PRD §5.1 FR-W-CFG-2 / FR-W-CFG-4 / §3.6 FR-W-SEC-4）。
///
/// <list type="bullet">
///   <item>仅允许 `https://` / `wss://`；</item>
///   <item>填入 `http://` / `ws://` 必须二次确认并明示「凭据将以明文传输」；</item>
///   <item>**仅** `localhost` / `127.0.0.1` / `::1` 免确认。</item>
/// </list>
/// </summary>
public static class UrlPolicy
{
    /// <summary>免二次确认的回环主机集合。</summary>
    public static readonly IReadOnlyList<string> LoopbackHosts = ["localhost", "127.0.0.1", "::1"];

    /// <summary>校验 REST 基址。</summary>
    public static UrlValidationResult ValidateApiBaseUrl(string? value)
        => Validate(value, requirePathSuffix: false, expectedSecureScheme: "https", allowedSchemes: ["https", "http"]);

    /// <summary>校验 WS 基址。</summary>
    public static UrlValidationResult ValidateWsBaseUrl(string? value)
        => Validate(value, requirePathSuffix: false, expectedSecureScheme: "wss", allowedSchemes: ["wss", "ws"]);

    private static UrlValidationResult Validate(
        string? value,
        bool requirePathSuffix,
        string expectedSecureScheme,
        string[] allowedSchemes)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return UrlValidationResult.Invalid("settings.url.error.empty");
        }

        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
        {
            return UrlValidationResult.Invalid("settings.url.error.invalid");
        }

        var scheme = uri.Scheme.ToLowerInvariant();
        if (!allowedSchemes.Contains(scheme, StringComparer.Ordinal))
        {
            return UrlValidationResult.Invalid("settings.url.error.scheme");
        }

        if (scheme == expectedSecureScheme)
        {
            return UrlValidationResult.Valid();
        }

        // 非安全协议：回环免确认，其他必须二次确认。
        return IsLoopback(uri.Host)
            ? UrlValidationResult.Valid()
            : UrlValidationResult.NeedsConfirmation("settings.url.warning.plaintext");
    }

    /// <summary>主机是否为回环地址。</summary>
    public static bool IsLoopback(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        var normalized = host.Trim().Trim('[', ']');
        return LoopbackHosts.Contains(normalized, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 由 `apiBaseUrl` 推导 `wsBaseUrl`（FR-W-CFG-4，与另一端 `deriveWsBaseUrl` 行为等价）：
    /// `https→wss`、`http→ws`、清空 path 与 query、末尾去 `/`。
    /// </summary>
    public static string DeriveWsBaseUrl(string? apiBaseUrl)
    {
        if (string.IsNullOrWhiteSpace(apiBaseUrl) || !Uri.TryCreate(apiBaseUrl.Trim(), UriKind.Absolute, out var uri))
        {
            return Contracts.ProtocolConstants.DefaultWsBaseUrl;
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

        var derived = builder.Uri.GetLeftPart(UriPartial.Authority);
        return derived.TrimEnd('/');
    }

    /// <summary>
    /// 规范化 REST 基址：确保末尾有 `/`（`apiBaseUrl` 语义为「基址」）。
    /// </summary>
    public static string NormalizeApiBaseUrl(string value)
    {
        var trimmed = value.Trim();
        return trimmed.EndsWith('/') ? trimmed : trimmed + "/";
    }
}
