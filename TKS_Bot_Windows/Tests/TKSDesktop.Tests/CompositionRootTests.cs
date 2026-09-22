using TKSDesktop.App;
using TKSDesktop.Core.Services;
using TKSDesktop.Contracts;
using TKSDesktop.Contracts.Dtos;
using TKSDesktop.Core.Services.Ports;
using TKSDesktop.Core.Services.Chat;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TKSDesktop.Core.Platform;
using Xunit;

namespace TKSDesktop.Tests;

public sealed class CompositionRootTests
{
    [Fact]
    public async Task ProductionCompositionRoot_ResolvesM0ThroughM3RuntimeGraph()
    {
        var root = Path.Combine(Path.GetTempPath(), "tks-composition-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        ServiceProvider? provider = null;
        try
        {
            var environment = new Dictionary<string, string?>
            {
                [AppPaths.EnvConfigDir] = Path.Combine(root, "config"),
                [AppPaths.EnvDataDir] = Path.Combine(root, "data"),
                [AppPaths.EnvStateDir] = Path.Combine(root, "state"),
            };
            var paths = new AppPaths(root, environment);
            paths.EnsureDirectories();

            // BuildServiceProvider 内启用 ValidateOnBuild/ValidateScopes：任何遗漏的端口、
            // 服务或 ViewModel 依赖都会在这里失败，无需创建真实 Windows 消息窗口。
            provider = AppBootstrap.BuildServiceProvider(CliOptions.Parse([]), paths);
            Assert.NotNull(provider);
            Assert.NotNull(provider.GetService(typeof(IAuthService)));
            Assert.NotNull(provider.GetService(typeof(IChatService)));
            Assert.NotNull(provider.GetService(typeof(ISyncService)));
            Assert.NotNull(provider.GetService(typeof(IMediaService)));

            var repository = provider.GetRequiredService<IChatRepositoryPort>();
            var rest = new FallbackRest();
            var connection = new DegradedConnection();
            using var chat = new ChatService(repository, connection,
                provider.GetRequiredService<IReminderScheduler>(),
                provider.GetRequiredService<IUserNotificationService>(),
                provider.GetRequiredService<IUiDispatcher>(),
                NullLogger<ChatService>.Instance, rest: rest);
            var changes = new List<ChatMessageView>();
            chat.MessageChanged += (_, message) => changes.Add(message);

            var sent = await chat.SendFallbackAsync("request-1", "hello");
            Assert.True(sent.Success);
            Assert.Equal("request-1", rest.Request?.RequestId);
            Assert.False(rest.Request?.Stream);
            Assert.Equal(ProtocolConstants.StatusSent, (await repository.GetMessageAsync("request-1"))?.Status);
            Assert.Equal("first", (await repository.GetMessageAsync("reply-1_0"))?.Content);
            Assert.Equal("second", (await repository.GetMessageAsync("reply-1_1"))?.Content);
            Assert.Contains(changes, message => message.Status == ProtocolConstants.StatusSending);

            rest.Fail = true;
            Assert.False((await chat.SendFallbackAsync("request-2", "retry later")).Success);
            Assert.Equal(ProtocolConstants.StatusError, (await repository.GetMessageAsync("request-2"))?.Status);

            connection.Current = new(ConnectionStatus.Disconnected, 0, null, false);
            Assert.False((await chat.SendFallbackAsync("request-3", "offline")).Success);
            Assert.Null(await repository.GetMessageAsync("request-3"));

            var attachments = provider.GetRequiredService<IAttachmentPort>();
            var media = provider.GetRequiredService<IMediaService>();
            var imagePath = Path.Combine(paths.AttachmentsDir, "retained.png");
            await File.WriteAllBytesAsync(imagePath, [1, 2, 3]);
            var draft = await attachments.InsertDraftAsync("image/png", imagePath, 3, null, null);
            await media.RestoreDraftsAsync();
            await attachments.BindAsync([draft.AttachmentId], "request-1");
            await media.ClearDraftsAsync();
            Assert.Empty(media.Drafts);
            Assert.True(File.Exists(imagePath));
            Assert.Single(await attachments.ListByMessageAsync("request-1"));

            var cleared = false;
            chat.ConversationCleared += (_, _) => cleared = true;
            await chat.ClearLocalConversationAsync();
            Assert.True(cleared);
            Assert.Null(await repository.GetMessageAsync("request-1"));
        }
        finally
        {
            if (provider is not null)
            {
                // ILoggerFactory owns the file logger provider. Dispose it explicitly before
                // tearing down the async service graph so Windows can remove the temp tree.
                (provider.GetService<Microsoft.Extensions.Logging.ILoggerFactory>() as IDisposable)?.Dispose();
                await provider.DisposeAsync();
            }

            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private sealed class FallbackRest : IRestPort
    {
        public RestChatRequestDto? Request { get; private set; }
        public bool Fail { get; set; }
        public Task<PortCall<RestChatDataDto>> SendChatAsync(RestChatRequestDto request, CancellationToken cancellationToken = default)
        {
            Request = request;
            return Task.FromResult(Fail
                ? PortCall<RestChatDataDto>.Fail("TIMEOUT", "error.unknown")
                : PortCall<RestChatDataDto>.Ok(new() { MessageId = "reply-1", Reply = "first\nsecond" }));
        }

        public Task<PortCall<IReadOnlyList<TimelineItemDto>>> GetChatHistoryAsync(long since, int limit, CancellationToken cancellationToken = default)
            => Task.FromResult(PortCall<IReadOnlyList<TimelineItemDto>>.Ok([]));

        public Task<PortCall<IReadOnlyList<UserFactDto>>> GetMemoryFactsAsync(long since, int limit, CancellationToken cancellationToken = default)
            => Task.FromResult(PortCall<IReadOnlyList<UserFactDto>>.Ok([]));
    }

    private sealed class DegradedConnection : IWssPort
    {
        public ConnectionSnapshot Current { get; set; } = new(ConnectionStatus.Degraded, 15, null, true);
        public bool IsConnected => false;
        public bool NeedsFullSync => true;
        public event EventHandler<ConnectionSnapshot>? ConnectionChanged { add { } remove { } }
        public event EventHandler<WsServerFrame>? FrameReceived { add { } remove { } }
        public bool SendChatMessage(string requestId, string? content, IReadOnlyList<ChatImagePayloadDto>? images) => false;
        public Task ReconnectAsync(bool manual, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void MarkFullSyncRequired() { }
    }
}
