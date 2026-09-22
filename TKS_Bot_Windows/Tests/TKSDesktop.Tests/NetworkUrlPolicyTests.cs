using Xunit;
using TKSDesktop.Contracts;
using TKSDesktop.Core.Network;

namespace TKSDesktop.Tests;

/// <summary>
/// URL 校验与派生测试（FR-W-CFG-2 / FR-W-CFG-4 / FR-W-SEC-4）。
/// 覆盖：`https`/`wss` 通过；`http`/`ws` 非回环被拒（需二次确认）；回环免确认；`DeriveWsBaseUrl` 各分支。
/// </summary>
public sealed class NetworkUrlPolicyTests
{
    /* ---------------- scheme 校验：https / wss 通过 ------------------------- */

    [Theory]
    [InlineData("https://takishiinabot.top/api/v1/")]
    [InlineData("https://example.com")]
    [InlineData("https://localhost:8000/api/v1/")]
    public void CheckAllowedScheme_RestHttps_IsValidWithoutConfirmation(string value)
    {
        var check = HealthProbe.CheckAllowedScheme(value, ServiceEndpointKind.Rest);

        Assert.True(check.IsValid);
        Assert.False(check.RequiresConfirmation);
        Assert.True(check.IsAllowedWithoutConfirmation);
        Assert.Null(check.MessageKey);
    }

    [Theory]
    [InlineData("wss://takishiinabot.top")]
    [InlineData("wss://localhost:8001")]
    public void CheckAllowedScheme_WsWss_IsValidWithoutConfirmation(string value)
    {
        var check = HealthProbe.CheckAllowedScheme(value, ServiceEndpointKind.WebSocket);

        Assert.True(check.IsValid);
        Assert.False(check.RequiresConfirmation);
        Assert.True(check.IsAllowedWithoutConfirmation);
    }

    /* ---------------- http / ws 非回环：合法但需二次确认 --------------------- */

    [Theory]
    [InlineData("http://takishiinabot.top/api/v1/")]
    [InlineData("http://192.168.1.10:8000/api/v1/")]
    public void CheckAllowedScheme_RestHttpNonLoopback_RequiresConfirmation(string value)
    {
        var check = HealthProbe.CheckAllowedScheme(value, ServiceEndpointKind.Rest);

        Assert.True(check.IsValid);
        Assert.True(check.RequiresConfirmation);
        Assert.False(check.IsLoopback);
        Assert.False(check.IsAllowedWithoutConfirmation);
        Assert.Equal("settings.url.warning.plaintext", check.MessageKey);
    }

    [Fact]
    public void CheckAllowedScheme_WsNonLoopback_RequiresConfirmation()
    {
        var check = HealthProbe.CheckAllowedScheme("ws://takishiinabot.top", ServiceEndpointKind.WebSocket);

        Assert.True(check.IsValid);
        Assert.True(check.RequiresConfirmation);
        Assert.Equal("ws", check.Scheme);
    }

    /* ---------------- 回环免确认（FR-W-CFG-2 的唯一豁免） -------------------- */

    [Theory]
    [InlineData("http://localhost:8000/api/v1/")]
    [InlineData("http://127.0.0.1:8000/api/v1/")]
    [InlineData("http://[::1]:8000/api/v1/")]
    public void CheckAllowedScheme_RestHttpLoopback_IsExemptFromConfirmation(string value)
    {
        var check = HealthProbe.CheckAllowedScheme(value, ServiceEndpointKind.Rest);

        Assert.True(check.IsValid);
        Assert.True(check.IsLoopback);
        Assert.False(check.RequiresConfirmation);
        Assert.True(check.IsAllowedWithoutConfirmation);
    }

    [Theory]
    [InlineData("ws://localhost:8001")]
    [InlineData("ws://127.0.0.1:8001")]
    [InlineData("ws://[::1]:8001")]
    public void CheckAllowedScheme_WsLoopback_IsExemptFromConfirmation(string value)
    {
        var check = HealthProbe.CheckAllowedScheme(value, ServiceEndpointKind.WebSocket);

        Assert.True(check.IsValid);
        Assert.True(check.IsLoopback);
        Assert.False(check.RequiresConfirmation);
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("LOCALHOST")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("[::1]")]
    public void IsLoopback_RecognizesOnlyExemptHosts(string host)
    {
        Assert.True(HealthProbe.IsLoopback(host));
    }

    [Theory]
    [InlineData("takishiinabot.top")]
    [InlineData("192.168.1.10")]
    [InlineData("localhost.evil.com")]
    [InlineData("127.0.0.2")]
    [InlineData(null)]
    [InlineData("")]
    public void IsLoopback_RejectsNonLoopbackHosts(string? host)
    {
        Assert.False(HealthProbe.IsLoopback(host));
    }

    /* ---------------- 其它 scheme 一律非法 ---------------------------------- */

    [Theory]
    [InlineData("ftp://takishiinabot.top")]
    [InlineData("file:///C:/x")]
    [InlineData("takishiinabot.top")]
    [InlineData("")]
    [InlineData("   ")]
    public void CheckAllowedScheme_OtherSchemes_AreInvalid(string value)
    {
        var check = HealthProbe.CheckAllowedScheme(value, ServiceEndpointKind.Rest);

        Assert.False(check.IsValid);
        Assert.False(check.IsAllowedWithoutConfirmation);
    }

    [Fact]
    public void CheckAllowedScheme_CrossSchemeRest_IsInvalid()
    {
        // REST 基址必须 http(s)，ws(s) 不得当作 REST 基址。
        var check = HealthProbe.CheckAllowedScheme("wss://takishiinabot.top", ServiceEndpointKind.Rest);

        Assert.False(check.IsValid);
        Assert.Equal("settings.url.error.scheme", check.MessageKey);
    }

    /* ---------------- DeriveWsBaseUrl（FR-W-CFG-4） ------------------------- */

    [Theory]
    [InlineData("https://takishiinabot.top/api/v1/", "wss://takishiinabot.top")]
    [InlineData("https://takishiinabot.top/api/v1", "wss://takishiinabot.top")]
    [InlineData("http://localhost:8000/api/v1/", "ws://localhost:8000")]
    [InlineData("http://127.0.0.1:8000/api/v1/", "ws://127.0.0.1:8000")]
    [InlineData("https://example.com", "wss://example.com")]
    [InlineData("https://example.com/some/deep/path?x=1&y=2", "wss://example.com")]
    [InlineData("wss://takishiinabot.top", "wss://takishiinabot.top")]
    [InlineData("ws://localhost:8001", "ws://localhost:8001")]
    [InlineData("https://example.com:8443/api/v1/", "wss://example.com:8443")]
    public void DeriveWsBaseUrl_ConvertsSchemeClearsPathAndTrailingSlash(string apiBaseUrl, string expected)
    {
        Assert.Equal(expected, HealthProbe.DeriveWsBaseUrl(apiBaseUrl));
    }

    [Fact]
    public void DeriveWsBaseUrl_ClearsQueryAndFragment()
    {
        var derived = HealthProbe.DeriveWsBaseUrl("https://takishiinabot.top/api/v1/?foo=bar#frag");

        Assert.Equal("wss://takishiinabot.top", derived);
        Assert.DoesNotContain("?", derived, StringComparison.Ordinal);
        Assert.DoesNotContain("#", derived, StringComparison.Ordinal);
        Assert.DoesNotContain("/api", derived, StringComparison.Ordinal);
    }

    [Fact]
    public void DeriveWsBaseUrl_NeverEndsWithSlash()
    {
        Assert.False(HealthProbe.DeriveWsBaseUrl("https://takishiinabot.top/api/v1/").EndsWith('/'));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-url")]
    public void DeriveWsBaseUrl_InvalidInput_FallsBackToDefault(string? apiBaseUrl)
    {
        Assert.Equal(ProtocolConstants.DefaultWsBaseUrl, HealthProbe.DeriveWsBaseUrl(apiBaseUrl));
    }

    [Fact]
    public void DeriveWsBaseUrl_DefaultApiBaseUrl_MatchesDefaultWsBaseUrl()
    {
        // 默认配置下派生结果必须与另一个默认常量一致（防漂移）。
        Assert.Equal(
            ProtocolConstants.DefaultWsBaseUrl,
            HealthProbe.DeriveWsBaseUrl(ProtocolConstants.DefaultApiBaseUrl));
    }

    [Fact]
    public void NormalizeApiBaseUrl_AppendsTrailingSlashExactlyOnce()
    {
        Assert.Equal("https://a/api/v1/", HealthProbe.NormalizeApiBaseUrl("https://a/api/v1"));
        Assert.Equal("https://a/api/v1/", HealthProbe.NormalizeApiBaseUrl("https://a/api/v1/"));
        Assert.Equal("https://a/api/v1/", HealthProbe.NormalizeApiBaseUrl("  https://a/api/v1  "));
    }

    /* ---------------- /healthz 地址构造（EDGE-W-28） ------------------------ */

    [Theory]
    [InlineData("https://takishiinabot.top/api/v1/", "https://takishiinabot.top/healthz")]
    [InlineData("https://takishiinabot.top/api/v1", "https://takishiinabot.top/healthz")]
    [InlineData("https://takishiinabot.top/API/V1/", "https://takishiinabot.top/healthz")]
    [InlineData("http://localhost:8000/api/v1/", "http://localhost:8000/healthz")]
    public void TryBuildHealthUri_StripsApiV1Prefix(string apiBaseUrl, string expected)
    {
        Assert.True(HealthProbe.TryBuildHealthUri(apiBaseUrl, out var uri));
        Assert.Equal(expected, uri.ToString());
    }

    [Fact]
    public void TryBuildHealthUri_BaseUrlWithoutApiPrefix_StillAppendsHealthz()
    {
        Assert.True(HealthProbe.TryBuildHealthUri("https://example.com/", out var uri));
        Assert.Equal("https://example.com/healthz", uri.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-url")]
    public void TryBuildHealthUri_InvalidInput_ReturnsFalseWithoutThrowing(string? apiBaseUrl)
    {
        Assert.False(HealthProbe.TryBuildHealthUri(apiBaseUrl, out var uri));
        Assert.Null(uri);
    }

    /* ---------------- 三态语义（文案可区分） --------------------------------- */

    [Fact]
    public void HealthStatus_HasDistinguishableMiddleState()
    {
        Assert.Equal(3, Enum.GetValues<HealthStatus>().Length);

        var middle = new HealthProbeResult(HealthStatus.HealthEndpointUnavailable, "u", 404, 5, null);
        var unreachable = new HealthProbeResult(HealthStatus.Unreachable, "u", null, null, null);
        var reachable = new HealthProbeResult(HealthStatus.Reachable, "u", 200, 5, null);

        // 中间态**不得**与完全不可达混同（EDGE-W-28 要求文案区别于「无法连接服务」）。
        Assert.NotEqual(middle.Status, unreachable.Status);
        Assert.True(middle.IsHealthEndpointUnavailable);
        Assert.False(middle.IsReachable);
        Assert.True(reachable.IsReachable);
    }
}
