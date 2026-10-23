using TKSDesktop.Contracts;
using TKSDesktop.Core.Data.Records;
using TKSDesktop.Core.Data.Repositories;
using TKSDesktop.Core.Services;
using TKSDesktop.Core.Services.Ports;

namespace TKSDesktop.Core.Data;

/// <summary>将 SQLite 仓储接入领域层窄端口的唯一适配器。</summary>
public sealed class ChatRepositoryPortAdapter : IChatRepositoryPort, IAttachmentPort, IMemoryEventRepositoryPort, IHistoryCheckpointPort
{
    private readonly MessageRepository _messages;
    private readonly CursorRepository _cursors;
    private readonly NotificationRepository _notifications;
    private readonly FactRepository _facts;

    public ChatRepositoryPortAdapter(
        MessageRepository messages,
        CursorRepository cursors,
        NotificationRepository notifications,
        FactRepository facts)
    {
        _messages = messages;
        _cursors = cursors;
        _notifications = notifications;
        _facts = facts;
    }

    public async Task<MessageWriteOutcome> UpsertMessageAsync(ChatMessageRecord message, CancellationToken cancellationToken = default)
    {
        var outcome = await _messages.UpsertAsync(ToData(message), cancellationToken).ConfigureAwait(false);
        return outcome switch
        {
            UpsertOutcome.Inserted => MessageWriteOutcome.Inserted,
            UpsertOutcome.Updated => MessageWriteOutcome.Updated,
            _ => MessageWriteOutcome.Unchanged,
        };
    }

    public async Task<ChatMessageRecord?> GetMessageAsync(string messageId, CancellationToken cancellationToken = default)
    {
        var row = await _messages.GetAsync(messageId, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        var attachments = await _messages.ListByMessageAsync(messageId, cancellationToken).ConfigureAwait(false);
        return ToCore(row) with { AttachmentPaths = attachments.Select(static item => item.LocalPath).ToArray() };
    }

    public async Task<bool> UpdateMessageStatusAsync(string messageId, string status, string? errorCode, CancellationToken cancellationToken = default)
    {
        if (await _messages.GetAsync(messageId, cancellationToken).ConfigureAwait(false) is null)
        {
            return false;
        }

        await _messages.UpdateStatusAsync(messageId, status, errorCode, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> DeleteMessageAsync(string messageId, CancellationToken cancellationToken = default)
        => await _messages.DeleteAsync(messageId, cancellationToken).ConfigureAwait(false) > 0;

    public async Task<IReadOnlyList<ChatMessageRecord>> ListMessagesAsync(int limit, CancellationToken cancellationToken = default)
        => (await _messages.ListRecentAsync(limit, cancellationToken: cancellationToken).ConfigureAwait(false))
            .Select(ToCore)
            .ToArray();

    public Task<long> MaxTimestampAsync(CancellationToken cancellationToken = default)
        => _messages.ReadMaxTimestampAsync(cancellationToken: cancellationToken);

    public async Task<StartupNormalizationResult> NormalizeStreamingOnStartupAsync(CancellationToken cancellationToken = default)
    {
        var result = await _messages.NormalizeOnStartupAsync(cancellationToken).ConfigureAwait(false);
        return new(result.DeletedEmptyStreaming, result.PromotedStreaming, result.DeletedBlankBot);
    }

    public async Task<IReadOnlyList<ChatMessageRecord>> ListSendingMessagesAsync(CancellationToken cancellationToken = default)
        => (await _messages.ListByStatusAsync(ProtocolConstants.StatusSending, cancellationToken).ConfigureAwait(false))
            .Select(ToCore)
            .ToArray();

    public async Task<IReadOnlyList<ChatMessageRecord>> SearchMessagesAsync(string term, int limit, CancellationToken cancellationToken = default)
        => (await _messages.SearchAsync(term, limit, cancellationToken).ConfigureAwait(false))
            .Select(static hit => ToCore(hit.Message))
            .ToArray();

    public async Task<int> ClearConversationAsync(bool advanceCursor, CancellationToken cancellationToken = default)
    {
        var previousCursor = advanceCursor
            ? 0
            : await _cursors.GetChatCursorAsync(cancellationToken).ConfigureAwait(false);
        var result = await _messages.ClearConversationAsync(cancellationToken).ConfigureAwait(false);
        if (!advanceCursor)
        {
            await _cursors.SetChatCursorAsync(previousCursor, cancellationToken).ConfigureAwait(false);
        }

        return result.DeletedMessages;
    }

    public Task<long> GetCursorAsync(string key, CancellationToken cancellationToken = default)
        => _cursors.GetAsync(key, cancellationToken);

    public Task SetCursorAsync(string key, long value, CancellationToken cancellationToken = default)
        => _cursors.SetAsync(key, value, cancellationToken);

    public Task<string?> GetHistoryStateAsync(string key, CancellationToken ct = default) => _cursors.GetHistoryStateAsync(key, ct);
    public Task SetHistoryStateAsync(string key, string value, CancellationToken ct = default) => _cursors.SetHistoryStateAsync(key, value, ct);

    public Task<bool> IsDeliveredAsync(string messageId, CancellationToken cancellationToken = default)
        => _cursors.IsDeliveredAsync(messageId, cancellationToken);

    public async Task MarkDeliveredAsync(IReadOnlyList<string> messageIds, CancellationToken cancellationToken = default)
        => _ = await _cursors.MarkDeliveredAsync(messageIds, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), cancellationToken).ConfigureAwait(false);

    public async Task InsertBotNotificationAsync(string errorCode, string message, long timestamp, CancellationToken cancellationToken = default)
        => _ = await _notifications.InsertAsync(errorCode, message, timestamp, cancellationToken: cancellationToken).ConfigureAwait(false);

    public async Task UpsertFactAsync(string factId, string userId, string fact, long timestamp, CancellationToken cancellationToken = default)
        => _ = await _facts.UpsertAsync(new FactRecord(factId, userId, fact, timestamp), cancellationToken).ConfigureAwait(false);

    public Task<long> MaxFactTimestampAsync(CancellationToken cancellationToken = default)
        => _facts.ReadMaxTimestampAsync(cancellationToken);

    public Task<int> ApplyFactPageAsync(TKSDesktop.Contracts.Dtos.MemoryFactsDataDto page, CancellationToken cancellationToken = default)
        => _facts.ApplyEventPageAsync(page, cancellationToken);

    public async Task<AttachmentDraft> InsertDraftAsync(string mimeType, string localPath, long fileSize, int? width, int? height, CancellationToken cancellationToken = default)
        => ToCore(await _messages.InsertDraftAttachmentAsync(
            ProtocolConstants.DefaultSessionId, mimeType, localPath, fileSize, width, height, cancellationToken).ConfigureAwait(false));

    public async Task<IReadOnlyList<AttachmentDraft>> ListDraftsAsync(CancellationToken cancellationToken = default)
        => (await _messages.ListDraftsAsync(cancellationToken).ConfigureAwait(false)).Select(ToCore).ToArray();

    public async Task<bool> DeleteAsync(string attachmentId, CancellationToken cancellationToken = default)
        => await _messages.DeleteAttachmentAsync(attachmentId, cancellationToken).ConfigureAwait(false) > 0;

    public async Task BindAsync(IReadOnlyList<string> attachmentIds, string messageId, CancellationToken cancellationToken = default)
        => _ = await _messages.AttachToMessageAsync(attachmentIds, messageId, cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<AttachmentDraft>> ListByMessageAsync(string messageId, CancellationToken cancellationToken = default)
        => (await _messages.ListByMessageAsync(messageId, cancellationToken).ConfigureAwait(false)).Select(ToCore).ToArray();

    public Task<(int DeletedCount, IReadOnlyList<string> Paths)> DeleteOlderThanAsync(int days, CancellationToken cancellationToken = default)
        => _messages.DeleteOlderThanAsync(days, cancellationToken);

    public async Task<long> TotalSizeAsync(CancellationToken cancellationToken = default)
        => (await _messages.StatsAsync(cancellationToken).ConfigureAwait(false)).Bytes;

    private static MessageUpsert ToData(ChatMessageRecord message) => new()
    {
        MessageId = message.MessageId,
        SessionId = ProtocolConstants.DefaultSessionId,
        Role = message.Role,
        MessageType = message.MessageType ?? "text",
        ContentType = message.ContentType,
        ModelProvider = message.ModelProvider,
        Content = message.Content,
        Status = message.Status,
        Timestamp = message.Timestamp,
        ErrorCode = message.ErrorCode,
        Authoritative = message.Authoritative,
    };

    private static ChatMessageRecord ToCore(MessageRecord row) => new(
        row.MessageId,
        row.Role,
        row.ContentType,
        row.Content,
        row.Status,
        row.Timestamp,
        row.ErrorCode,
        row.ModelProvider,
        row.MessageType);

    private static AttachmentDraft ToCore(AttachmentRecord row)
        => new(row.AttachmentId, row.MimeType, row.LocalPath, row.FileSize, row.Width, row.Height);
}
