using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using TKSDesktop.Contracts;
using TKSDesktop.Contracts.Dtos;
using TKSDesktop.Core.Platform;
using TKSDesktop.Core.Services.Delivery;
using TKSDesktop.Core.Services.Ports;

namespace TKSDesktop.Core.Services.Sync;

/// <summary>
/// 历史与记忆同步（PRD FR-W-SYNC-1..11 / 陷阱 10）。
///
/// <list type="bullet">
///   <item>新服务端以不透明游标分页；全量从 start 幂等重扫。旧服务端保留有限时间戳同步并提示。</item>
///   <item><b>落库幂等</b>（FR-W-SYNC-5）：按 <c>messageId</c> 判重，Bot 消息**复用**
///         <see cref="MessageSplitter"/> 得到 <c>{{messageId}}_{{index}}</c> 主键；</item>
///   <item><b>不截断</b>（FR-W-SYNC-10）：服务端上限 300 只是提示，本地**不得**截断；</item>
///   <item><b>失败可见</b>（FR-W-SYNC-6）：记日志**且**经 <see cref="SyncFailed"/> 让 UI 可见；</item>
///   <item><b>记忆独立游标</b>（FR-W-SYNC-7）：<see cref="ProtocolConstants.CursorKeyMemory"/> 与聊天游标互不影响；</item>
///   <item><b>历史前缀剥离</b>（FR-W-SYNC-9）：<c>【MM-DD HH:MM】</c> 前缀由
///         <see cref="HistoryText.StripTimestampPrefix"/> 剥离后才落库；</item>
///   <item><b>启动规整</b>（FR-W-CHAT-18 / EDGE-W-7）：真实调用
///         <see cref="IChatRepositoryPort.NormalizeStreamingOnStartupAsync"/>（见
///         <see cref="NormalizeOnStartupAsync"/> 与其在 <see cref="SyncFullAsync"/> /
///         <see cref="SyncIncrementalAsync"/> 中的调用点）；</item>
///   <item><b>互动行</b>（FR-W-SYNC-11）：<c>contentType == "interaction"</c> 或
///         <c>messageKind ∈ {{interaction, interaction_failed}}</c> 的历史行落库为
///         <c>content_type = 'interaction'</c>，**不得**当普通文本气泡。</item>
/// </list>
///
/// <para><b>并发</b>：同时只允许一次同步在途（重复调用复用同一任务），避免重连抖动导致并发拉取与游标竞争。</para>
///
/// <para><b>依赖</b>：只依赖窄接口。构造末尾的 <see cref="IWssPort"/> 为可选：提供时
/// 「连接层要求全量同步」标志会让增量调用自动升级为 <c>since=0</c>（FR-W-SYNC-4）。</para>
/// </summary>
public sealed class SyncService : ISyncService, IHistoryImageRestorer, IDisposable
{
    /// <summary>同步失败事件里的阶段标识（聊天历史）。</summary>
    private const string StageChat = "sync.chat";

    /// <summary>同步失败事件里的阶段标识（记忆事实）。</summary>
    private const string StageFacts = "sync.facts";

    private readonly IChatRepositoryPort _repository;
    private readonly IRestPort _rest;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<SyncService> _logger;
    private readonly IWssPort? _wss;
    private readonly IHistoryImagePort? _images;
    private readonly IAttachmentPort? _attachments;
    private readonly IFileStoragePort? _files;
    private readonly IPaths? _paths;

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>启动期孤儿流式消息规整只做一次（<c>0</c> = 未做，<c>1</c> = 已做）。</summary>
    private int _normalized;

    private bool _disposed;

    /// <summary>构造。</summary>
    /// <param name="repository">聊天落库端口（消息 / 游标 / 去重表 / 事实 / 启动规整）。</param>
    /// <param name="rest">REST 端口（<c>GET /chat/history</c> 与 <c>GET /memory/facts</c>）。</param>
    /// <param name="dispatcher">UI 线程调度（失败事件必须经此投递 —— FR-W-ARCH-3）。</param>
    /// <param name="logger">日志。</param>
    /// <param name="wss">可选的连接端口；用于读取 <c>NeedsFullSync</c>。</param>
    public SyncService(
        IChatRepositoryPort repository,
        IRestPort rest,
        IUiDispatcher dispatcher,
        ILogger<SyncService> logger,
        IWssPort? wss = null,
        IAttachmentPort? attachments = null,
        IFileStoragePort? files = null,
        IPaths? paths = null)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(rest);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(logger);

        _repository = repository;
        _rest = rest;
        _dispatcher = dispatcher;
        _logger = logger;
        _wss = wss;
        _images = rest as IHistoryImagePort;
        _attachments = attachments;
        _files = files;
        _paths = paths;
    }

    /// <inheritdoc />
    public event EventHandler<SyncFailure>? SyncFailed;

    public event EventHandler? ChatSynchronized;

    /// <summary>最近一次失败（诊断 / 自检用；成功不清空，便于排障时看到上一次失败原因）。</summary>
    public SyncFailure? LastFailure { get; private set; }

    /* ===================================================================== */
    /* 启动规整（FR-W-CHAT-18 / EDGE-W-7）                                    */
    /* ===================================================================== */

    /// <summary>
    /// 启动期孤儿流式消息规整：删除「<c>streaming</c> 且正文为空」的占位、把其余 <c>streaming</c>
    /// 提升为 <c>received</c>、删除正文为空的 <c>bot</c> 空气泡（同一事务，端口实现负责）。
    ///
    /// ⚠️ 这是一个**真实调用点**（不是死代码）：<see cref="SyncFullAsync"/> 与
    /// <see cref="SyncIncrementalAsync"/> 都会先调用它，而 <c>App/StartupCoordinator</c> 在启动流程中
    /// 必然调用 <see cref="SyncFullAsync"/>。幂等：只真正执行一次。
    /// </summary>
    /// <param name="ct">取消。</param>
    /// <returns>被改动的行数（删除 + 提升）。</returns>
    public async Task<int> NormalizeOnStartupAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _normalized, 1) == 1)
        {
            return 0;
        }

        try
        {
            var result = await _repository.NormalizeStreamingOnStartupAsync(ct).ConfigureAwait(false);
            if (result.HasChanges)
            {
                _logger.LogInformation("启动规整：删除空流式占位 {DeletedEmptyStreaming}、提升残留流式 {PromotedStreaming}、删除空 Bot 气泡 {DeletedBlankBot}", result.DeletedEmptyStreaming, result.PromotedStreaming, result.DeletedBlankBot);
            }

            return result.DeletedEmptyStreaming + result.PromotedStreaming + result.DeletedBlankBot;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 规整失败不得让启动流程崩掉；复位标志以便下次调用重试。
            Interlocked.Exchange(ref _normalized, 0);
            _logger.LogError(ex, "启动规整失败（孤儿流式消息可能残留）");
            return 0;
        }
    }

    /* ===================================================================== */
    /* 同步入口                                                               */
    /* ===================================================================== */

    /// <inheritdoc />
    public async Task<int> SyncIncrementalAsync(CancellationToken ct = default)
    {
        await NormalizeOnStartupAsync(ct).ConfigureAwait(false);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // FR-W-SYNC-4：连接层要求全量同步（重连成功 / 游标失效）时，增量调用也必须走 since=0。
            var forceFull = _wss?.NeedsFullSync == true;
            return await SyncChatAsync(full: forceFull, StageChat, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<int> SyncFullAsync(CancellationToken ct = default)
    {
        await NormalizeOnStartupAsync(ct).ConfigureAwait(false);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await SyncChatAsync(full: true, StageChat, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<int> SyncFactsAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_rest is IMemoryEventPort eventRest && _repository is IMemoryEventRepositoryPort eventRepository)
            {
                var eventCursor = await _repository.GetCursorAsync(ProtocolConstants.CursorKeyMemory, ct).ConfigureAwait(false);
                var total = 0;
                for (var pageNumber = 0; pageNumber < 100; pageNumber++)
                {
                    var pageCall = await eventRest.GetMemoryEventPageAsync(eventCursor, ProtocolConstants.SyncLimit, ct).ConfigureAwait(false);
                    if (!pageCall.Success || pageCall.Value is null)
                    {
                        await ReportFailureAsync(StageFacts, pageCall.ErrorCode, pageCall.I18nKey, ct).ConfigureAwait(false);
                        return total;
                    }
                    var page = pageCall.Value;
                    total += await eventRepository.ApplyFactPageAsync(page, ct).ConfigureAwait(false);
                    if (!page.HasMore) return total;
                    if (page.NextCursor <= eventCursor) throw new InvalidDataException("Memory event cursor did not advance");
                    eventCursor = page.NextCursor;
                }
                throw new InvalidDataException("Memory pagination exceeded 100 pages");
            }

            // FR-W-SYNC-7：记忆使用**独立游标**（与 chat 游标互不影响）。
            var localMax = await _repository.MaxFactTimestampAsync(ct).ConfigureAwait(false);
            var cursor = await _repository.GetCursorAsync(ProtocolConstants.CursorKeyMemory, ct).ConfigureAwait(false);
            var since = Math.Max(localMax, cursor);

            _logger.LogInformation("开始记忆同步 since={Since}（本地最大 {LocalMax}，游标 {Cursor}）", since, localMax, cursor);

            var call = await _rest
                .GetMemoryFactsAsync(since, ProtocolConstants.SyncLimit, ct)
                .ConfigureAwait(false);

            if (!call.Success || call.Value is null)
            {
                await ReportFailureAsync(StageFacts, call.ErrorCode, call.I18nKey, ct).ConfigureAwait(false);
                return 0;
            }

            var written = 0;
            var maxTimestamp = since;

            foreach (var fact in call.Value)
            {
                if (string.IsNullOrWhiteSpace(fact.FactId))
                {
                    continue;
                }

                await _repository
                    .UpsertFactAsync(fact.FactId, fact.UserId, fact.Fact, fact.Timestamp, ct)
                    .ConfigureAwait(false);

                written++;
                maxTimestamp = Math.Max(maxTimestamp, fact.Timestamp);
            }

            if (maxTimestamp > 0 && maxTimestamp != cursor)
            {
                await _repository.SetCursorAsync(ProtocolConstants.CursorKeyMemory, maxTimestamp, ct).ConfigureAwait(false);
            }

            _logger.LogInformation("记忆同步完成 fetched={Fetched} written={Written}", call.Value.Count, written);
            return written;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await ReportFailureAsync(StageFacts, null, ErrorCatalog.UnknownI18nKey, CancellationToken.None).ConfigureAwait(false);
            _logger.LogError(ex, "记忆同步异常");
            return 0;
        }
        finally
        {
            _gate.Release();
        }
    }

    /* ===================================================================== */
    /* 聊天历史                                                               */
    /* ===================================================================== */

    /// <summary>
    /// 聊天历史同步（FR-W-SYNC-3/4/5/10）：
    /// <c>since = full ? 0 : max(本地最大 timestamp, 游标)</c>，<c>limit = SyncLimit</c>；
    /// **本地不截断**返回条数。
    /// </summary>
    private async Task<int> SyncChatAsync(bool full, string stage, CancellationToken ct)
    {
        try
        {
            var cursor = await _repository.GetCursorAsync(ProtocolConstants.CursorKeyChat, ct).ConfigureAwait(false);
            var localMax = await _repository.MaxTimestampAsync(ct).ConfigureAwait(false);

            // FR-W-SYNC-3 / 陷阱 10：**必须**取两者的较大值。
            // 只取本地最大时间戳会让「清空会话后推进游标」形同虚设：清空后本地最大值为 0，
            // since 退回 0，下一次增量同步就把全部历史又拉回来了。
            var since = full ? 0L : Math.Max(localMax, cursor);

            _logger.LogInformation("开始历史同步 mode={Mode} since={Since}（本地最大 {LocalMax}，游标 {Cursor}）", full ? "full" : "incremental", since, localMax, cursor);

            var transport = _rest as IHistoryCursorPort;
            var checkpoints = _repository as IHistoryCheckpointPort;
            var scope = transport?.HistoryScope;
            if (transport is not null && scope is null) throw new InvalidOperationException("History sync requires authentication");
            void CheckAccount() { if (transport is not null && transport.HistoryScope != scope) throw new InvalidOperationException("History account changed"); }
            var checkpoint = full || checkpoints is null || scope is null ? "start"
                : await checkpoints.GetHistoryStateAsync(scope, ct).ConfigureAwait(false) ?? "start";
            var written = 0;
            var resets = 0;
            while (true)
            {
                CheckAccount();
                var call = transport is not null && checkpoints is not null
                    ? await transport.GetHistoryCursorPageAsync(checkpoint, ProtocolConstants.SyncLimit, ct).ConfigureAwait(false)
                    : await _rest.GetChatHistoryPageAsync(since, ProtocolConstants.SyncLimit, ct).ConfigureAwait(false);
                CheckAccount();
                if (!call.Success || call.Value is null)
                {
                    if (call.ErrorCode == "40910" && resets++ == 0) { checkpoint = "start"; continue; }
                    await ReportFailureAsync(stage, call.ErrorCode, call.I18nKey, ct).ConfigureAwait(false);
                    return written;
                }
                var limited = call.Value.HistorySyncVersion != 1;
                if (limited && transport is not null && since > 0)
                {
                    call = await _rest.GetChatHistoryPageAsync(since, ProtocolConstants.SyncLimit, ct).ConfigureAwait(false);
                    CheckAccount();
                    if (!call.Success || call.Value is null) { await ReportFailureAsync(stage, call.ErrorCode, call.I18nKey, ct).ConfigureAwait(false); return written; }
                }
                if (!limited && (string.IsNullOrEmpty(call.Value.NextCursor) || string.IsNullOrEmpty(call.Value.DatasetGeneration)))
                    throw new InvalidDataException("Missing history checkpoint");
                var items = call.Value.Items;
                var maxTimestamp = since;

                foreach (var item in items)
                {
                    CheckAccount();
                    var authorityKey = $"{scope}:sql:{item.MessageId}";
                    if (scope is not null && checkpoints is not null && item.HistorySource == "legacy"
                        && await checkpoints.GetHistoryStateAsync(authorityKey, ct).ConfigureAwait(false) is not null) continue;
                    written += await IngestTimelineItemAsync(item, ct).ConfigureAwait(false);
                    await RestoreHistoryImagesAsync(item, ct, () => transport is null || transport.HistoryScope == scope).ConfigureAwait(false);
                    CheckAccount();
                    if (scope is not null && checkpoints is not null && item.HistorySource == "sql")
                        await checkpoints.SetHistoryStateAsync(authorityKey, "1", ct).ConfigureAwait(false);
                    maxTimestamp = Math.Max(maxTimestamp, item.Timestamp);
                }

                foreach (var operation in call.Value.Operations.Where(value => value.Status == "failed"))
                {
                    foreach (var source in operation.Sources)
                    {
                        CheckAccount();
                        if (await _repository.GetMessageAsync(source.MessageId, ct).ConfigureAwait(false) is null)
                        {
                            if (limited && source.Timestamp <= since) continue;
                            written += await IngestTimelineItemAsync(source, ct).ConfigureAwait(false);
                        }
                        await _repository.UpdateMessageStatusAsync(source.MessageId, ProtocolConstants.StatusError, "RESEND_NEW_REQUEST", ct).ConfigureAwait(false);
                        await RestoreHistoryImagesAsync(source, ct, () => transport is null || transport.HistoryScope == scope).ConfigureAwait(false);
                    }
                    foreach (var requestId in operation.RequestIds)
                    {
                        await _repository.UpdateMessageStatusAsync(requestId, ProtocolConstants.StatusError, "RESEND_NEW_REQUEST", ct).ConfigureAwait(false);
                        _ = await _repository.DeleteMessageAsync("pending_" + requestId, ct).ConfigureAwait(false);
                    }
                }

                // ⚠️ FR-W-SYNC-10：本地**不得**按 300 截断；服务端条数上限只是提示。
                CheckAccount();
                if (limited)
                {
                    if (maxTimestamp > 0 && maxTimestamp != cursor)
                        await _repository.SetCursorAsync(ProtocolConstants.CursorKeyChat, maxTimestamp, ct).ConfigureAwait(false);
                    await ReportFailureAsync(stage, null, "sync.limited", ct).ConfigureAwait(false);
                    break;
                }
                // 现有异步端口逐条提交；检查点最后写，崩溃只能重放。
                if (checkpoints is not null && scope is not null)
                {
                    await checkpoints.SetHistoryStateAsync(scope, call.Value.NextCursor!, ct).ConfigureAwait(false);
                    await checkpoints.SetHistoryStateAsync($"{scope}:generation", call.Value.DatasetGeneration!, ct).ConfigureAwait(false);
                }
                checkpoint = call.Value.NextCursor!;
                if (call.Value.Blocked)
                {
                    await ReportFailureAsync(stage, null, "sync.waiting", ct).ConfigureAwait(false);
                    await _dispatcher.InvokeAsync(() => ChatSynchronized?.Invoke(this, EventArgs.Empty)).ConfigureAwait(false);
                    await Task.Delay(Math.Clamp(call.Value.PollAfterMs, 1000, 30000), ct).ConfigureAwait(false);
                    continue;
                }
                if (!call.Value.HasMore) break;
            }

            _logger.LogInformation("历史同步完成 mode={Mode} written={Written}", full ? "full" : "incremental", written);

            await _dispatcher.InvokeAsync(() => ChatSynchronized?.Invoke(this, EventArgs.Empty)).ConfigureAwait(false);

            return written;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // FR-W-SYNC-6：失败必须记日志**且**让 UI 可见。
            _logger.LogError(ex, "历史同步异常 mode={Mode}", full ? "full" : "incremental");
            await ReportFailureAsync(stage, null, ErrorCatalog.UnknownI18nKey, CancellationToken.None).ConfigureAwait(false);
            return 0;
        }
    }

    /// <summary>
    /// 落库一条历史行（FR-W-SYNC-5/8/9/11）。返回本地写入（新建或更新）的条数。
    /// </summary>
    private async Task<int> IngestTimelineItemAsync(TimelineItemDto item, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(item.MessageId))
        {
            return 0;
        }

        // FR-W-SYNC-9：剥离服务端写入的 `【MM-DD HH:MM】` 前缀（它只用于喂模型）。
        var content = HistoryText.StripTimestampPrefix(item.Content);

        // 角色未知的历史行按忽略处理（NFR-W-12：不得因未知值中断同步）。
        var role = item.Role switch
        {
            "user" => "user",
            "bot" => "bot",
            "system" => "system",
            _ => null,
        };

        if (role is null)
        {
            _logger.LogDebug("忽略未知角色的历史行 role={Role} messageId={MessageId}", item.Role, item.MessageId);
            return 0;
        }

        var timestamp = item.Timestamp > 0 ? item.Timestamp : 0;

        // C-5 / FR-W-SYNC-8：历史分隔标记是**分隔线**，绝不按正文拆分、只在末尾补一条。
        if (ProtocolConstants.IsHistorySeparator(content))
        {
            var separator = new ChatMessageRecord(
                item.MessageId,
                role,
                "text",
                ProtocolConstants.HistorySeparator,
                ProtocolConstants.StatusReceived,
                timestamp);

            var outcome = await _repository.UpsertMessageAsync(separator, ct).ConfigureAwait(false);
            return outcome == MessageWriteOutcome.Unchanged ? 0 : 1;
        }

        var contentType = ResolveHistoryContentType(item);

        if (string.Equals(role, "bot", StringComparison.Ordinal))
        {
            // FR-W-SYNC-5：**必须**复用 MessageSplitter，与实时链路产出同一套 `{messageId}_{index}` 主键。
            var segments = MessageSplitter.Split(item.MessageId, content, timestamp);
            var segmentsWritten = 0;

            foreach (var segment in segments)
            {
                // FR-W-INT-12 / EDGE-W-21：三路到达去重（WS / 降级 REST / 历史补拉）。
                if (item.HistorySource != "sql" && await _repository.IsDeliveredAsync(segment.MessageId, ct).ConfigureAwait(false))
                {
                    continue;
                }

                var record = new ChatMessageRecord(
                    segment.MessageId,
                    "bot",
                    contentType,
                    segment.Content,
                    ProtocolConstants.StatusReceived,
                    segment.Timestamp,
                    ModelProvider: item.ModelProvider) { Authoritative = item.HistorySource == "sql" };

                var outcome = await _repository.UpsertMessageAsync(record, ct).ConfigureAwait(false);
                await _repository.MarkDeliveredAsync([segment.MessageId], ct).ConfigureAwait(false);

                if (outcome != MessageWriteOutcome.Unchanged)
                {
                    segmentsWritten++;
                }
            }

            return segmentsWritten;
        }

        // 用户行：按主键就地 UPDATE（把本地 error 修复为 sent —— 这正是必须区分 Updated/Unchanged 的理由）。
        var userRecord = new ChatMessageRecord(
            item.MessageId,
            role,
            contentType,
            content,
            ProtocolConstants.StatusSent,
            timestamp,
            ModelProvider: item.ModelProvider,
            MessageType: item.ImageCount > 0 ? "image" : "text") { Authoritative = item.HistorySource == "sql" };

        var userOutcome = await _repository.UpsertMessageAsync(userRecord, ct).ConfigureAwait(false);
        return userOutcome == MessageWriteOutcome.Unchanged ? 0 : 1;
    }

    /// <summary>
    /// FR-W-SYNC-11：历史互动行的 <c>contentType</c> 为 <c>interaction</c>，
    /// 或 <c>messageKind ∈ {{interaction, interaction_failed}}</c>（失败的互动历史形态）。
    /// 两者都必须落库为 <c>interaction</c>，否则重启后互动气泡会退化成普通文本。
    /// </summary>
    private static string ResolveHistoryContentType(TimelineItemDto item)
    {
        if (string.Equals(item.ContentType, "interaction", StringComparison.Ordinal)
            || string.Equals(item.MessageKind, "interaction", StringComparison.Ordinal)
            || string.Equals(item.MessageKind, "interaction_failed", StringComparison.Ordinal))
        {
            return "interaction";
        }

        if (item.ImageCount > 0 && item.ContentType is not ("image" or "mixed"))
        {
            return string.IsNullOrWhiteSpace(item.Content) || item.Content == ProtocolConstants.ImagePlaceholder ? "image" : "mixed";
        }

        return item.ContentType switch
        {
            "text" or "image" or "mixed" => item.ContentType,
            _ => "text",
        };
    }

    /// <summary>Restore exact original bytes after the user row exists, then bind through the normal attachment table.</summary>
    private async Task RestoreHistoryImagesAsync(TimelineItemDto item, CancellationToken ct, Func<bool>? stillCurrent = null)
    {
        if (item.Role != "user" || string.IsNullOrWhiteSpace(item.RequestId) || item.ImageCount == 0)
        {
            return;
        }
        if (item.ImageCount < 0 || item.ImageCount > ProtocolConstants.MaxImageCount)
        {
            throw new InvalidOperationException("invalid history image count");
        }
        if (_images is null || _attachments is null || _files is null || _paths is null)
        {
            throw new InvalidOperationException("history image restore is not configured");
        }

        var attached = await _attachments.ListByMessageAsync(item.MessageId, ct).ConfigureAwait(false);
        var existing = new List<AttachmentDraft>();
        foreach (var attachment in attached)
        {
            if (_files.FileExists(attachment.LocalPath)) existing.Add(attachment);
            else await _attachments.DeleteAsync(attachment.AttachmentId, ct).ConfigureAwait(false);
        }
        if (existing.Count >= item.ImageCount) return;

        var localHashes = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var attachment in existing)
        {
            var localBytes = _files.TryReadAllBytes(attachment.LocalPath);
            if (localBytes is null) continue;
            var hash = Convert.ToHexString(SHA256.HashData(localBytes));
            localHashes[hash] = localHashes.GetValueOrDefault(hash) + 1;
        }

        _files.CreateDirectory(_paths.AttachmentsDir);
        for (var index = 0; index < item.ImageCount; index++)
        {
            var image = await _images.GetHistoryImageAsync(item.RequestId, index, ct).ConfigureAwait(false);
            if (!image.Success || image.Value is null)
            {
                throw new InvalidOperationException($"history image download failed at index {index}");
            }
            if (stillCurrent?.Invoke() == false) throw new InvalidOperationException("History image account changed");
            var bytes = image.Value.Bytes;
            var mime = image.Value.MimeType;
            var valid = mime switch
            {
                "image/png" => bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
                "image/jpeg" => bytes.Length >= 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff,
                _ => false,
            };
            if (!valid || bytes.Length > ProtocolConstants.MaxImageBytes)
            {
                throw new InvalidOperationException("invalid history image response");
            }
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            if (localHashes.GetValueOrDefault(hash) > 0)
            {
                localHashes[hash]--;
                continue;
            }
            if (_files.GetAvailableFreeSpaceBytes(_paths.AttachmentsDir)
                < ProtocolConstants.AttachmentFreeSpaceReserveBytes + bytes.Length)
            {
                throw new IOException("insufficient attachment storage");
            }

            var destination = Path.Combine(_paths.AttachmentsDir,
                $"history-{Guid.NewGuid():N}.{(mime == "image/png" ? "png" : "jpg")}");
            _files.WriteAllBytes(destination, bytes);
            try
            {
                var draft = await _attachments.InsertDraftAsync(mime, destination, bytes.Length, null, null, ct)
                    .ConfigureAwait(false);
                try
                {
                    await _attachments.BindAsync([draft.AttachmentId], item.MessageId, ct).ConfigureAwait(false);
                }
                catch
                {
                    await _attachments.DeleteAsync(draft.AttachmentId, CancellationToken.None).ConfigureAwait(false);
                    throw;
                }
            }
            catch
            {
                _files.TryDeleteFile(destination);
                throw;
            }
        }
    }

    public async Task RestoreEchoImagesAsync(string requestId, string messageId, int imageCount, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await RestoreHistoryImagesAsync(new TimelineItemDto
            {
                RequestId = requestId,
                MessageId = messageId,
                ImageCount = imageCount,
                Role = "user",
            }, ct).ConfigureAwait(false);
            await _dispatcher.InvokeAsync(() => ChatSynchronized?.Invoke(this, EventArgs.Empty)).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /* ===================================================================== */
    /* 失败上报（FR-W-SYNC-6）                                                */
    /* ===================================================================== */

    private async Task ReportFailureAsync(string stage, string? errorCode, string i18nKey, CancellationToken ct)
    {
        var failure = new SyncFailure(stage, errorCode, i18nKey);
        LastFailure = failure;

        _logger.LogWarning("同步失败 stage={Stage} code={Code} i18n={I18n}", stage, errorCode ?? "<none>", i18nKey);

        try
        {
            await _dispatcher.InvokeAsync(() => SyncFailed?.Invoke(this, failure)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "同步失败事件投递失败 stage={Stage}", stage);
        }
    }

    /* ===================================================================== */

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gate.Dispose();
    }
}
