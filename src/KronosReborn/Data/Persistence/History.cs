using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text.Json.Serialization;
using KronosReborn.Data.Runtimes;

namespace KronosReborn.Data.Persistence;

/// <summary>
/// A single history entry recording a swap operation on a game.
/// </summary>
/// <remarks>
/// R26: undoing can be repeated as many times as swaps have been made. Each is separately
/// reversible. This entry contains everything needed to undo or redo.
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
/// <param name="BackupPaths">Paths to the backed-up files (for undo).</param>
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
    IReadOnlyList<string> BackupPaths,
    bool WasDowngrade,
    bool WasUntrusted)
{
    /// <summary>A short description for display.</summary>
    public string Describe()
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

        var prev = PreviousVersion ?? "none";
        var direction = WasDowngrade ? "⬇ downgrade" : "⬆ upgrade";
        var trust = WasUntrusted ? " ⚠ untrusted" : "";

        return $"{kindName}: {prev} → {NewVersion} ({direction}{trust})";
    }

    /// <summary>Whether this entry can be undone (has backups).</summary>
    public bool CanUndo => BackupPaths.Count > 0;
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