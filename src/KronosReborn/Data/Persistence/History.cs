using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text.Json.Serialization;
using KronosReborn.Data.Runtimes;

namespace KronosReborn.Data.Persistence;

/// <summary>
/// One file a swap overwrote, and what it would take to put it back.
/// </summary>
/// <remarks>
/// <para>
/// This is deliberately not just a list of backup paths. Undo has to answer one question before it
/// touches anything: <em>is the game still in the state we left it in?</em> The old model could not,
/// because a history entry recorded only where the original had been copied to. Comparing the live
/// file against that backup is meaningless — after a successful swap the two are guaranteed to
/// differ, since the swap is what made them differ. That check consequently rejected every undo,
/// which is the bug <c>PostSwapSha256</c> exists to fix.
/// </para>
/// <para>
/// So the entry records the hash of the file <em>after</em> we wrote it. Undo restores only when the
/// live file still matches, which is what R28 is actually asking.
/// </para>
/// </remarks>
/// <param name="OriginalPath">The file in the game folder that was overwritten.</param>
/// <param name="BackupPath">Where the original content was saved.</param>
/// <param name="PostSwapSha256">SHA-256 of the file as this swap left it, lowercase hex.</param>
public sealed record BackupRecord(
    string OriginalPath,
    string BackupPath,
    string PostSwapSha256);

/// <summary>
/// A single history entry recording a swap operation on a game.
/// </summary>
/// <remarks>
/// R26: undoing can be repeated as many times as swaps have been made, and each is separately
/// reversible. A swap that only added files records them in <paramref name="CreatedFiles"/> rather
/// than <paramref name="Backups"/>, and undo deletes them again — which is why a swap that added a
/// runtime to a game that had none is still reversible.
/// </remarks>
/// <param name="Timestamp">When the swap was performed.</param>
/// <param name="GameId">The game's library identifier.</param>
/// <param name="GameName">The game's display name at the time.</param>
/// <param name="GamePath">The game's installation path at the time.</param>
/// <param name="RuntimeKind">Which runtime was swapped.</param>
/// <param name="PreviousVersion">The version that was there before (null if none).</param>
/// <param name="NewVersion">The version that was installed.</param>
/// <param name="PreviousTrust">Trust level of the previous version.</param>
/// <param name="NewTrust">Trust level of the new version.</param>
/// <param name="Backups">Files that existed and were overwritten, with the state we left them in.</param>
/// <param name="CreatedFiles">Files that did not exist before and were created by this swap.</param>
/// <param name="WasDowngrade">Whether this was a version downgrade (R30).</param>
/// <param name="WasUntrusted">Whether the new version was untrusted (R33).</param>
public sealed record HistoryEntry(
    DateTimeOffset Timestamp,
    string GameId,
    string GameName,
    string GamePath,
    RuntimeKind RuntimeKind,
    string? PreviousVersion,
    string NewVersion,
    RuntimeTrust PreviousTrust,
    RuntimeTrust NewTrust,
    IReadOnlyList<BackupRecord> Backups,
    IReadOnlyList<string> CreatedFiles,
    bool WasDowngrade,
    bool WasUntrusted)
{
    /// <summary>A short description for display.</summary>
    public string Describe()
    {
        var kindName = RuntimeKindNames.Display(RuntimeKind);

        var prev = PreviousVersion ?? "none";
        var direction = WasDowngrade ? "⬇ downgrade" : "⬆ upgrade";
        var trust = WasUntrusted ? " ⚠ untrusted" : "";

        return $"{kindName}: {prev} → {NewVersion} ({direction}{trust})";
    }

    /// <summary>
    /// Whether undoing this entry would change the machine.
    /// </summary>
    /// <remarks>
    /// True if anything at all needs restoring, which is what R25 requires. An earlier version
    /// tested only that backups existed, so undoing a swap that had merely *added* a runtime
    /// reported "nothing to undo" and left the added DLLs in the game forever.
    /// </remarks>
    public bool CanUndo => Backups.Count > 0 || CreatedFiles.Count > 0;

    /// <summary>Every path this entry is responsible for putting back the way it found it.</summary>
    public IEnumerable<string> AffectedPaths =>
        Backups.Select(b => b.OriginalPath).Concat(CreatedFiles);

    /// <summary>An entry describing the reverse of this one, written when an undo succeeds.</summary>
    /// <remarks>
    /// The swap that was just undone put <paramref name="preSwapSha256"/> on disk, so undoing *this*
    /// entry means restoring that state — which is redoing the original swap. Passing the hashes
    /// forward is what makes undo/redo nest properly instead of dead-ending after one step.
    /// </remarks>
    public HistoryEntry AsReverted(
        IReadOnlyList<BackupRecord> preSwapState,
        IReadOnlyList<string> removedFiles)
    {
        ArgumentNullException.ThrowIfNull(preSwapState);
        ArgumentNullException.ThrowIfNull(removedFiles);

        return new HistoryEntry(
            Timestamp,
            GameId,
            GameName,
            GamePath,
            RuntimeKind,
            NewVersion,
            PreviousVersion ?? "none",
            NewTrust,
            PreviousTrust,
            preSwapState,
            removedFiles,
            WasDowngrade,
            WasUntrusted);
    }
}

/// <summary>
/// The complete history for one game.
/// </summary>
/// <remarks>
/// R53: history is retained per game up to a limit, and the limit is changeable by the user.
/// R54: discarding history beyond the limit discards the oldest entries first.
/// </remarks>
/// <param name="GameId">The game's library identifier.</param>
/// <param name="Entries">History entries, newest first.</param>
public sealed record GameHistory(
    string GameId,
    ImmutableList<HistoryEntry> Entries)
{
    /// <summary>Adds a new entry at the front, enforcing the max limit by dropping oldest.</summary>
    public GameHistory Add(HistoryEntry entry, int maxEntries)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxEntries);

        var newEntries = Entries.Insert(0, entry);
        if (newEntries.Count > maxEntries)
        {
            // Drop oldest (which are at the end since we add to front)
            newEntries = newEntries.RemoveRange(maxEntries, newEntries.Count - maxEntries);
        }
        return this with { Entries = newEntries };
    }

    /// <summary>Gets the most recent entry for a specific runtime kind.</summary>
    public HistoryEntry? GetLatestFor(RuntimeKind kind)
        => Entries.FirstOrDefault(e => e.RuntimeKind == kind);

    /// <summary>Gets the most recent entry that could actually be undone.</summary>
    /// <remarks>
    /// R29: a game whose original files are missing is reported as unrecoverable rather than being
    /// presented as restorable, so entries with nothing to restore are skipped rather than offered
    /// and then failing.
    /// </remarks>
    public HistoryEntry? GetLatestUndoableFor(RuntimeKind kind)
        => Entries.FirstOrDefault(e => e.RuntimeKind == kind && e.CanUndo);
}

/// <summary>
/// JSON wrapper for GameHistory.
/// </summary>
internal sealed record GameHistoryData(
    string GameId,
    List<HistoryEntry> Entries)
{
    public GameHistory ToHistory() => new(GameId, Entries.ToImmutableList());

    public static GameHistoryData FromHistory(GameHistory h) => new(h.GameId, [.. h.Entries]);
}
