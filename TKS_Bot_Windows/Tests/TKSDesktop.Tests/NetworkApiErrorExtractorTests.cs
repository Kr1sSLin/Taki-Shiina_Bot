using Xunit;
using System.Text.Json;
using TKSDesktop.Contracts;
using TKSDesktop.Core.Network;

namespace TKSDesktop.Tests;

/// <summary>
/// 双形状错误码提取测试（PRD §5.2 / §5.6 **陷阱 3**）。
/// 覆盖：顶层 <c>code</c>、<c>detail.code</c>、未知形状（返回 null 且**不抛错**）。
/// </summary>
public sealed class NetworkApiErrorExtractorTests
{
    /* ---------------- 形状 ①：顶层 code（业务失败，HTTP 200） ---------------- */

    [Fact]
    public void ExtractCode_TopLevelShape_ReturnsCode()
    {
        const string body = """{"code":40101,"message":"鉴权失败","traceId":"trace_abc"}""";

        var info = ApiErrorExtractor.Extract(body);

        Assert.Equal(ErrorCatalog.AuthFailed, info.Code);
        Assert.Equal("鉴权失败", info.Message);
        Assert.Equal("trace_abc", info.TraceId);
        Assert.True(info.HasAny);
    }

    [Fact]
    public void ExtractCode_TopLevelShape_WithoutMessageAndTrace_StillReturnsCode()
    {
        var code = ApiErrorExtractor.ExtractCode("""{"code":40201}""");

        Assert.Equal(ErrorCatalog.PointsInsufficient, code);
    }

    /* ---------------- 形状 ②：detail.code（http_api 二次包装） --------------- */

    [Fact]
    public void ExtractCode_DetailShape_ReturnsCodeFromDetail()
    {
        const string body = """{"detail":{"code":40101,"message":"鉴权失败"}}""";

        var info = ApiErrorExtractor.Extract(body);

        Assert.Equal(ErrorCatalog.AuthFailed, info.Code);
        Assert.Equal("鉴权失败", info.Message);
    }

    [Fact]
    public void ExtractCode_DetailShape_WithTraceId()
    {
        const string body = """{"detail":{"code":40102,"message":"refresh token invalid","traceId":"trace_xyz"}}""";

        Assert.Equal(ErrorCatalog.TokenInvalid, ApiErrorExtractor.ExtractCode(body));
        Assert.Equal("trace_xyz", ApiErrorExtractor.ExtractTraceId(body));
    }

    [Fact]
    public void ExtractCode_TopLevelWinsOverDetail()
    {
        // 顶层与 detail 同时存在时以顶层为准（顶层是更贴近业务失败的位置）。
        const string body = """{"code":40204,"message":"AI 失败","detail":{"code":40101,"message":"鉴权失败"}}""";

        Assert.Equal(ErrorCatalog.InteractionAiFailed, ApiErrorExtractor.ExtractCode(body));
        Assert.Equal("AI 失败", ApiErrorExtractor.ExtractMessage(body));
    }

    /* ---------------- 兼容补充形状 ------------------------------------------- */

    [Fact]
    public void ExtractMessage_DetailAsPlainString_ReturnsFastApiDefaultShape()
    {
        // FastAPI 默认 HTTPException(detail="...")：detail 是字符串，无 code。
        const string body = """{"detail":"Not Found"}""";

        Assert.Null(ApiErrorExtractor.ExtractCode(body));
        Assert.Equal("Not Found", ApiErrorExtractor.ExtractMessage(body));
    }

    [Fact]
    public void ExtractCode_NumericStringCode_IsAccepted()
    {
        // 服务端偶尔把码序列化成字符串。
        Assert.Equal(ErrorCatalog.ParamMissing, ApiErrorExtractor.ExtractCode("""{"code":"40001"}"""));
    }

    [Fact]
    public void ExtractCode_ErrorCodeFallbackField_IsAccepted()
    {
        Assert.Equal(ErrorCatalog.InteractionAiFailed, ApiErrorExtractor.ExtractCode("""{"errorCode":40204}"""));
    }

    /* ---------------- 未知形状：返回 null 且不抛错 --------------------------- */

    [Fact]
    public void Extract_UnknownShape_ReturnsAllNullWithoutThrowing()
    {
        const string body = """{"status":"ok","service":"ws_api"}""";

        var info = ApiErrorExtractor.Extract(body);

        Assert.Null(info.Code);
        Assert.Null(info.Message);
        Assert.Null(info.TraceId);
        Assert.False(info.HasAny);
    }

    [Fact]
    public void Extract_InvalidJson_ReturnsAllNullWithoutThrowing()
    {
        var info = ApiErrorExtractor.Extract("{ this is not json");

        Assert.Null(info.Code);
        Assert.Null(info.Message);
        Assert.Null(info.TraceId);
    }

    [Fact]
    public void Extract_NonObjectRoot_ReturnsAllNullWithoutThrowing()
    {
        Assert.Null(ApiErrorExtractor.ExtractCode("[1,2,3]"));
        Assert.Null(ApiErrorExtractor.ExtractCode("\"just a string\""));
        Assert.Null(ApiErrorExtractor.ExtractCode("42"));
        Assert.Null(ApiErrorExtractor.ExtractCode("null"));
    }

    [Fact]
    public void Extract_NullOrWhitespaceBody_ReturnsAllNullWithoutThrowing()
    {
        // `Extract` 返回的是记录（永不 null）；空输入的语义是「没有任何字段」。
        Assert.False(ApiErrorExtractor.Extract(null).HasAny);
        Assert.False(ApiErrorExtractor.Extract(string.Empty).HasAny);
        Assert.False(ApiErrorExtractor.Extract("   ").HasAny);
        Assert.Null(ApiErrorExtractor.ExtractCode(null));
        Assert.Null(ApiErrorExtractor.ExtractMessage(null));
        Assert.Null(ApiErrorExtractor.ExtractTraceId(null));
    }

    [Fact]
    public void Extract_DetailAsValidationArray_PicksFirstMessageWithoutThrowing()
    {
        const string body = """{"detail":[{"loc":["body","username"],"msg":"field required","type":"value_error"}]}""";

        var info = ApiErrorExtractor.Extract(body);

        Assert.Null(info.Code);
        Assert.Equal("field required", info.Message);
    }

    [Fact]
    public void Extract_CodeWithWrongJsonType_ReturnsNullInsteadOfThrowing()
    {
        // code 是布尔 / 对象时一律返回 null，**不得**抛错（NFR-W-12）。
        Assert.Null(ApiErrorExtractor.ExtractCode("""{"code":true}"""));
        Assert.Null(ApiErrorExtractor.ExtractCode("""{"code":{"nested":1}}"""));
        Assert.Null(ApiErrorExtractor.ExtractCode("""{"code":null}"""));
    }

    /* ---------------- 从已解析 JsonElement 提取 ------------------------------ */

    [Fact]
    public void Extract_FromJsonElement_MatchesStringOverload()
    {
        const string body = """{"detail":{"code":40301,"message":"device not allowed"}}""";

        using var document = JsonDocument.Parse(body);
        var fromElement = ApiErrorExtractor.Extract(document.RootElement);

        Assert.Equal(ErrorCatalog.DeviceNotAllowed, fromElement.Code);
        Assert.Equal("device not allowed", fromElement.Message);
    }

    [Fact]
    public void ExtractCode_ZeroIsPreserved()
    {
        // `code == 0` 也必须被提取出来（判成功用它），不能被当成「未找到」。
        Assert.Equal(ErrorCatalog.CodeOk, ApiErrorExtractor.ExtractCode("""{"code":0,"data":{"a":1}}"""));
    }
}
