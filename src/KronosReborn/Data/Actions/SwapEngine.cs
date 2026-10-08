using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
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
    IReadOnlyList<string> TargetFiles,
    IReadOnlyList<RuntimeKind>? UnmatchedParts = null)
{
    /// <summary>
    /// Parts of the same runtime this game already carries that could not be brought along.
    /// </summary>
    /// <remarks>
    /// DLSS, Frame Generation and Ray Reconstruction are supported at matching versions. When the
    /// collection does not hold a companion at the version being installed, that companion cannot be
    /// brought into step. Rather than refuse the whole upgrade — which would mean a game stuck on an
    /// old DLSS version until the user manually imports something — the swap goes ahead and the
    /// preview says plainly which parts were left behind, because silently leaving a game running
    /// Frame Generation built for a different Super Resolution version is the problem this exists to
    /// prevent.
    /// </remarks>
    public IReadOnlyList<RuntimeKind> Unmatched => UnmatchedParts ?? [];

    /// <summary>Whether this plan involves a change that warrants a warning.</summary>
    public bool HasWarning => IsDowngrade || IsUntrusted || Unmatched.Count > 0;

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

            // Spelled out rather than left to the user to work out from a version number: a stack that
            // is out of step is the exact situation this whole rule exists to avoid, so the preview
            // has to say what is out of step and what would fix it.
            var leftBehind = Unmatched.Count == 0
                ? string.Empty
                : " — but " + string.Join(" and ", Unmatched.Select(RuntimeKindNames.Display)) +
                  " stays on the version it already has. Import " + TargetVersion +
                  " of it too, or the game will run the parts out of step.";

            return $"{GameName}: {kindName} {current} → {TargetVersion}{warning}{leftBehind}";
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

            // R38: a swap to the version already installed has nothing to do. Offered as a plan it
            // would copy a file onto itself, which either throws or leaves the game untouched while
            // reporting a change — and the picker lists the game's own version first, so this is the
            // option most likely to be chosen by accident.
            if (hasCurrentVersion
                && VersionNumber.AreEquivalent(currentVersion, targetEntry.Version))
            {
                continue;
            }

            var targets = ResolveTargetFiles(game.GamePath, runtimeKind);
            if (targets.Count == 0)
            {
                continue; // Can't install if we don't know where to put it
            }

            // Never write a file onto itself. The collection entry for a version found in a game
            // points at that game's own DLL, so selecting it would otherwise be a self-copy.
            if (targets.Any(target => sourceFiles.Any(
                    source => source.Equals(target, SwapPlan.PathComparison))))
            {
                continue;
            }

            // R30/R32: downgrade warning only when both versions are known
            var isDowngrade = false;
            if (currentVersion != null && targetEntry.Version != null)
            {
                isDowngrade = VersionComparer.IsOlder(targetEntry.Version, currentVersion);
            }

            // R33: warn before swapping in a runtime from an untrusted source. This covers a file the user
            // explicitly accepted at import time as well as one that was never accepted: R42 governs
            // whether an untrusted file may enter the collection, while R33 governs whether the user is
            // told when it is about to be put into a game. Accepting it once is not a statement that
            // it is genuine, and the warning is the only thing standing between the two.
            var isUntrusted = targetEntry.Trust != RuntimeTrust.Trusted;

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
    /// <summary>
    /// Plans a swap for one game together with every part of the same runtime it already carries.
    /// </summary>
    /// <remarks>
    /// <para>
    /// DLSS, Frame Generation and Ray Reconstruction are one runtime in up to three files and are only
    /// supported at matching versions. Choosing a DLSS version therefore brings the rest of the stack
    /// with it, so a game cannot be left running Frame Generation built for a different Super
    /// Resolution version than the one beside it.
    /// </para>
    /// <para>
    /// A part is included only when the game already has it and the collection holds that part at the
    /// same version. Both conditions matter. Adding Frame Generation to a game that never shipped it is
    /// adding runtime support to that game, which the specification puts out of scope, and offering a
    /// companion version the collection does not hold would produce a plan that cannot be carried out.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<SwapPlan> PlanWithFamily(
        IEnumerable<SwapGameInfo> games,
        RuntimeCatalog catalog,
        RuntimeKind runtimeKind,
        string targetEntryId,
        Settings settings)
    {
        ArgumentNullException.ThrowIfNull(games);
        ArgumentNullException.ThrowIfNull(catalog);

        if (!catalog.TryGet(targetEntryId, out var target) || target is null)
        {
            return [];
        }

        var version = target.Version;
        var all = games.ToList();

        var companions = RuntimeDlls.Related(runtimeKind)
            .Where(kind => kind != runtimeKind)
            .Where(kind => catalog.Entries.Any(e =>
                e.Kind == kind && VersionNumber.AreEquivalent(e.Version, version)))
            .ToList();

        var plans = Plan(all, catalog, runtimeKind, targetEntryId, settings).ToList();

        foreach (var game in all)
        {
            var companionsForGame = new List<SwapPlan>();

            foreach (var kind in companions)
            {
                companionsForGame.AddRange(PlanCompanion(game, catalog, kind, version, settings));
            }

            plans.AddRange(companionsForGame);

            // Anything the game carries but that could not be planned above is recorded on the
            // primary plan, so the preview can say the stack would be left half-matched rather than
            // letting it pass unremarked.
            if (plans.Count > 0 && !string.Equals(plans[0].GameId, game.GameId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var leftBehind = LeftBehind(game, catalog, runtimeKind, version, companionsForGame);

            if (leftBehind.Count > 0)
            {
                var index = plans.ToList().FindIndex(p =>
                    string.Equals(p.GameId, game.GameId, StringComparison.OrdinalIgnoreCase) &&
                    p.RuntimeKind == runtimeKind);

                if (index >= 0)
                {
                    plans[index] = plans[index] with { UnmatchedParts = leftBehind };
                }
            }
        }

        return plans;
    }

    /// <summary>
    /// Parts of the same runtime a game carries that this swap would leave at a different version.
    /// </summary>
    private static IReadOnlyList<RuntimeKind> LeftBehind(
        SwapGameInfo game,
        RuntimeCatalog catalog,
        RuntimeKind runtimeKind,
        string version,
        IReadOnlyList<SwapPlan> plannedCompanions)
    {
        if (game.Snapshot is not { } snapshot)
        {
            return [];
        }

        var planned = plannedCompanions.Select(p => p.RuntimeKind).ToHashSet();

        return
        [
            .. RuntimeDlls.Related(runtimeKind)
                .Where(kind => kind != runtimeKind && !planned.Contains(kind))
                .Where(kind =>
                    snapshot.Runtimes.Any(r => r.Kind == kind && r.Exists))
                // A part the game already runs at the chosen version is in step, and nothing about it
                // is being left behind. Only a part that would end up on a different version counts.
                .Where(kind =>
                    !VersionNumber.AreEquivalent(
                        snapshot.Runtimes.First(r => r.Kind == kind).Version,
                        version))
                .Where(kind =>
                    !catalog.Entries.Any(e =>
                        e.Kind == kind && VersionNumber.AreEquivalent(e.Version, version))),
        ];
    }

    /// <summary>
    /// Plans one companion part of a matched set for one game, where that game already has it.
    /// </summary>
    private static IReadOnlyList<SwapPlan> PlanCompanion(
        SwapGameInfo game,
        RuntimeCatalog catalog,
        RuntimeKind kind,
        string version,
        Settings settings)
    {
        _ = settings;

        if (!game.ExistsOnDisk || game.Snapshot is not { } snapshot)
        {
            return [];
        }

        var current = snapshot.Runtimes.FirstOrDefault(r => r.Kind == kind);
        if (current is not { Exists: true })
        {
            // The game does not carry this part, and adding it is out of scope.
            return [];
        }

        // Already running it: nothing to do.
        if (VersionNumber.AreEquivalent(current.Version, version))
        {
            return [];
        }

        var entry = catalog.Entries.FirstOrDefault(e =>
            e.Kind == kind && VersionNumber.AreEquivalent(e.Version, version));

        if (entry is null)
        {
            return [];
        }

        var targets = ResolveTargetFiles(game.GamePath, kind);
        if (targets.Count == 0)
        {
            return [];
        }

        return
        [
            new SwapPlan(
                game.GameId,
                game.GameName,
                game.GamePath,
                kind,
                current.Version,
                version,
                entry.Trust,
                IsDowngrade: false,
                IsUntrusted: entry.Trust != RuntimeTrust.Trusted,
                HasCurrentVersion: true,
                SourceFiles: targets.Where(File.Exists).ToArray(),
                TargetFiles: targets)
        ];
    }

    /// <summary>
    /// The catalog files to copy, one per DLL the runtime kind consists of.
    /// </summary>
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
        // Split on dots and commas. Versions are normalised when they are read, so this should only
        // ever see dots, but a comma-separated value that slipped through would otherwise arrive here
        // as one unparseable token and become zero — which is what made every DLSS version compare
        // equal to every other, and silenced the downgrade warning R30 requires. Splitting on both
        // separators means a value that has not been normalised still compares correctly.
        return version.Split('.', ',', '-', '+')
            .Select(s => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0)
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
    [SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "An instance method because it is the engine's operation, not a function of " +
                        "its arguments. The persistence it needs is reached through ExecuteSingle, " +
                        "and making this static would invite a second entry point that records no " +
                        "history at all.")]
    public BatchSwapResult Execute(
        IReadOnlyList<SwapPlan> plans,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(plans);

        var results = new List<SwapResult>();
        var cancelled = false;
        var notAttempted = 0;

        // A game's plans are one unit. DLSS, Frame Generation and Ray Reconstruction must end up at
        // matching versions, so a failure part way through one game rolls back the parts already done
        // to that game rather than leaving it half swapped. Failures still do not stop other games,
        // which is R39.
        foreach (var group in GroupByGame(plans))
        {
            if (cancellation.IsCancellationRequested)
            {
                // R40: stop, but report what was already applied rather than throwing it away.
                cancelled = true;
                notAttempted += group.Count;
                continue;
            }

            notAttempted += ApplyGroup(group, results);
        }

        return new BatchSwapResult(results.ToImmutableList(), cancelled, notAttempted);
    }

    /// <summary>The plans that make up each game's change, in the order they must be applied.</summary>
    private static IReadOnlyList<IReadOnlyList<SwapPlan>> GroupByGame(IReadOnlyList<SwapPlan> plans) =>
        [.. plans.GroupBy(p => p.GameId, StringComparer.OrdinalIgnoreCase)
                 .Select(g => (IReadOnlyList<SwapPlan>)g.ToArray())];

    /// <summary>
    /// Applies one game's plans together.
    /// </summary>
    /// <returns>How many of them were never attempted because an earlier part failed.</returns>
    private int ApplyGroup(IReadOnlyList<SwapPlan> group, List<SwapResult> results)
    {
        var applied = new List<SwapResult>();

        for (var i = 0; i < group.Count; i++)
        {
            var result = ExecuteSingle(group[i]);
            results.Add(result);

            if (!result.Succeeded)
            {
                // The game is now inconsistent with itself. Put back what was already done to it, so
                // the parts that did apply do not survive on their own.
                foreach (var earlier in applied)
                {
                    RollBack(earlier.CreatedFiles, earlier.Backups);
                }

                // Each earlier part is reported as failed, with the reason, rather than being left
                // claiming success: R63 is explicit that a summary must account for every item.
                foreach (var earlier in applied)
                {
                    var index = results.IndexOf(earlier);
                    if (index < 0)
                    {
                        continue;
                    }

                    results[index] = earlier with
                    {
                        Succeeded = false,
                        HistoryEntry = null,
                        ErrorMessage =
                            "Rolled back because another part of the same runtime could not be applied.",
                    };
                }

                return group.Count - i - 1;
            }

            applied.Add(result);
        }

        // Every part applied, so the game can now be recorded as changed.
        foreach (var result in applied)
        {
            if (result.HistoryEntry is { } entry)
            {
                _persistence.AddHistoryEntry(group[0].GameId, entry);
            }
        }

        return 0;
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
