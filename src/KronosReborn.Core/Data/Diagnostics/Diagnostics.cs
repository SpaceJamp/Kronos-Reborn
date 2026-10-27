using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using KronosReborn.Data.Libraries;
using KronosReborn.Data.Logging;
using KronosReborn.Data.Persistence;
using KronosReborn.Data.Runtimes;

namespace KronosReborn.Data.Diagnostics;

/// <summary>How much a diagnostic finding matters.</summary>
public enum DiagnosticSeverity
{
    /// <summary>Checked and fine.</summary>
    Ok,

    /// <summary>Worth knowing, nothing is broken.</summary>
    Info,

    /// <summary>Something will not work as expected, but nothing is at risk.</summary>
    Warning,

    /// <summary>Something is broken: a swap or undo that relies on it will fail.</summary>
    Error,
}

/// <summary>One finding of a diagnostics run.</summary>
/// <param name="Area">What was checked, for grouping.</param>
/// <param name="Severity">How much it matters.</param>
/// <param name="Summary">One line, in terms the user can act on.</param>
/// <param name="Detail">The paths and values behind it, when there are any.</param>
public sealed record DiagnosticCheck(
    string Area,
    DiagnosticSeverity Severity,
    string Summary,
    string? Detail = null)
{
    /// <summary>A mark for the severity, so the list reads at a glance without colour.</summary>
    public string Glyph => Severity switch
    {
        DiagnosticSeverity.Ok => "✓",
        DiagnosticSeverity.Info => "ℹ",
        DiagnosticSeverity.Warning => "⚠",
        _ => "✖",
    };
}

/// <summary>One line of the environment section.</summary>
public sealed record DiagnosticFact(string Name, string Value);

/// <summary>Everything a diagnostics run found.</summary>
public sealed record DiagnosticsReport(
    DateTimeOffset CreatedAt,
    IReadOnlyList<DiagnosticFact> Environment,
    IReadOnlyList<DiagnosticCheck> Checks,
    IReadOnlyList<LogEntry> RecentErrors)
{
    public int Count(DiagnosticSeverity severity) => Checks.Count(c => c.Severity == severity);

    /// <summary>A one-line verdict for the top of the panel.</summary>
    public string Summary
    {
        get
        {
            var errors = Count(DiagnosticSeverity.Error);
            var warnings = Count(DiagnosticSeverity.Warning);

            return errors == 0 && warnings == 0
                ? $"No problems found ({Checks.Count} checks)."
                : $"{errors} error(s), {warnings} warning(s) across {Checks.Count} checks.";
        }
    }

    /// <summary>The whole report as plain text, for copying into a bug report.</summary>
    public string ToText()
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"Kronos Reborn diagnostics — {CreatedAt:u}");
        text.AppendLine(Summary);
        text.AppendLine();

        text.AppendLine("Environment");
        foreach (var fact in Environment)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  {fact.Name}: {fact.Value}");
        }

        foreach (var area in Checks.GroupBy(c => c.Area))
        {
            text.AppendLine();
            text.AppendLine(area.Key);
            foreach (var check in area)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"  {check.Glyph} {check.Summary}");
                if (check.Detail is not null)
                {
                    text.AppendLine(CultureInfo.InvariantCulture, $"      {check.Detail}");
                }
            }
        }

        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"Recent errors ({RecentErrors.Count})");
        foreach (var entry in RecentErrors)
        {
            text.AppendLine(CultureInfo.InvariantCulture,
                $"  {entry.Timestamp:u} [{entry.Level}] {entry.Category}: {entry.Message}");
            if (entry.ExceptionMessage is not null)
            {
                text.AppendLine(CultureInfo.InvariantCulture,
                    $"      {entry.ExceptionType}: {entry.ExceptionMessage}");
            }
        }

        return text.ToString();
    }
}

/// <summary>
/// Checks the program's own data for the states that make swaps and undos fail, and packages it
/// for a bug report.
/// </summary>
/// <remarks>
/// Read-only apart from one probe file in the data directory: diagnosing must never be the thing
/// that changes a game or the collection.
/// </remarks>
public static class DiagnosticsRunner
{
    /// <summary>Below this much free space, a swap's backups may not fit.</summary>
    internal const long LowDiskSpaceBytes = 500L * 1024 * 1024;

    /// <summary>How many recent errors the report carries.</summary>
    internal const int RecentErrorLimit = 50;

    /// <summary>Runs every check.</summary>
    /// <param name="paths">The data directory being diagnosed.</param>
    /// <param name="catalog">The runtime collection.</param>
    /// <param name="histories">Every game's history.</param>
    /// <param name="problems">Problems already reported by loading and scanning.</param>
    /// <param name="recentLog">Log entries, such as <see cref="ReadLog"/> returns; only errors are kept.</param>
    /// <param name="appFacts">Facts only the app knows, such as its build version.</param>
    public static DiagnosticsReport Run(
        DataPaths paths,
        RuntimeCatalog catalog,
        IEnumerable<GameHistory> histories,
        IEnumerable<ScanProblem> problems,
        IEnumerable<LogEntry> recentLog,
        IEnumerable<DiagnosticFact>? appFacts = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(histories);
        ArgumentNullException.ThrowIfNull(problems);
        ArgumentNullException.ThrowIfNull(recentLog);

        var historyList = histories.ToArray();
        var checks = new List<DiagnosticCheck>();

        checks.AddRange(CheckDataDirectory(paths));
        checks.AddRange(problems.Select(p =>
            new DiagnosticCheck("Problems reported", DiagnosticSeverity.Warning, p.OneLine, p.Describe())));
        checks.AddRange(CheckCollection(catalog));
        checks.AddRange(CheckHistory(paths, historyList));

        var errors = recentLog
            .Where(e => e.Level >= LogLevel.Error)
            .OrderByDescending(e => e.Timestamp)
            .Take(RecentErrorLimit)
            .ToArray();

        if (errors.Length > 0)
        {
            checks.Add(new DiagnosticCheck(
                "Log", DiagnosticSeverity.Warning,
                $"{errors.Length} error(s) or crash(es) in the log. The most recent: {errors[0].Message}",
                $"{errors[0].Timestamp.ToLocalTime():g}"));
        }

        return new DiagnosticsReport(
            DateTimeOffset.UtcNow,
            [.. (appFacts ?? []), .. EnvironmentFacts(paths, catalog, historyList)],
            checks,
            errors);
    }

    /// <summary>
    /// Writes a zip of the report, logs, settings, collection and history to <paramref name="zipPath"/>.
    /// </summary>
    /// <remarks>
    /// Backups and the collection's DLLs are left out: they are large, they are not evidence of
    /// anything the JSON does not already describe, and they are not the user's to redistribute.
    /// </remarks>
    public static void Export(DiagnosticsReport report, DataPaths paths, string zipPath)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(zipPath);

        // Built beside the target and moved into place, so a failure never leaves half a zip there.
        var partial = zipPath + ".partial";

        try
        {
            using (var archive = ZipFile.Open(partial, ZipArchiveMode.Create))
            {
                var reportEntry = archive.CreateEntry("report.txt");
                using (var writer = new StreamWriter(reportEntry.Open(), Encoding.UTF8))
                {
                    writer.Write(report.ToText());
                }

                AddFile(archive, paths.SettingsPath, "settings.json");
                AddFile(archive, paths.CatalogPath, "catalog.json");
                AddFolder(archive, paths.HistoryDirectory, "history");
                AddFolder(archive, paths.LogsDirectory, "logs");
            }

            File.Move(partial, zipPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(partial))
            {
                File.Delete(partial);
            }
        }
    }

    /// <summary>
    /// Every entry in the log and its rotated files, so errors and crashes from earlier runs are seen.
    /// </summary>
    /// <remarks>
    /// The log was only held in memory for this, so an error was gone once the program closed, and a
    /// crash, which ends the program, could never be shown. A line that cannot be read is skipped: it
    /// is most likely the last one, cut short when the program ended.
    /// </remarks>
    public static IReadOnlyList<LogEntry> ReadLog(string logsFolder)
    {
        var entries = new List<LogEntry>();

        string[] files;
        try
        {
            files = Directory.Exists(logsFolder) ? Directory.GetFiles(logsFolder, "kronosreborn.log*") : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return entries;
        }

        foreach (var file in files)
        {
            try
            {
                // Shared, because the running logger holds the current file open.
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);

                while (reader.ReadLine() is { } line)
                {
                    try
                    {
                        if (System.Text.Json.JsonSerializer.Deserialize<LogEntry>(line) is { } entry)
                        {
                            entries.Add(entry);
                        }
                    }
                    catch (System.Text.Json.JsonException)
                    {
                        // A torn or foreign line.
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // That file is skipped; the others still count.
            }
        }

        return entries;
    }

    private static IEnumerable<DiagnosticFact> EnvironmentFacts(
        DataPaths paths, RuntimeCatalog catalog, IReadOnlyList<GameHistory> histories)
    {
        yield return new("Operating system", RuntimeInformation.OSDescription);
        yield return new(".NET", RuntimeInformation.FrameworkDescription);
        yield return new("Architecture", RuntimeInformation.ProcessArchitecture.ToString());
        yield return new("Culture", CultureInfo.CurrentCulture.Name);
        yield return new("Data directory", paths.Root);

        if (FreeSpace(paths.Root) is { } free)
        {
            yield return new("Free space", FormatBytes(free));
        }

        yield return new("Versions held", catalog.Entries.Count.ToString(CultureInfo.InvariantCulture));
        yield return new("Games with history", histories.Count.ToString(CultureInfo.InvariantCulture));
        yield return new("Backups", FormatBytes(FolderSize(paths.BackupDirectory)));
    }

    private static IEnumerable<DiagnosticCheck> CheckDataDirectory(DataPaths paths)
    {
        const string area = "Data directory";
        var probe = Path.Combine(paths.Root, $".diagnostics-{Guid.NewGuid():N}.tmp");

        DiagnosticCheck writable;
        try
        {
            Directory.CreateDirectory(paths.Root);
            File.WriteAllText(probe, "probe");
            File.Delete(probe);
            writable = new(area, DiagnosticSeverity.Ok, "The data directory is writable.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            writable = new(area, DiagnosticSeverity.Error,
                "The data directory cannot be written, so swaps cannot save backups or history.",
                $"{paths.Root}: {ex.Message}");
        }

        yield return writable;

        if (FreeSpace(paths.Root) is { } free && free < LowDiskSpaceBytes)
        {
            yield return new(area, DiagnosticSeverity.Warning,
                $"Only {FormatBytes(free)} free on the data drive; backups for a swap may not fit.",
                paths.Root);
        }
    }

    /// <summary>Whether every version in the collection can still be installed.</summary>
    internal static IEnumerable<DiagnosticCheck> CheckCollection(RuntimeCatalog catalog)
    {
        const string area = "Collection";
        var entries = catalog.Entries;
        var findings = new List<DiagnosticCheck>();

        foreach (var entry in entries)
        {
            var name = $"{RuntimeKindNames.Display(entry.Kind)} {entry.Version}";
            var folder = Path.GetDirectoryName(entry.FilePath) ?? string.Empty;

            if (!File.Exists(entry.FilePath))
            {
                // A version found in a game goes when the game updates or is uninstalled, which is
                // normal; a version the collection owns going missing is not.
                findings.Add(new(area,
                    entry.Source == RuntimeSource.Discovered ? DiagnosticSeverity.Warning : DiagnosticSeverity.Error,
                    $"{name} is listed but its file is gone, so it cannot be installed.",
                    entry.FilePath));
                continue;
            }

            var missing = RuntimeDlls.ByKind[entry.Kind]
                .Where(file => !File.Exists(Path.Combine(folder, file)))
                .ToArray();

            if (missing.Length > 0)
            {
                findings.Add(new(area, DiagnosticSeverity.Error,
                    $"{name} is missing {string.Join(" and ", missing)}, so it cannot be installed.",
                    folder));
                continue;
            }

            if (VersionNumber.TryRead(entry.FilePath, out var onDisk, out _, out _)
                && onDisk is not null
                && !VersionNumber.AreEquivalent(onDisk, entry.Version))
            {
                findings.Add(new(area, DiagnosticSeverity.Warning,
                    $"{name} is now version {onDisk} on disk; the file was replaced after it was added.",
                    entry.FilePath));
            }

            if (entry.Trust != RuntimeTrust.Trusted)
            {
                findings.Add(new(area, DiagnosticSeverity.Info,
                    $"{name} is not signed by its vendor; a warning is shown each time it is installed.",
                    entry.FilePath));
            }
        }

        if (!findings.Any(f => f.Severity >= DiagnosticSeverity.Warning))
        {
            findings.Insert(0, new(area, DiagnosticSeverity.Ok,
                $"All {entries.Count} version(s) in the collection have their files."));
        }

        return findings;
    }

    /// <summary>Whether each game's next undo can actually be carried out.</summary>
    internal static IEnumerable<DiagnosticCheck> CheckHistory(DataPaths paths, IReadOnlyList<GameHistory> histories)
    {
        const string area = "Undo history";
        var findings = new List<DiagnosticCheck>();
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var history in histories)
        {
            foreach (var entry in history.Entries)
            {
                foreach (var backup in entry.Backups)
                {
                    referenced.Add(Path.GetFullPath(backup.BackupPath));
                }
            }

            foreach (var kind in history.Entries.Select(e => e.RuntimeKind).Distinct())
            {
                if (history.NextToUndo(kind) is not { } next)
                {
                    continue;
                }

                var name = $"{next.GameName}: {RuntimeKindNames.Display(kind)}";
                var lost = next.Backups.Where(b => !File.Exists(b.BackupPath)).ToArray();

                if (lost.Length > 0)
                {
                    findings.Add(new(area, DiagnosticSeverity.Error,
                        $"{name} cannot be undone: the saved copy of " +
                        $"{string.Join(" and ", lost.Select(b => Path.GetFileName(b.OriginalPath)))} is missing.",
                        string.Join("; ", lost.Select(b => b.BackupPath))));
                }
                else if (!Directory.Exists(next.GamePath))
                {
                    findings.Add(new(area, DiagnosticSeverity.Info,
                        $"{name} has history but the game folder is gone.",
                        next.GamePath));
                }
            }
        }

        var orphans = Directory.Exists(paths.BackupDirectory)
            ? Directory.EnumerateFiles(paths.BackupDirectory, "*", SearchOption.AllDirectories)
                .Where(file => !referenced.Contains(Path.GetFullPath(file)))
                .ToArray()
            : [];

        if (orphans.Length > 0)
        {
            findings.Add(new(area, DiagnosticSeverity.Info,
                $"{orphans.Length} backup file(s) ({FormatBytes(orphans.Sum(f => new FileInfo(f).Length))}) " +
                "are no longer referenced by any history and could be deleted.",
                paths.BackupDirectory));
        }

        if (!findings.Any(f => f.Severity >= DiagnosticSeverity.Warning))
        {
            findings.Insert(0, new(area, DiagnosticSeverity.Ok,
                $"Every pending undo across {histories.Count} game(s) has its saved copies."));
        }

        return findings;
    }

    private static void AddFile(ZipArchive archive, string source, string name)
    {
        if (!File.Exists(source))
        {
            return;
        }

        // Shared read, because the log file is held open by the running logger.
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var output = archive.CreateEntry(name).Open();
        input.CopyTo(output);
    }

    private static void AddFolder(ZipArchive archive, string folder, string name)
    {
        if (!Directory.Exists(folder))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(folder))
        {
            AddFile(archive, file, $"{name}/{Path.GetFileName(file)}");
        }
    }

    private static long? FreeSpace(string path)
    {
        try
        {
            return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static long FolderSize(string folder) =>
        Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length)
            : 0;

    internal static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{bytes / 1024.0:0.0} KB",
        _ => $"{bytes} B",
    };
}
