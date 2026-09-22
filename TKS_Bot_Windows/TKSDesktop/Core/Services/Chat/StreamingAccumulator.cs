using System.Globalization;
using Microsoft.Extensions.Logging;
using TKSDesktop.Contracts;

namespace TKSDesktop.Core.Services.Chat;

/// <summary>流式会话在途状态（纯数据，无行为）。</summary>
/// <param name="RequestId">顶层 <c>requestId</c>（占位主键为 <c>pending_{requestId}</c>）。</param>
/// <param name="Content">已累积的 delta 文本（<c>done=true</c> 缺 <c>finalContent</c> 时的回退来源）。</param>
/// <param name="ContentType">帧内 <c>contentType</c>（默认 <c>text</c>）。</param>
/// <param name="ModelProvider">帧内 <c>modelProvider</c>。</param>
/// <param name="Deadline">下一次超时判定的截止时刻（每收到 delta 重置为 <c>now + 150s</c>）。</param>
public sealed record StreamingEntry(
    string RequestId,
    string Content,
    string ContentType,
    string? ModelProvider,
    DateTimeOffset Deadline);

/// <summary>
/// 流式聚合与 150 秒重置超时（PRD FR-W-CHAT-5 / FR-W-CHAT-8 / EDGE-W-7）。
///
/// <para><b>纯状态机 + 可注入时钟</b>：所有时间判断都由调用方传入的 <see cref="DateTimeOffset"/>
/// 或 <see cref="TimeProvider.GetUtcNow"/> 决定，因此 <b>不需要真实等待 150 秒</b>即可单测
/// （V-W-B22 要求的测试时钟注入）。真正的定时器只在 <see cref="StreamingTimeoutWatch"/> 中，
/// 它同样由 <see cref="TimeProvider"/> 驱动。</para>
///
/// <para><b>规则</b>：
/// <list type="bullet">
///   <item>首个**非空** delta → 需要创建占位 <c>pending_{requestId}</c>（状态 <c>streaming</c>）；</item>
///   <item>每收到一个 delta → 截止时刻重置为 <c>now + <see cref="ProtocolConstants.StreamingTimeoutMs"/></c>；</item>
///   <item><c>now &gt;= Deadline</c> → 超时，调用方标 <c>TIMEOUT</c> 并提示 <c>chat.timeout</c>。</item>
/// </list></para>
///
/// <para><b>并发</b>：本类不是线程安全的。帧派发与超时回调都必须在同一串行上下文内调用
/// （<c>ChatService</c> 用 <see cref="SemaphoreSlim"/> 串行化），避免竞态。
/// 这也是刻意的：把「加锁」集中在服务层一处，而非散落在状态机里。</para>
/// </summary>
public sealed class StreamingAccumulator
{
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<string, StreamingEntry> _entries = new(StringComparer.Ordinal);

    /// <summary>构造。</summary>
    /// <param name="timeProvider">时钟（测试注入假时钟以断言 150s 语义）。</param>
    public StreamingAccumulator(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
    }

    /// <summary>在途请求数。</summary>
    public int Count => _entries.Count;

    /// <summary>当前全部在途 <c>requestId</c>（连接断开时逐个标记 <c>CONNECTION_LOST</c>）。</summary>
    public IReadOnlyList<string> ActiveRequestIds => [.. _entries.Keys];

    /// <summary>占位消息主键 <c>pending_{requestId}</c>。</summary>
    public static string PendingMessageId(string requestId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        return string.Create(CultureInfo.InvariantCulture, $"pending_{requestId}");
    }

    /// <summary>是否已在聚合该 <paramref name="requestId"/>。</summary>
    public bool Contains(string requestId) => _entries.ContainsKey(requestId);

    /// <summary>读取在途条目；不存在返回 <c>null</c>。</summary>
    public StreamingEntry? Get(string requestId)
        => _entries.TryGetValue(requestId, out var entry) ? entry : null;

    /// <summary>
    /// 追加一个 delta。
    /// </summary>
    /// <param name="requestId">顶层 <c>requestId</c>。</param>
    /// <param name="delta">增量文本（**空 / 全空白时不做任何事**：不得为空白 delta 建占位）。</param>
    /// <param name="contentType">帧内 <c>contentType</c>（<c>null</c> → <c>text</c>）。</param>
    /// <param name="modelProvider">帧内 <c>modelProvider</c>。</param>
    /// <returns>
    /// <see langword="true"/> 表示**本次是该 <paramref name="requestId"/> 的首个非空 delta**，
    /// 调用方必须创建占位消息并发 <c>MessageChanged</c>。
    /// </returns>
    public bool Append(string requestId, string? delta, string? contentType, string? modelProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);

        if (string.IsNullOrEmpty(delta) || string.IsNullOrWhiteSpace(delta))
        {
            return false;
        }

        var now = _timeProvider.GetUtcNow();
        var deadline = now + TimeSpan.FromMilliseconds(ProtocolConstants.StreamingTimeoutMs);

        if (_entries.TryGetValue(requestId, out var existing))
        {
            _entries[requestId] = existing with
            {
                Content = string.Concat(existing.Content, delta),
                ContentType = NormalizeContentType(contentType) ?? existing.ContentType,
                ModelProvider = modelProvider ?? existing.ModelProvider,
                Deadline = deadline,
            };

            return false;
        }

        _entries[requestId] = new StreamingEntry(
            requestId,
            delta,
            NormalizeContentType(contentType) ?? "text",
            modelProvider,
            deadline);

        return true;
    }

    /// <summary>
    /// 判定是否已超时（<c>now &gt;= Deadline</c>）。
    /// **不**移除条目 —— 调用方需先读出累积内容再调 <see cref="Remove"/>，
    /// 否则「超时提示」会丢掉已收到的半截文本上下文。
    /// </summary>
    public bool IsExpired(string requestId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        return _entries.TryGetValue(requestId, out var entry)
               && _timeProvider.GetUtcNow() >= entry.Deadline;
    }

    /// <summary>距超时还剩多久（已超时时为 <see cref="TimeSpan.Zero"/>）；无该条目时返回 <c>null</c>。</summary>
    public TimeSpan? Remaining(string requestId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        if (!_entries.TryGetValue(requestId, out var entry))
        {
            return null;
        }

        var remaining = entry.Deadline - _timeProvider.GetUtcNow();
        return remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;
    }

    /// <summary>
    /// `done=true` 收尾：移除并返回累积条目（调用方据此回退到已累积内容）。
    /// 无该条目时返回 <c>null</c>（例如仅收到 <c>done</c> 而从无 delta 的极短回复）。
    /// </summary>
    public StreamingEntry? Complete(string requestId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        if (!_entries.Remove(requestId, out var entry))
        {
            return null;
        }

        return entry;
    }

    /// <summary>移除一个在途条目（超时 / 断线 / 服务端错误后清理）。</summary>
    public bool Remove(string requestId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        return _entries.Remove(requestId);
    }

    /// <summary>清空全部在途条目（清空会话时）。返回被清掉的 requestId 列表。</summary>
    public IReadOnlyList<string> Clear()
    {
        var ids = new List<string>(_entries.Keys);
        _entries.Clear();
        return ids;
    }

    /// <summary>把帧内 <c>contentType</c> 规范化为 PRD §9.1 的取值集合（未知值回落 <c>text</c>）。</summary>
    private static string? NormalizeContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return null;
        }

        return contentType switch
        {
            "text" or "image" or "mixed" or "interaction" => contentType,
            _ => "text",
        };
    }
}
///
/// <para>每个 <c>requestId</c> 拥有**自己的**计时器：<see cref="Arm"/> 重设该 id 的截止时刻
/// （这正是「每收到一个 delta 重置计时」的落地形态），<see cref="Disarm"/> 取消它。
/// 用「一 id 一计时器」而非「单个全局计时器」是必须的 —— 服务端允许**并发**的流式回复
/// （同账号多设备、防抖尾批），单计时器会让后到的请求顶掉前一个的超时保护，
/// 前一个就永远停在 <c>streaming</c> 且无人回收。</para>
///
/// <para>用 <see cref="TimeProvider.CreateTimer"/> 而非直接 <c>System.Threading.Timer</c>，
/// 因此注入假时钟即可在单测里「推进 150 秒」而不真实等待。</para>
/// </summary>
public sealed class StreamingTimeoutWatch : IDisposable
{
    private readonly TimeProvider _timeProvider;
    private readonly Action<string> _onTimeout;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, ITimer> _timers = new(StringComparer.Ordinal);

    private bool _disposed;

    /// <summary>构造。</summary>
    /// <param name="timeProvider">时钟。</param>
    /// <param name="onTimeout">超时回调（参数为 requestId）。回调内部抛异常不会击穿计时器线程。</param>
    /// <param name="logger">日志（超时是异常路径，必须可见）。</param>
    public StreamingTimeoutWatch(TimeProvider timeProvider, Action<string> onTimeout, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(onTimeout);
        ArgumentNullException.ThrowIfNull(logger);

        _timeProvider = timeProvider;
        _onTimeout = onTimeout;
        _logger = logger;
    }

    /// <summary>当前受计时保护的 <c>requestId</c> 数量（自检 / 排障用）。</summary>
    public int ArmedCount
    {
        get
        {
            lock (_gate)
            {
                return _timers.Count;
            }
        }
    }

    /// <summary>
    /// （重新）为 <paramref name="requestId"/> 计时：
    /// <see cref="ProtocolConstants.StreamingTimeoutMs"/> 后触发超时。已有计时被取消（150s 重置）。
    /// </summary>
    public void Arm(string requestId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (_timers.TryGetValue(requestId, out var existing))
            {
                existing.Change(
                    TimeSpan.FromMilliseconds(ProtocolConstants.StreamingTimeoutMs),
                    Timeout.InfiniteTimeSpan);
                return;
            }

            var timer = _timeProvider.CreateTimer(
                static state =>
                {
                    var (watch, id) = ((StreamingTimeoutWatch Watch, string RequestId))state!;
                    watch.OnTimer(id);
                },
                (this, requestId),
                TimeSpan.FromMilliseconds(ProtocolConstants.StreamingTimeoutMs),
                Timeout.InfiniteTimeSpan);

            _timers[requestId] = timer;
        }
    }

    /// <summary>取消并移除 <paramref name="requestId"/> 的计时（<c>done=true</c>、断线、服务端错误时调用）。</summary>
    public void Disarm(string requestId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);

        lock (_gate)
        {
            if (_timers.Remove(requestId, out var timer))
            {
                timer.Dispose();
            }
        }
    }

    /// <summary>取消全部计时（清空会话 / 断开连接）。</summary>
    public void DisarmAll()
    {
        lock (_gate)
        {
            foreach (var timer in _timers.Values)
            {
                timer.Dispose();
            }

            _timers.Clear();
        }
    }

    /// <summary>释放全部计时器。</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var timer in _timers.Values)
            {
                timer.Dispose();
            }

            _timers.Clear();
        }
    }

    private void OnTimer(string requestId)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            // 单次计时器已到期：移除引用，避免重复回调。
            if (_timers.Remove(requestId, out var timer))
            {
                timer.Dispose();
            }
        }

        try
        {
            _onTimeout(requestId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 回调失败不得让计时器线程崩掉（否则后续所有请求都失去超时保护）。
            _logger.LogWarning(ex, "流式超时回调抛出异常，已吞掉 requestId={RequestId}", requestId);
        }
    }
}

