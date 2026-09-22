using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace TKSDesktop.App;

/// <summary>
/// JSON 行日志 Provider（PRD FR-W-LOG-1 / FR-W-LOG-3 / NFR-W-7）。
///
/// <list type="bullet">
///   <item>每行一个 JSON 对象；至少含 `ts`（ISO 8601 本地时间）/ `level` / `scope` / `message`，
///         以及上下文字段（`traceId` / `deviceId` / `status` / `code` 等）；</item>
///   <item>按天滚动，保留 <see cref="Contracts.ProtocolConstants.LogRetentionDays"/> 天，单文件上限 10MB；</item>
///   <item>写入失败（磁盘满 / 权限）**不得**导致应用崩溃，仅降级为静默丢弃并计数；</item>
///   <item>**不得**上报到任何外部服务（FR-W-LOG-4）。</item>
/// </list>
///
/// ⚠️ 写入前**必须**经 <see cref="LogRedactor"/> 过滤（FR-W-LOG-2 硬约束）。
/// </summary>
public sealed class JsonLineFileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly object _gate = new();
    private readonly Dictionary<string, object?> _scope = new(StringComparer.Ordinal);

    private string? _currentDate;
    private StreamWriter? _writer;
    private long _currentSize;
    private long _droppedCount;
    private int _partIndex;
    private bool _disposed;

    public JsonLineFileLoggerProvider(string directory)
    {
        _directory = directory;
    }

    /// <summary>因写入失败被丢弃的日志条数（供自检断言「不崩溃」）。</summary>
    public long DroppedCount
    {
        get
        {
            lock (_gate)
            {
                return _droppedCount;
            }
        }
    }

    /// <summary>设置全局上下文字段（如 `deviceId`）。</summary>
    public void SetScopeField(string key, object? value)
    {
        lock (_gate)
        {
            if (value is null)
            {
                _scope.Remove(key);
            }
            else
            {
                _scope[key] = value;
            }
        }
    }

    public ILogger CreateLogger(string categoryName) => new JsonLineLogger(this, categoryName);

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _writer?.Flush();
            _writer?.Dispose();
            _writer = null;
        }
    }

    internal void Write(
        LogLevel level,
        string category,
        EventId eventId,
        string message,
        Exception? exception,
        IReadOnlyList<KeyValuePair<string, object?>> fields)
    {
        // 脱敏（FR-W-LOG-2）：先合并上下文字段，再统一过滤。
        var merged = new List<KeyValuePair<string, object?>>();
        lock (_gate)
        {
            foreach (var kv in _scope)
            {
                merged.Add(kv);
            }
        }

        merged.AddRange(fields);
        merged.Add(new KeyValuePair<string, object?>("scope", category));
        if (eventId.Id != 0)
        {
            merged.Add(new KeyValuePair<string, object?>("eventId", eventId.Id));
        }

        var redacted = LogRedactor.Redact(merged);

        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["ts"] = DateTimeOffset.Now.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture),
            ["level"] = level.ToString(),
            ["message"] = LogRedactor.RedactString(message),
        };

        foreach (var (key, value) in redacted)
        {
            // scope 单独写入，避免与业务字段同名冲突。
            payload[key] = value;
        }

        if (exception is not null)
        {
            payload["exception"] = LogRedactor.RedactText(exception.ToString());
        }

        var line = JsonSerializer.Serialize(payload, JsonOptions);

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                EnsureWriter(level);
                if (_writer is null)
                {
                    _droppedCount++;
                    return;
                }

                _writer.WriteLine(line);
                _currentSize += Encoding.UTF8.GetByteCount(line) + 1;

                if (_currentSize >= Contracts.ProtocolConstants.LogMaxFileBytes)
                {
                    RotatePart();
                }

                // 每条都 flush：崩溃时不丢最后一段日志（量小，性能可接受）。
                _writer.Flush();
            }
            catch (Exception)
            {
                // FR-W-LOG-3：写入失败不得崩溃，仅降级为静默丢弃并计数。
                _droppedCount++;
            }
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    private void EnsureWriter(LogLevel level)
    {
        var today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (_writer is not null && _currentDate == today)
        {
            return;
        }

        _writer?.Flush();
        _writer?.Dispose();
        _writer = null;
        _currentDate = today;
        _partIndex = 0;
        _currentSize = 0;

        Directory.CreateDirectory(_directory);
        CleanupOldFiles();

        var path = Path.Combine(_directory, $"tks-{today}.jsonl");
        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read), Encoding.UTF8);
        _currentSize = new FileInfo(path).Length;
    }

    private void RotatePart()
    {
        if (_writer is null || _currentDate is null)
        {
            return;
        }

        _writer.Flush();
        _writer.Dispose();
        _partIndex++;
        var path = Path.Combine(_directory, $"tks-{_currentDate}.{_partIndex}.jsonl");
        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read), Encoding.UTF8);
        _currentSize = 0;
    }

    private void CleanupOldFiles()
    {
        try
        {
            var cutoff = DateTime.Now.Date.AddDays(-Contracts.ProtocolConstants.LogRetentionDays);
            foreach (var file in Directory.EnumerateFiles(_directory, "tks-*.jsonl"))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                // tks-yyyy-MM-dd[.part]
                var parts = name.Split('.');
                if (parts.Length >= 2
                    && DateTime.TryParseExact(parts[1], "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var date)
                    && date < cutoff)
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception)
        {
            // 清理失败不影响主流程。
        }
    }

    private sealed class JsonLineLogger : ILogger
    {
        private readonly JsonLineFileLoggerProvider _provider;
        private readonly string _category;

        public JsonLineLogger(JsonLineFileLoggerProvider provider, string category)
        {
            _provider = provider;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel) || formatter is null)
            {
                return;
            }

            var message = formatter(state, exception);
            var fields = new List<KeyValuePair<string, object?>>();
            if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
            {
                foreach (var (key, value) in pairs)
                {
                    if (key == "{OriginalFormat}")
                    {
                        continue;
                    }

                    fields.Add(new KeyValuePair<string, object?>(key, value));
                }
            }

            _provider.Write(logLevel, _category, eventId, message, exception, fields);
        }
    }
}
