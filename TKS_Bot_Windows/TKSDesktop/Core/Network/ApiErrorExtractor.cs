using System.Globalization;
using System.Text.Json;

namespace TKSDesktop.Core.Network;

/// <summary>
/// 错误码 / 消息 / traceId 的提取结果。任一字段缺失即为 <c>null</c>。
/// </summary>
public sealed record ApiErrorInfo(int? Code, string? Message, string? TraceId)
{
    /// <summary>是否至少提取到一个字段（未知形状时为 <c>false</c>）。</summary>
    public bool HasAny => Code.HasValue || !string.IsNullOrEmpty(Message) || !string.IsNullOrEmpty(TraceId);
}

/// <summary>
/// 响应体错误信息提取（PRD §5.2 / §5.6 **陷阱 3**）。
///
/// 后端存在**两种**业务失败形状，**必须同时兼容**：
/// <list type="number">
///   <item>顶层 <c>{"code":40101,"message":"…","traceId":"…"}</c> —— 积分 / 等级 / 互动 / 补签卡四组路由
///         （注意：这类业务失败是 **HTTP 200**）；</item>
///   <item><c>{"detail":{"code":40101,"message":"鉴权失败"}}</c> —— <c>http_api</c> 的
///         <c>HTTPException(detail=response_body(...))</c> 被 FastAPI **二次包装**，<c>code</c> 不在顶层。</item>
/// </list>
///
/// 硬要求：
/// <list type="bullet">
///   <item>**未知形状返回 <c>null</c> 且不抛错**（NFR-W-12：不得因契约漂移导致崩溃）；</item>
///   <item>**不解析响应头** —— <c>ws_api</c> 不回传响应头，traceId 只在响应体里（FR-W-NET-1）；</item>
///   <item>数字 / 数字字符串都接受（服务端偶尔把码序列化成字符串）；</item>
///   <item><c>detail</c> 为**字符串**时（FastAPI 默认 <c>HTTPException</c> 形态）取其作为 message。</item>
/// </list>
/// </summary>
public static class ApiErrorExtractor
{
    /// <summary>提取失败码（双形状）。未知形状 → <c>null</c>。</summary>
    public static int? ExtractCode(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        return TryParse(body, out var info) ? info.Code : null;
    }

    /// <summary>提取消息（双形状，含 <c>detail</c> 为字符串的 FastAPI 默认形态）。</summary>
    public static string? ExtractMessage(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        return TryParse(body, out var info) ? info.Message : null;
    }

    /// <summary>提取 traceId（双形状）。⚠️ **只从响应体取**，不读响应头。</summary>
    public static string? ExtractTraceId(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        return TryParse(body, out var info) ? info.TraceId : null;
    }

    /// <summary>
    /// 一次性提取 code / message / traceId。
    /// **永不抛错**：非法 JSON 或未知形状时返回全 <c>null</c> 的 <see cref="ApiErrorInfo"/>。
    /// </summary>
    public static ApiErrorInfo Extract(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return new ApiErrorInfo(null, null, null);
        }

        return TryParse(body, out var info) ? info : new ApiErrorInfo(null, null, null);
    }

    /// <summary>
    /// 从已解析的 JSON 元素提取（供已持有 <see cref="JsonDocument"/> 的调用方复用，避免二次解析）。
    /// </summary>
    public static ApiErrorInfo Extract(JsonElement element)
    {
        try
        {
            return ExtractCore(element);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ObjectDisposedException)
        {
            // 未知形状 / 已释放的文档：一律返回空结果，不抛错。
            return new ApiErrorInfo(null, null, null);
        }
    }

    private static bool TryParse(string body, out ApiErrorInfo info)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            info = ExtractCore(document.RootElement);
            return true;
        }
        catch (JsonException)
        {
            info = new ApiErrorInfo(null, null, null);
            return true;
        }
    }

    private static ApiErrorInfo ExtractCore(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return new ApiErrorInfo(null, null, null);
        }

        // ── 形状 ①：顶层 code / message / traceId
        var code = TryGetInt(root, "code");
        var message = TryGetString(root, "message");
        var traceId = TryGetString(root, "traceId");
        var found = code.HasValue || message is not null || traceId is not null;

        // ── 形状 ②：detail 包装（http_api 鉴权失败，FastAPI 二次包装）
        if (root.TryGetProperty("detail", out var detail))
        {
            if (detail.ValueKind == JsonValueKind.Object)
            {
                code ??= TryGetInt(detail, "code");
                message ??= TryGetString(detail, "message");
                traceId ??= TryGetString(detail, "traceId");
                found = true;
            }
            else if (detail.ValueKind == JsonValueKind.String)
            {
                // FastAPI 默认 HTTPException(detail="…")：message 是字符串，无 code。
                message ??= detail.GetString();
                found = true;
            }
            else if (detail.ValueKind is JsonValueKind.Array)
            {
                // FastAPI 422 校验错误：detail 为 [{loc,msg,type}] —— 取首条 msg 作为 message。
                message ??= FirstValidationMessage(detail);
                found = found || message is not null;
            }
        }

        // ── 形状 ③：部分路由把业务码放在 errorCode（宽松兼容，缺失不报错）
        code ??= TryGetInt(root, "errorCode");

        return found || code.HasValue ? new ApiErrorInfo(code, message, traceId) : new ApiErrorInfo(null, null, null);
    }

    private static string? FirstValidationMessage(JsonElement array)
    {
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object)
            {
                var text = TryGetString(item, "msg");
                if (!string.IsNullOrEmpty(text))
                {
                    return text;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 取整数字段：接受 JSON number 与数字字符串。
    /// ⚠️ 非数字内容（如 <c>"abc"</c>）或布尔等类型一律返回 <c>null</c>，**不抛错**。
    /// </summary>
    private static int? TryGetInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
                return value.TryGetInt32(out var number)
                    ? number
                    : (value.TryGetDouble(out var d) && d is >= int.MinValue and <= int.MaxValue
                        ? (int)d
                        : null);
            case JsonValueKind.String:
                return int.TryParse(
                    value.GetString(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var parsed)
                    ? parsed
                    : null;
            default:
                return null;
        }
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            // 少量路由会把 message 写成数字/布尔，宽容转换而不是抛错。
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True or JsonValueKind.False => value.GetBoolean() ? "true" : "false",
            _ => null,
        };
    }
}
