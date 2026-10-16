using System.Net;
using System.Net.Http;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using TKSDesktop.Core.Network;
using Xunit;

namespace TKSDesktop.Tests;

public sealed class HistoryImageDownloadTests
{
    [Fact]
    public async Task OriginalBytesSurviveBearerRefreshAndEncodedRequestId()
    {
        var original = new byte[] { 0xff, 0xd8, 0xff, 0xe1, 0x00, 0x0b, 0x45, 0x58, 0x49, 0x46, 0x20, 0x31, 0x32, 0xff, 0xd9 };
        var handler = new ImageHandler(original);
        var tokens = new FakeTokenProvider();
        using var rest = new RestClient(new HttpClient(handler), tokens,
            () => "https://example.test/api/v1/", NullLogger<RestClient>.Instance);

        var result = await rest.GetHistoryImageAsync("source id/1", 0);

        Assert.True(result.IsSuccess);
        Assert.Equal("image/jpeg", result.Value!.MimeType);
        Assert.Equal(original, result.Value.Bytes);
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, r => Assert.Contains("source%20id%2F1/0", r.Path));
        Assert.Equal("Bearer access-token-old", handler.Requests[0].Authorization);
        Assert.Equal("Bearer access-token-new", handler.Requests[1].Authorization);
        Assert.Equal(1, tokens.RefreshCount);
    }

    [Fact]
    public async Task OversizedOriginalIsRejectedBeforeStorage()
    {
        var handler = new ImageHandler([0xff, 0xd8, 0xff], oversizeHeader: true);
        using var rest = new RestClient(new HttpClient(handler), new FakeTokenProvider(),
            () => "https://example.test/api/v1/", NullLogger<RestClient>.Instance);

        var result = await rest.GetHistoryImageAsync("source", 0);

        Assert.True(result.IsFailure);
        Assert.Equal(ApiFailureKind.InvalidResponse, result.Error!.Kind);
    }

    private sealed class ImageHandler(byte[] bytes, bool oversizeHeader = false) : HttpMessageHandler
    {
        public List<(string Path, string? Authorization)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!.AbsolutePath, request.Headers.Authorization?.ToString()));
            if (!oversizeHeader && Requests.Count == 1)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json"),
                });
            }
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
            if (oversizeHeader) content.Headers.ContentLength = ProtocolConstants.MaxImageBytes + 1;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
