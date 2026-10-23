using Xunit;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using TKSDesktop.Core.Network.Ws;
using System.Text.Json;
using TKSDesktop.Contracts;

namespace TKSDesktop.Tests;

/// <summary>
/// 帧契约测试（PRD §5.6 陷阱 1、2；NFR-W-12 / FR-W-NET-2）。
/// 覆盖：`chat.message` 顶层 `requestId`、`pong` 无 payload 包装、未知 type 不抛错、
/// 4001 关闭码判定。
/// </summary>
public sealed class NetworkFrameContractTests
{
    [Fact]
    public void SharedSamples_PreserveLegacyAndTerminalRecovery()
    {
        var path = Path.Combine(Gate.GateRepo.Root, "..", "design", "foundation-protocol-samples.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var recovery = document.RootElement.GetProperty("recovery");
        var old = Assert.IsType<WsBotErrorFrame>(WsFrameParser.Parse(recovery.GetProperty("legacyError").GetRawText()).Frame);
        Assert.Null(old.Payload.RecoveryAction);
        var terminal = Assert.IsType<WsBotErrorFrame>(WsFrameParser.Parse(recovery.GetProperty("terminalError").GetRawText()).Frame);
        Assert.False(terminal.Payload.Retryable);
        Assert.Equal("resend_new_request", terminal.Payload.RecoveryAction);
        Assert.Equal(new[] { "sample-text", "sample-gift" }, terminal.Payload.RequestIds);
        var history = JsonSerializer.Deserialize<Contracts.Dtos.ChatHistoryDataDto>(recovery.GetProperty("historyData").GetRawText(), WsFrameParser.Options)!;
        Assert.Equal("refunded", history.Operations.Single().RefundStatus);
        Assert.Equal("保留的文字", history.Operations.Single().Sources[0].Content);
    }

    [Fact]
    public async Task Heartbeat_RequiresCurrentEchoAndIgnoresOldSocketCallbacks()
    {
        var portProbe = new TcpListener(IPAddress.Loopback, 0);
        portProbe.Start();
        var port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
        portProbe.Stop();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        using var client = new WsClient(NullLogger<WsClient>.Instance);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var connect = client.ConnectAsync($"ws://127.0.0.1:{port}", "synthetic-token", deadline.Token);
        var context = await listener.GetContextAsync().WaitAsync(deadline.Token);
        using var peer = (await context.AcceptWebSocketAsync(null)).WebSocket;
        Assert.True(await connect);
        var socketField = typeof(WsClient).GetField("_socket", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var oldSocket = (ClientWebSocket)socketField.GetValue(client)!;
        var handle = typeof(WsClient).GetMethod("HandleRawFrame", BindingFlags.Instance | BindingFlags.NonPublic)!;
        long ping = 0;
        for (var index = 0; index < 3; index++)
        {
            Assert.True(client.SendPing());
            var buffer = new byte[1024];
            var received = await peer.ReceiveAsync(new ArraySegment<byte>(buffer), deadline.Token);
            using var frame = JsonDocument.Parse(Encoding.UTF8.GetString(buffer, 0, received.Count));
            ping = frame.RootElement.GetProperty("payload").GetProperty("timestamp").GetInt64();
        }
        Assert.True(client.HeartbeatTimedOut);
        handle.Invoke(client, ["{\"type\":\"pong\",\"pingTimestamp\":-1}", oldSocket]);
        Assert.True(client.HeartbeatTimedOut);
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.FrameReceived += (_, _) => observed.TrySetResult();
        var pong = Encoding.UTF8.GetBytes($"{{\"type\":\"pong\",\"timestamp\":123,\"pingTimestamp\":{ping}}}");
        await peer.SendAsync(new ArraySegment<byte>(pong), WebSocketMessageType.Text, true, deadline.Token);
        await observed.Task.WaitAsync(deadline.Token);
        Assert.False(client.HeartbeatTimedOut);
        // 换连接后旧接收回调不能提供新的存活证据。
        peer.Abort();
        await client.CloseAsync(cancellationToken: deadline.Token);
        using var replacement = new ClientWebSocket();
        socketField.SetValue(client, replacement);
        var pending = (HashSet<long>)typeof(WsClient).GetField("_pendingPings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client)!;
        pending.UnionWith([11, 12, 13]);
        handle.Invoke(client, ["{\"type\":\"pong\"}", oldSocket]);
        Assert.True(client.HeartbeatTimedOut);
        handle.Invoke(client, ["{\"type\":\"pong\"}", replacement]);
        Assert.False(client.HeartbeatTimedOut);
        socketField.SetValue(client, null);
    }

    [Fact]
    public async Task Heartbeat_MissingPongReconnectsAndRequestsHistoryRecovery()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        using var client = new WsClient(NullLogger<WsClient>.Instance);
        using var manager = new WsConnectionManager(client, new FakeTokenProvider(), () => $"ws://127.0.0.1:{port}", NullLogger<WsConnectionManager>.Instance);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var reopened = new TaskCompletionSource<ConnectionOpenedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        var opens = 0;
        manager.Opened += (_, info) => { if (Interlocked.Increment(ref opens) == 2) reopened.TrySetResult(info); };
        var connect = manager.ConnectAsync(cancellationToken: deadline.Token);
        var first = await listener.GetContextAsync().WaitAsync(deadline.Token);
        using var peer = (await first.AcceptWebSocketAsync(null)).WebSocket;
        Assert.True(await connect);
        for (var index = 0; index < 3; index++) Assert.True(client.SendPing());
        var closeHandshake = Task.Run(async () =>
        {
            var buffer = new byte[1024];
            while ((await peer.ReceiveAsync(new ArraySegment<byte>(buffer), deadline.Token)).MessageType != WebSocketMessageType.Close) { }
            await peer.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "heartbeat timeout", deadline.Token);
        });
        var second = await listener.GetContextAsync().WaitAsync(deadline.Token);
        using var replacement = (await second.AcceptWebSocketAsync(null)).WebSocket;
        var recovery = await reopened.Task.WaitAsync(deadline.Token);
        Assert.True(recovery.FullSync);
        Assert.True(manager.IsConnected);
        Assert.False(client.HeartbeatTimedOut);
        // 超时连接沿用既有强制关闭路径，服务端可能先看到断开。
        try { await closeHandshake; }
        catch (WebSocketException) { }
        replacement.Abort();
        await manager.DisconnectAsync(cancellationToken: deadline.Token);
    }

    /* ---------------- 陷阱 1：requestId 在帧顶层 ----------------------------- */

    [Fact]
    public void WsChatMessageFrameDto_SerializesRequestIdAtTopLevel()
    {
        var frame = new WsChatMessageFrameDto
        {
            RequestId = "req-1",
            Payload = new ChatMessagePayloadDto { Content = "hello" },
        };

        var json = JsonSerializer.Serialize(frame, WsFrameParser.Options);
        using var document = JsonDocument.Parse(json);

        Assert.True(document.RootElement.TryGetProperty("requestId", out var topLevel));
        Assert.Equal("req-1", topLevel.GetString());

        // ⚠️ requestId 绝不能在 payload 内（后端用 message.get("requestId") 读顶层）。
        var payload = document.RootElement.GetProperty("payload");
        Assert.False(payload.TryGetProperty("requestId", out _));
    }

    [Fact]
    public void Parse_ReplyStreamWithTopLevelRequestId_ExposesRequestId()
    {
        const string raw = """
            {"type":"chat.reply.stream","requestId":"req-42","payload":{"delta":"hi","done":false}}
            """;

        var result = WsFrameParser.Parse(raw);

        Assert.Equal(WsFrameParseKind.Known, result.Kind);
        var frame = Assert.IsType<WsReplyStreamFrame>(result.Frame);
        Assert.Equal("req-42", frame.RequestId);
        Assert.Equal("hi", frame.Payload.Delta);
    }

    [Fact]
    public void Parse_EchoWithTopLevelRequestId_ExposesRequestId()
    {
        const string raw = """
            {"type":"chat.message.echo","requestId":"req-7","payload":{"content":"x","imageCount":0,"timestamp":1700000000000}}
            """;

        var result = WsFrameParser.Parse(raw);

        var frame = Assert.IsType<WsEchoFrame>(result.Frame);
        Assert.Equal("req-7", frame.RequestId);
        Assert.Equal(1700000000000, frame.Timestamp);
    }

    [Fact]
    public void Parse_QueuedWithTopLevelRequestId_ExposesRequestIdAndDebounceWindow()
    {
        const string raw = """
            {"type":"chat.queued","requestId":"req-9","payload":{"debounceWindowSec":8.0}}
            """;

        var frame = Assert.IsType<WsQueuedFrame>(WsFrameParser.Parse(raw).Frame);

        Assert.Equal("req-9", frame.RequestId);
        Assert.Equal(8.0, frame.Payload.DebounceWindowSec);
    }

    [Fact]
    public void Parse_RequestIdPlacedInsidePayload_IsNotPickedUp()
    {
        // 陷阱 1 的反例：requestId 若落在 payload 内（契约漂移），顶层读取会取不到。
        const string raw = """
            {"type":"chat.queued","payload":{"requestId":"wrong","debounceWindowSec":8.0}}
            """;

        var frame = Assert.IsType<WsQueuedFrame>(WsFrameParser.Parse(raw).Frame);

        Assert.Null(frame.RequestId);
    }

    /* ---------------- 陷阱 2：pong 无 payload 包装 -------------------------- */

    [Fact]
    public void Parse_PongTopLevelTimestamp_WithoutPayloadWrapper()
    {
        const string raw = """{"type":"pong","timestamp":1700000000123}""";

        var result = WsFrameParser.Parse(raw);

        Assert.Equal(WsFrameParseKind.Known, result.Kind);
        var frame = Assert.IsType<WsPongFrame>(result.Frame);
        Assert.Equal(1700000000123, frame.Timestamp);
        Assert.Null(frame.PingTimestamp);
        var echoed = Assert.IsType<WsPongFrame>(WsFrameParser.Parse("""{"type":"pong","timestamp":999,"pingTimestamp":123}""").Frame);
        Assert.Equal(123, echoed.PingTimestamp);
    }

    [Fact]
    public void Parse_PongWithPayloadWrapper_TimestampStaysZero()
    {
        // 反例：若按「payload.timestamp」读取则取不到值 —— 证明实现读的是顶层。
        const string raw = """{"type":"pong","payload":{"timestamp":1700000000123}}""";

        var frame = Assert.IsType<WsPongFrame>(WsFrameParser.Parse(raw).Frame);

        Assert.Equal(0, frame.Timestamp);
    }

    [Fact]
    public void Parse_PongWithoutTimestamp_DefaultsToZeroWithoutThrowing()
    {
        var frame = Assert.IsType<WsPongFrame>(WsFrameParser.Parse("""{"type":"pong"}""").Frame);

        Assert.Equal(0, frame.Timestamp);
    }

    /* ---------------- 未知 type / 非法 JSON：忽略且不抛错 -------------------- */

    [Fact]
    public void Parse_UnknownType_IsReportedAsUnknownAndDoesNotThrow()
    {
        const string raw = """{"type":"brand.new.event","payload":{"whatever":true}}""";

        var result = WsFrameParser.Parse(raw);

        Assert.Equal(WsFrameParseKind.UnknownType, result.Kind);
        Assert.Null(result.Frame);
        Assert.Equal("brand.new.event", result.RawType);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("[1,2,3]")]
    [InlineData("\"a string\"")]
    public void Parse_InvalidJson_IsReportedAndDoesNotThrow(string? raw)
    {
        var result = WsFrameParser.Parse(raw);

        Assert.Equal(WsFrameParseKind.InvalidJson, result.Kind);
        Assert.Null(result.Frame);
    }

    [Fact]
    public void Parse_MissingType_IsTreatedAsUnknownNotInvalid()
    {
        var result = WsFrameParser.Parse("""{"payload":{"a":1}}""");

        Assert.Equal(WsFrameParseKind.UnknownType, result.Kind);
    }

    [Fact]
    public void Parse_KnownTypeWithUnexpectedPayloadShape_DoesNotThrow()
    {
        // 契约漂移：payload 字段类型不符 → 丢弃该帧但**不得**抛错（NFR-W-12）。
        const string raw = """{"type":"chat.reply.stream","requestId":"r","payload":{"delta":123}}""";

        var result = WsFrameParser.Parse(raw);

        Assert.NotEqual(WsFrameParseKind.Known, result.Kind);
    }

    [Fact]
    public void Parse_KnownTypeWithUnknownExtraFields_IsIgnoredNotFatal()
    {
        // 只增不改：未知字段必须被静默忽略。
        const string raw = """
            {"type":"chat.typing","payload":{"typing":true,"stage":"vision","newFieldFromFuture":42}}
            """;

        var frame = Assert.IsType<WsTypingFrame>(WsFrameParser.Parse(raw).Frame);

        Assert.True(frame.Payload.Typing);
        Assert.Equal("vision", frame.Payload.Stage);
    }

    [Fact]
    public void Parse_NumericFieldsAsStrings_AreAccepted()
    {
        // 服务端偶尔把数字序列化成字符串（AllowReadingFromString）。
        const string raw = """{"type":"pong","timestamp":"1700000000123"}""";

        var frame = Assert.IsType<WsPongFrame>(WsFrameParser.Parse(raw).Frame);

        Assert.Equal(1700000000123, frame.Timestamp);
    }

    /* ---------------- 已知 type 全集（防漂移） ------------------------------ */

    [Fact]
    public void KnownServerFrameTypes_CoversAllTrapsAndIsStable()
    {
        var types = WsFrameParser.KnownServerFrameTypes;

        foreach (var required in new[]
                 {
                     "pong", "chat.message.echo", "chat.queued", "chat.typing", "chat.reply.stream",
                     "bot.error", "memory.fact.created", "auth.expired", "points.changed",
                     "level.changed", "streak.warning", "makeup_card.changed", "points.snapshot",
                 })
        {
            Assert.Contains(required, types);
        }

        Assert.Equal(types.Count, types.Distinct(StringComparer.Ordinal).Count());
    }

    /* ---------------- 陷阱 6：4001 关闭码判定 ------------------------------- */

    [Theory]
    [InlineData(4001)]
    public void IsAuthFailureClose_Recognizes4001(int code)
    {
        Assert.True(WsFrameParser.IsAuthFailureClose(code));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(1000)]
    [InlineData(1006)]
    [InlineData(4000)]
    [InlineData(4002)]
    public void IsAuthFailureClose_RejectsOtherCodes(int? code)
    {
        Assert.False(WsFrameParser.IsAuthFailureClose(code));
    }

    [Fact]
    public void WsCloseInvalidToken_ConstantIs4001()
    {
        // ⚠️ WebSocketCloseStatus 枚举无法表达 4001，必须依赖该常量与原始数值比较。
        Assert.Equal(4001, ProtocolConstants.WsCloseInvalidToken);
        Assert.False(Enum.IsDefined(typeof(System.Net.WebSockets.WebSocketCloseStatus), 4001));
    }

    /* ---------------- 连接与退避常量（FR-W-CONN-2 / 3） --------------------- */

    [Fact]
    public void ProtocolConstants_ConnectionTimingsAreExact()
    {
        Assert.Equal(25_000, ProtocolConstants.HeartbeatIntervalMs);
        Assert.Equal(1_000, ProtocolConstants.ReconnectBaseMs);
        Assert.Equal(60_000, ProtocolConstants.ReconnectMaxMs);
        Assert.Equal(15, ProtocolConstants.ReconnectMaxAttempts);
        Assert.Equal("/ws/chat", ProtocolConstants.WsChatPath);
    }
}
