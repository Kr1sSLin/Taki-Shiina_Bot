using Microsoft.Extensions.Logging;
using TKSDesktop.App;
using TKSDesktop.Contracts;
using TKSDesktop.Contracts.Dtos;
using TKSDesktop.Core.Platform;
using TKSDesktop.Core.Services.Delivery;
using TKSDesktop.Core.Services.Ports;

namespace TKSDesktop.Core.Services.Chat;

/// <summary>
/// 对话核心（PRD §6.3–§6.5 / §10.2–§10.3）。
///
/// <list type="bullet">
///   <item><b>乐观发送</b>（FR-W-CHAT-3）：先落库 <c>sending</c> 并发 <see cref="MessageChanged"/>，
///         WS 入队成功再置 <c>sent</c>，失败置 <c>error</c> + <c>SEND_FAILED</c>；</item>
///   <item><b>未连接禁止发送</b>（FR-W-CHAT-4 / EDGE-W-6）：直接拒绝，**不做离线队列**；</item>
///   <item><b>幂等键</b>（FR-W-CHAT-2 / 陷阱 1）：<c>requestId</c> 同时是本地主键、WS 帧顶层
///         <c>requestId</c> 与重发键；</item>
///   <item><b>回声跳过</b>（EDGE-W-4）：<c>originDeviceId</c> 等于本机 deviceId 的 <c>chat.message.echo</c>
///         必须跳过，否则自己的消息会重复上屏；</item>
///   <item><b>排队的防抖窗口</b>（FR-W-CHAT-15 / EDGE-W-17）：一律使用服务端下发的
///         <c>debounceWindowSec</c>，**严禁硬编码 8/20**；</item>
///   <item><b>思考态</b>（FR-W-CHAT-14）：<c>chat.typing.stage</c> 原样透传（无 <c>stage</c> → <c>null</c>）；</item>
///   <item><b>流式</b>（FR-W-CHAT-5/6/7/8）：复用 <see cref="StreamingAccumulator"/>（150s 重置超时）与
///         <see cref="StreamingTimeoutWatch"/>；<c>done</c> 时按 <c>finalContent</c> 经
///         <see cref="MessageSplitter"/> 拆分落库、删除 <c>pending_{requestId}</c> 占位、按
///         <c>payload.requestIds</c> 批量标记被防抖合并的用户消息为已送达；</item>
///   <item><b>三路去重</b>（FR-W-INT-12 / EDGE-W-21）：按拆分后 <c>{messageId}_{index}</c> 查
///         <c>delivered_bot_messages</c>，已投递则跳过，写库后标记；</item>
///   <item><b>历史分隔符</b>（C-5 / FR-W-SYNC-8）：经 <see cref="ProtocolConstants.IsHistorySeparator"/>
///         识别并置 <see cref="ChatMessageView.IsHistorySeparator"/>，**不得**当普通气泡；</item>
///   <item><b>互动行</b>（FR-W-SYNC-11）：<c>contentType == "interaction"</c> 或
///         <c>messageKind ∈ {{interaction, interaction_failed}}</c> → 落库为 <c>interaction</c> 并按互动标记渲染；</item>
///   <item><b>断线兜底</b>（FR-W-CONN-8）：连接断开时把 <c>sending</c> 用户消息标记为
///         <c>CONNECTION_LOST</c> 并清空打字态，**非静默丢弃**；</item>
///   <item><b>清空会话</b>（FR-W-CHAT-12 / 陷阱 10）：<c>ClearConversationAsync(advanceCursor: true)</c>。</item>
/// </list>
///
/// <para><b>线程与事件</b>：WS 帧派发用 <see cref="SemaphoreSlim"/> 串行化（<see cref="StreamingAccumulator"/>
/// 不是线程安全的），所有事件一律经 <see cref="IUiDispatcher"/> 投递（FR-W-ARCH-3）；
/// 全程不使用 <c>.Result</c> / <c>.Wait()</c>。</para>
///
/// <para><b>依赖</b>：只依赖 <c>Core/Services/Ports</c> 的窄接口。构造末尾三个参数是
/// <b>可选</b>窄能力：<see cref="TimeProvider"/>（时间源，DI 已注册 <c>TimeProvider.System</c>）、
/// <see cref="IAuthService"/>（取本机 deviceId，仅用于 EDGE-W-4 的回声比对）、
/// <see cref="IAttachmentPort"/>（FR-W-IMG-6 附件绑定）。缺失时功能降级并记日志，不影响发送主链路。</para>
/// </summary>
public sealed class ChatService : IChatService, IInteractionDelivery, IDisposable
{
    /// <summary>未连接提示（i18n key）。</summary>
    private const string NoticeNotConnected = "chat.notConnected";

    /// <summary>流式超时提示（i18n key）。</summary>
    private const string NoticeTimeout = "chat.timeout";

    /// <summary>空回复提示（i18n key）。</summary>
    private const string NoticeEmptyReply = "chat.emptyReply";

    /// <summary>断线提示（i18n key）。</summary>
    private const string NoticeConnectionLost = "chat.connectionLost";

    private readonly IChatRepositoryPort _repository;
    private readonly IRestPort? _rest;
    private readonly IWssPort _wss;
    private readonly IReminderScheduler _reminders;
    private readonly IUserNotificationService _notifications;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<ChatService> _logger;
    private readonly IAuthService? _auth;
    private readonly IAttachmentPort? _attachments;
    private readonly TimeProvider _timeProvider;

    private readonly StreamingAccumulator _streaming;
    private readonly StreamingTimeoutWatch _timeoutWatch;

    /// <summary>帧派发串行闸（<see cref="StreamingAccumulator"/> 非线程安全）。</summary>
    private readonly SemaphoreSlim _frameGate = new(1, 1);

    private readonly object _stateGate = new();

    /// <summary>流式占位消息的创建时刻（占位的时间戳必须稳定，不随每个 delta 抖动）。</summary>
    private readonly Dictionary<string, long> _streamingStartedAt = new(StringComparer.Ordinal);

    private ChatTypingState _typing = new(false, null, null);

    private string? _deviceId;

    private bool _disposed;

    /// <summary>构造。</summary>
    /// <param name="repository">聊天落库端口。</param>
    /// <param name="wss">WS 长连接端口（帧 / 连接状态 / 发送 / 重连）。</param>
    /// <param name="reminders">提醒调度（<c>timerInstruction</c> —— FR-W-REM-1）。</param>
    /// <param name="notifications">通知服务（错误类通知 —— FR-W-NOTI-1）。</param>
    /// <param name="dispatcher">UI 线程调度（FR-W-ARCH-3：所有事件必须经此投递）。</param>
    /// <param name="logger">日志。</param>
    /// <param name="timeProvider">时间源；<c>null</c> 时用 <see cref="TimeProvider.System"/>（测试可注入假时钟）。</param>
    /// <param name="authService">本机 deviceId 来源（EDGE-W-4 回声比对）；<c>null</c> 时退化为「本地已有同 requestId → 跳过」。</param>
    /// <param name="attachmentPort">附件端口（FR-W-IMG-6 把草稿附件挂到真实消息）；<c>null</c> 时跳过绑定。</param>
    public ChatService(
        IChatRepositoryPort repository,
        IWssPort wss,
        IReminderScheduler reminders,
        IUserNotificationService notifications,
        IUiDispatcher dispatcher,
        ILogger<ChatService> logger,
        TimeProvider? timeProvider = null,
        IAuthService? authService = null,
        IAttachmentPort? attachmentPort = null,
        IRestPort? rest = null)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(wss);
        ArgumentNullException.ThrowIfNull(reminders);
        ArgumentNullException.ThrowIfNull(notifications);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(logger);

        _repository = repository;
        _rest = rest;
        _wss = wss;
        _reminders = reminders;
        _notifications = notifications;
        _dispatcher = dispatcher;
        _logger = logger;
        _auth = authService;
        _attachments = attachmentPort;
        _timeProvider = timeProvider ?? TimeProvider.System;

        _streaming = new StreamingAccumulator(_timeProvider);
        _timeoutWatch = new StreamingTimeoutWatch(_timeProvider, OnStreamTimeout, logger);

        _wss.ConnectionChanged += OnConnectionChanged;
        _wss.FrameReceived += OnFrameReceived;
    }

    /// <inheritdoc />
    public event EventHandler<ConnectionSnapshot>? ConnectionChanged;

    /// <inheritdoc />
    public event EventHandler<ChatTypingState>? TypingChanged;

    /// <inheritdoc />
    public event EventHandler<ChatMessageView>? MessageChanged;

    /// <inheritdoc />
    public event EventHandler<string>? MessageRemoved;

    /// <inheritdoc />
    public event EventHandler? ConversationCleared;

    /// <inheritdoc />
    public event EventHandler<string>? NoticeRaised;

    /// <inheritdoc />
    public event EventHandler<string>? ScrollToMessageRequested;

    /// <inheritdoc />
    public ConnectionSnapshot State => _wss.Current;

    /// <summary>当前时间（可注入的毫秒时间戳）。</summary>
    private long NowMs => _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

    /// <summary>当前打字 / 排队态（只读，供诊断与自检）。</summary>
    public ChatTypingState Typing
    {
        get
        {
            lock (_stateGate)
            {
                return _typing;
            }
        }
    }

    /// <summary>在途流式请求数（诊断 / 自检用）。</summary>
    public int InFlightStreamCount => _streaming.Count;

    /* ===================================================================== */
    /* 发送（FR-W-CHAT-2/3/4）                                                */
    /* ===================================================================== */

    /// <inheritdoc />
    public async Task<SendResult> SendAsync(
        string requestId,
        string? text,
        IReadOnlyList<AttachmentDraft> attachments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentNullException.ThrowIfNull(attachments);

        // FR-W-CHAT-4 / EDGE-W-6：未连接**禁止**发送（不做离线队列），且必须让 UI 可见。
        if (!_wss.IsConnected)
        {
            _logger.LogInformation("未连接，已拒绝发送 requestId={RequestId}", requestId);
            await RaiseNoticeAsync(NoticeNotConnected).ConfigureAwait(false);
            return new SendResult(false, null, NoticeNotConnected);
        }

        var content = (text ?? string.Empty).Trim();
        if (content.Length == 0 && attachments.Count == 0)
        {
            // 文本为空且无附件 → 本地先拒绝，避免服务端 EMPTY_MESSAGE 往返。
            var i18nKey = ErrorCatalog.I18nKeyOf(ErrorCatalog.EmptyMessage);
            await RaiseNoticeAsync(i18nKey).ConfigureAwait(false);
            return new SendResult(false, ErrorCatalog.EmptyMessage, i18nKey);
        }

        // FR-W-CHAT-3：乐观入库（status=sending），requestId 即本地主键。
        var record = BuildUserRecord(requestId, content, attachments.Count > 0, ProtocolConstants.StatusSending, null);
        await _repository.UpsertMessageAsync(record).ConfigureAwait(false);

        if (_attachments is not null && attachments.Count > 0)
        {
            // FR-W-IMG-6：把草稿附件挂到真实消息上（失败不影响发送本身）。
            var bound = await TryBindAttachmentsAsync(requestId, attachments).ConfigureAwait(false);
            if (bound)
            {
                record = record with { AttachmentPaths = [.. attachments.Select(static a => a.LocalPath)] };
            }
        }

        await RaiseMessageChangedAsync(ToView(record)).ConfigureAwait(false);

        var images = BuildImages(attachments.Select(static a => (a.LocalPath, (string?)a.MimeType)));
        var enqueued = _wss.SendChatMessage(
            requestId,
            content.Length == 0 ? null : content,
            images.Count == 0 ? null : images);

        return await CompleteSendAsync(requestId, enqueued).ConfigureAwait(false);
    }

    public async Task<SendResult> SendFallbackAsync(string requestId, string text)
    {
        if (_rest is null || State.Status != ConnectionStatus.Degraded || string.IsNullOrWhiteSpace(text))
        {
            return new(false, null, NoticeNotConnected);
        }

        var row = BuildUserRecord(requestId, text.Trim(), false, ProtocolConstants.StatusSending, null);
        await _repository.UpsertMessageAsync(row).ConfigureAwait(false);
        await RaiseMessageChangedAsync(ToView(row)).ConfigureAwait(false);
        var result = await _rest.SendChatAsync(new RestChatRequestDto
        {
            RequestId = requestId,
            ConversationId = ProtocolConstants.DefaultSessionId,
            Message = text.Trim(),
            Stream = false,
        }).ConfigureAwait(false);
        var status = result.Success ? ProtocolConstants.StatusSent : ProtocolConstants.StatusError;
        await _repository.UpdateMessageStatusAsync(requestId, status, result.ErrorCode).ConfigureAwait(false);
        await RaiseMessageChangedAsync(ToView(row with { Status = status, ErrorCode = result.ErrorCode })).ConfigureAwait(false);
        if (!result.Success || result.Value is null)
        {
            return new(false, result.ErrorCode, result.I18nKey);
        }

        var data = result.Value;
        var finalId = string.IsNullOrWhiteSpace(data.MessageId) ? "rest_" + requestId : data.MessageId;
        foreach (var segment in MessageSplitter.Split(finalId, data.Reply, NowMs))
        {
            var bot = new ChatMessageRecord(segment.MessageId, "bot", "text", segment.Content,
                ProtocolConstants.StatusReceived, segment.Timestamp);
            await _repository.UpsertMessageAsync(bot).ConfigureAwait(false);
            await RaiseMessageChangedAsync(ToView(bot)).ConfigureAwait(false);
        }

        if (data.TimerInstruction is { } timer)
        {
            await _reminders.ScheduleAsync([requestId], timer).ConfigureAwait(false);
        }

        return new(true, null, null);
    }

    /// <inheritdoc />
    /// <remarks>FR-W-CHAT-11：**复用同一 requestId**（本地主键 = WS 顶层 requestId = 幂等键），
    /// 因此服务端侧不会产生第二条消息。</remarks>
    public async Task<SendResult> RetryAsync(string messageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);

        var row = await _repository.GetMessageAsync(messageId).ConfigureAwait(false);
        if (row is null || !row.IsUser
            || !string.Equals(row.Status, ProtocolConstants.StatusError, StringComparison.Ordinal))
        {
            // 只有「失败的用户消息」可重发；其余情况不猜、不重发。
            _logger.LogWarning("不可重发 messageId={MessageId}（不存在 / 非用户消息 / 非失败态）", messageId);
            return new SendResult(false, null, ErrorCatalog.UnknownI18nKey);
        }

        if (!_wss.IsConnected)
        {
            await RaiseNoticeAsync(NoticeNotConnected).ConfigureAwait(false);
            return new SendResult(false, null, NoticeNotConnected);
        }

        await _repository
            .UpdateMessageStatusAsync(messageId, ProtocolConstants.StatusSending, null)
            .ConfigureAwait(false);
        await RaiseRowAsync(messageId).ConfigureAwait(false);

        var images = BuildImages(row.AttachmentPaths.Select(static p => (p, (string?)null)));
        var enqueued = _wss.SendChatMessage(
            messageId,
            row.Content.Length == 0 ? null : row.Content,
            images.Count == 0 ? null : images);

        return await CompleteSendAsync(messageId, enqueued).ConfigureAwait(false);
    }

    /* ===================================================================== */
    /* 连接（FR-W-CONN-6/8）                                                  */
    /* ===================================================================== */

    /// <inheritdoc />
    /// <remarks>FR-W-CONN-6：手动重连必须先置 full-sync 标志，再请求连接层重连。</remarks>
    public async Task ReconnectAsync()
    {
        _wss.MarkFullSyncRequired();
        await _wss.ReconnectAsync(manual: true).ConfigureAwait(false);
    }

    /* ===================================================================== */
    /* 本地操作（FR-W-CHAT-12/17）                                            */
    /* ===================================================================== */

    /// <inheritdoc />
    public async Task DeleteLocalAsync(string messageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);

        var deleted = await _repository.DeleteMessageAsync(messageId).ConfigureAwait(false);
        if (!deleted)
        {
            return;
        }

        await RaiseMessageRemovedAsync(messageId).ConfigureAwait(false);
        _logger.LogInformation("已删除本地消息 messageId={MessageId}", messageId);
    }

    /// <inheritdoc />
    /// <remarks>FR-W-CHAT-12 / 陷阱 10：**必须**推进游标（<c>advanceCursor: true</c>），
    /// 否则清空后 <c>since = max(本地最大 timestamp, 游标)</c> 退化为 0，历史立刻被回灌。</remarks>
    public async Task ClearLocalConversationAsync()
    {
        _timeoutWatch.DisarmAll();
        _ = _streaming.Clear();
        _streamingStartedAt.Clear();

        var deleted = await _repository.ClearConversationAsync(advanceCursor: true).ConfigureAwait(false);
        await RaiseTypingAsync(new ChatTypingState(false, null, null)).ConfigureAwait(false);
        await RaiseAsync(() => ConversationCleared?.Invoke(this, EventArgs.Empty)).ConfigureAwait(false);

        _logger.LogInformation("已清空本地会话并推进游标 deletedMessages={Deleted}", deleted);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ChatMessageView>> SearchAsync(string term, int limit)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            return [];
        }

        // FR-W-CHAT-17：分词器路由（trigram / LIKE 回退）由仓储实现，这里只透传。
        var safeLimit = limit <= 0 ? ProtocolConstants.LocalFirstPageSize : limit;
        var rows = await _repository.SearchMessagesAsync(term, safeLimit).ConfigureAwait(false);

        var views = new List<ChatMessageView>(rows.Count);
        foreach (var row in rows)
        {
            views.Add(ToView(row));
        }

        return views;
    }

    /// <summary>
    /// FR-W-NOTI-4：请求 UI 滚动定位到某条消息（点击通知后）。
    /// 通知激活的解析在平台层，本方法供其回调（事件投递经 <see cref="IUiDispatcher"/>）。
    /// </summary>
    public Task RequestScrollTo(string messageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        return RaiseAsync(() => ScrollToMessageRequested?.Invoke(this, messageId));
    }

    /* ===================================================================== */
    /* WS 帧派发                                                              */
    /* ===================================================================== */

    private void OnFrameReceived(object? sender, WsServerFrame frame) => _ = DispatchFrameAsync(frame);

    /// <summary>帧派发串行化；单个帧解析失败不得影响后续帧（NFR-W-12）。</summary>
    private async Task DispatchFrameAsync(WsServerFrame frame)
    {
        try
        {
            await _frameGate.WaitAsync().ConfigureAwait(false);
            try
            {
                switch (frame)
                {
                    case WsEchoFrame echo:
                        await HandleEchoAsync(echo).ConfigureAwait(false);
                        break;
                    case WsQueuedFrame queued:
                        await HandleQueuedAsync(queued).ConfigureAwait(false);
                        break;
                    case WsTypingFrame typing:
                        await HandleTypingAsync(typing).ConfigureAwait(false);
                        break;
                    case WsReplyStreamFrame reply:
                        await HandleReplyStreamAsync(reply).ConfigureAwait(false);
                        break;
                    case WsBotErrorFrame error:
                        await HandleBotErrorAsync(error).ConfigureAwait(false);
                        break;
                    default:
                        // 其余帧（pong / memory.fact.created / 积分等级等）由各自的订阅方处理。
                        break;
                }
            }
            finally
            {
                _frameGate.Release();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "处理 WS 帧失败 frame={Frame}", frame.GetType().Name);
        }
    }

    /// <summary>`chat.message.echo`：多设备回声，按 <c>requestId</c> 幂等（EDGE-W-4 / FR-W-CHAT-2）。</summary>
    private async Task HandleEchoAsync(WsEchoFrame echo)
    {
        var requestId = echo.RequestId;
        if (string.IsNullOrWhiteSpace(requestId))
        {
            return;
        }

        var payload = echo.Payload;

        // EDGE-W-4：自己发的消息会收到**自己的**回声 —— 必须跳过，否则重复上屏。
        if (IsOwnEcho(payload.OriginDeviceId))
        {
            _logger.LogDebug("收到本机回声，已跳过 requestId={RequestId}", requestId);
            return;
        }

        var existing = await _repository.GetMessageAsync(requestId).ConfigureAwait(false);
        if (existing is not null)
        {
            // 幂等：同一 requestId 只保留一条；仍在 sending 时补一次终态（其它设备已确认送达）。
            if (string.Equals(existing.Status, ProtocolConstants.StatusSending, StringComparison.Ordinal))
            {
                await _repository
                    .UpdateMessageStatusAsync(requestId, ProtocolConstants.StatusSent, null)
                    .ConfigureAwait(false);
                await RaiseRowAsync(requestId).ConfigureAwait(false);
            }

            return;
        }

        var hasImages = payload.ImageCount > 0;
        var record = new ChatMessageRecord(
            requestId,
            "user",
            hasImages ? "image" : "text",
            payload.Content ?? string.Empty,
            ProtocolConstants.StatusSent,
            payload.Timestamp > 0 ? payload.Timestamp : NowMs,
            MessageType: hasImages ? "image" : "text");

        await _repository.UpsertMessageAsync(record).ConfigureAwait(false);
        await RaiseMessageChangedAsync(ToView(record)).ConfigureAwait(false);

        _logger.LogInformation("已入库其它设备的回声 requestId={RequestId} images={ImageCount}", requestId, payload.ImageCount);
    }

    /// <summary>`chat.queued`（FR-W-CHAT-15 / EDGE-W-17）：窗口秒数**必须**取服务端下发值。</summary>
    private Task HandleQueuedAsync(WsQueuedFrame queued)
    {
        string? stage;
        lock (_stateGate)
        {
            stage = _typing.Stage;
        }

        var state = new ChatTypingState(Typing: true, Stage: stage, DebounceWindowSec: queued.Payload.DebounceWindowSec);
        return RaiseTypingAsync(state);
    }

    /// <summary>`chat.typing`（FR-W-CHAT-14）：<c>stage</c> 原样透传，无值时 <c>null</c>。</summary>
    private Task HandleTypingAsync(WsTypingFrame typing)
    {
        var payload = typing.Payload;
        return RaiseTypingAsync(new ChatTypingState(payload.Typing, payload.Stage, null));
    }

    /// <summary>`chat.reply.stream`（FR-W-CHAT-5/7/8）。</summary>
    private async Task HandleReplyStreamAsync(WsReplyStreamFrame reply)
    {
        var requestId = reply.RequestId;
        if (string.IsNullOrWhiteSpace(requestId))
        {
            // 顶层 requestId 是归属依据（陷阱 1）；缺失时无法安全落库。
            _logger.LogWarning("chat.reply.stream 缺少顶层 requestId，已忽略");
            return;
        }

        if (!reply.Payload.Done)
        {
            await AppendDeltaAsync(requestId, reply.Payload).ConfigureAwait(false);
            return;
        }

        await FinishStreamAsync(requestId, reply.Payload).ConfigureAwait(false);
    }

    /// <summary>流式增量：首个非空 delta 建占位，之后就地 UPDATE（FR-W-CHAT-5/8）。</summary>
    private async Task AppendDeltaAsync(string requestId, ChatReplyStreamPayloadDto payload)
    {
        var hadEntry = _streaming.Contains(requestId);

        // Append 对空 / 全空白 delta 返回 false 且不建条目 —— 不得为空白建占位。
        var created = _streaming.Append(requestId, payload.Delta, payload.ContentType, payload.ModelProvider);
        if (!created && !hadEntry)
        {
            return;
        }

        var entry = _streaming.Get(requestId);
        if (entry is null)
        {
            return;
        }

        if (created)
        {
            _streamingStartedAt[requestId] = NowMs;
        }

        var timestamp = _streamingStartedAt.TryGetValue(requestId, out var startedAt) ? startedAt : NowMs;
        var pendingId = StreamingAccumulator.PendingMessageId(requestId);

        var record = new ChatMessageRecord(
            pendingId,
            "bot",
            entry.ContentType,
            entry.Content,
            ProtocolConstants.StatusStreaming,
            timestamp,
            ModelProvider: entry.ModelProvider);

        await _repository.UpsertMessageAsync(record).ConfigureAwait(false);
        await RaiseMessageChangedAsync(ToView(record)).ConfigureAwait(false);

        // FR-W-CHAT-8：每收到一个 delta 重置 150s 计时。
        _timeoutWatch.Arm(requestId);
    }

    /// <summary>`done=true` 收尾（§10.3）：删占位 → 批量送达 → 拆分落库 → 排程提醒 → 通知。</summary>
    public async Task BeginAsync(string requestId, string itemId, string? text)
    {
        await _repository.UpsertMessageAsync(new ChatMessageRecord(requestId, "user", "interaction",
            string.IsNullOrWhiteSpace(text) ? itemId : text, ProtocolConstants.StatusSending, NowMs)).ConfigureAwait(false);
        await RaiseRowAsync(requestId).ConfigureAwait(false);
        await RaiseTypingAsync(new ChatTypingState(true, "interaction_merge", null)).ConfigureAwait(false);
    }

    public async Task CompleteAsync(string requestId, InteractionSendDataDto? response, bool success, string? errorCode)
    {
        await _frameGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var existing = await _repository.GetMessageAsync(requestId).ConfigureAwait(false);
            // A successful WS response can precede a lost HTTP response.
            if (success || existing?.Status != ProtocolConstants.StatusSent)
                await _repository.UpdateMessageStatusAsync(requestId,
                    success ? ProtocolConstants.StatusSent : ProtocolConstants.StatusError, errorCode).ConfigureAwait(false);
            await RaiseRowAsync(requestId).ConfigureAwait(false);
            var ids = (response?.MergedRequestIds ?? []).Append(requestId).Distinct(StringComparer.Ordinal).ToList();
            if (response is not null && (!string.IsNullOrWhiteSpace(response.MessageId) || ids.Count > 1))
            {
                var payload = new ChatReplyStreamPayloadDto
                {
                    Done = true,
                    MessageId = response.MessageId,
                    FinalContent = response.Reply ?? response.FallbackText,
                    RequestIds = success ? ids : response.MergedRequestIds ?? [],
                    MessageKind = success ? "interaction" : "interaction_failed",
                    InteractionItemName = response.Item?.Name,
                    InteractionItemIcon = response.Item?.Icon,
                    InteractionFailed = !success,
                    TimerInstruction = response.TimerInstruction,
                };
                await FinishStreamAsync(requestId, payload).ConfigureAwait(false);
            }
            await RaiseTypingAsync(new ChatTypingState(false, null, null)).ConfigureAwait(false);
        }
        finally
        {
            _frameGate.Release();
        }
    }

    private async Task FinishStreamAsync(string requestId, ChatReplyStreamPayloadDto payload)
    {
        var entry = _streaming.Complete(requestId);
        _timeoutWatch.Disarm(requestId);
        _streamingStartedAt.Remove(requestId);

        // ① 删除流式占位（占位主键是 pending_{requestId}，与服务端正式 messageId 不同）。
        var pendingId = StreamingAccumulator.PendingMessageId(requestId);
        if (await _repository.GetMessageAsync(pendingId).ConfigureAwait(false) is not null)
        {
            _ = await _repository.DeleteMessageAsync(pendingId).ConfigureAwait(false);
            await RaiseMessageRemovedAsync(pendingId).ConfigureAwait(false);
        }

        // ② FR-W-CHAT-7：一次回复可能对应多条被防抖合并的用户消息 —— 按服务端下发的 requestIds 批量置 sent。
        IReadOnlyList<string> requestIds = payload.RequestIds is { Count: > 0 } ids ? ids : [requestId];
        foreach (var id in requestIds)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var row = await _repository.GetMessageAsync(id).ConfigureAwait(false);
            if (row is null || !row.IsUser
                || string.Equals(row.Status, ProtocolConstants.StatusSent, StringComparison.Ordinal))
            {
                continue;
            }

            await _repository
                .UpdateMessageStatusAsync(id, ProtocolConstants.StatusSent, null)
                .ConfigureAwait(false);
            await RaiseRowAsync(id).ConfigureAwait(false);
        }

        // ③ 最终内容：优先 finalContent（服务端已清洗并剥离 [[TIMER:...]]），缺失时回退到累积 delta。
        var finalContent = HistoryText.StripTimestampPrefix(
            string.IsNullOrWhiteSpace(payload.FinalContent) ? entry?.Content ?? string.Empty : payload.FinalContent);
        var finalMessageId = string.IsNullOrWhiteSpace(payload.MessageId)
            ? string.Create(CultureInfo.InvariantCulture, $"bot_{requestId}")
            : payload.MessageId;
        var timestamp = payload.Timestamp is { } ts && ts > 0 ? ts : NowMs;

        var written = await PersistBotReplyAsync(finalMessageId, finalContent, timestamp, payload).ConfigureAwait(false);

        if (finalContent.Trim().Length == 0)
        {
            // FR-W-CHAT-10：空内容不得落库（服务端已做重采样与占位兜底，这里只是兜底）。
            _logger.LogWarning("收到空 Bot 回复，已丢弃 requestId={RequestId}", requestId);
            await RaiseNoticeAsync(NoticeEmptyReply).ConfigureAwait(false);
        }

        // ④ FR-W-REM-1：只消费服务端已解析的 timerInstruction。
        if (payload.TimerInstruction is not null)
        {
            await _reminders.ScheduleAsync(requestIds, payload.TimerInstruction).ConfigureAwait(false);
        }

        await RaiseTypingAsync(new ChatTypingState(false, null, null)).ConfigureAwait(false);

        // ⑤ FR-W-NOTI-1：仅在本次**首次**落库时通知，避免三路到达重复弹（EDGE-W-21）。
        if (written.Count > 0)
        {
            await NotifyBotReplyAsync(payload, written[0]).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 落库 Bot 回复（FR-W-CHAT-6 / FR-W-SYNC-5）：**唯一**拆分实现 <see cref="MessageSplitter"/>，
    /// 主键 <c>{{messageId}}_{{index}}</c>，并用 <c>delivered_bot_messages</c> 做三路去重（FR-W-INT-12 / EDGE-W-21）。
    /// </summary>
    private async Task<IReadOnlyList<ChatMessageView>> PersistBotReplyAsync(
        string finalMessageId,
        string content,
        long timestamp,
        ChatReplyStreamPayloadDto payload)
    {
        var segments = MessageSplitter.Split(finalMessageId, content, timestamp);
        if (segments.Count == 0)
        {
            return [];
        }

        var contentType = ResolveBotContentType(payload);
        var interactionFailed = IsInteractionFailed(payload);

        var views = new List<ChatMessageView>(segments.Count);
        var delivered = new List<string>(segments.Count);

        foreach (var segment in segments)
        {
            // 三路去重：已投递过的拆分后 messageId 必须跳过（WS / HTTP reply / 历史补拉）。
            if (await _repository.IsDeliveredAsync(segment.MessageId).ConfigureAwait(false))
            {
                _logger.LogDebug("Bot 消息片段已投递，跳过 messageId={MessageId}", segment.MessageId);
                continue;
            }

            var record = new ChatMessageRecord(
                segment.MessageId,
                "bot",
                contentType,
                segment.Content,
                ProtocolConstants.StatusReceived,
                segment.Timestamp,
                ModelProvider: payload.ModelProvider);

            await _repository.UpsertMessageAsync(record).ConfigureAwait(false);
            delivered.Add(segment.MessageId);

            var view = ToView(record, payload.InteractionItemName, payload.InteractionItemIcon, interactionFailed);
            views.Add(view);
            await RaiseMessageChangedAsync(view).ConfigureAwait(false);
        }

        if (delivered.Count > 0)
        {
            await _repository.MarkDeliveredAsync(delivered).ConfigureAwait(false);
        }

        return views;
    }

    /// <summary>`bot.error`：置 <c>error</c> + 落 <c>bot_notifications</c> + 错误类通知（§5.3.2）。</summary>
    private async Task HandleBotErrorAsync(WsBotErrorFrame error)
    {
        var payload = error.Payload;
        var code = string.IsNullOrWhiteSpace(payload.ErrorCode) ? ErrorCatalog.InternalError : payload.ErrorCode;
        var timestamp = payload.Timestamp > 0 ? payload.Timestamp : NowMs;

        IReadOnlyList<string> requestIds = payload.RequestIds is { Count: > 0 } ids
            ? ids
            : string.IsNullOrWhiteSpace(error.RequestId) ? Array.Empty<string>() : [error.RequestId];

        foreach (var requestId in requestIds)
        {
            if (string.IsNullOrWhiteSpace(requestId))
            {
                continue;
            }

            // 释放该请求的流式在途状态并删除半截占位（错误后不会再有 done 帧）。
            _ = _streaming.Remove(requestId);
            _timeoutWatch.Disarm(requestId);
            _streamingStartedAt.Remove(requestId);

            var pendingId = StreamingAccumulator.PendingMessageId(requestId);
            if (await _repository.GetMessageAsync(pendingId).ConfigureAwait(false) is not null)
            {
                _ = await _repository.DeleteMessageAsync(pendingId).ConfigureAwait(false);
                await RaiseMessageRemovedAsync(pendingId).ConfigureAwait(false);
            }

            _ = await _repository
                .UpdateMessageStatusAsync(requestId, ProtocolConstants.StatusError, code)
                .ConfigureAwait(false);
            await RaiseRowAsync(requestId).ConfigureAwait(false);
        }

        await _repository
            .InsertBotNotificationAsync(code, payload.Message ?? string.Empty, timestamp)
            .ConfigureAwait(false);

        await RaiseTypingAsync(new ChatTypingState(false, null, null)).ConfigureAwait(false);

        // FR-W-NOTI-1：错误类走独立通知渠道（语义 ID 1004）。
        var title = I18n.T("notification.error.title");
        var body = string.IsNullOrWhiteSpace(payload.Message) ? I18n.T(ErrorCatalog.I18nKeyOf(code)) : payload.Message;

        _logger.LogWarning("服务端错误 code={Code} requestIds={RequestIds}", code, string.Join(",", requestIds));

        try
        {
            await _dispatcher.InvokeAsync(() =>
            {
                _notifications.Notify(NotificationCategory.Error, title, body);
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "错误通知投递失败 code={Code}", code);
        }
    }

    /* ===================================================================== */
    /* 连接状态（FR-W-CONN-8）                                                */
    /* ===================================================================== */

    private void OnConnectionChanged(object? sender, ConnectionSnapshot snapshot)
        => _ = HandleConnectionChangedAsync(snapshot);

    private async Task HandleConnectionChangedAsync(ConnectionSnapshot snapshot)
    {
        try
        {
            lock (_stateGate)
            {
                _typing = new ChatTypingState(false, null, null);
            }

            await RaiseAsync(() => ConnectionChanged?.Invoke(this, snapshot)).ConfigureAwait(false);

            if (snapshot.Status == ConnectionStatus.Connected)
            {
                return;
            }

            // FR-W-CONN-8：断开时把在途 / sending 的请求显式标记，**不得静默丢弃**；并清空打字态。
            await MarkInFlightAsConnectionLostAsync().ConfigureAwait(false);
            await RaiseTypingAsync(new ChatTypingState(false, null, null)).ConfigureAwait(false);

            _logger.LogInformation("连接状态 {Status}：在途流式请求 {InFlight} 个已按 CONNECTION_LOST 处理", snapshot.Status, _streaming.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "处理连接状态变化失败");
        }
    }

    private async Task MarkInFlightAsConnectionLostAsync()
    {
        _timeoutWatch.DisarmAll();
        _streamingStartedAt.Clear();

        var touched = 0;

        foreach (var requestId in _streaming.Clear())
        {
            // 丢弃半截流式占位：其主键是 pending_{requestId}，与重连后全量同步拉回的正式消息不同，
            // 保留会同时出现「半截 + 完整」两份内容。
            var pendingId = StreamingAccumulator.PendingMessageId(requestId);
            if (await _repository.GetMessageAsync(pendingId).ConfigureAwait(false) is not null)
            {
                _ = await _repository.DeleteMessageAsync(pendingId).ConfigureAwait(false);
                await RaiseMessageRemovedAsync(pendingId).ConfigureAwait(false);
            }

            if (await MarkSendingAsConnectionLostAsync(requestId).ConfigureAwait(false))
            {
                touched++;
            }
        }

        // 兜底：仍处于 sending 的用户消息（含本进程重启前遗留的发送中气泡）。
        var sending = await _repository.ListSendingMessagesAsync().ConfigureAwait(false);
        foreach (var row in sending)
        {
            if (await MarkSendingAsConnectionLostAsync(row.MessageId).ConfigureAwait(false))
            {
                touched++;
            }
        }

        if (touched > 0)
        {
            await RaiseNoticeAsync(NoticeConnectionLost).ConfigureAwait(false);
        }
    }

    /// <summary>把一条处于 <c>sending</c> 的用户消息标记为 <c>CONNECTION_LOST</c>。</summary>
    private async Task<bool> MarkSendingAsConnectionLostAsync(string messageId)
    {
        if (string.IsNullOrWhiteSpace(messageId))
        {
            return false;
        }

        var row = await _repository.GetMessageAsync(messageId).ConfigureAwait(false);
        if (row is null || !row.IsUser
            || !string.Equals(row.Status, ProtocolConstants.StatusSending, StringComparison.Ordinal))
        {
            return false;
        }

        _ = await _repository
            .UpdateMessageStatusAsync(messageId, ProtocolConstants.StatusError, ErrorCatalog.ConnectionLost)
            .ConfigureAwait(false);
        await RaiseRowAsync(messageId).ConfigureAwait(false);

        _logger.LogWarning("连接断开，消息标记为 CONNECTION_LOST messageId={MessageId}", messageId);
        return true;
    }

    /* ===================================================================== */
    /* 流式超时（FR-W-CHAT-8）                                                */
    /* ===================================================================== */

    private void OnStreamTimeout(string requestId) => _ = HandleStreamTimeoutAsync(requestId);

    private async Task HandleStreamTimeoutAsync(string requestId)
    {
        try
        {
            await _frameGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!_streaming.Contains(requestId))
                {
                    return;
                }

                _ = _streaming.Remove(requestId);
                _streamingStartedAt.Remove(requestId);

                var pendingId = StreamingAccumulator.PendingMessageId(requestId);
                if (await _repository.GetMessageAsync(pendingId).ConfigureAwait(false) is not null)
                {
                    _ = await _repository.DeleteMessageAsync(pendingId).ConfigureAwait(false);
                    await RaiseMessageRemovedAsync(pendingId).ConfigureAwait(false);
                }

                _ = await _repository
                    .UpdateMessageStatusAsync(requestId, ProtocolConstants.StatusError, ErrorCatalog.Timeout)
                    .ConfigureAwait(false);
                await RaiseRowAsync(requestId).ConfigureAwait(false);

                _logger.LogWarning("流式超时（150s 无新 delta）requestId={RequestId}", requestId);
                await RaiseNoticeAsync(NoticeTimeout).ConfigureAwait(false);
            }
            finally
            {
                _frameGate.Release();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "处理流式超时失败 requestId={RequestId}", requestId);
        }
    }

    /* ===================================================================== */
    /* 内部工具                                                               */
    /* ===================================================================== */

    private async Task<SendResult> CompleteSendAsync(string requestId, bool enqueued)
    {
        if (enqueued)
        {
            _ = await _repository
                .UpdateMessageStatusAsync(requestId, ProtocolConstants.StatusSent, null)
                .ConfigureAwait(false);
            await RaiseRowAsync(requestId).ConfigureAwait(false);
            return new SendResult(true, null, null);
        }

        // FR-W-CHAT-3：入队失败 → error + SEND_FAILED（可由 RetryAsync 复用同一 requestId 重发）。
        _ = await _repository
            .UpdateMessageStatusAsync(requestId, ProtocolConstants.StatusError, ErrorCatalog.SendFailed)
            .ConfigureAwait(false);
        await RaiseRowAsync(requestId).ConfigureAwait(false);

        _logger.LogWarning("消息入队失败（SEND_FAILED）requestId={RequestId}", requestId);
        return new SendResult(false, ErrorCatalog.SendFailed, ErrorCatalog.I18nKeyOf(ErrorCatalog.SendFailed));
    }

    private static ChatMessageRecord BuildUserRecord(
        string requestId,
        string content,
        bool hasAttachments,
        string status,
        string? errorCode)
    {
        var contentType = hasAttachments ? (content.Length > 0 ? "mixed" : "image") : "text";
        return new ChatMessageRecord(
            requestId,
            "user",
            contentType,
            content,
            status,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            errorCode,
            MessageType: hasAttachments ? "image" : "text");
    }

    private async Task<bool> TryBindAttachmentsAsync(string messageId, IReadOnlyList<AttachmentDraft> attachments)
    {
        var ids = attachments
            .Select(static a => a.AttachmentId)
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .ToArray();

        if (ids.Length == 0)
        {
            return false;
        }

        try
        {
            await _attachments!.BindAsync(ids, messageId).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "附件绑定失败 messageId={MessageId}", messageId);
            return false;
        }
    }

    /// <summary>
    /// 把本地图片路径读成 WS 载荷（FR-W-IMG-2：<c>mimeType</c> + <c>dataBase64</c> + 本地 <c>localUri</c>）。
    /// 读取失败的项被跳过并记警告（一张图失败不得让整条消息发不出去）。
    /// </summary>
    private IReadOnlyList<ChatImagePayloadDto> BuildImages(IEnumerable<(string Path, string? MimeType)> items)
    {
        var images = new List<ChatImagePayloadDto>();

        foreach (var (path, mimeType) in items)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            byte[]? bytes = TryReadAllBytes(path);
            if (bytes is null || bytes.Length == 0)
            {
                _logger.LogWarning("附件读取失败，已跳过 file={File}", Path.GetFileName(path));
                continue;
            }

            images.Add(new ChatImagePayloadDto
            {
                MimeType = string.IsNullOrWhiteSpace(mimeType) ? GuessMimeType(path) : mimeType,
                DataBase64 = Convert.ToBase64String(bytes),
                LocalUri = path,
            });
        }

        return images;
    }

    private byte[]? TryReadAllBytes(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _logger.LogWarning("读取附件失败 file={File}", Path.GetFileName(path));
            return null;
        }
    }

    /// <summary>按扩展名推断 MIME（草稿未给出 <c>MimeType</c> 时的回退，与服务端 VISION_* 判定集合一致）。</summary>
    private static string GuessMimeType(string path)
        => Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase)
            ? "image/png"
            : "image/jpeg";

    /// <summary>
    /// FR-W-SYNC-11：`messageKind ∈ {{interaction, interaction_failed}}` 或 <c>contentType == "interaction"</c>
    /// 的回复按互动落库（<c>content_type = 'interaction'</c>，重启后据此恢复互动气泡）。
    /// </summary>
    private static string ResolveBotContentType(ChatReplyStreamPayloadDto payload)
    {
        if (string.Equals(payload.ContentType, "interaction", StringComparison.Ordinal))
        {
            return "interaction";
        }

        if (string.Equals(payload.MessageKind, "interaction", StringComparison.Ordinal)
            || string.Equals(payload.MessageKind, "interaction_failed", StringComparison.Ordinal))
        {
            return "interaction";
        }

        return payload.ContentType switch
        {
            "text" or "image" or "mixed" => payload.ContentType,
            _ => "text",
        };
    }

    private static bool IsInteractionFailed(ChatReplyStreamPayloadDto payload)
        => string.Equals(payload.MessageKind, "interaction_failed", StringComparison.Ordinal)
           || payload.InteractionFailed == true;

    private bool IsOwnEcho(string? originDeviceId)
    {
        if (string.IsNullOrWhiteSpace(originDeviceId))
        {
            return false;
        }

        var local = LocalDeviceId();
        return local is not null && string.Equals(local, originDeviceId, StringComparison.Ordinal);
    }

    private string? LocalDeviceId()
    {
        if (_deviceId is not null)
        {
            return _deviceId;
        }

        try
        {
            // 只经窄接口取 deviceId（**不得**依赖 Core.Auth 的具体类）。
            _deviceId = _auth?.DeviceId;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug("读取本机 deviceId 失败（回声比对降级）: {Error}", ex.Message);
        }

        return _deviceId;
    }

    /// <summary>
    /// 落库行 → 渲染视图。`IsHistorySeparator` **必须**经
    /// <see cref="ProtocolConstants.IsHistorySeparator"/> 判定（C-5 / FR-W-SYNC-8）。
    /// </summary>
    private static ChatMessageView ToView(
        ChatMessageRecord record,
        string? interactionItemName = null,
        string? interactionItemIcon = null,
        bool interactionFailed = false)
        => new(
            record.MessageId,
            record.Role,
            record.ContentType,
            record.Content,
            record.Status,
            record.Timestamp,
            record.ErrorCode,
            interactionItemName,
            interactionItemIcon,
            interactionFailed,
            ProtocolConstants.IsHistorySeparator(record.Content),
            record.AttachmentPaths);

    private async Task NotifyBotReplyAsync(ChatReplyStreamPayloadDto payload, ChatMessageView first)
    {
        var greeting = string.Equals(payload.MessageKind, "greeting", StringComparison.Ordinal);
        var category = greeting ? NotificationCategory.Greeting : NotificationCategory.Chat;
        var title = I18n.T(greeting ? "notification.greeting.title" : "notification.chat.title");

        try
        {
            await _dispatcher.InvokeAsync(() =>
            {
                // FR-W-NOTI-4：带上 messageId，点击后可定位到该条消息。
                _notifications.Notify(category, title, first.Content, first.MessageId);
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "回复通知投递失败 messageId={MessageId}", first.MessageId);
        }
    }

    private Task RaiseTypingAsync(ChatTypingState state)
    {
        lock (_stateGate)
        {
            _typing = state;
        }

        return RaiseAsync(() => TypingChanged?.Invoke(this, state));
    }

    private Task RaiseAsync(Action action) => _dispatcher.InvokeAsync(action);

    private Task RaiseMessageChangedAsync(ChatMessageView view)
        => RaiseAsync(() => MessageChanged?.Invoke(this, view));

    private Task RaiseMessageRemovedAsync(string messageId)
        => RaiseAsync(() => MessageRemoved?.Invoke(this, messageId));


    private Task RaiseNoticeAsync(string i18nKey)
        => RaiseAsync(() => NoticeRaised?.Invoke(this, i18nKey));

    /// <summary>按主键重读并投递一次 <see cref="MessageChanged"/>（行不存在时静默跳过）。</summary>
    private async Task RaiseRowAsync(string messageId)
    {
        var row = await _repository.GetMessageAsync(messageId).ConfigureAwait(false);
        if (row is null)
        {
            return;
        }

        await RaiseMessageChangedAsync(ToView(row)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _wss.ConnectionChanged -= OnConnectionChanged;
        _wss.FrameReceived -= OnFrameReceived;

        _timeoutWatch.Dispose();
        _frameGate.Dispose();
    }
}
