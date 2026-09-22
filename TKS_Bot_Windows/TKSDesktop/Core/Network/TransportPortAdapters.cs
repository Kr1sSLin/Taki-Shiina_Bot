using TKSDesktop.Contracts;
using TKSDesktop.Contracts.Dtos;
using TKSDesktop.Core.Network.Ws;
using TKSDesktop.Core.Services;
using TKSDesktop.Core.Services.Ports;

namespace TKSDesktop.Core.Network;

/// <summary>将 REST 客户端接入同步服务窄端口。</summary>
public sealed class RestPortAdapter : IRestPort
{
    private readonly RestClient _rest;

    public RestPortAdapter(RestClient rest) => _rest = rest;

    public async Task<PortCall<RestChatDataDto>> SendChatAsync(RestChatRequestDto request, CancellationToken cancellationToken = default)
        => ToPortCall(await _rest.PostAsync<RestChatDataDto>("/chat", request,
            ProtocolConstants.RestTimeoutMs, cancellationToken: cancellationToken).ConfigureAwait(false), static data => data);

    public async Task<PortCall<IReadOnlyList<TimelineItemDto>>> GetChatHistoryAsync(
        long since,
        int limit,
        CancellationToken cancellationToken = default)
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

        return ToPortCall(result, static data => (IReadOnlyList<TimelineItemDto>)data.Items);
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
