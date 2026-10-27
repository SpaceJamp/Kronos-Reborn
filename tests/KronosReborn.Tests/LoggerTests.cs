using System;
using System.Collections.Generic;
using System.IO;
using KronosReborn.Data.Logging;
using Xunit;

namespace KronosReborn.Tests;

public class LoggerTests
{
    [Fact]
    public void ASinkThatCannotWriteDoesNotFailTheCaller()
    {
        using var logger = new Logger();
        var memory = new ListSink();
        logger.AddSink(new BrokenSink());
        logger.AddSink(memory);

        // A full disk used to throw from here into whatever was being logged.
        logger.Information("still fine");
        logger.Error(new InvalidOperationException("boom"), "also fine");

        Assert.Equal(2, memory.Entries.Count);
        Assert.Equal("boom", memory.Entries[1].ExceptionMessage);
    }

    private sealed class ListSink : ILogSink
    {
        public List<LogEntry> Entries { get; } = [];
        public LogLevel MinimumLevel => LogLevel.Trace;
        public void Write(LogEntry entry) => Entries.Add(entry);
        public void Flush() { }
    }

    private sealed class BrokenSink : ILogSink
    {
        public LogLevel MinimumLevel => LogLevel.Trace;
        public void Write(LogEntry entry) => throw new IOException("disk full");
        public void Flush() => throw new IOException("disk full");
    }
}
