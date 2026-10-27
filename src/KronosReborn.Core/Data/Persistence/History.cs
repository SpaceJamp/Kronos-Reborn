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

/// <summary>What produced a history entry.</summary>
public enum HistoryAction
{
    /// <summary>A swap the user applied.</summary>
    Swap,

    /// <summary>An undo of an earlier swap or redo.</summary>
    Undo,

    /// <summary>A redo of an earlier undo.</summary>
    Redo,
}

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
/// <param name="Action">What produced the entry. Entries written before this existed read as swaps.</param>
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
    bool WasUntrusted,
    HistoryAction Action = HistoryAction.Swap)
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

    /// <summary>An entry describing the reverse of this one, written when an undo or redo succeeds.</summary>
    /// <remarks>
    /// Reverting this entry again means putting back what <paramref name="savedState"/> holds and
    /// deleting <paramref name="createdFiles"/>, which is the state this entry left behind.
    /// </remarks>
    /// <param name="savedState">Copies of the files the revert overwrote or deleted.</param>
    /// <param name="createdFiles">Files the revert put where nothing was.</param>
    /// <param name="action">Whether this revert is an undo or a redo.</param>
    public HistoryEntry AsReverted(
        IReadOnlyList<BackupRecord> savedState,
        IReadOnlyList<string> createdFiles,
        HistoryAction action = HistoryAction.Undo)
    {
        ArgumentNullException.ThrowIfNull(savedState);
        ArgumentNullException.ThrowIfNull(createdFiles);

        var previousKnown = PreviousVersion is not null && PreviousVersion != "none";

        return new HistoryEntry(
            DateTimeOffset.UtcNow,
            GameId,
            GameName,
            GamePath,
            RuntimeKind,
            NewVersion,
            PreviousVersion ?? "none",
            NewTrust,
            PreviousTrust,
            savedState,
            createdFiles,
            // Going back over an upgrade is a downgrade, and going back over a downgrade is not.
            !WasDowngrade && previousKnown && VersionNumber.IsOlder(PreviousVersion, NewVersion),
            PreviousTrust != RuntimeTrust.Trusted,
            action);
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

    /// <summary>The entry the next undo of this kind reverts, or null when there is none.</summary>
    /// <remarks>
    /// R26: each undo pairs with the newest swap or redo not already undone, so repeated undos walk
    /// back through history instead of toggling between the last two states.
    /// </remarks>
    public HistoryEntry? NextToUndo(RuntimeKind kind)
    {
        var undone = 0;

        foreach (var entry in Entries.Where(e => e.RuntimeKind == kind))
        {
            if (entry.Action == HistoryAction.Undo)
            {
                undone++;
            }
            else if (undone > 0)
            {
                undone--;
            }
            else if (entry.CanUndo)
            {
                return entry;
            }
        }

        return null;
    }

    /// <summary>The undo entry the next redo of this kind reverts, or null when there is none.</summary>
    /// <remarks>A new swap after an undo ends what can be redone, as in any undo stack.</remarks>
    public HistoryEntry? NextToRedo(RuntimeKind kind)
    {
        var redone = 0;

        foreach (var entry in Entries.Where(e => e.RuntimeKind == kind))
        {
            switch (entry.Action)
            {
                case HistoryAction.Redo:
                    redone++;
                    break;
                case HistoryAction.Undo when redone > 0:
                    redone--;
                    break;
                case HistoryAction.Undo:
                    return entry.CanUndo ? entry : null;
                default:
                    return null;
            }
        }

        return null;
    }
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
