using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

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
/// Central logging service with multiple sinks and structured properties.
/// </summary>
public sealed class Logger : IDisposable
{
    private readonly List<ILogSink> _sinks = new();
    private readonly object _lock = new();
    private readonly Dictionary<string, object?> _globalProperties = new();

    /// <summary>Adds a sink. All entries are written to every sink.</summary>
    public void AddSink(ILogSink sink)
    {
        lock (_lock)
        {
            _sinks.Add(sink);
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

    /// <summary>Logs an exception at Error level.</summary>
    public void Error(Exception exception, string? message = null, string? category = null)
    {
        WriteException(LogLevel.Error, exception, message, category);
    }

    /// <summary>Flushes all sinks.</summary>
    public void Flush()
    {
        lock (_lock)
        {
            foreach (var sink in _sinks)
            {
                try
                {
                    sink.Flush();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
                {
                    // See Dispatch.
                }
            }
        }
    }

    private void Write(LogLevel level, string message, string? category, IEnumerable<(string Name, object? Value)> properties)
    {
        Dispatch(level, message, category, properties, exception: null);
    }

    private void WriteException(LogLevel level, Exception exception, string? message, string? category)
    {
        Dispatch(level, message ?? exception.Message, category, [], exception);
    }

    private void Dispatch(
        LogLevel level,
        string message,
        string? category,
        IEnumerable<(string Name, object? Value)> properties,
        Exception? exception)
    {
        lock (_lock)
        {
            // Built under the lock, because the global properties are a shared dictionary, and copied,
            // so a later change to them cannot rewrite an entry already logged.
            var merged = new Dictionary<string, object?>(_globalProperties);
            foreach (var (name, value) in properties)
            {
                merged[name] = value;
            }

            var entry = new LogEntry(
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                level,
                message,
                category,
                null,
                null,
                merged,
                exception?.GetType().FullName,
                exception?.Message,
                exception?.StackTrace);

            foreach (var sink in _sinks)
            {
                if (entry.Level < sink.MinimumLevel)
                {
                    continue;
                }

                try
                {
                    sink.Write(entry);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
                {
                    // A log that cannot be written must not fail the work being logged: a full disk
                    // used to turn a finished swap into a rolled-back one, and crash an undo.
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
