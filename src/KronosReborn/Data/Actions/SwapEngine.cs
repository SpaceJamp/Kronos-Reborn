using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using KronosReborn.Data.Libraries;
using KronosReborn.Data.Persistence;
using KronosReborn.Data.Runtimes;

namespace KronosReborn.Data.Actions;

/// <summary>
/// Minimal game info for swap planning (avoids UI dependency).
/// </summary>
public sealed record SwapGameInfo(
    string GameId,
    string GameName,
    string GamePath,
    bool ExistsOnDisk,
    GameRuntimeSnapshot? Snapshot);

/// <summary>
/// The result of a preview or swap operation on one game.
/// </summary>
/// <remarks>
/// R35/R36: preview shows what would happen to every selected game.
/// R37: downgrades and untrusted sources are listed separately.
/// </remarks>
public sealed record SwapPlan(
    string GameId,
    string GameName,
    string GamePath,
    RuntimeKind RuntimeKind,
    string? CurrentVersion,
    string TargetVersion,
    RuntimeTrust TargetTrust,
    bool IsDowngrade,
    bool IsUntrusted,
    bool HasCurrentVersion,
    string[] SourceFiles,
    string[] TargetFiles)
{
    /// <summary>Whether this plan involves a change that warrants a warning.</summary>
    public bool HasWarning => IsDowngrade || IsUntrusted;

    /// <summary>A one-line summary for the preview list.</summary>
    public string Summary
    {
        get
        {
            var kindName = RuntimeKind switch
            {
                RuntimeKind.Dlss  => "DLSS",
                RuntimeKind.DlssG => "DLSS Frame Gen",
                RuntimeKind.DlssR => "DLSS Ray Recon",
                RuntimeKind.Fsr   => "FSR",
                RuntimeKind.Xess  => "XeSS",
                _ => RuntimeKind.ToString()
            };

            var current = CurrentVersion ?? "none";
            var warning = HasWarning ? " ⚠" : "";
            return $"{GameName}: {kindName} {current} → {TargetVersion}{warning}";
        }
    }
}

/// <summary>
/// The result of applying a swap to one game.
/// </summary>
public sealed record SwapResult(
    string GameId,
    bool Succeeded,
    string? ErrorMessage,
    IReadOnlyList<string> BackupPaths,
    HistoryEntry? HistoryEntry)
{
    public static SwapResult Success(string gameId, IReadOnlyList<string> backupPaths, HistoryEntry entry) =>
        new(gameId, true, null, backupPaths, entry);

    public static SwapResult Failed(string gameId, string error) =>
        new(gameId, false, error, [], null);
}

/// <summary>
/// The complete result of a batch swap operation.
/// </summary>
/// <remarks>
/// R39: applying a change to many games continues past a failure.
/// </remarks>
public sealed record BatchSwapResult(
    IReadOnlyList<SwapResult> Results,
    bool WasCancelled)
{
    public int Succeeded => Results.Count(r => r.Succeeded);
    public int Failed => Results.Count(r => !r.Succeeded);
    public bool AllSucceeded => Failed == 0 && Results.Count > 0;
}

/// <summary>
/// Plans a swap for a set of games against the catalog.
/// </summary>
public static class SwapPlanner
{
    /// <summary>
    /// Creates swap plans for the selected games and runtime kinds.
    /// </summary>
    /// <param name="games">The games to plan for (from UI selection).</param>
    /// <param name="catalog">The runtime catalog.</param>
    /// <param name="runtimeKind">Which runtime kind to swap (DLSS, FSR, etc.).</param>
    /// <param name="targetEntryId">The catalog entry ID to install.</param>
    /// <param name="settings">Current settings (for AllowUntrustedRuntimes).</param>
    /// <returns>A list of plans, one per game that has the target runtime kind present or missing.</returns>
    public static IReadOnlyList<SwapPlan> Plan(
        IEnumerable<SwapGameInfo> games,
        RuntimeCatalog catalog,
        RuntimeKind runtimeKind,
        string targetEntryId,
        Settings settings)
    {
        if (!catalog.TryGet(targetEntryId, out var targetEntry))
        {
            return [];
        }

        if (targetEntry.Kind != runtimeKind)
        {
            return [];
        }

        var plans = new List<SwapPlan>();

        foreach (var game in games)
        {
            if (!game.ExistsOnDisk)
            {
                continue; // Skip missing games - they can't be swapped
            }

            var snapshot = game.Snapshot;
            if (snapshot == null)
            {
                continue;
            }

            var currentRuntime = snapshot.Runtimes.FirstOrDefault(r => r.Kind == runtimeKind);
            if (currentRuntime == null)
            {
                // Game doesn't have this runtime kind at all - could still install it
                // but we need the target DLL file to exist
                var earlyTargetFiles = FindTargetFiles(targetEntry.FilePath, game.GamePath, runtimeKind);
                if (earlyTargetFiles.Length == 0)
                {
                    continue;
                }
            }

            var currentVersion = currentRuntime?.Version;
            var hasCurrentVersion = currentRuntime?.Exists == true;

            // R30/R32: downgrade warning only when both versions are known
            var isDowngrade = false;
            if (currentVersion != null && targetEntry.Version != null)
            {
                isDowngrade = VersionComparer.IsOlder(targetEntry.Version, currentVersion);
            }

            // R33: untrusted warning
            var isUntrusted = targetEntry.Trust == RuntimeTrust.Untrusted && !settings.AllowUntrustedRuntimes;

            // Source files (what will be backed up)
            var sourceFiles = currentRuntime?.Exists == true
                ? FindSourceFiles(game.GamePath, runtimeKind)
                : [];

            // Target files (what will be copied)
            var targetFiles = FindTargetFiles(targetEntry.FilePath, game.GamePath, runtimeKind);

            if (targetFiles.Length == 0)
            {
                continue; // Can't install if we don't know where to put it
            }

            plans.Add(new SwapPlan(
                game.GameId,
                game.GameName,
                game.GamePath,
                runtimeKind,
                currentVersion,
                targetEntry.Version ?? "unknown",
                targetEntry.Trust,
                isDowngrade,
                isUntrusted,
                hasCurrentVersion,
                sourceFiles,
                targetFiles));
        }

        return plans;
    }

    private static string[] FindSourceFiles(string gamePath, RuntimeKind kind)
    {
        var files = new List<string>();
        foreach (var fileName in RuntimeDlls.ByKind[kind])
        {
            var path = Path.Combine(gamePath, fileName);
            if (File.Exists(path))
            {
                files.Add(path);
            }
            else
            {
                // Check common subdirectories
                foreach (var sub in new[] { "x64", "bin", "bin64", "Win64", "Binaries/Win64" })
                {
                    var candidate = Path.Combine(gamePath, sub, fileName);
                    if (File.Exists(candidate))
                    {
                        files.Add(candidate);
                        break;
                    }
                }
            }
        }
        return [.. files];
    }

    private static string[] FindTargetFiles(string sourceDllPath, string gamePath, RuntimeKind kind)
    {
        // For now, install to the game root. The source DLL name determines the target name.
        var fileName = Path.GetFileName(sourceDllPath);
        var targetPath = Path.Combine(gamePath, fileName);

        // If the source file exists in a subdirectory in the game, mirror that
        var relativeSource = Path.GetRelativePath(
            Path.GetDirectoryName(sourceDllPath) ?? "", sourceDllPath);

        if (relativeSource.Contains(Path.DirectorySeparatorChar))
        {
            targetPath = Path.Combine(gamePath, relativeSource);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        }

        return [targetPath];
    }
}

/// <summary>
/// Version comparison for downgrade detection.
/// </summary>
internal static class VersionComparer
{
    /// <summary>
    /// Returns true if versionA is older than versionB (semantic version comparison).
    /// </summary>
    public static bool IsOlder(string versionA, string versionB)
    {
        var a = Parse(versionA);
        var b = Parse(versionB);

        for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var av = i < a.Length ? a[i] : 0;
            var bv = i < b.Length ? b[i] : 0;
            if (av != bv) return av < bv;
        }
        return false; // equal
    }

    private static int[] Parse(string version)
    {
        return version.Split('.', '-', '+')
            .Select(s => int.TryParse(s, out var n) ? n : 0)
            .ToArray();
    }
}

/// <summary>
/// Executes swaps and manages backups.
/// </summary>
public sealed class SwapExecutor
{
    private readonly PersistenceService _persistence;
    private readonly RuntimeCatalog _catalog;

    public SwapExecutor(PersistenceService persistence, RuntimeCatalog catalog)
    {
        _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    /// <summary>
    /// Executes a batch of swap plans.
    /// </summary>
    /// <remarks>
    /// R20: swap refused if game is being modified (we use file locks).
    /// R21: backup of every affected file made before overwrite.
    /// R22: on failure, game restored to pre-swap state.
    /// R23: swap never leaves game in indescribable state.
    /// R24: only runtime files are written.
    /// R25/R26: every swap creates a history entry for undo.
    /// R39: batch continues past failure.
    /// R40: cancellation stops work not yet started.
    /// </remarks>
    public BatchSwapResult Execute(
        IReadOnlyList<SwapPlan> plans,
        CancellationToken cancellation = default)
    {
        var results = new List<SwapResult>();

        foreach (var plan in plans)
        {
            cancellation.ThrowIfCancellationRequested();

            var result = ExecuteSingle(plan);
            results.Add(result);

            // R26: record in history for undo
            if (result.Succeeded && result.HistoryEntry != null)
            {
                _persistence.AddHistoryEntry(plan.GameId, result.HistoryEntry);
            }
        }

        return new BatchSwapResult(results.ToImmutableList(), cancellation.IsCancellationRequested);
    }

    private SwapResult ExecuteSingle(SwapPlan plan)
    {
        var backupPaths = new List<string>();

        try
        {
            // R20: check for concurrent modification by trying to open files exclusively
            foreach (var sourceFile in plan.SourceFiles)
            {
                if (!File.Exists(sourceFile)) continue;

                // Try to open for exclusive write - if another process has it locked, this fails
                using var fs = File.Open(sourceFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                // If we get here, we have exclusive access
            }

            // R21: backup every file that will be overwritten
            foreach (var sourceFile in plan.SourceFiles)
            {
                if (!File.Exists(sourceFile)) continue;

                var backupPath = sourceFile + $".kronos_bak_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss_fff}";
                File.Copy(sourceFile, backupPath, overwrite: true);
                backupPaths.Add(backupPath);
            }

            // Also backup target files if they exist (for the case where we're adding a new runtime)
            foreach (var targetFile in plan.TargetFiles)
            {
                if (File.Exists(targetFile) && !plan.SourceFiles.Contains(targetFile))
                {
                    var backupPath = targetFile + $".kronos_bak_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss_fff}";
                    File.Copy(targetFile, backupPath, overwrite: true);
                    backupPaths.Add(backupPath);
                }
            }

            // R24: only write the runtime DLLs
            var catalogEntry = _catalog.Entries.FirstOrDefault(e =>
                e.Kind == plan.RuntimeKind && e.Version == plan.TargetVersion);
            if (catalogEntry == null)
            {
                return SwapResult.Failed(plan.GameId, "Catalog entry not found");
            }

            // Copy the catalog DLL to each target location
            foreach (var targetFile in plan.TargetFiles)
            {
                var dir = Path.GetDirectoryName(targetFile)!;
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.Copy(catalogEntry.FilePath, targetFile, overwrite: true);
            }

            // R25/R26: create history entry
            var historyEntry = new HistoryEntry(
                DateTimeOffset.UtcNow,
                plan.GameId,
                plan.GameName,
                plan.GamePath,
                plan.RuntimeKind,
                plan.CurrentVersion,
                plan.TargetVersion,
                plan.HasCurrentVersion ? RuntimeTrust.Trusted : RuntimeTrust.Untrusted, // previous trust unknown, assume trusted
                plan.TargetTrust,
                backupPaths,
                plan.IsDowngrade,
                plan.IsUntrusted);

            return SwapResult.Success(plan.GameId, backupPaths, historyEntry);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            // R22: restore on failure
            RestoreBackups(backupPaths);
            return SwapResult.Failed(plan.GameId, ex.Message);
        }
        catch (Exception ex)
        {
            RestoreBackups(backupPaths);
            return SwapResult.Failed(plan.GameId, $"Unexpected error: {ex.Message}");
        }
    }

    private static void RestoreBackups(IEnumerable<string> backupPaths)
    {
        foreach (var backupPath in backupPaths)
        {
            try
            {
                if (!File.Exists(backupPath)) continue;

                var originalPath = backupPath.Substring(0, backupPath.LastIndexOf(".kronos_bak_"));
                if (File.Exists(originalPath))
                {
                    File.Delete(originalPath);
                }
                File.Move(backupPath, originalPath);
            }
            catch
            {
                // Best effort - if restore fails, the backup remains for manual recovery
            }
        }
    }
}

/// <summary>
/// Undoes the last swap for a game.
/// </summary>
public sealed class UndoEngine
{
    private readonly PersistenceService _persistence;

    public UndoEngine(PersistenceService persistence)
    {
        _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
    }

    /// <summary>
    /// Undoes the most recent swap for a specific game and runtime kind.
    /// </summary>
    /// <remarks>
    /// R25: every swap can be undone.
    /// R26: undoing can be repeated as many times as swaps made.
    /// R27: backups from previous versions are adopted.
    /// R28: external changes detected via hash comparison.
    /// R29: missing originals reported as unrecoverable.
    /// </remarks>
    public SwapResult Undo(string gameId, RuntimeKind runtimeKind)
    {
        var history = _persistence.GetHistory(gameId);
        var lastEntry = history.GetLatestFor(runtimeKind);

        if (lastEntry == null || !lastEntry.CanUndo)
        {
            return SwapResult.Failed(gameId, "Nothing to undo");
        }

        // R28: check if files were modified externally since backup
        var externallyModified = false;
        foreach (var backupPath in lastEntry.BackupPaths)
        {
            var originalPath = backupPath.Substring(0, backupPath.LastIndexOf(".kronos_bak_"));
            if (File.Exists(originalPath) && File.Exists(backupPath))
            {
                var originalHash = ComputeHash(originalPath);
                var backupHash = ComputeHash(backupPath);
                if (originalHash != backupHash)
                {
                    externallyModified = true;
                    break;
                }
            }
        }

        if (externallyModified)
        {
            // R28: game modified externally - can't safely undo
            return SwapResult.Failed(gameId, "Game files were modified externally since the swap. Manual recovery required.");
        }

        try
        {
            // Restore each backed-up file
            foreach (var backupPath in lastEntry.BackupPaths)
            {
                var originalPath = backupPath.Substring(0, backupPath.LastIndexOf(".kronos_bak_"));

                if (File.Exists(backupPath))
                {
                    if (File.Exists(originalPath))
                    {
                        File.Delete(originalPath);
                    }
                    File.Move(backupPath, originalPath);
                }
                else
                {
                    // R29: original missing and no backup = unrecoverable
                    return SwapResult.Failed(gameId, $"Backup missing for {originalPath}. Cannot undo.");
                }
            }

            // Create a history entry for the undo (so it can be redone)
            var undoEntry = new HistoryEntry(
                DateTimeOffset.UtcNow,
                gameId,
                lastEntry.GameName,
                lastEntry.GamePath,
                lastEntry.RuntimeKind,
                lastEntry.NewVersion, // we're going back to this
                lastEntry.PreviousVersion ?? "none",
                lastEntry.NewTrust,
                lastEntry.PreviousTrust,
                [], // no new backups for undo
                false,
                false);

            _persistence.AddHistoryEntry(gameId, undoEntry);

            return SwapResult.Success(gameId, lastEntry.BackupPaths, undoEntry);
        }
        catch (Exception ex)
        {
            return SwapResult.Failed(gameId, $"Undo failed: {ex.Message}");
        }
    }

    private static string ComputeHash(string path)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(stream));
    }
}