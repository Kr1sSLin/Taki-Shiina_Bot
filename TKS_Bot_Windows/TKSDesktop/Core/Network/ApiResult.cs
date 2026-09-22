using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using TKSDesktop.Contracts;

namespace TKSDesktop.Core.Network;

/// <summary>
/// 失败类别（强类型结果的核心字段；**不得**把错误编码进 message 字符串 —— FR-W-ARCH-2）。
/// </summary>
public enum ApiFailureKind
{
    /// <summary>不是失败。</summary>
    None,

    /// <summary>连接层失败（DNS / TLS / 连接被拒 / 传输中断）。**必须保留凭据**，不视为鉴权失效。</summary>
    Network,

    /// <summary>请求超时（本地超时到期）。**必须保留凭据**。</summary>
    Timeout,

    /// <summary>调用方主动取消。</summary>
    Cancelled,

    /// <summary>非 2xx 且非 401（含 403 / 5xx）。</summary>
    Http,

    /// <summary>HTTP 200 但信封 <c>code != 0</c>（陷阱 4：业务失败）。</summary>
    Business,

    /// <summary>401（或鉴权类业务码）：Token 已失效，允许清凭据。</summary>
    Unauthorized,

    /// <summary>响应形状不可用（空响应体、非 JSON、信封缺 <c>data</c> 且调用方要求值）。</summary>
    InvalidResponse,

    /// <summary>本地配置问题（基址不合法、scheme 未确认等），请求未发出。</summary>
    Configuration,

    /// <summary>DPAPI 加解密失败（凭据**不落盘**，仅内存持有）。</summary>
    Crypto,

    /// <summary>本地文件 / 存储层失败。</summary>
    Storage,

    /// <summary>协议层失败（WS 帧、关闭码等）。</summary>
    Protocol,
}

/// <summary>
/// 强类型错误载荷。所有失败路径都必须能给出 <see cref="Code"/>（服务端业务码，可能为 <c>null</c>）、
/// <see cref="HttpStatus"/> 与 <see cref="TraceId"/>（FR-W-NET-1：trace 只在响应体 / 本地生成，**不读响应头**）。
/// </summary>
public sealed record ApiError(
    ApiFailureKind Kind,
    string Message,
    int? Code = null,
    int? HttpStatus = null,
    string? TraceId = null,
    string? ClientCode = null,
    string? I18nKey = null,
    bool CredentialsCleared = false)
{
    /// <summary>
    /// 界面文案 key：服务端码走 <see cref="ErrorCatalog"/>，客户端码（TIMEOUT / CONNECTION_LOST 等）
    /// 同样走目录；未知码回落中性兜底 key（V-W-S6：**不得**显示原始数字）。
    /// </summary>
    public string ErrorKey
        => I18nKey
           ?? (Code.HasValue ? ErrorCatalog.I18nKeyOf(Code.Value) : null)
           ?? (ClientCode is not null ? ErrorCatalog.I18nKeyOf(ClientCode) : null)
           ?? ErrorCatalog.UnknownI18nKey;

    /// <summary>是否值得重试（网络抖动 / 超时 / 服务端瞬时故障）。</summary>
    public bool IsRetryable
        => Kind is ApiFailureKind.Network or ApiFailureKind.Timeout
           || (Code.HasValue && ErrorCatalog.Resolve(Code.Value).Retryable);

    /// <summary>
    /// ⚠️ **仅 401/403 才允许清凭据**（FR-W-AUTH-4 / EDGE-W-2）。
    /// 网络故障 / 超时的 <see cref="HttpStatus"/> 为 <c>null</c>，因此恒为 <c>false</c> —— 凭据必须保留、不踢人。
    /// </summary>
    public bool ShouldClearCredentials => ErrorCatalog.ShouldClearCredentials(HttpStatus);

    /// <summary>是否为鉴权类失败（40101 / 40102 业务码）。</summary>
    public bool IsAuthFailure => ErrorCatalog.IsAuthFailure(Code);
}

/// <summary>
/// 统一失败异常（对齐 Linux 端 <c>TksApiError</c> 的语义字段，但**不**移植其
/// <c>IPC_ERROR_MARK</c> 把错误码编码进 message 的变通 —— FR-W-ARCH-2：Windows 端为进程内调用，
/// 自定义字段不会丢失，因此错误码**只**存在于强类型属性上）。
/// </summary>
public sealed class TksApiException : Exception
{
    /// <param name="kind">失败类别。</param>
    /// <param name="message">人类可读描述（英文，界面文案由 i18n key 决定，NFR-W-10）。</param>
    /// <param name="code">服务端业务错误码（信封 <c>code</c> 或 <c>detail.code</c>）。</param>
    /// <param name="httpStatus">HTTP 状态码；网络故障 / 超时为 <c>null</c>。</param>
    /// <param name="traceId">trace id（本地生成，或响应体回带）。</param>
    /// <param name="clientCode">客户端自有错误码（<see cref="ProtocolConstants.ClientErrorTimeout"/> 等）。</param>
    /// <param name="i18nKey">显式 i18n key（缺省则由 <see cref="ErrorCatalog"/> 推导）。</param>
    /// <param name="credentialInvalidated">本次失败是否已判定凭据失效（仅 401/403）。</param>
    /// <param name="innerException">内层异常。</param>
    public TksApiException(
        ApiFailureKind kind,
        string message,
        int? code = null,
        int? httpStatus = null,
        string? traceId = null,
        string? clientCode = null,
        string? i18nKey = null,
        bool credentialInvalidated = false,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
        Code = code;
        HttpStatus = httpStatus;
        TraceId = traceId;
        ClientCode = clientCode;
        I18nKey = i18nKey;
        CredentialInvalidated = credentialInvalidated;
    }

    /// <summary>失败类别。</summary>
    public ApiFailureKind Kind { get; }

    /// <summary>服务端业务错误码。</summary>
    public int? Code { get; }

    /// <summary>HTTP 状态码（网络故障为 <c>null</c>）。</summary>
    public int? HttpStatus { get; }

    /// <summary>trace id（FR-W-NET-1）。</summary>
    public string? TraceId { get; }

    /// <summary>客户端自有错误码。</summary>
    public string? ClientCode { get; }

    /// <summary>显式 i18n key（可为 <c>null</c>）。</summary>
    public string? I18nKey { get; }

    /// <summary>是否已判定凭据失效（只影响提示与跳登录页，不在此处落盘）。</summary>
    public bool CredentialInvalidated { get; }

    /// <summary>⚠️ 仅 401/403 为 <c>true</c>；网络故障 / 超时恒为 <c>false</c>（凭据必须保留）。</summary>
    public bool ShouldClearCredentials => ErrorCatalog.ShouldClearCredentials(HttpStatus);

    /// <summary>是否为鉴权类失败。</summary>
    public bool IsAuthFailure => Kind == ApiFailureKind.Unauthorized || ErrorCatalog.IsAuthFailure(Code);

    /// <summary>是否值得重试。</summary>
    public bool IsRetryable
        => Kind is ApiFailureKind.Network or ApiFailureKind.Timeout
           || (Code.HasValue && ErrorCatalog.Resolve(Code.Value).Retryable);

    /// <summary>转成强类型错误载荷。</summary>
    public ApiError ToError() => new(
        Kind,
        Message,
        Code,
        HttpStatus,
        TraceId,
        ClientCode,
        I18nKey,
        CredentialsCleared: ShouldClearCredentials);

    public static TksApiException Network(string message, string? traceId, Exception? inner = null)
        => new(ApiFailureKind.Network, message, traceId: traceId, i18nKey: ErrorCatalog.I18nKeyOf(ErrorCatalog.ConnectionLost), innerException: inner);

    public static TksApiException Timeout(string? traceId, int? timeoutMs = null)
        => new(
            ApiFailureKind.Timeout,
            timeoutMs.HasValue
                ? string.Create(CultureInfo.InvariantCulture, $"request timeout after {timeoutMs.Value} ms")
                : "request timeout",
            traceId: traceId,
            clientCode: ProtocolConstants.ClientErrorTimeout,
            i18nKey: ErrorCatalog.I18nKeyOf(ErrorCatalog.Timeout));

    public static TksApiException Cancelled(string? traceId)
        => new(ApiFailureKind.Cancelled, "request cancelled", traceId: traceId);

    public static TksApiException Http(int httpStatus, int? code, string message, string? traceId)
        => new(ApiFailureKind.Http, message, code, httpStatus, traceId);

    /// <summary>HTTP 200 + <c>code != 0</c>（陷阱 4）。</summary>
    public static TksApiException Business(int code, string? message, int? httpStatus, string? traceId)
        => new(
            ApiFailureKind.Business,
            message ?? string.Create(CultureInfo.InvariantCulture, $"business error {code}"),
            code,
            httpStatus,
            traceId);

    /// <summary>401 且续签未成功（凭据失效）。</summary>
    public static TksApiException Unauthorized(int? code, string? message, string? traceId, bool credentialInvalidated = true)
        => new(
            ApiFailureKind.Unauthorized,
            message ?? "unauthorized",
            code ?? ErrorCatalog.AuthFailed,
            httpStatus: 401,
            traceId: traceId,
            credentialInvalidated: credentialInvalidated);

    public static TksApiException InvalidResponse(string message, string? traceId)
        => new(ApiFailureKind.InvalidResponse, message, traceId: traceId);

    public static TksApiException Configuration(string message)
        => new(ApiFailureKind.Configuration, message);

    public static TksApiException Crypto(string message, Exception? inner = null)
        => new(ApiFailureKind.Crypto, message, innerException: inner);

    public static TksApiException Storage(string message, Exception? inner = null)
        => new(ApiFailureKind.Storage, message, innerException: inner);

    /// <summary>排障用文本（含 code / status / traceId，**不含**凭据）。</summary>
    public override string ToString()
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{GetType().Name}({Kind}): {Message} [code={Code?.ToString(CultureInfo.InvariantCulture) ?? "-"}, http={HttpStatus?.ToString(CultureInfo.InvariantCulture) ?? "-"}, trace={TraceId ?? "-"}]");
}

/// <summary>
/// 无值结果（POST / DELETE 等不关心返回体的调用）。
/// </summary>
public class ApiResult
{
    /// <summary>构造成功结果。</summary>
    protected ApiResult(string? traceId)
    {
        IsSuccess = true;
        TraceId = traceId;
    }

    /// <summary>构造失败结果。</summary>
    protected ApiResult(ApiError error)
    {
        IsSuccess = false;
        Error = error;
        TraceId = error.TraceId;
    }

    /// <summary>是否成功。</summary>
    public bool IsSuccess { get; }

    /// <summary>是否失败。</summary>
    public bool IsFailure => !IsSuccess;

    /// <summary>失败载荷（成功时为 <c>null</c>）。</summary>
    public ApiError? Error { get; }

    /// <summary>本次请求的 trace id。</summary>
    public string? TraceId { get; }

    /// <summary>失败时的界面文案 key（成功时为中性 key）。</summary>
    public string ErrorKey => Error?.ErrorKey ?? ErrorCatalog.UnknownI18nKey;

    /// <summary>成功（无值）。</summary>
    public static ApiResult Ok(string? traceId = null) => new(traceId);

    /// <summary>失败。</summary>
    public static ApiResult Fail(ApiError error) => new(error);

    /// <summary>失败。</summary>
    public static ApiResult Fail(TksApiException exception) => new(exception.ToError());

    /// <summary>失败（直接给字段）。</summary>
    public static ApiResult Fail(ApiFailureKind kind, string message, int? code = null, int? httpStatus = null, string? traceId = null)
        => new(new ApiError(kind, message, code, httpStatus, traceId));

    /// <summary>成功（带值）。</summary>
    public static ApiResult<T> Success<T>(T value, string? traceId = null) => new(value, traceId);

    /// <summary>失败（带类型）。</summary>
    public static ApiResult<T> Fail<T>(ApiError error) => new(error);

    /// <summary>失败（带类型）。</summary>
    public static ApiResult<T> Fail<T>(TksApiException exception) => new(exception.ToError());

    public override string ToString()
        => IsSuccess
            ? $"ApiResult(success, trace={TraceId ?? "-"})"
            : $"ApiResult(failure, {Error})";
}

/// <summary>
/// 带类型的强类型结果。**调用方一律通过 <see cref="Error"/> / <see cref="ErrorKey"/> 取错误语义**，
/// 不允许从 <see cref="ApiError.Message"/> 里解析错误码（FR-W-ARCH-2）。
/// </summary>
/// <typeparam name="T">响应信封 <c>data</c> 的类型。</typeparam>
public sealed class ApiResult<T> : ApiResult
{
    internal ApiResult(T? value, string? traceId)
        : base(traceId)
    {
        Value = value;
        // ⚠️ `data: null` / 缺失必须表现为「成功但无值」，而不是 HasValue=true 配 default 值
        //    （否则调用方 TryGetValue 会拿到看似合法的默认对象）。
        HasValue = value is not null;
    }

    internal ApiResult(ApiError error)
        : base(error)
    {
        Value = default;
        HasValue = false;
    }

    /// <summary>
    /// 成功时的值（<see cref="HasValue"/> 为 <c>false</c> 时为 <c>default</c>：
    /// 例如 <c>code == 0</c> 但 <c>data</c> 缺失的空响应）。
    /// </summary>
    public T? Value { get; }

    /// <summary>是否带值（<c>code == 0</c> 且 <c>data</c> 非 <c>null</c>）。</summary>
    public bool HasValue { get; }

    /// <summary>
    /// 尝试取值。返回 <c>false</c> 表示失败或响应体无 <c>data</c>（NFR-W-12 宽容处理，不抛错）。
    /// </summary>
    public bool TryGetValue([NotNullWhen(true)] out T? value)
    {
        if (IsSuccess && HasValue && Value is not null)
        {
            value = Value;
            return true;
        }

        value = default;
        return false;
    }

    public override string ToString()
        => IsSuccess
            ? $"ApiResult<{typeof(T).Name}>(success, hasValue={HasValue}, trace={TraceId ?? "-"})"
            : $"ApiResult<{typeof(T).Name}>(failure, {Error})";
}
