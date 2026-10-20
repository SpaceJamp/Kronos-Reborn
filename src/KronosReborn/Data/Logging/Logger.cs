using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace KronosReborn.Data.Logging;

/// <summary>
/// Severity levels for log entries, ordered from least to most severe.
/// </summary>
public enum LogLevel
{
    Trace = 0,
    Debug = 1,
    Information = 2,
    Warning = 3,
    Error = 4,
    Critical = 5,
}

/// <summary>
/// A single structured log entry with full provenance and optional structured metadata.
/// </summary>
public sealed record LogEntry(
    Guid Id,
    DateTimeOffset Timestamp,
    LogLevel Level,
    string Message,
    string? Category,
    string? CorrelationId,
    string? Source,
    IReadOnlyDictionary<string, object?> Properties,
    string? ExceptionType,
    string? ExceptionMessage,
    string? ExceptionStackTrace);

/// <summary>
/// Builder for constructing log entries with optional structured properties.
/// </summary>
public sealed class LogEntryBuilder
{
    private readonly LogLevel _level;
    private readonly string _message;
    private readonly string? _category;
    private readonly string? _correlationId;
    private readonly string? _source;
    private readonly Dictionary<string, object?> _properties = new();

    internal LogEntryBuilder(LogLevel level, string message, string? category, string? correlationId, string? source)
    {
        _level = level;
        _message = message;
        _category = category;
        _correlationId = correlationId;
        _source = source;
    }

    /// <summary>Adds a structured property to the log entry.</summary>
    public LogEntryBuilder WithProperty(string name, object? value)
    {
        _properties[name] = value;
        return this;
    }

    internal LogEntry Build(string? exceptionType, string? exceptionMessage, string? exceptionStackTrace)
    {
        return new LogEntry(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            _level,
            _message,
            _category,
            _correlationId,
            _source,
            _properties,
            exceptionType,
            exceptionMessage,
            exceptionStackTrace);
    }
}

/// <summary>
/// Destination for log entries. Implementations write entries to files, consoles,
/// network endpoints, or in-memory buffers.
/// </summary>
public interface ILogSink
{
    /// <summary>Writes a log entry to the sink.</summary>
    void Write(LogEntry entry);

    /// <summary>Flushes any buffered writes.</summary>
    void Flush();

    /// <summary>Minimum level the sink accepts.</summary>
    LogLevel MinimumLevel { get; }
}

/// <summary>
/// Writes log entries as JSON lines to a file, with automatic rotation when the file
/// exceeds a size limit.
/// </summary>
public sealed class FileLogSink : ILogSink, IDisposable
{
    private readonly string _filePath;
    private readonly long _maxFileSizeBytes;
    private readonly int _maxRetainedFiles;
    private readonly object _lock = new();
    private long _currentSizeBytes;
    private int _currentFileIndex;
    private StreamWriter? _writer;

    public FileLogSink(string filePath, long maxFileSizeBytes = 10_000_000, int maxRetainedFiles = 5)
    {
        _filePath = filePath;
        _maxFileSizeBytes = maxFileSizeBytes;
        _maxRetainedFiles = maxRetainedFiles;
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        OpenWriter();
    }

    public LogLevel MinimumLevel => LogLevel.Trace;

    public void Write(LogEntry entry)
    {
        lock (_lock)
        {
            var line = JsonSerializer.Serialize(entry) + Environment.NewLine;
            var bytes = Encoding.UTF8.GetByteCount(line);

            if (_currentSizeBytes + bytes > _maxFileSizeBytes)
            {
                Rotate();
            }

            _writer!.Write(line);
            _writer.Flush();
            _currentSizeBytes += bytes;
        }
    }

    public void Flush()
    {
        lock (_lock)
        {
            _writer?.Flush();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    private void OpenWriter()
    {
        var path = GetFilePath(_currentFileIndex);
        _writer = new StreamWriter(path, append: true, Encoding.UTF8);
        _currentSizeBytes = new FileInfo(path).Length;
    }

    private void Rotate()
    {
        _writer?.Dispose();
        _writer = null;

        // Shift existing files
        for (int i = _maxRetainedFiles - 1; i > 0; i--)
        {
            var src = GetFilePath(i - 1);
            var dst = GetFilePath(i);
            if (File.Exists(src))
            {
                if (File.Exists(dst)) File.Delete(dst);
                File.Move(src, dst);
            }
        }

        _currentFileIndex = 0;
        OpenWriter();
    }

    private string GetFilePath(int index) => index == 0
        ? _filePath
        : $"{_filePath}.{index}";
}

/// <summary>
/// Writes log entries to an in-memory collection for UI display.
/// </summary>
public sealed class MemoryLogSink : ILogSink
{
    private readonly int _maxEntries;
    private readonly ConcurrentQueue<LogEntry> _entries = new();

    public MemoryLogSink(int maxEntries = 5000)
    {
        _maxEntries = maxEntries;
    }

    public LogLevel MinimumLevel => LogLevel.Trace;

    public IReadOnlyList<LogEntry> Entries => _entries.ToArray();

    public void Write(LogEntry entry)
    {
        _entries.Enqueue(entry);
        while (_entries.Count > _maxEntries)
        {
            _entries.TryDequeue(out _);
        }
    }

    public void Flush() { }

    public void Clear() => _entries.Clear();
}

/// <summary>
/// Filters log entries by minimum level and optional category.
/// </summary>
public sealed class LogFilter : ILogSink
{
    private readonly ILogSink _inner;
    private readonly LogLevel _minimumLevel;
    private readonly string? _category;

    public LogFilter(ILogSink inner, LogLevel minimumLevel = LogLevel.Trace, string? category = null)
    {
        _inner = inner;
        _minimumLevel = minimumLevel;
        _category = category;
    }

    public LogLevel MinimumLevel => _minimumLevel;

    public void Write(LogEntry entry)
    {
        if (entry.Level < _minimumLevel) return;
        if (_category is not null && !string.Equals(entry.Category, _category, StringComparison.OrdinalIgnoreCase)) return;
        _inner.Write(entry);
    }

    public void Flush() => _inner.Flush();
}

/// <summary>
/// Central logging service with multiple sinks, structured properties, and
/// correlation-ID support for tracing operations across components.
/// </summary>
public sealed class Logger : IDisposable
{
    private readonly List<ILogSink> _sinks = new();
    private readonly object _lock = new();
    private readonly AsyncLocal<string?> _correlationId = new();
    private readonly Dictionary<string, object?> _globalProperties = new();
    private int _totalEntries;
    private int _entriesByLevel;

    /// <summary>Number of entries written so far.</summary>
    public int TotalEntries => _totalEntries;

    /// <summary>Adds a sink. All entries are written to every sink.</summary>
    public void AddSink(ILogSink sink)
    {
        lock (_lock)
        {
            _sinks.Add(sink);
        }
    }

    /// <summary>Removes a sink.</summary>
    public bool RemoveSink(ILogSink sink)
    {
        lock (_lock)
        {
            return _sinks.Remove(sink);
        }
    }

    /// <summary>Sets a global property included in every log entry.</summary>
    public void SetGlobalProperty(string name, object? value)
    {
        lock (_lock)
        {
            _globalProperties[name] = value;
        }
    }

    /// <summary>Removes a global property.</summary>
    public bool RemoveGlobalProperty(string name)
    {
        lock (_lock)
        {
            return _globalProperties.Remove(name);
        }
    }

    /// <summary>Gets or creates a correlation ID for the current async flow.</summary>
    public string? CorrelationId
    {
        get => _correlationId.Value;
        set => _correlationId.Value = value;
    }

    /// <summary>Creates a new correlation ID and sets it for the current async flow.</summary>
    public string CreateCorrelationId()
    {
        var id = Guid.NewGuid().ToString("N")[..12];
        _correlationId.Value = id;
        return id;
    }

    /// <summary>Runs an operation with a new correlation ID.</summary>
    public async Task<T> RunWithCorrelationId<T>(string correlationId, Func<Task<T>> action)
    {
        var previous = _correlationId.Value;
        _correlationId.Value = correlationId;
        try
        {
            return await action();
        }
        finally
        {
            _correlationId.Value = previous;
        }
    }

    /// <summary>Logs at Trace level.</summary>
    public void Trace(string message, string? category = null, params (string Name, object? Value)[] properties)
    {
        Write(LogLevel.Trace, message, category, properties);
    }

    /// <summary>Logs at Debug level.</summary>
    public void Debug(string message, string? category = null, params (string Name, object? Value)[] properties)
    {
        Write(LogLevel.Debug, message, category, properties);
    }

    /// <summary>Logs at Information level.</summary>
    public void Information(string message, string? category = null, params (string Name, object? Value)[] properties)
    {
        Write(LogLevel.Information, message, category, properties);
    }

    /// <summary>Logs at Warning level.</summary>
    public void Warning(string message, string? category = null, params (string Name, object? Value)[] properties)
    {
        Write(LogLevel.Warning, message, category, properties);
    }

    /// <summary>Logs at Error level.</summary>
    public void Error(string message, string? category = null, params (string Name, object? Value)[] properties)
    {
        Write(LogLevel.Error, message, category, properties);
    }

    /// <summary>Logs at Critical level.</summary>
    public void Critical(string message, string? category = null, params (string Name, object? Value)[] properties)
    {
        Write(LogLevel.Critical, message, category, properties);
    }

    /// <summary>Logs an exception at Error level.</summary>
    public void Error(Exception exception, string? message = null, string? category = null)
    {
        WriteException(LogLevel.Error, exception, message, category);
    }

    /// <summary>Logs an exception at Critical level.</summary>
    public void Critical(Exception exception, string? message = null, string? category = null)
    {
        WriteException(LogLevel.Critical, exception, message, category);
    }

    /// <summary>Flushes all sinks.</summary>
    public void Flush()
    {
        lock (_lock)
        {
            foreach (var sink in _sinks) sink.Flush();
        }
    }

    private void Write(LogLevel level, string message, string? category, IEnumerable<(string Name, object? Value)> properties)
    {
        var entry = new LogEntry(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            level,
            message,
            category,
            _correlationId.Value,
            null,
            _globalProperties.Select(kv => (Name: kv.Key, Value: kv.Value)).Concat(properties).GroupBy(kv => kv.Name).ToDictionary(g => g.Key, g => g.Last().Value),
            null, null, null);

        lock (_lock)
        {
            _totalEntries++;
            _entriesByLevel++;
            foreach (var sink in _sinks)
            {
                if (entry.Level >= sink.MinimumLevel)
                {
                    sink.Write(entry);
                }
            }
        }
    }

    private void WriteException(LogLevel level, Exception exception, string? message, string? category)
    {
        var entry = new LogEntry(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            level,
            message ?? exception.Message,
            category,
            _correlationId.Value,
            null,
            _globalProperties,
            exception.GetType().FullName,
            exception.Message,
            exception.StackTrace);

        lock (_lock)
        {
            _totalEntries++;
            foreach (var sink in _sinks)
            {
                if (entry.Level >= sink.MinimumLevel)
                {
                    sink.Write(entry);
                }
            }
        }
    }

    /// <summary>Flushes and disposes all sinks.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var sink in _sinks.OfType<IDisposable>())
            {
                sink.Dispose();
            }
            _sinks.Clear();
        }
    }
}

/// <summary>
/// Singleton access to the application-wide logger. Use Logger.Instance to log from
/// anywhere in the application.
/// </summary>
public static class Log
{
    public static Logger Instance { get; } = new();

    static Log()
    {
        Instance.SetGlobalProperty("Application", "KronosReborn");
        Instance.SetGlobalProperty("Version", typeof(Log).Assembly.GetName().Version?.ToString() ?? "unknown");
    }
}
