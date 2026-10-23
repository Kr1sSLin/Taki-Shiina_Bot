using TKSDesktop.Contracts;
using TKSDesktop.Contracts.Dtos;
using TKSDesktop.Core.Network.Ws;
using TKSDesktop.Core.Services;
using TKSDesktop.Core.Services.Ports;

namespace TKSDesktop.Core.Network;

/// <summary>将 REST 客户端接入同步服务窄端口。</summary>
public sealed class RestPortAdapter : IRestPort, IHistoryImagePort, IMemoryEventPort, IHistoryCursorPort
{
    private readonly RestClient _rest;

    private readonly Core.Auth.TokenManager? _tokens;
    public RestPortAdapter(RestClient rest, Core.Auth.TokenManager? tokens = null) { _rest = rest; _tokens = tokens; }

    public string? HistoryScope => _tokens?.UserId is { } owner
        ? System.Text.Json.JsonSerializer.Serialize(new[] { _rest.ApiBaseUrl.TrimEnd('/'), owner }) : null;

    public async Task<PortCall<ChatHistoryDataDto>> GetHistoryCursorPageAsync(string cursor, int limit, CancellationToken cancellationToken = default)
        => ToPortCall(await _rest.GetAsync<ChatHistoryDataDto>("/chat/history",
            new Dictionary<string, string?> { ["cursor"] = cursor, ["limit"] = limit.ToString(CultureInfo.InvariantCulture) },
            ProtocolConstants.RestTimeoutMs, cancellationToken: cancellationToken).ConfigureAwait(false), static data => data);

    public async Task<PortCall<HistoryImageBytes>> GetHistoryImageAsync(
        string requestId, int index, CancellationToken cancellationToken = default)
        => ToPortCall(await _rest.GetHistoryImageAsync(requestId, index, cancellationToken).ConfigureAwait(false), static data => data);

    public async Task<PortCall<RestChatDataDto>> SendChatAsync(RestChatRequestDto request, CancellationToken cancellationToken = default)
    {
        var result = await _rest.PostAsync<RestChatDataDto>("/chat", request,
            ProtocolConstants.RestTimeoutMs, cancellationToken: cancellationToken).ConfigureAwait(false);
        var call = ToPortCall(result, static data => data);
        return call.Success ? call : call with { Value = result.FailureValue };
    }

    public async Task<PortCall<IReadOnlyList<TimelineItemDto>>> GetChatHistoryAsync(
        long since,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var call = await GetChatHistoryPageAsync(since, limit, cancellationToken).ConfigureAwait(false);
        return call.Success && call.Value is not null
            ? PortCall<IReadOnlyList<TimelineItemDto>>.Ok(call.Value.Items)
            : PortCall<IReadOnlyList<TimelineItemDto>>.Fail(call.ErrorCode, call.I18nKey);
    }

    public async Task<PortCall<ChatHistoryDataDto>> GetChatHistoryPageAsync(long since, int limit, CancellationToken cancellationToken = default)
    {
        var result = await _rest.GetAsync<ChatHistoryDataDto>(
            "/chat/history",
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["since"] = since.ToString(CultureInfo.InvariantCulture),
                ["limit"] = limit.ToString(CultureInfo.InvariantCulture),
            },
            ProtocolConstants.RestTimeoutMs,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return ToPortCall(result, static data => data);
    }

    public async Task<PortCall<IReadOnlyList<UserFactDto>>> GetMemoryFactsAsync(
        long since,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var result = await _rest.GetAsync<MemoryFactsDataDto>(
            "/memory/facts",
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["since"] = since.ToString(CultureInfo.InvariantCulture),
                ["limit"] = limit.ToString(CultureInfo.InvariantCulture),
            },
            ProtocolConstants.RestTimeoutMs,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return ToPortCall(result, static data => (IReadOnlyList<UserFactDto>)data.Items);
    }

    public async Task<PortCall<MemoryFactsDataDto>> GetMemoryEventPageAsync(
        long cursor, int limit, CancellationToken cancellationToken = default)
    {
        var result = await _rest.GetAsync<MemoryFactsDataDto>(
            "/memory/facts",
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["cursor"] = cursor.ToString(CultureInfo.InvariantCulture),
                ["limit"] = limit.ToString(CultureInfo.InvariantCulture),
            },
            ProtocolConstants.RestTimeoutMs,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return ToPortCall(result, static data => data);
    }

    private static PortCall<TOut> ToPortCall<TIn, TOut>(ApiResult<TIn> result, Func<TIn, TOut> map)
    {
        if (result.IsSuccess && result.Value is { } value)
        {
            return PortCall<TOut>.Ok(map(value));
        }

        return PortCall<TOut>.Fail(
            result.Error?.Code?.ToString(CultureInfo.InvariantCulture) ?? result.Error?.ClientCode,
            result.ErrorKey);
    }
}

/// <summary>将 WS 状态机接入聊天服务窄端口，事件只转发已解析帧。</summary>
public sealed class WssPortAdapter : IWssPort, IDisposable
{
    private readonly WsConnectionManager _manager;

    public WssPortAdapter(WsConnectionManager manager)
    {
        _manager = manager;
        _manager.StateChanged += OnStateChanged;
        _manager.FrameReceived += OnFrameReceived;
    }

    public ConnectionSnapshot Current => Map(_manager.State);

    public bool IsConnected => _manager.IsConnected;

    public bool NeedsFullSync => _manager.NeedsFullSync;

    public event EventHandler<ConnectionSnapshot>? ConnectionChanged;

    public event EventHandler<WsServerFrame>? FrameReceived;

    public bool SendChatMessage(string requestId, string? content, IReadOnlyList<ChatImagePayloadDto>? images)
        => _manager.SendChatMessageAsync(requestId, content, images);

    public async Task ReconnectAsync(bool manual, CancellationToken cancellationToken = default)
        => _ = await _manager.RequestReconnectAsync(manual, cancellationToken).ConfigureAwait(false);

    public void MarkFullSyncRequired() => _manager.MarkFullSyncRequired();

    private void OnStateChanged(object? sender, ConnectionStateSnapshot snapshot)
        => ConnectionChanged?.Invoke(this, Map(snapshot));

    private void OnFrameReceived(object? sender, WsInboundFrame inbound)
    {
        if (inbound.Frame is { } frame)
        {
            FrameReceived?.Invoke(this, frame);
        }
    }

    private static ConnectionSnapshot Map(ConnectionStateSnapshot snapshot) => new(
        snapshot.State switch
        {
            ConnectionState.Unauthenticated => ConnectionStatus.Unauthenticated,
            ConnectionState.Connecting => ConnectionStatus.Connecting,
            ConnectionState.Connected => ConnectionStatus.Connected,
            ConnectionState.Reconnecting => ConnectionStatus.Reconnecting,
            ConnectionState.Refreshing => ConnectionStatus.Refreshing,
            ConnectionState.Degraded => ConnectionStatus.Degraded,
            _ => ConnectionStatus.Disconnected,
        },
        snapshot.Attempt,
        snapshot.LastError,
        snapshot.Degraded);

    public void Dispose()
    {
        _manager.StateChanged -= OnStateChanged;
        _manager.FrameReceived -= OnFrameReceived;
    }
}
