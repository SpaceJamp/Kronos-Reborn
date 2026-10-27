using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using KronosReborn.Data.Diagnostics;
using KronosReborn.Data.Libraries;
using KronosReborn.Data.Logging;
using KronosReborn.Data.Runtimes;
using Xunit;

namespace KronosReborn.Tests;

public class DiagnosticsTests
{
    [Fact]
    public void AHealthyInstallReportsNoProblems()
    {
        using var fixture = new SwapFixture(RuntimeKind.Dlss);
        fixture.InstallRuntime("3.8.0");

        var report = Run(fixture);

        Assert.Equal(0, report.Count(DiagnosticSeverity.Error));
        Assert.Equal(0, report.Count(DiagnosticSeverity.Warning));
        Assert.Contains(report.Environment, f => f.Name == "Data directory" && f.Value == fixture.Paths.Root);
    }

    [Fact]
    public void AVersionWhoseFileWasReplacedIsReported()
    {
        using var fixture = new SwapFixture(RuntimeKind.Fsr);
        fixture.InstallRuntime("99.0.0");
        File.Copy(typeof(DiagnosticsTests).Assembly.Location,
            Path.Combine(fixture.CollectionFolder, RuntimeDlls.ByKind[RuntimeKind.Fsr][0]), overwrite: true);

        var report = Run(fixture);

        Assert.Contains(report.Checks, c =>
            c.Severity == DiagnosticSeverity.Warning && c.Summary.Contains("replaced after it was added"));
    }

    [Fact]
    public void AnUndoWhoseSavedCopyIsGoneIsAnError()
    {
        using var fixture = new SwapFixture(RuntimeKind.Dlss);
        fixture.InstallRuntime("3.8.0");
        var swap = Assert.Single(fixture.Swap().Results);
        File.Delete(swap.Backups[0].BackupPath);

        var report = Run(fixture);

        Assert.Contains(report.Checks, c =>
            c.Severity == DiagnosticSeverity.Error && c.Summary.Contains("cannot be undone"));
    }

    [Fact]
    public void BackupsNoHistoryReferencesAreReportedForCleanUp()
    {
        using var fixture = new SwapFixture(RuntimeKind.Dlss);
        var stray = Path.Combine(fixture.Paths.BackupDirectory, "game-1", "leftover.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(stray)!);
        File.WriteAllText(stray, "x");

        var report = Run(fixture);

        Assert.Contains(report.Checks, c =>
            c.Severity == DiagnosticSeverity.Info && c.Summary.Contains("no longer referenced"));
    }

    [Fact]
    public void ReportedProblemsAndLoggedErrorsAreIncluded()
    {
        using var fixture = new SwapFixture(RuntimeKind.Dlss);
        var problem = new ScanProblem("Steam", null, "Library could not be read");
        var error = Entry(LogLevel.Error, "Swap failed");

        var report = DiagnosticsRunner.Run(
            fixture.Paths, fixture.Catalog, fixture.Persistence.Histories.Values,
            [problem], [error, Entry(LogLevel.Information, "fine")]);

        Assert.Contains(report.Checks, c => c.Summary.Contains("Library could not be read"));
        Assert.Equal("Swap failed", Assert.Single(report.RecentErrors).Message);
        Assert.Contains("Swap failed", report.ToText());
    }

    [Fact]
    public void ExportHoldsTheReportAndDataButNoBackups()
    {
        using var fixture = new SwapFixture(RuntimeKind.Dlss);
        fixture.InstallRuntime("3.8.0");
        Assert.True(Assert.Single(fixture.Swap().Results).Succeeded);

        var logs = Path.Combine(fixture.Paths.Root, "logs");
        Directory.CreateDirectory(logs);
        File.WriteAllText(Path.Combine(logs, "kronosreborn.log"), "{}");

        var zip = Path.Combine(fixture.Paths.Root, "..", "out.zip");
        DiagnosticsRunner.Export(Run(fixture), fixture.Paths, zip);

        using var archive = ZipFile.OpenRead(zip);
        var names = archive.Entries.Select(e => e.FullName).ToArray();

        Assert.Contains("report.txt", names);
        Assert.Contains("catalog.json", names);
        Assert.Contains("logs/kronosreborn.log", names);
        Assert.Contains(names, n => n.StartsWith("history/", StringComparison.Ordinal));
        Assert.DoesNotContain(names, n => n.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
        Assert.False(File.Exists(zip + ".partial"));
    }

    private static DiagnosticsReport Run(SwapFixture fixture) =>
        DiagnosticsRunner.Run(
            fixture.Paths, fixture.Catalog, fixture.Persistence.Histories.Values, [], []);

    private static LogEntry Entry(LogLevel level, string message) => new(
        Guid.NewGuid(), DateTimeOffset.UtcNow, level, message, "Test", null, null,
        new System.Collections.Generic.Dictionary<string, object?>(), null, null, null);
}
