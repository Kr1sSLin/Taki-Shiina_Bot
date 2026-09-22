namespace TKSDesktop.Contracts;

/// <summary>错误码文案来源类别（决定回退策略与是否可重试）。</summary>
public enum ErrorCodeKind
{
    /// <summary>HTTP / 业务错误码（REST 信封 `code`，或 `detail.code`）。</summary>
    Api,

    /// <summary>WebSocket 服务端错误码（`bot.error.payload.errorCode`，字符串）。</summary>
    Server,

    /// <summary>客户端自有错误码（本地状态标记）。</summary>
    Client,
}

/// <summary>一个错误码的完整描述。</summary>
public sealed record ErrorCodeInfo(
    string Key,
    ErrorCodeKind Kind,
    string I18nKey,
    bool Retryable);

/// <summary>
/// 错误码目录（PRD §5.5 **C-6** / §5.3.3 / §7.0）。
///
/// 硬要求：
/// ① **必须覆盖全部 14 个码**：`0` / `40001` / `40002` / `40101` / `40102` / `40201` / `40202` /
///    `40204` / `40205` / `40206` / `40207` / `40301` / `40302` / `50301` / `5000`
///    —— 其中 `40302` 为后端死代码，只作兜底文案（EDGE-W-3）。
/// ② 未知码必须有**中性兜底文案**，**不得**显示原始数字或空白（V-W-S6）。
/// ③ 文案本身在 i18n 资源文件中，本类只维护「码 → i18n key」映射（NFR-W-10：代码不硬编码中文）。
/// </summary>
public static class ErrorCatalog
{
    /// <summary>成功码。</summary>
    public const int CodeOk = 0;

    /* ---- HTTP / 业务错误码（数字） ---- */

    public const int ParamMissing = 40001;
    public const int CityEmpty = 40002;
    public const int AuthFailed = 40101;
    public const int TokenInvalid = 40102;
    public const int DeviceNotAllowed = 40301;

    /// <summary>⚠️ 后端**死代码**（`DeviceLimitError` 从未被抛出）；仅作兜底文案。</summary>
    public const int DeviceLimitExceeded = 40302;

    public const int PointsInsufficient = 40201;
    public const int ItemUnavailable = 40202;
    public const int InteractionAiFailed = 40204;
    public const int MakeupCardInsufficient = 40205;
    public const int DateAlreadyHasActivity = 40206;
    public const int InvalidTargetDate = 40207;
    public const int ServiceUnavailable = 50301;
    public const int ServerError = 5000;

    /* ---- WS 服务端错误码（字符串） ---- */

    public const string InvalidJson = "INVALID_JSON";
    public const string UnknownType = "UNKNOWN_TYPE";
    public const string EmptyMessage = "EMPTY_MESSAGE";
    public const string VisionImageCountExceeded = "VISION_IMAGE_COUNT_EXCEEDED";
    public const string VisionInvalidMime = "VISION_INVALID_MIME";
    public const string VisionInvalidBase64 = "VISION_INVALID_BASE64";
    public const string VisionImageTooLarge = "VISION_IMAGE_TOO_LARGE";
    public const string GeminiNotConfigured = "GEMINI_NOT_CONFIGURED";
    public const string AiTimeout = "AI_TIMEOUT";
    public const string InternalError = "INTERNAL_ERROR";

    /* ---- 客户端自有错误码 ---- */

    public const string SendFailed = ProtocolConstants.ClientErrorSendFailed;
    public const string Timeout = ProtocolConstants.ClientErrorTimeout;
    public const string ConnectionLost = ProtocolConstants.ClientErrorConnectionLost;

    /// <summary>未知码的中性兜底 i18n key。</summary>
    public const string UnknownI18nKey = "error.unknown";

    private static readonly Dictionary<string, ErrorCodeInfo> ApiByKey = new(StringComparer.Ordinal)
    {
        ["0"] = new("0", ErrorCodeKind.Api, "error.api.0", Retryable: false),
        ["40001"] = new("40001", ErrorCodeKind.Api, "error.api.40001", Retryable: false),
        ["40002"] = new("40002", ErrorCodeKind.Api, "error.api.40002", Retryable: false),
        ["40101"] = new("40101", ErrorCodeKind.Api, "error.api.40101", Retryable: true),
        ["40102"] = new("40102", ErrorCodeKind.Api, "error.api.40102", Retryable: true),
        ["40301"] = new("40301", ErrorCodeKind.Api, "error.api.40301", Retryable: false),
        ["40302"] = new("40302", ErrorCodeKind.Api, "error.api.40302", Retryable: false),
        ["40201"] = new("40201", ErrorCodeKind.Api, "error.api.40201", Retryable: false),
        ["40202"] = new("40202", ErrorCodeKind.Api, "error.api.40202", Retryable: false),
        ["40204"] = new("40204", ErrorCodeKind.Api, "error.api.40204", Retryable: false),
        ["40205"] = new("40205", ErrorCodeKind.Api, "error.api.40205", Retryable: false),
        ["40206"] = new("40206", ErrorCodeKind.Api, "error.api.40206", Retryable: false),
        ["40207"] = new("40207", ErrorCodeKind.Api, "error.api.40207", Retryable: false),
        ["50301"] = new("50301", ErrorCodeKind.Api, "error.api.50301", Retryable: true),
        ["5000"] = new("5000", ErrorCodeKind.Api, "error.api.5000", Retryable: true),
    };

    private static readonly Dictionary<string, ErrorCodeInfo> ServerByKey = new(StringComparer.Ordinal)
    {
        [InvalidJson] = new(InvalidJson, ErrorCodeKind.Server, "error.server.INVALID_JSON", Retryable: false),
        [UnknownType] = new(UnknownType, ErrorCodeKind.Server, "error.server.UNKNOWN_TYPE", Retryable: false),
        [EmptyMessage] = new(EmptyMessage, ErrorCodeKind.Server, "error.server.EMPTY_MESSAGE", Retryable: false),
        [VisionImageCountExceeded] = new(VisionImageCountExceeded, ErrorCodeKind.Server, "error.server.VISION_IMAGE_COUNT_EXCEEDED", Retryable: false),
        [VisionInvalidMime] = new(VisionInvalidMime, ErrorCodeKind.Server, "error.server.VISION_INVALID_MIME", Retryable: false),
        [VisionInvalidBase64] = new(VisionInvalidBase64, ErrorCodeKind.Server, "error.server.VISION_INVALID_BASE64", Retryable: false),
        [VisionImageTooLarge] = new(VisionImageTooLarge, ErrorCodeKind.Server, "error.server.VISION_IMAGE_TOO_LARGE", Retryable: false),
        [GeminiNotConfigured] = new(GeminiNotConfigured, ErrorCodeKind.Server, "error.server.GEMINI_NOT_CONFIGURED", Retryable: false),
        [AiTimeout] = new(AiTimeout, ErrorCodeKind.Server, "error.server.AI_TIMEOUT", Retryable: true),
        [InternalError] = new(InternalError, ErrorCodeKind.Server, "error.server.INTERNAL_ERROR", Retryable: true),
    };

    private static readonly Dictionary<string, ErrorCodeInfo> ClientByKey = new(StringComparer.Ordinal)
    {
        [SendFailed] = new(SendFailed, ErrorCodeKind.Client, "error.client.SEND_FAILED", Retryable: true),
        [Timeout] = new(Timeout, ErrorCodeKind.Client, "error.client.TIMEOUT", Retryable: true),
        [ConnectionLost] = new(ConnectionLost, ErrorCodeKind.Client, "error.client.CONNECTION_LOST", Retryable: true),
    };

    /// <summary>
    /// C-6 要求的 14 个码（数字形式，供机械校验 V-W-S6 断言）。
    /// ⚠️ 注意 `0` 也在清单内。
    /// </summary>
    public static readonly IReadOnlyList<int> RequiredApiCodes =
    [
        CodeOk, ParamMissing, CityEmpty, AuthFailed, TokenInvalid,
        PointsInsufficient, ItemUnavailable, InteractionAiFailed,
        MakeupCardInsufficient, DateAlreadyHasActivity, InvalidTargetDate,
        DeviceNotAllowed, DeviceLimitExceeded, ServiceUnavailable, ServerError,
    ];

    /// <summary>全部已登记错误码（含字符串码），供机械校验遍历。</summary>
    public static IEnumerable<ErrorCodeInfo> All =>
        ApiByKey.Values.Concat(ServerByKey.Values).Concat(ClientByKey.Values);

    /// <summary>
    /// 按数字码解析；未知码返回中性兜底条目（**不得**抛错、**不得**返回 null）。
    /// </summary>
    public static ErrorCodeInfo Resolve(int code)
    {
        return ApiByKey.TryGetValue(code.ToString(System.Globalization.CultureInfo.InvariantCulture), out var info)
            ? info
            : new ErrorCodeInfo(
                code.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ErrorCodeKind.Api,
                UnknownI18nKey,
                Retryable: false);
    }

    /// <summary>
    /// 按字符串码解析（WS 错误码 / 客户端错误码）；未知码返回中性兜底条目。
    /// </summary>
    public static ErrorCodeInfo Resolve(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return new ErrorCodeInfo(string.Empty, ErrorCodeKind.Server, UnknownI18nKey, Retryable: false);
        }

        if (ServerByKey.TryGetValue(code, out var server))
        {
            return server;
        }

        if (ClientByKey.TryGetValue(code, out var client))
        {
            return client;
        }

        // 服务端偶尔会把数字码放进 errorCode 字符串字段，一并兼容。
        if (int.TryParse(code, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var numeric))
        {
            return Resolve(numeric);
        }

        return new ErrorCodeInfo(code, ErrorCodeKind.Server, UnknownI18nKey, Retryable: false);
    }

    /// <summary>取 i18n key（未知码 → 中性兜底 key）。</summary>
    public static string I18nKeyOf(string? code) => Resolve(code).I18nKey;

    /// <summary>取 i18n key（数字码）。</summary>
    public static string I18nKeyOf(int code) => Resolve(code).I18nKey;

    /// <summary>
    /// 取 i18n key（可空数字码）。⚠️ 这不是多余重载：`PortCall.ErrorCode` / DTO 的可空 `int?`
    /// 字段若无此重载，C# 重载解析会**隐式选到 `I18nKeyOf(string?)`** 并把 `int?` 装箱，
    /// 导致编译错误或落到兜底文案。此处显式处理 `null` → 中性兜底文案。
    /// </summary>
    public static string I18nKeyOf(int? code)
        => code is { } value ? Resolve(value).I18nKey : UnknownI18nKey;

    /// <summary>
    /// 判断是否为鉴权类失败（仅此类才允许清除凭据；网络故障必须保留凭据 —— FR-W-AUTH-4 / EDGE-W-2）。
    /// </summary>
    public static bool IsAuthFailure(int? code) => code is AuthFailed or TokenInvalid;

    /// <summary>
    /// 判断「401/403 才清凭据」中的状态码判定（网络异常没有状态码，返回 false）。
    /// </summary>
    public static bool ShouldClearCredentials(int? httpStatus)
        => httpStatus is 401 or 403;
}
