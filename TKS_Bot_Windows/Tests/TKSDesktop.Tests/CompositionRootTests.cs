using TKSDesktop.App;
using TKSDesktop.ViewModels;
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
                NullLogger<ChatService>.Instance, rest: rest, media: provider.GetRequiredService<IMediaService>(), attachmentPort: provider.GetRequiredService<IAttachmentPort>());
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
            var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=");
            await File.WriteAllBytesAsync(imagePath, png);
            var draft = await attachments.InsertDraftAsync("image/png", imagePath, png.Length, null, null);
            await media.RestoreDraftsAsync();
            await attachments.BindAsync([draft.AttachmentId], "request-1");
            await media.ClearDraftsAsync();
            Assert.Empty(media.Drafts);
            Assert.True(File.Exists(imagePath));
            Assert.Single(await attachments.ListByMessageAsync("request-1"));

            // M4: REST works without WS, shares stream delivery and historical deduplication.
            await repository.UpsertMessageAsync(new("merged-chat", "user", "text", "before gift", ProtocolConstants.StatusSending, 1));
            await chat.BeginAsync("gift-1", "coffee", "gift note");
            var giftReply = new InteractionSendDataDto
            {
                Success = true,
                MessageId = "gift-reply",
                Reply = "thank you\naccepted",
                MergedRequestIds = ["merged-chat"],
                Item = new() { Name = "coffee", Icon = "C" },
            };
            await chat.CompleteAsync("gift-1", giftReply, true, null);
            Assert.Equal(ProtocolConstants.StatusSent, (await repository.GetMessageAsync("merged-chat"))?.Status);
            Assert.Equal(ProtocolConstants.StatusSent, (await repository.GetMessageAsync("gift-1"))?.Status);

            var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            chat.TypingChanged += (_, state) => { if (!state.Typing) finished.TrySetResult(); };
            connection.Raise(new WsReplyStreamFrame("gift-1", new()
            {
                Done = true,
                MessageId = "gift-reply",
                FinalContent = giftReply.Reply,
                RequestIds = ["gift-1", "merged-chat"],
                MessageKind = "interaction",
            }));
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            rest.History = [new() { MessageId = "gift-reply", Role = "bot", Content = giftReply.Reply,
                ContentType = "interaction", Timestamp = 2 }];
            using var giftSync = new Core.Services.Sync.SyncService(repository, rest,
                provider.GetRequiredService<IUiDispatcher>(), NullLogger<Core.Services.Sync.SyncService>.Instance);
            await giftSync.SyncFullAsync();
            Assert.Equal(2, changes.Count(m => m.MessageId.StartsWith("gift-reply_", StringComparison.Ordinal)));
            Assert.True(await repository.IsDeliveredAsync("gift-reply_0"));
            Assert.True(await repository.IsDeliveredAsync("gift-reply_1"));
            Assert.Equal("interaction", (await repository.GetMessageAsync("gift-reply_0"))?.ContentType);
            await chat.CompleteAsync("gift-1", null, false, "TIMEOUT");
            Assert.Equal(ProtocolConstants.StatusSent, (await repository.GetMessageAsync("gift-1"))?.Status);

            using var samples = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(Gate.GateRepo.Root, "..", "design", "foundation-protocol-samples.json")));
            var recovery = samples.RootElement.GetProperty("recovery");
            var recovered = System.Text.Json.JsonSerializer.Deserialize<ChatHistoryDataDto>(recovery.GetProperty("historyData").GetRawText(), WsFrameParser.Options)!;
            recovered.Operations[0].RequestIds.Add("request-1");
            rest.Operations = recovered.Operations;
            await giftSync.SyncFullAsync();
            Assert.Equal("RESEND_NEW_REQUEST", (await repository.GetMessageAsync("sample-text"))?.ErrorCode);
            Assert.Equal("保留的文字", (await repository.GetMessageAsync("sample-text"))?.Content);
            Assert.Equal("RESEND_NEW_REQUEST", (await repository.GetMessageAsync("request-1"))?.ErrorCode);
            Assert.Single(await attachments.ListByMessageAsync("request-1"));
            connection.Current = new(ConnectionStatus.Connected, 0, null, false);
            var resend = await chat.RetryAsync("request-1");
            Assert.True(resend.Success);
            var newId = connection.SentRequests.Single();
            Assert.NotEqual("request-1", newId);
            Assert.Equal("hello", (await repository.GetMessageAsync(newId))?.Content);
            Assert.Single(await attachments.ListByMessageAsync(newId));
            Assert.Single(await attachments.ListByMessageAsync("request-1"));
            Assert.Equal(png, await File.ReadAllBytesAsync((await attachments.ListByMessageAsync(newId)).Single().LocalPath));

            await repository.UpdateMessageStatusAsync("request-1", ProtocolConstants.StatusError, "EDIT_INPUT");
            var blockedRetry = await chat.RetryAsync("request-1");
            Assert.False(blockedRetry.Success);
            Assert.Equal("EDIT_INPUT", blockedRetry.ErrorCode);
            Assert.Single(connection.SentRequests);
            var viewModel = provider.GetRequiredService<ChatViewModel>();
            var editable = new MessageItemViewModel(new("request-1", "user", "text", "保留待编辑原文", ProtocolConstants.StatusError,
                1, "EDIT_INPUT", null, null, false, false, []));
            await viewModel.RetryMessageCommand.ExecuteAsync(editable);
            Assert.Equal("保留待编辑原文", viewModel.InputText);
            Assert.Single(connection.SentRequests);

            const int totalHistory = 10050;
            var checkpointPort = Assert.IsAssignableFrom<IHistoryCheckpointPort>(repository);
            var cursorRest = new CursorRest();
            var pageRequests = new List<string>();
            var interrupt = true;
            cursorRest.Page = cursor =>
            {
                pageRequests.Add(cursor);
                if (cursor == "page-300" && interrupt) return PortCall<ChatHistoryDataDto>.Fail("TIMEOUT", "error.unknown");
                var start = cursor == "start" ? 0 : int.Parse(cursor[5..], System.Globalization.CultureInfo.InvariantCulture);
                var end = Math.Min(start + 300, totalHistory);
                return PortCall<ChatHistoryDataDto>.Ok(new() { HistorySyncVersion = 1, DatasetGeneration = "g1",
                    NextCursor = $"page-{end}", HasMore = end < totalHistory,
                    Items = Enumerable.Range(start, end - start).Select(index => new TimelineItemDto {
                        MessageId = $"cursor-{index:D5}", Role = "user", Content = $"content {index}", Timestamp = index / 2,
                        HistorySource = "sql" }).ToList() });
            };
            using var cursorSync = new Core.Services.Sync.SyncService(repository, cursorRest,
                provider.GetRequiredService<IUiDispatcher>(), NullLogger<Core.Services.Sync.SyncService>.Instance,
                attachments: attachments, files: provider.GetRequiredService<IFileStoragePort>(), paths: paths);
            await cursorSync.SyncIncrementalAsync();
            Assert.Equal("page-300", await checkpointPort.GetHistoryStateAsync(cursorRest.HistoryScope));
            Assert.NotNull(await repository.GetMessageAsync("cursor-00299"));
            Assert.Null(await repository.GetMessageAsync("cursor-00300"));
            interrupt = false;
            pageRequests.Clear();
            await cursorSync.SyncIncrementalAsync();
            Assert.Equal(Enumerable.Range(1, 33).Select(index => $"page-{index * 300}").ToArray(), pageRequests);
            Assert.Equal($"page-{totalHistory}", await checkpointPort.GetHistoryStateAsync(cursorRest.HistoryScope));
            var historyReader = provider.GetRequiredService<ILocalHistoryReader>();
            var historyRows = new List<ChatMessageView>();
            for (var offset = 0; ; offset += 500)
            {
                var page = await historyReader.ListRecentMessagesAsync(500, offset);
                historyRows.AddRange(page.Where(item => item.MessageId.StartsWith("cursor-", StringComparison.Ordinal)));
                if (page.Count < 500) break;
            }
            Assert.Equal(totalHistory, historyRows.Count);
            var uniqueHistory = historyRows.ToDictionary(item => item.MessageId, StringComparer.Ordinal);
            for (var index = 0; index < totalHistory; index++) Assert.Equal($"content {index}", uniqueHistory[$"cursor-{index:D5}"].Content);
            var notices = new List<string>();
            cursorSync.SyncFailed += (_, failure) => notices.Add(failure.I18nKey);
            cursorRest.Page = _ => PortCall<ChatHistoryDataDto>.Ok(new() { Items = [new() {
                MessageId = "legacy-only", Role = "user", Content = "limited", Timestamp = long.MaxValue }] });
            await cursorSync.SyncFullAsync();
            Assert.Contains("sync.limited", notices);
            Assert.Equal($"page-{totalHistory}", await checkpointPort.GetHistoryStateAsync(cursorRest.HistoryScope));
            var reset = true;
            cursorRest.Page = _ =>
            {
                if (reset) { reset = false; return PortCall<ChatHistoryDataDto>.Fail("40910", "error.unknown"); }
                return PortCall<ChatHistoryDataDto>.Ok(new() { HistorySyncVersion = 1, DatasetGeneration = "g2", NextCursor = "reset-done",
                    Items = [new() { MessageId = "cursor-00000", Role = "user", Content = "SQL authority", Timestamp = 10, HistorySource = "sql" }] });
            };
            await cursorSync.SyncIncrementalAsync();
            Assert.Equal("SQL authority", (await repository.GetMessageAsync("cursor-00000"))?.Content);
            cursorRest.Page = _ => PortCall<ChatHistoryDataDto>.Ok(new() { HistorySyncVersion = 1, DatasetGeneration = "g2", NextCursor = "legacy-done",
                Items = [new() { MessageId = "cursor-00000", Role = "user", Content = "old projection", Timestamp = 0, HistorySource = "legacy" }] });
            await cursorSync.SyncFullAsync();
            Assert.Equal("SQL authority", (await repository.GetMessageAsync("cursor-00000"))?.Content);
            cursorRest.ImageBytes = png;
            cursorRest.ImageFails = true;
            cursorRest.Page = _ => PortCall<ChatHistoryDataDto>.Ok(new() { HistorySyncVersion = 1, DatasetGeneration = "g2", NextCursor = "image-done",
                Items = [new() { MessageId = "history-original", Role = "user", Content = "[图片]", ContentType = "image",
                    RequestId = "history-source", ImageCount = 1, Timestamp = 20, HistorySource = "sql" }] });
            var beforeImage = await checkpointPort.GetHistoryStateAsync(cursorRest.HistoryScope);
            await cursorSync.SyncIncrementalAsync();
            Assert.Equal(beforeImage, await checkpointPort.GetHistoryStateAsync(cursorRest.HistoryScope));
            cursorRest.ImageFails = false;
            await cursorSync.SyncIncrementalAsync();
            var restoredOriginal = Assert.Single(await attachments.ListByMessageAsync("history-original"));
            Assert.Equal(png, await File.ReadAllBytesAsync(restoredOriginal.LocalPath));
            Assert.Equal("image-done", await checkpointPort.GetHistoryStateAsync(cursorRest.HistoryScope));
            await cursorSync.SyncIncrementalAsync();
            Assert.Single(await attachments.ListByMessageAsync("history-original"));
            cursorRest.Page = _ => { cursorRest.HistoryScope = "other-account"; return PortCall<ChatHistoryDataDto>.Ok(new() {
                HistorySyncVersion = 1, DatasetGeneration = "g3", NextCursor = "wrong", Items = [new() { MessageId = "wrong-account", Role = "user", Content = "wrong" }] }); };
            await cursorSync.SyncIncrementalAsync();
            Assert.Null(await repository.GetMessageAsync("wrong-account"));
            Assert.Null(await checkpointPort.GetHistoryStateAsync("other-account"));

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

    private class FallbackRest : IRestPort
    {
        public RestChatRequestDto? Request { get; private set; }
        public bool Fail { get; set; }
        public IReadOnlyList<TimelineItemDto> History { get; set; } = [];
        public List<ChatOperationDto> Operations { get; set; } = [];
        public Task<PortCall<ChatHistoryDataDto>> GetChatHistoryPageAsync(long since, int limit, CancellationToken cancellationToken = default)
            => Task.FromResult(PortCall<ChatHistoryDataDto>.Ok(new() { Items = History.ToList(), Operations = Operations }));
        public Task<PortCall<RestChatDataDto>> SendChatAsync(RestChatRequestDto request, CancellationToken cancellationToken = default)
        {
            Request = request;
            return Task.FromResult(Fail
                ? PortCall<RestChatDataDto>.Fail("TIMEOUT", "error.unknown")
                : PortCall<RestChatDataDto>.Ok(new() { MessageId = "reply-1", Reply = "first\nsecond" }));
        }

        public Task<PortCall<IReadOnlyList<TimelineItemDto>>> GetChatHistoryAsync(long since, int limit, CancellationToken cancellationToken = default)
            => Task.FromResult(PortCall<IReadOnlyList<TimelineItemDto>>.Ok(History));

        public Task<PortCall<IReadOnlyList<UserFactDto>>> GetMemoryFactsAsync(long since, int limit, CancellationToken cancellationToken = default)
            => Task.FromResult(PortCall<IReadOnlyList<UserFactDto>>.Ok([]));
    }

    private sealed class CursorRest : FallbackRest, IHistoryCursorPort, IHistoryImagePort
    {
        public string HistoryScope { get; set; } = "test-server:test-owner";
        public Func<string, PortCall<ChatHistoryDataDto>> Page { get; set; } = _ => throw new InvalidOperationException();
        public byte[] ImageBytes { get; set; } = [];
        public bool ImageFails { get; set; }
        public Task<PortCall<HistoryImageBytes>> GetHistoryImageAsync(string requestId, int index, CancellationToken cancellationToken = default)
            => Task.FromResult(ImageFails ? PortCall<HistoryImageBytes>.Fail("TIMEOUT", "error.unknown")
                : PortCall<HistoryImageBytes>.Ok(new("image/png", ImageBytes)));
        public Task<PortCall<ChatHistoryDataDto>> GetHistoryCursorPageAsync(string cursor, int limit, CancellationToken cancellationToken = default)
            => Task.FromResult(Page(cursor));
    }

    private sealed class DegradedConnection : IWssPort
    {
        public ConnectionSnapshot Current { get; set; } = new(ConnectionStatus.Degraded, 15, null, true);
        public bool IsConnected => Current.Status == ConnectionStatus.Connected;
        public List<string> SentRequests { get; } = [];
        public bool NeedsFullSync => true;
        public event EventHandler<ConnectionSnapshot>? ConnectionChanged { add { } remove { } }
        public event EventHandler<WsServerFrame>? FrameReceived;
        public void Raise(WsServerFrame frame) => FrameReceived?.Invoke(this, frame);
        public bool SendChatMessage(string requestId, string? content, IReadOnlyList<ChatImagePayloadDto>? images)
        {
            SentRequests.Add(requestId);
            return true;
        }
        public Task ReconnectAsync(bool manual, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void MarkFullSyncRequired() { }
    }
}
