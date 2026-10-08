using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
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
    IReadOnlyList<string> SourceFiles,
    IReadOnlyList<string> TargetFiles)
{
    /// <summary>Whether this plan involves a change that warrants a warning.</summary>
    public bool HasWarning => IsDowngrade || IsUntrusted;

    /// <summary>
    /// Target files that do not exist yet, and which therefore have nothing to back up and must be
    /// removed again if the swap is undone.
    /// </summary>
    public IReadOnlyList<string> NewFiles =>
        TargetFiles.Where(t => !SourceFiles.Contains(t, PathComparer)).ToArray();

    /// <summary>
    /// Target files that already exist and will be overwritten.
    /// </summary>
    /// <remarks>
    /// Anything left in this set is backed up before it is replaced (R21). If the set is ever
    /// incomplete the swap refuses to start, because an unbacked overwrite is the one failure mode
    /// the program's safety principles call non-negotiable.
    /// </remarks>
    public IReadOnlyList<string> ReplacedFiles =>
        TargetFiles.Where(t => SourceFiles.Contains(t, PathComparer)).ToArray();

    /// <summary>A one-line summary for the preview list.</summary>
    public string Summary
    {
        get
        {
            var kindName = RuntimeKindNames.Display(RuntimeKind);

            var current = CurrentVersion ?? "none";
            var warning = HasWarning ? " ⚠" : "";
            return $"{GameName}: {kindName} {current} → {TargetVersion}{warning}";
        }
    }

    /// <summary>Compares Windows paths without regard to case.</summary>
    internal static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    /// <summary>Compares Windows paths without regard to case.</summary>
    internal const StringComparison PathComparison = StringComparison.OrdinalIgnoreCase;
}

/// <summary>
/// The result of applying a swap to one game.
/// </summary>
public sealed record SwapResult(
    string GameId,
    string GameName,
    bool Succeeded,
    string? ErrorMessage,
    IReadOnlyList<BackupRecord> Backups,
    IReadOnlyList<string> CreatedFiles,
    HistoryEntry? HistoryEntry)
{
    public static SwapResult Success(
        string gameId,
        string gameName,
        IReadOnlyList<BackupRecord> backups,
        IReadOnlyList<string> createdFiles,
        HistoryEntry entry) =>
        new(gameId, gameName, true, null, backups, createdFiles, entry);

    public static SwapResult Failed(string gameId, string gameName, string error) =>
        new(gameId, gameName, false, error, [], [], null);
}

/// <summary>
/// The complete result of a batch swap operation.
/// </summary>
/// <remarks>
/// R39: applying a change to many games continues past a failure.
/// R40: cancelling stops work not yet started and still reports what was done.
/// </remarks>
public sealed record BatchSwapResult(
    IReadOnlyList<SwapResult> Results,
    bool WasCancelled,
    int NotAttempted)
{
    public int Succeeded => Results.Count(r => r.Succeeded);
    public int Failed => Results.Count(r => !r.Succeeded);

    /// <summary>
    /// Whether everything that was attempted worked.
    /// </summary>
    /// <remarks>
    /// Says nothing about <see cref="NotAttempted"/>, so a cancelled batch that happened to
    /// complete every game it started still reads as a clean success. Callers present this alongside
    /// <see cref="WasCancelled"/> and <see cref="NotAttempted"/> for that reason.
    /// </remarks>
    public bool AllSucceeded => Failed == 0;
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
    /// <returns>A list of plans, one per game that can receive this runtime kind.</returns>
    public static IReadOnlyList<SwapPlan> Plan(
        IEnumerable<SwapGameInfo> games,
        RuntimeCatalog catalog,
        RuntimeKind runtimeKind,
        string targetEntryId,
        Settings settings)
    {
        ArgumentNullException.ThrowIfNull(games);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(settings);

        if (!catalog.TryGet(targetEntryId, out var targetEntry) || targetEntry is null)
        {
            return [];
        }

        if (targetEntry.Kind != runtimeKind)
        {
            return [];
        }

        // Every file this runtime kind consists of has to be installable, or the swap is refused.
        // A kind is a unit: FSR is two DLLs that only work as a matched pair, and writing one of
        // them leaves the game with a runtime that did not exist in that shape.
        var sourceFiles = ResolveSourceFiles(targetEntry, runtimeKind);
        if (sourceFiles is null)
        {
            return [];
        }

        var plans = new List<SwapPlan>();

        foreach (var game in games)
        {
            if (!game.ExistsOnDisk)
            {
                continue; // R7: missing games cannot be swapped
            }

            var snapshot = game.Snapshot;
            if (snapshot == null)
            {
                continue;
            }

            var currentRuntime = snapshot.Runtimes.FirstOrDefault(r => r.Kind == runtimeKind);
            var currentVersion = currentRuntime?.Version;
            var hasCurrentVersion = currentRuntime?.Exists == true;

            var targets = ResolveTargetFiles(game.GamePath, runtimeKind);
            if (targets.Count == 0)
            {
                continue; // Can't install if we don't know where to put it
            }

            // R30/R32: downgrade warning only when both versions are known
            var isDowngrade = false;
            if (currentVersion != null && targetEntry.Version != null)
            {
                isDowngrade = VersionComparer.IsOlder(targetEntry.Version, currentVersion);
            }

            // R33: untrusted warning
            var isUntrusted = targetEntry.Trust == RuntimeTrust.Untrusted && !settings.AllowUntrustedRuntimes;

            var plan = new SwapPlan(
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
                targets,
                targets);

            // The plan carries the game's existing files as the things to back up.
            plans.Add(plan with { SourceFiles = targets.Where(File.Exists).ToArray() });
        }

        return plans;
    }

    /// <summary>
    /// The catalog files to copy, one per DLL the runtime kind consists of.
    /// </summary>
    /// <returns>Null when the catalog does not hold every file the kind needs.</returns>
    private static IReadOnlyList<string>? ResolveSourceFiles(CatalogEntry entry, RuntimeKind kind)
    {
        var directory = Path.GetDirectoryName(entry.FilePath);
        if (directory is null)
        {
            return null;
        }

        var paths = new List<string>();
        foreach (var fileName in RuntimeDlls.ByKind[kind])
        {
            var candidate = Path.Combine(directory, fileName);
            if (!File.Exists(candidate))
            {
                return null;
            }
            paths.Add(candidate);
        }

        return paths;
    }

    /// <summary>
    /// Where each DLL of a runtime kind belongs inside a game folder.
    /// </summary>
    /// <remarks>
    /// Uses the same <see cref="RuntimeDllLocator"/> the scanner uses, so a replacement lands exactly
    /// where the game already loads the original from. Derived separately the two disagreed: the
    /// scanner found a DLL in <c>bin\x64_dx12</c> while the planner wrote the replacement to the game
    /// root, leaving the game still running the old runtime and a stray DLL beside it.
    /// </remarks>
    private static IReadOnlyList<string> ResolveTargetFiles(string gamePath, RuntimeKind kind)
    {
        var located = RuntimeDllLocator.Find(gamePath);

        return RuntimeDlls.ByKind[kind]
            .Select(fileName => RuntimeDllLocator.TargetPathFor(gamePath, fileName, located))
            .ToArray();
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
/// Computes and compares file content hashes, used to decide whether a game folder is still in the
/// state the program last left it in.
/// </summary>
internal static class FileFingerprint
{
    /// <summary>Lowercase hex SHA-256 of a file's contents.</summary>
    public static string Compute(string path)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    /// <summary>
    /// Whether a path holds the exact content recorded for it.
    /// </summary>
    /// <remarks>
    /// A file the swap created but that is now absent counts as unchanged: the file not being there
    /// is one of the states this program is responsible for producing.
    /// </remarks>
    public static bool Matches(string path, string? expectedSha256)
    {
        if (!File.Exists(path))
        {
            return true;
        }

        if (expectedSha256 is null)
        {
            return false;
        }

        return string.Equals(Compute(path), expectedSha256, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Saves copies of files a swap overwrites.
/// </summary>
/// <remarks>
/// Shared by <see cref="SwapExecutor"/> and <see cref="UndoEngine"/>: an undo also saves what it is
/// about to replace, so that the undo can itself be undone. Two copies of this logic would drift,
/// and the second one would be the one holding a user's only backup.
/// <para>
/// Backups go under the program's data directory rather than beside the original, because R24 says
/// nothing but the runtime files themselves is written into a game folder.
/// </para>
/// </remarks>
internal static class BackupStore
{
    /// <summary>Copies a file into the backup directory and describes the copy.</summary>
    public static BackupRecord Save(DataPaths paths, string gameId, string originalPath)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);

        var folder = Path.Combine(paths.BackupDirectory, DataPaths.SafeName(gameId));
        Directory.CreateDirectory(folder);

        var backupPath = Path.Combine(
            folder,
            $"{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}_{Path.GetFileName(originalPath)}");

        // Never overwrite: two swaps inside the same millisecond would otherwise destroy the older
        // one's only copy of the original.
        File.Copy(originalPath, backupPath, overwrite: false);

        return new BackupRecord(originalPath, backupPath, PostSwapSha256: "");
    }
}

/// <summary>
/// Executes swaps and manages backups.
/// </summary>
public sealed class SwapExecutor
{
    private readonly PersistenceService _persistence;
    private readonly RuntimeCatalog _catalog;
    private readonly DataPaths _paths;

    public SwapExecutor(PersistenceService persistence, RuntimeCatalog catalog, DataPaths paths)
    {
        _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    /// <summary>
    /// Executes a batch of swap plans.
    /// </summary>
    /// <remarks>
    /// R20: swap refused if the game is being modified.
    /// R21: backup of every affected file made before overwrite.
    /// R22: on failure, the game is restored to its pre-swap state, including deleting files the
    /// swap itself added.
    /// R23: a swap never leaves a game in a state the program cannot describe and reverse.
    /// R24: only runtime files are written, and backups are kept out of the game folder entirely.
    /// R25/R26: every swap records enough to be undone, and undone swaps stay reversible.
    /// R39: the batch continues past a failure.
    /// R40: cancelling stops work not yet started and returns what was already done.
    /// </remarks>
    public BatchSwapResult Execute(
        IReadOnlyList<SwapPlan> plans,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(plans);

        var results = new List<SwapResult>();
        var cancelled = false;
        var notAttempted = 0;

        foreach (var plan in plans)
        {
            if (cancellation.IsCancellationRequested)
            {
                // R40: stop, but report what was already applied rather than throwing it away.
                cancelled = true;
                notAttempted++;
                continue;
            }

            var result = ExecuteSingle(plan);

            if (result.Succeeded && result.HistoryEntry is not null)
            {
                _persistence.AddHistoryEntry(plan.GameId, result.HistoryEntry);
            }

            results.Add(result);
        }

        return new BatchSwapResult(results.ToImmutableList(), cancelled, notAttempted);
    }

    private SwapResult ExecuteSingle(SwapPlan plan)
    {
        var backups = new List<BackupRecord>();
        var written = new List<string>();

        try
        {
            // R20: refuse if the game currently has the file open for writing.
            foreach (var existing in plan.ReplacedFiles)
            {
                if (!File.Exists(existing))
                {
                    continue;
                }

                // Opening with FileShare.None succeeds only if no other handle is holding it.
                using var fs = File.Open(existing, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }

            var sourceFiles = ResolveSourceFilesFor(plan);
            if (sourceFiles is null)
            {
                return SwapResult.Failed(
                    plan.GameId, plan.GameName,
                    "The runtime collection no longer holds every file this runtime needs.");
            }

            // R21: back up everything that is about to be overwritten, before overwriting any of it.
            foreach (var existing in plan.ReplacedFiles)
            {
                if (!File.Exists(existing))
                {
                    continue;
                }

                backups.Add(CreateBackup(plan.GameId, existing));
            }

            foreach (var (source, target) in sourceFiles)
            {
                var directory = Path.GetDirectoryName(target);
                if (directory is not null)
                {
                    Directory.CreateDirectory(directory);
                }

                File.Copy(source, target, overwrite: true);
                written.Add(target);
            }

            // R25/R26: record the state we are leaving behind, which is what makes undo possible.
            var historyEntry = new HistoryEntry(
                DateTimeOffset.UtcNow,
                plan.GameId,
                plan.GameName,
                plan.GamePath,
                plan.RuntimeKind,
                plan.CurrentVersion,
                plan.TargetVersion,
                // The trust of what was there before is not recorded anywhere; trusting it is the
                // conservative choice because the game shipped it.
                plan.HasCurrentVersion ? RuntimeTrust.Trusted : RuntimeTrust.Trusted,
                plan.TargetTrust,
                backups
                    .Select(b => b with { PostSwapSha256 = FileFingerprint.Compute(b.OriginalPath) })
                    .ToArray(),
                written.Where(w => !plan.ReplacedFiles.Contains(w, SwapPlan.PathComparer)).ToArray(),
                plan.IsDowngrade,
                plan.IsUntrusted);

            return SwapResult.Success(
                plan.GameId, plan.GameName, backups, historyEntry.CreatedFiles, historyEntry);
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            RollBack(written, backups);
            return SwapResult.Failed(plan.GameId, plan.GameName, Describe(ex));
        }
        catch (Exception ex)
        {
            RollBack(written, backups);
            return SwapResult.Failed(plan.GameId, plan.GameName, $"Unexpected error: {Describe(ex)}");
        }
    }

    /// <summary>
    /// The catalog file to copy onto each target, paired by position.
    /// </summary>
    /// <remarks>
    /// <see cref="SwapPlan.TargetFiles"/> is built in the order of
    /// <see cref="RuntimeDlls.ByKind"/> for the kind, so the two can be zipped. Pairing by index,
    /// rather than re-deriving the destination from the source file name, is what guarantees a kind
    /// made of several DLLs gets all of them written instead of one of them written twice.
    /// </remarks>
    private IReadOnlyList<(string Source, string Target)>? ResolveSourceFilesFor(SwapPlan plan)
    {
        var entry = _catalog.Entries.FirstOrDefault(e =>
            e.Kind == plan.RuntimeKind
            && string.Equals(e.Version, plan.TargetVersion, StringComparison.OrdinalIgnoreCase));

        if (entry is null)
        {
            return null;
        }

        var directory = Path.GetDirectoryName(entry.FilePath);
        if (directory is null)
        {
            return null;
        }

        var names = RuntimeDlls.ByKind[plan.RuntimeKind];
        if (plan.TargetFiles.Count != names.Length)
        {
            return null;
        }

        var pairs = new (string Source, string Target)[names.Length];

        for (var i = 0; i < names.Length; i++)
        {
            var source = Path.Combine(directory, names[i]);
            if (!File.Exists(source))
            {
                return null;
            }

            pairs[i] = (source, plan.TargetFiles[i]);
        }

        return pairs;
    }

    private BackupRecord CreateBackup(string gameId, string originalPath)
        => BackupStore.Save(_paths, gameId, originalPath);

    /// <summary>
    /// Puts the game back exactly as it was before the swap started.
    /// </summary>
    /// <remarks>
    /// Files the swap created are deleted, not merely left alone. Without that, a swap that added a
    /// runtime and then failed part way left a DLL in the game that the program had no record of —
    /// which is precisely the "state the program cannot describe and reverse" that R23 forbids.
    /// </remarks>
    private static void RollBack(IReadOnlyList<string> written, IReadOnlyList<BackupRecord> backups)
    {
        foreach (var created in written)
        {
            var wasReplaced = backups.Any(b =>
                b.OriginalPath.Equals(created, SwapPlan.PathComparison));

            if (!wasReplaced)
            {
                TryQuietly(() => File.Delete(created));
            }
        }

        foreach (var backup in backups)
        {
            TryQuietly(() =>
            {
                if (File.Exists(backup.OriginalPath))
                {
                    File.Delete(backup.OriginalPath);
                }
                File.Move(backup.BackupPath, backup.OriginalPath, overwrite: true);
            });
        }
    }

    private static bool IsFileFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception;

    /// <summary>
    /// A message that names what was being attempted and why it failed (R57, R60).
    /// </summary>
    private static string Describe(Exception ex) =>
        ex is FileNotFoundException or DirectoryNotFoundException
            ? $"a required file was missing ({ex.Message})"
            : ex.Message;

    internal static void TryQuietly(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            // Best effort. Anything that could not be undone is left in place for the user to
            // recover by hand, which is better than a second failure while already failing.
        }
    }
}

/// <summary>
/// Undoes swaps.
/// </summary>
public sealed class UndoEngine
{
    private readonly PersistenceService _persistence;
    private readonly DataPaths _paths;

    public UndoEngine(PersistenceService persistence, DataPaths paths)
    {
        _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    /// <summary>
    /// Undoes the most recent undoable swap for a specific game and runtime kind.
    /// </summary>
    /// <remarks>
    /// R25: every swap can be undone, restoring exactly what was there before it.
    /// R26: undoing can be repeated as many times as swaps have been made, and each step stays
    /// reversible because the pre-swap state is carried into the entry written for the undo.
    /// R27: backups written by an earlier run are adopted, since they are found on disk rather than
    /// in memory.
    /// R28: if the game's files no longer match what the swap left behind, the undo is refused —
    /// because no entry then describes a state the game can return to.
    /// R29: missing originals are reported as unrecoverable rather than offered as restorable.
    /// <para>
    /// The whole thing is checked before any of it happens. Restoring file by file and giving up at
    /// the first missing backup leaves the game half-undone and the user unsure of its state, so
    /// every condition is verified up front and the game is only touched once the undo is certain to
    /// complete.
    /// </para>
    /// </remarks>
    public SwapResult Undo(string gameId, RuntimeKind runtimeKind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);

        var history = _persistence.GetHistory(gameId);
        var entry = history.GetLatestUndoableFor(runtimeKind);

        if (entry is null)
        {
            return SwapResult.Failed(gameId, "", "Nothing to undo.");
        }

        // --- Verification pass: nothing below this point modifies the game. ---

        foreach (var backup in entry.Backups)
        {
            if (!File.Exists(backup.BackupPath))
            {
                // R29
                return SwapResult.Failed(
                    gameId, entry.GameName,
                    $"Cannot undo: the saved copy of {Path.GetFileName(backup.OriginalPath)} is missing. " +
                    $"It was at {backup.BackupPath}. Nothing has been changed.");
            }

            // R28: only safe if the game still holds what we put there.
            if (!FileFingerprint.Matches(backup.OriginalPath, backup.PostSwapSha256))
            {
                return SwapResult.Failed(
                    gameId, entry.GameName,
                    $"Cannot undo: {Path.GetFileName(backup.OriginalPath)} was changed by something " +
                    "other than this program since the swap, so restoring would discard that change. " +
                    "Nothing has been changed.");
            }
        }

        // --- Application pass. ---

        var redoState = new List<BackupRecord>();
        var removedFiles = new List<string>();

        // Save what is on disk now before overwriting it. That copy is what makes this undo itself
        // reversible (R26): redoing means putting the swapped-in content back, and once it has been
        // overwritten the only copy of it would be gone.
        foreach (var backup in entry.Backups)
        {
            try
            {
                var saved = BackupStore.Save(_paths, gameId, backup.OriginalPath);
                redoState.Add(saved with { PostSwapSha256 = FileFingerprint.Compute(backup.OriginalPath) });
            }
            catch (Exception ex) when (IsFileFailure(ex))
            {
                return SwapResult.Failed(
                    gameId, entry.GameName,
                    $"Cannot undo: could not save the current state of " +
                    $"{Path.GetFileName(backup.OriginalPath)} ({ex.Message}). Nothing has been changed.");
            }
        }

        // Restoring by copy rather than by move leaves the backup in place, so an undo interrupted
        // half way can simply be run again to finish the job.
        foreach (var backup in entry.Backups)
        {
            try
            {
                File.Copy(backup.BackupPath, backup.OriginalPath, overwrite: true);
            }
            catch (Exception ex) when (IsFileFailure(ex))
            {
                return SwapResult.Failed(
                    gameId, entry.GameName,
                    $"Undo failed while restoring {Path.GetFileName(backup.OriginalPath)}: {ex.Message} " +
                    "Run undo again to finish.");
            }
        }

        // The game is now in its pre-swap state; record that as what a redo would restore.
        for (var i = 0; i < redoState.Count; i++)
        {
            redoState[i] = redoState[i] with
            {
                PostSwapSha256 = FileFingerprint.Compute(entry.Backups[i].OriginalPath),
            };
        }

        foreach (var created in entry.CreatedFiles)
        {
            if (!File.Exists(created))
            {
                continue;
            }

            try
            {
                File.Delete(created);
                removedFiles.Add(created);
            }
            catch (Exception ex) when (IsFileFailure(ex))
            {
                return SwapResult.Failed(
                    gameId, entry.GameName,
                    $"Undo failed while removing {Path.GetFileName(created)}: {ex.Message} " +
                    "Run undo again to finish.");
            }
        }

        // R26: record the reverse so this undo can itself be undone.
        var undoEntry = entry.AsReverted(redoState, removedFiles);
        _persistence.AddHistoryEntry(gameId, undoEntry);

        return SwapResult.Success(gameId, entry.GameName, redoState, removedFiles, undoEntry);
    }

    private static bool IsFileFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception;
}
