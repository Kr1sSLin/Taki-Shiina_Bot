using System.Text.Json.Serialization;

namespace TKSDesktop.Contracts.Dtos;

/// <summary>
/// 统一响应包络（PRD §5.2）：除 `/auth/*` 与两个 `/healthz` 外，全部走此包络。
///
/// ⚠️ **业务失败是 HTTP 200 + `code != 0`**（积分/等级/互动/补签卡四组路由），
///    客户端**不得只判断 HTTP 状态码**（§7.0 / 陷阱 4）。
/// </summary>
public sealed class ApiEnvelope<T>
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("data")]
    public T? Data { get; set; }

    [JsonPropertyName("traceId")]
    public string? TraceId { get; set; }
}

/// <summary>
/// 仅取错误码用的宽松包络（`data` 形态未知时不反序列化，避免因未知结构抛错 —— NFR-W-12）。
/// </summary>
public sealed class ApiErrorEnvelope
{
    [JsonPropertyName("code")]
    public int? Code { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("traceId")]
    public string? TraceId { get; set; }

    /// <summary>
    /// `http_api` 的鉴权失败形状陷阱（§5.2 / 陷阱 3）：
    /// 后端用 `HTTPException(detail=response_body(...))` 抛出，FastAPI 会再包一层，
    /// 客户端实际收到 `{"detail": {"code": 40101, "message": "鉴权失败"}}`，**`code` 不在顶层**。
    /// </summary>
    [JsonPropertyName("detail")]
    public ApiErrorDetail? Detail { get; set; }
}

/// <summary>`detail` 包装体（仅 `http_api` 鉴权失败路径出现）。</summary>
public sealed class ApiErrorDetail
{
    [JsonPropertyName("code")]
    public int? Code { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("traceId")]
    public string? TraceId { get; set; }
}

/* -------------------------------------------------------------------------- */
/* 鉴权（§5.2）—— 无包络，直接返回对象                                          */
/* -------------------------------------------------------------------------- */

/// <summary>`POST /auth/login` 请求体。</summary>
public sealed class LoginRequestDto
{
    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("password")]
    public string Password { get; set; } = string.Empty;

    [JsonPropertyName("deviceId")]
    public string? DeviceId { get; set; }
}

/// <summary>`POST /auth/refresh` 请求体。</summary>
public sealed class RefreshRequestDto
{
    [JsonPropertyName("refreshToken")]
    public string RefreshToken { get; set; } = string.Empty;
}

/// <summary>
/// `/auth/login` 与 `/auth/refresh` 的响应体（**无包络**）。
/// ⚠️ Refresh Token **轮转**：每次续签返回新的 refreshToken，旧的立即失效（FR-W-AUTH-5 的互斥原因）。
/// </summary>
public sealed class AuthTokensDto
{
    [JsonPropertyName("accessToken")]
    public string AccessToken { get; set; } = string.Empty;

    [JsonPropertyName("refreshToken")]
    public string RefreshToken { get; set; } = string.Empty;

    [JsonPropertyName("tokenType")]
    public string TokenType { get; set; } = "Bearer";

    [JsonPropertyName("expiresIn")]
    public int ExpiresIn { get; set; }

    [JsonPropertyName("userId")]
    public string UserId { get; set; } = string.Empty;

    [JsonPropertyName("deviceId")]
    public string DeviceId { get; set; } = string.Empty;
}
