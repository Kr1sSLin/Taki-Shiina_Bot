using System.Buffers;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using TKSDesktop.Contracts;
using TKSDesktop.Contracts.Dtos;

namespace TKSDesktop.Core.Network.Ws;

/// <summary>
/// WS 关闭信息（含**原始**关闭码）。
/// ⚠️ `WebSocketCloseStatus` 枚举**无法表达 4001**（陷阱 6）：必须用 <see cref="Code"/> 承载原始数值。
/// </summary>
public sealed record WsCloseInfo(
    int Code,
    string Reason,
    bool WasClean,
    bool AuthFailure);

/// <summary>一条已识别的服务端帧（未知 type / 非法 JSON 不会产生实例）。</summary>
public sealed record WsInboundFrame(
    WsFrameParseKind Kind,
    WsServerFrame? Frame,
    string? RawType,
    string? Error);

/// <summary>
/// `ClientWebSocket` 长连接（FR-W-CONN-1 / FR-W-CONN-2 / FR-W-CONN-4 / 陷阱 1、2、6）。
///
/// 硬约束：
/// <list type="bullet">
///   <item>鉴权**只用**请求头 `Authorization: Bearer {token}` ——
///         **不得**用 query `?token=`（后端已拒绝并告警）；</item>
///   <item>**4001 特例**：读 `ClientWebSocket.CloseStatus` 的**原始数值**（`(int?)socket.CloseStatus`）
///         并用 <see cref="WsFrameParser.IsAuthFailureClose"/> 判定，同时参考 `CloseStatusDescription`；
///         4001 **不得**按可重试网络错误处理；</item>
///   <item>出站用**专用 <see cref="Channel{T}"/>** 串行化 —— 避免多线程并发写同一
///         `ClientWebSocket`（并发 Send 会抛异常 / 撕裂帧）；</item>
///   <item>帧解析一律走 <see cref="WsFrameParser.Parse"/>：未知 type / 非法 JSON
///         **只记 debug 日志后丢弃**，**不中断连接**（NFR-W-12 / FR-W-NET-2）；</item>
///   <item>连接断开时把**在途请求**标记为 <see cref="ProtocolConstants.ClientErrorConnectionLost"/>
///         （不是静默丢弃）并对外发事件。</item>
/// </list>
/// </summary>
public sealed class WsClient : IDisposable
{
    /// <summary>出站队列容量（有界，防止断线期间无限堆积）。</summary>
    public const int SendQueueCapacity = 256;

    /// <summary>接收缓冲区字节数。</summary>
    private const int ReceiveBufferBytes = 16 * 1024;

    private readonly ILogger<WsClient> _logger;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly Channel<string> _sendQueue = Channel.CreateBounded<string>(
        new BoundedChannelOptions(SendQueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
        });

    private readonly HashSet<string> _pendingRequests = new(StringComparer.Ordinal);
    private readonly object _pendingGate = new();

    private ClientWebSocket? _socket;
    private CancellationTokenSource? _pumpCts;
    private Task? _sendPump;
    private Task? _receivePump;
    private bool _disposed;

    /// <summary>连接建立（握手成功）时触发。</summary>
    public event EventHandler? Opened;

    /// <summary>连接关闭时触发（含 4001 鉴权失败）。</summary>
    public event EventHandler<WsCloseInfo>? Closed;

    /// <summary>收到一条已识别的服务端帧。</summary>
    public event EventHandler<WsInboundFrame>? FrameReceived;

    /// <summary>发送失败时触发（订阅方负责**立即**重连，FR-W-CONN-2）。</summary>
    public event EventHandler<Exception>? SendFailed;

    /// <summary>连接断开时把在途请求标记为 `CONNECTION_LOST`（不是静默丢弃）。</summary>
    public event EventHandler<IReadOnlyList<string>>? PendingRequestsLost;

    /// <param name="logger">日志（**禁止**记录 Token / base64）。</param>
    public WsClient(ILogger<WsClient> logger)
    {
        _logger = logger;
    }

    /// <summary>是否处于打开状态。</summary>
    public bool IsOpen => _socket?.State == WebSocketState.Open;

    /// <summary>当前 socket 状态（排障用）。</summary>
    public WebSocketState State => _socket?.State ?? WebSocketState.None;

    /// <summary>最近一次连接的关闭码（**原始数值**，可为 4001）。</summary>
    public int? LastCloseCode { get; private set; }

    /// <summary>最近一次连接的关闭原因文本。</summary>
    public string? LastCloseReason { get; private set; }

    /* ---------------------------------------------------------------------- */
    /* 连接                                                                    */
    /* ---------------------------------------------------------------------- */

    /// <summary>
    /// 连接 <paramref name="wsBaseUrl"/> + <see cref="ProtocolConstants.WsChatPath"/>。
    /// </summary>
    /// <param name="wsBaseUrl">形如 `wss://takishiinabot.top`（不含 path）。</param>
    /// <param name="accessToken">Access Token（写入 `Authorization` 请求头）。</param>
    /// <param name="cancellationToken">取消。</param>
    /// <returns>握手成功返回 `true`；失败返回 `false`（**不抛错**，由上层走退避重连）。</returns>
    public async Task<bool> ConnectAsync(string wsBaseUrl, string accessToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(wsBaseUrl) || string.IsNullOrWhiteSpace(accessToken))
        {
            _logger.LogWarning("WS 连接参数不完整（缺 base url 或 token），放弃建连");
            return false;
        }

        await CloseAsync(reason: "reconnect", cancellationToken).ConfigureAwait(false);

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var uri = BuildChatUri(wsBaseUrl);
            var socket = new ClientWebSocket();

            // ⚠️ 鉴权必须走请求头：URL query ?token= 已被后端明确拒绝（FR-W-CONN-1）。
            socket.Options.SetRequestHeader("Authorization", "Bearer " + accessToken);

            var pumpCts = new CancellationTokenSource();
            _socket = socket;
            _pumpCts = pumpCts;
            LastCloseCode = null;
            LastCloseReason = null;

            _logger.LogInformation("WS 连接中 uri={Uri}", uri.GetLeftPart(UriPartial.Path));

            try
            {
                using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, pumpCts.Token);
                handshakeCts.CancelAfter(ProtocolConstants.ConnectivityTimeoutMs + 5_000);
                await socket.ConnectAsync(uri, handshakeCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or InvalidOperationException)
            {
                var status = TryGetHandshakeHttpStatus(ex);
                _logger.LogWarning("WS 握手失败 status={Status} error={Error}", status, ex.Message);

                LastCloseCode = status;
                LastCloseReason = ex.Message;

                SafeDispose(socket);
                if (ReferenceEquals(_socket, socket))
                {
                    _socket = null;
                }

                pumpCts.Dispose();
                if (ReferenceEquals(_pumpCts, pumpCts))
                {
                    _pumpCts = null;
                }

                // 握手阶段的 401/403 同样是鉴权失败，交由上层走刷新流程（FR-W-CONN-4）。
                var authFailure = status is 401 or 403;
                Closed?.Invoke(this, new WsCloseInfo(status ?? 0, ex.Message, WasClean: false, AuthFailure: authFailure));
                return false;
            }

            _sendPump = Task.Run(() => SendPumpAsync(socket, _sendQueue.Reader, pumpCts.Token), CancellationToken.None);
            _receivePump = Task.Run(() => ReceivePumpAsync(socket, pumpCts.Token), CancellationToken.None);

            _logger.LogInformation("WS 已连接");
            Opened?.Invoke(this, EventArgs.Empty);
            return true;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <summary>拼出 `/ws/chat` 的连接地址（基址末尾多余 `/` 会被去掉）。</summary>
    public static Uri BuildChatUri(string wsBaseUrl)
    {
        var trimmed = wsBaseUrl.Trim().TrimEnd('/');
        return new Uri(trimmed + ProtocolConstants.WsChatPath, UriKind.Absolute);
    }

    /// <summary>
    /// 主动关闭连接。幂等；**不抛错**。
    /// </summary>
    public async Task CloseAsync(string reason = "client closing", CancellationToken cancellationToken = default)
    {
        ClientWebSocket? socket;
        CancellationTokenSource? pumpCts;
        Task? sendPump;
        Task? receivePump;

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            socket = _socket;
            pumpCts = _pumpCts;
            sendPump = _sendPump;
            receivePump = _receivePump;

            _socket = null;
            _pumpCts = null;
            _sendPump = null;
            _receivePump = null;
        }
        finally
        {
            _lifecycleGate.Release();
        }

        if (socket is null && pumpCts is null)
        {
            return;
        }

        try
        {
            pumpCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已被其他地方释放，忽略。
        }

        if (socket is not null && socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                using var closeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                closeCts.CancelAfter(3_000);
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, reason, closeCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
            {
                _logger.LogDebug("WS 优雅关闭失败，转为强制释放 error={Error}", ex.Message);
            }
        }

        SafeDispose(socket);

        // 等待泵退出，避免后台任务触碰已释放的 socket。
        try
        {
            if (sendPump is not null)
            {
                await sendPump.ConfigureAwait(false);
            }

            if (receivePump is not null)
            {
                await receivePump.ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or WebSocketException)
        {
            // 泵已因取消退出。
        }

        pumpCts?.Dispose();
    }

    /* ---------------------------------------------------------------------- */
    /* 出站（专用 Channel 串行化）                                              */
    /* ---------------------------------------------------------------------- */

    /// <summary>
    /// 入队一条出站帧。**永不并发写 socket**：所有发送都由专用发送泵串行执行。
    /// </summary>
    /// <returns>入队成功返回 `true`；连接未打开或队列已关闭返回 `false`。</returns>
    public bool TryEnqueue(string json)
    {
        if (!IsOpen)
        {
            _logger.LogDebug("出站失败：连接未就绪");
            return false;
        }

        return _sendQueue.Writer.TryWrite(json);
    }

    /// <summary>
    /// 串行发送一条 `chat.message` 帧。
    /// ⚠️ 陷阱 1：`requestId` 必须在帧**顶层**（与 `type` 同级）。
    /// </summary>
    public bool SendChatMessage(string requestId, string? content, IReadOnlyList<ChatImagePayloadDto>? images = null)
    {
        if (string.IsNullOrEmpty(requestId))
        {
            _logger.LogWarning("chat.message 缺少 requestId，拒绝发送（幂等键为空会让重试失去意义）");
            return false;
        }

        var frame = new WsChatMessageFrameDto
        {
            RequestId = requestId,
            Payload = new ChatMessagePayloadDto
            {
                Content = content,
                Images = images is null ? null : [.. images],
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            },
        };

        lock (_pendingGate)
        {
            _pendingRequests.Add(requestId);
        }

        return TryEnqueue(JsonSerializer.Serialize(frame, WsFrameParser.Options));
    }

    /// <summary>
    /// 发送心跳 `{"type":"ping","payload":{"timestamp":&lt;ms&gt;}}`（FR-W-CONN-2）。
    /// 失败时立即触发 <see cref="SendFailed"/>（订阅方负责重连）。
    /// </summary>
    public bool SendPing()
    {
        var frame = new WsPingFrameDto
        {
            Payload = new WsPingPayloadDto { Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() },
        };

        var ok = TryEnqueue(JsonSerializer.Serialize(frame, WsFrameParser.Options));
        if (!ok)
        {
            SendFailed?.Invoke(this, new InvalidOperationException("ping enqueue failed"));
        }

        return ok;
    }

    private async Task SendPumpAsync(ClientWebSocket socket, ChannelReader<string> reader, CancellationToken cancellationToken)
    {
        try
        {
            while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (reader.TryRead(out var json))
                {
                    if (socket.State != WebSocketState.Open)
                    {
                        SendFailed?.Invoke(this, new InvalidOperationException("socket is not open"));
                        return;
                    }

                    try
                    {
                        var bytes = Encoding.UTF8.GetBytes(json);
                        await socket.SendAsync(
                            new ArraySegment<byte>(bytes),
                            WebSocketMessageType.Text,
                            endOfMessage: true,
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
                    {
                        _logger.LogWarning("WS 发送失败，触发重连 error={Error}", ex.Message);
                        SendFailed?.Invoke(this, ex);
                        return;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常关闭。
        }
        catch (Exception ex)
        {
            _logger.LogDebug("发送泵退出 error={Error}", ex.Message);
        }
    }

    /* ---------------------------------------------------------------------- */
    /* 入站                                                                    */
    /* ---------------------------------------------------------------------- */

    private async Task ReceivePumpAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(ReceiveBufferBytes);

        try
        {
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;

                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken)
                        .ConfigureAwait(false);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        // ⚠️ 陷阱 6：枚举无法表达 4001 → 必须读原始数值并额外处理 CloseStatusDescription。
                        var rawCode = (int?)socket.CloseStatus;
                        var reason = socket.CloseStatusDescription ?? result.CloseStatusDescription ?? string.Empty;
                        LastCloseCode = rawCode;
                        LastCloseReason = reason;

                        var isAuthFailure = WsFrameParser.IsAuthFailureClose(rawCode)
                                            || LooksLikeAuthFailureReason(reason);

                        _logger.LogInformation(
                            "WS 已断开 closeCode={Code} authFailure={AuthFailure} reason={Reason}",
                            rawCode, isAuthFailure, reason);

                        MarkPendingRequestsLost();
                        Closed?.Invoke(this, new WsCloseInfo(rawCode ?? 0, reason, WasClean: true, AuthFailure: isAuthFailure));
                        return;
                    }

                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                if (message.Length == 0)
                {
                    continue;
                }

                var raw = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                HandleRawFrame(raw);
            }
        }
        catch (OperationCanceledException)
        {
            // 主动关闭。
        }
        catch (WebSocketException ex)
        {
            LastCloseCode = TryGetHandshakeHttpStatus(ex);
            LastCloseReason = ex.Message;
            _logger.LogWarning("WS 接收异常（连接中断）error={Error}", ex.Message);
            MarkPendingRequestsLost();
            Closed?.Invoke(this, new WsCloseInfo(LastCloseCode ?? 0, ex.Message, WasClean: false, AuthFailure: false));
        }
        catch (Exception ex)
        {
            _logger.LogDebug("接收泵退出 error={Error}", ex.Message);
            MarkPendingRequestsLost();
            Closed?.Invoke(this, new WsCloseInfo(0, ex.Message, WasClean: false, AuthFailure: false));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// 帧解析入口：**一律走 <see cref="WsFrameParser.Parse"/>**；
    /// 未知 type / 非法 JSON **只记 debug 日志后丢弃**，**不得中断连接**（NFR-W-12 / FR-W-NET-2）。
    /// </summary>
    private void HandleRawFrame(string raw)
    {
        var parsed = WsFrameParser.Parse(raw);

        switch (parsed.Kind)
        {
            case WsFrameParseKind.Known when parsed.Frame is not null:
                ObserveFrame(parsed.Frame);
                FrameReceived?.Invoke(this, new WsInboundFrame(parsed.Kind, parsed.Frame, parsed.RawType, null));
                break;

            case WsFrameParseKind.UnknownType:
                _logger.LogDebug(
                    "忽略未知帧（NFR-W-12 向后兼容）type={Type} error={Error}",
                    parsed.RawType, parsed.Error);
                break;

            case WsFrameParseKind.InvalidJson:
                _logger.LogDebug("忽略非法 JSON 帧（NFR-W-12）error={Error}", parsed.Error);
                break;

            default:
                break;
        }
    }

    /// <summary>帧级别的在途请求收敛（送达 / 失败 / 作废）。</summary>
    private void ObserveFrame(WsServerFrame frame)
    {
        switch (frame)
        {
            case WsEchoFrame echo:
                RemovePending(echo.RequestId);
                break;

            case WsReplyStreamFrame reply when reply.Payload.Done:
                RemovePending(reply.RequestId);
                foreach (var id in reply.Payload.RequestIds ?? [])
                {
                    RemovePending(id);
                }

                break;

            case WsBotErrorFrame error:
                RemovePending(error.RequestId);
                foreach (var id in error.Payload.RequestIds ?? [])
                {
                    RemovePending(id);
                }

                break;

            default:
                break;
        }
    }

    private void RemovePending(string? requestId)
    {
        if (string.IsNullOrEmpty(requestId))
        {
            return;
        }

        lock (_pendingGate)
        {
            _pendingRequests.Remove(requestId);
        }
    }

    /// <summary>断开时把在途请求标记为 `CONNECTION_LOST`（不是静默丢弃）。</summary>
    private void MarkPendingRequestsLost()
    {
        string[] lost;
        lock (_pendingGate)
        {
            if (_pendingRequests.Count == 0)
            {
                return;
            }

            lost = [.. _pendingRequests];
            _pendingRequests.Clear();
        }

        _logger.LogWarning(
            "WS 断开，{Count} 个在途请求标记为 {Code}",
            lost.Length, ProtocolConstants.ClientErrorConnectionLost);

        PendingRequestsLost?.Invoke(this, lost);
    }

    /// <summary>后端在 `accept` 前 `close` 时可能只给出 reason 文本（无 4001）。</summary>
    private static bool LooksLikeAuthFailureReason(string reason)
        => !string.IsNullOrEmpty(reason)
           && reason.Contains("invalid token", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 从握手异常里尽力取出 HTTP 状态码。
    /// ⚠️ `WebSocketException` 没有状态码字段，.NET 只把 `"The server returned status code '401' ..."`
    /// 放进 message，因此这里做一次保守的文本解析；解析不到返回 `null`
    /// （按可重试网络错误处理，**不清凭据** —— 保留比误清安全）。
    /// </summary>
    private static int? TryGetHandshakeHttpStatus(Exception ex)
    {
        if (ex is not WebSocketException)
        {
            return null;
        }

        var match = Regex.Match(
            ex.Message ?? string.Empty,
            @"status code '?(?<code>\d{3})'?",
            RegexOptions.CultureInvariant);

        return match.Success
               && int.TryParse(
                   match.Groups["code"].Value,
                   NumberStyles.Integer,
                   CultureInfo.InvariantCulture,
                   out var status)
            ? status
            : null;
    }

    /// <summary>强制释放 socket（释放路径不抛错）。</summary>
    private static void SafeDispose(ClientWebSocket? socket)
    {
        if (socket is null)
        {
            return;
        }

        try
        {
            socket.Dispose();
        }
        catch (Exception)
        {
            // 释放路径不抛错。
        }
    }

    /// <summary>
    /// 同步释放：取消泵、释放 socket。**不阻塞等待**异步泵（避免 Dispose 里死等）。
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        ClientWebSocket? socket;
        CancellationTokenSource? pumpCts;

        _lifecycleGate.Wait();
        try
        {
            socket = _socket;
            pumpCts = _pumpCts;

            _socket = null;
            _pumpCts = null;
            _sendPump = null;
            _receivePump = null;
        }
        finally
        {
            _lifecycleGate.Release();
        }

        try
        {
            pumpCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已释放。
        }

        SafeDispose(socket);
        pumpCts?.Dispose();
        _sendQueue.Writer.TryComplete();
        _lifecycleGate.Dispose();
    }
}
