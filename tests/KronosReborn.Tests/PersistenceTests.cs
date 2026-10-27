using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using KronosReborn.Data.Persistence;
using KronosReborn.Data.Runtimes;
using Xunit;

namespace KronosReborn.Tests;

/// <summary>
/// Tests for the persistence layer: settings, history, atomic writes, corrupt file handling.
/// </summary>
/// <remarks>
/// Every test here points the service at a temporary directory. The previous version of this file
/// could not do that, because the data directory was a static hard-wired to the user's profile, and
/// said so in comments: "Can't easily inject temp path into static DataDirectory, so this tests the
/// logic by..." followed by a test that asserted nothing, or reimplemented the logic it claimed to
/// be testing. Several of those are now real tests of the real service.
/// </remarks>
public class PersistenceTests
{
    // ------------------------------------------------------------ DataPaths

    [Fact]
    public void DataPathsCreatesItsDirectoryOnDemand()
    {
        using var temp = new TemporaryDirectory();
        var paths = new DataPaths(Path.Combine(temp.Root, "data"));

        Assert.False(Directory.Exists(paths.Root));

        paths.EnsureCreated();

        Assert.True(Directory.Exists(paths.Root));
    }

    [Fact]
    public void DataPathsKeepsEveryKindOfFileItOwnsTogether()
    {
        using var temp = new TemporaryDirectory();
        var paths = new DataPaths(Path.Combine(temp.Root, "data"));

        Assert.Equal(paths.Root, Path.GetDirectoryName(paths.SettingsPath));
        Assert.Equal(paths.Root, Path.GetDirectoryName(paths.CatalogPath));
        Assert.Equal(paths.Root, Path.GetDirectoryName(paths.HistoryDirectory));
        Assert.Equal(paths.Root, Path.GetDirectoryName(paths.BackupDirectory));
    }

    [Fact]
    public void DataPathsSafeNameRemovesCharactersThatCannotBeInAFileName()
    {
        const string awkward = "game:with*invalid?chars\"<>|and/slashes\\here";
        var safe = DataPaths.SafeName(awkward);

        foreach (var invalid in new[] { ':', '*', '?', '"', '<', '>', '|', '/', '\\' })
        {
            Assert.DoesNotContain(invalid, safe);
        }

        // The real requirement is that the result is usable as a name, so check that rather than
        // the absence of a particular character. Enumerating the invalid set is avoided because it
        // includes control characters, which behave oddly in string comparisons.
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Root, safe);
        File.WriteAllText(path, "x");

        Assert.True(File.Exists(path));
        Assert.Equal(safe, Path.GetFileName(path));
    }

    [Fact]
    public void DataPathsSafeNameDistinguishesIdsThatSanitiseAlike()
    {
        // Two ids whose only difference is a character that cannot appear in a file name. If these
        // collided, two different games would share one history file and one backup folder, and one
        // game's undo would restore the other's files.
        var first = DataPaths.SafeName("game:1");
        var second = DataPaths.SafeName("game?1");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void DataPathsSafeNameIsStable()
    {
        Assert.Equal(DataPaths.SafeName("steam-620"), DataPaths.SafeName("steam-620"));
    }

    [Fact]
    public void DataPathsForCurrentUserIsUnderLocalApplicationData()
    {
        // R43: per-user and machine-wide installations write to the same place for a given user.
        var paths = DataPaths.ForCurrentUser();

        Assert.Equal(DataPaths.AppFolderName, Path.GetFileName(paths.Root));
        Assert.StartsWith(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            paths.Root,
            StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------ Settings

    [Fact]
    public void SettingsDefaultHasCorrectValues()
    {
        var s = Settings.Default;

        Assert.False(s.AllowUntrustedRuntimes); // R42: off by default
        Assert.Equal(50, s.MaxHistoryPerGame);  // R53: default limit
        Assert.Equal(1600, s.WindowWidth);
        Assert.Equal(960, s.WindowHeight);
        Assert.False(s.WindowMaximized);
    }

    [Fact]
    public void SettingsWithLeavesEverythingElseAlone()
    {
        var original = Settings.Default.With(maxHistoryPerGame: 7, windowWidth: 1200);

        var changed = original.With(allowUntrustedRuntimes: true);

        Assert.True(changed.AllowUntrustedRuntimes);
        Assert.Equal(7, changed.MaxHistoryPerGame);
        Assert.Equal(1200, changed.WindowWidth);
    }

    [Fact]
    public void SettingsRoundTripThroughTheirFile()
    {
        using var temp = new TemporaryDirectory();
        var original = Settings.Default.With(
            allowUntrustedRuntimes: true, maxHistoryPerGame: 25, windowWidth: 1200, windowHeight: 800, windowMaximized: true);

        NewService(temp).SaveSettings(original);

        Assert.Equal(original, NewService(temp).Load().Settings);
    }

    [Fact]
    public void ASettingsFileFromAnOlderVersionStillLoads()
    {
        using var temp = new TemporaryDirectory();
        var service = NewService(temp);

        // Settings that no longer exist, and one that is missing, as an older version wrote them.
        File.WriteAllText(service.Paths.SettingsPath, """
            { "enabledLibraryRoots": [], "groupByLibrary": false, "themeMode": "Dark",
              "allowUntrustedRuntimes": true, "windowWidth": 1300 }
            """);

        var result = service.Load();

        Assert.Empty(result.Problems);
        Assert.True(result.Settings.AllowUntrustedRuntimes);
        Assert.Equal(1300, result.Settings.WindowWidth);
        Assert.Equal(50, result.Settings.MaxHistoryPerGame);
    }

    // ------------------------------------------------------------ HistoryEntry

    [Fact]
    public void HistoryEntryDescribeFormatsCorrectly()
    {
        var desc = Entry("3.7.10", "3.8.0", wasDowngrade: false, wasUntrusted: false)
            .Describe();

        Assert.Contains("DLSS", desc);
        Assert.Contains("3.7.10", desc);
        Assert.Contains("3.8.0", desc);
        Assert.Contains("upgrade", desc);
        Assert.DoesNotContain("untrusted", desc);
    }

    [Fact]
    public void HistoryEntryDescribeShowsDowngradeAndUntrusted()
    {
        var desc = Entry("3.1.2", "3.0.0", wasDowngrade: true, wasUntrusted: true,
                kind: RuntimeKind.Fsr, newTrust: RuntimeTrust.Untrusted)
            .Describe();

        Assert.Contains("downgrade", desc);
        Assert.Contains("untrusted", desc);
        Assert.Contains("FSR", desc);
    }

    [Fact]
    public void HistoryEntryCanUndoWhenThereAreBackups()
    {
        var entry = Entry("3.7.10", "3.8.0", backups:
            [new BackupRecord(@"C:\G\nvngx_dlss.dll", @"C:\data\backups\x", new string('a', 64))]);

        Assert.True(entry.CanUndo);
        Assert.Equal([@"C:\G\nvngx_dlss.dll"], entry.AffectedPaths);
    }

    [Fact]
    public void HistoryEntryCanUndoWhenTheSwapOnlyAddedFiles()
    {
        // A swap that put a runtime into a game which had none has nothing to back up, but it still
        // has something to reverse: the files it created.
        var entry = Entry("3.7.10", "3.8.0", backups: [], createdFiles: [@"C:\G\nvngx_dlss.dll"]);

        Assert.True(entry.CanUndo);
        Assert.Equal([@"C:\G\nvngx_dlss.dll"], entry.AffectedPaths);
    }

    [Fact]
    public void HistoryEntryCannotUndoWhenItChangedNothing()
    {
        Assert.False(Entry("3.7.10", "3.8.0", backups: [], createdFiles: []).CanUndo);
    }

    // ------------------------------------------------------------ GameHistory

    [Fact]
    public void GameHistoryAddsEntriesNewestFirst()
    {
        var h = new GameHistory("1", ImmutableList<HistoryEntry>.Empty);

        h = h.Add(Entry("1.0", "1.0", at: DateTimeOffset.UtcNow.AddMinutes(-10)), 10);
        h = h.Add(Entry("1.0", "2.0", at: DateTimeOffset.UtcNow), 10);

        Assert.Equal(2, h.Entries.Count);
        Assert.Equal("2.0", h.Entries[0].NewVersion); // newest first
        Assert.Equal("1.0", h.Entries[1].NewVersion);
    }

    [Fact]
    public void GameHistoryEnforcesMaxLimitDropsOldestFirst()
    {
        // R54: discarding history beyond the limit discards the oldest entries first.
        var h = new GameHistory("1", ImmutableList<HistoryEntry>.Empty);

        for (var i = 0; i < 5; i++)
        {
            h = h.Add(Entry("1.0", $"{i}.0", at: DateTimeOffset.UtcNow.AddMinutes(-i)), maxEntries: 3);
        }

        Assert.Equal(3, h.Entries.Count);
        Assert.Equal(["4.0", "3.0", "2.0"], h.Entries.Select(e => e.NewVersion));
    }

    [Fact]
    public void GameHistoryNextToUndoIsTheNewestOfThatKind()
    {
        var h = new GameHistory("1", ImmutableList<HistoryEntry>.Empty);
        BackupRecord[] saved = [new BackupRecord(@"C:\G\a.dll", @"C:\data\b", new string('a', 64))];

        h = h.Add(Entry("1.0", "1.0", at: DateTimeOffset.UtcNow.AddMinutes(-10), backups: saved), 10);
        h = h.Add(Entry("3.0", "3.0", at: DateTimeOffset.UtcNow.AddMinutes(-5), kind: RuntimeKind.Fsr, backups: saved), 10);
        h = h.Add(Entry("1.0", "2.0", at: DateTimeOffset.UtcNow, backups: saved), 10);

        Assert.Equal("2.0", h.NextToUndo(RuntimeKind.Dlss)!.NewVersion);
        Assert.Equal("3.0", h.NextToUndo(RuntimeKind.Fsr)!.NewVersion);
    }

    [Fact]
    public void GameHistoryGetLatestUndoableSkipsEntriesThatChangedNothing()
    {
        var h = new GameHistory("1", ImmutableList<HistoryEntry>.Empty);

        // Newest first, so the entry with nothing to undo is the most recent one.
        h = h.Add(Entry("1.0", "1.5", backups:
            [new BackupRecord(@"C:\G\a.dll", @"C:\data\b", new string('a', 64))]), 10);
        h = h.Add(Entry("1.0", "2.0", backups: [], createdFiles: []), 10);

        // It must not be offered as undoable, so the one below it is found instead.
        Assert.Equal("1.5", h.NextToUndo(RuntimeKind.Dlss)!.NewVersion);
    }

    [Fact]
    public void GameHistoryAsRevertedDescribesTheSwapBackwards()
    {
        var original = Entry(
            "3.7.10", "3.8.0",
            backups: [new BackupRecord(@"C:\G\a.dll", @"C:\data\b", "hash-before")],
            createdFiles: [@"C:\G\new.dll"]);

        var reverted = original.AsReverted(
            [new BackupRecord(@"C:\G\a.dll", @"C:\data\c", "hash-after")],
            [@"C:\G\new.dll"]);

        // Undoing a swap means going back to what was there before it.
        Assert.Equal("3.8.0", reverted.PreviousVersion);
        Assert.Equal("3.7.10", reverted.NewVersion);
        Assert.Equal(@"C:\data\c", reverted.Backups[0].BackupPath);
        Assert.Equal("hash-after", reverted.Backups[0].PostSwapSha256);
        Assert.True(reverted.CanUndo);

        // Going back over an upgrade is a downgrade, and it is recorded as an undo.
        Assert.True(reverted.WasDowngrade);
        Assert.Equal(HistoryAction.Undo, reverted.Action);
        Assert.False(reverted.AsReverted([], [], HistoryAction.Redo).WasDowngrade);
    }

    [Fact]
    public void GameHistoryDataRoundTrips()
    {
        var h = new GameHistory("123", ImmutableList<HistoryEntry>.Empty)
            .Add(Entry("1.0", "2.0",
                backups: [new BackupRecord(@"C:\G\a.dll", @"C:\data\b", new string('c', 64))],
                createdFiles: [@"C:\G\new.dll"]), 10);

        var restored = GameHistoryData.FromHistory(h).ToHistory();

        Assert.Equal(h.GameId, restored.GameId);
        Assert.Equal(h.Entries.Count, restored.Entries.Count);
        Assert.Equal("2.0", restored.Entries[0].NewVersion);
        Assert.Equal(new string('c', 64), restored.Entries[0].Backups[0].PostSwapSha256);
        Assert.Equal([@"C:\G\new.dll"], restored.Entries[0].CreatedFiles);
    }

    // ------------------------------------------------------------ PersistenceService

    [Fact]
    public void LoadReturnsDefaultsWhenNothingHasBeenSaved()
    {
        using var temp = new TemporaryDirectory();
        var service = NewService(temp);

        var result = service.Load();

        Assert.Empty(result.Problems);
        Assert.Equal(Settings.Default, result.Settings);
        Assert.Empty(result.Histories);
    }

    [Fact]
    public void SettingsSurviveASaveAndReload()
    {
        using var temp = new TemporaryDirectory();

        NewService(temp).SaveSettings(Settings.Default.With(maxHistoryPerGame: 12, allowUntrustedRuntimes: true));

        var reloaded = NewService(temp).Load();

        Assert.Equal(12, reloaded.Settings.MaxHistoryPerGame);
        Assert.True(reloaded.Settings.AllowUntrustedRuntimes);
        Assert.Empty(reloaded.Problems);
    }

    [Fact]
    public void HistorySurvivesASaveAndReload()
    {
        using var temp = new TemporaryDirectory();

        NewService(temp).AddHistoryEntry("game-1", Entry("3.7.10", "3.8.0",
            backups: [new BackupRecord(@"C:\G\a.dll", @"C:\data\b", new string('d', 64))]));

        var reloaded = NewService(temp).Load();

        // This is the round trip the undo feature depends on: an entry written by one run has to be
        // readable by the next, or a program's backups are no use to the person who needs them.
        var history = Assert.Single(reloaded.Histories);
        Assert.Equal("game-1", history.GameId);
        Assert.Equal("3.8.0", history.Entries[0].NewVersion);
        Assert.Equal(new string('d', 64), history.Entries[0].Backups[0].PostSwapSha256);
        Assert.Empty(reloaded.Problems);
    }

    [Fact]
    public void HistoryLimitComesFromSettings()
    {
        using var temp = new TemporaryDirectory();
        var service = NewService(temp);

        service.SaveSettings(Settings.Default.With(maxHistoryPerGame: 2));

        for (var i = 0; i < 5; i++)
        {
            service.AddHistoryEntry("game-1", Entry("1.0", $"{i}.0", at: DateTimeOffset.UtcNow.AddMinutes(-i)));
        }

        // R53/R54: the limit is the user's setting, and the oldest go first.
        Assert.Equal(2, service.GetHistory("game-1").Entries.Count);
        Assert.Equal(["4.0", "3.0"], service.GetHistory("game-1").Entries.Select(e => e.NewVersion));
    }

    [Fact]
    public void HistoriesForDifferentGamesAreKeptApart()
    {
        using var temp = new TemporaryDirectory();
        var service = NewService(temp);

        service.AddHistoryEntry("game-1", Entry("1.0", "1.1"));
        service.AddHistoryEntry("game-2", Entry("1.0", "2.1"));

        Assert.Single(service.GetHistory("game-1").Entries);
        Assert.Single(service.GetHistory("game-2").Entries);
        Assert.Equal("1.1", service.GetHistory("game-1").Entries[0].NewVersion);
        Assert.Equal("2.1", service.GetHistory("game-2").Entries[0].NewVersion);

        // And after a reload, so the separation survives a restart.
        Assert.Equal(2, NewService(temp).Load().Histories.Count);
    }

    [Fact]
    public void GetHistoryForAnUnknownGameIsEmptyRatherThanNull()
    {
        using var temp = new TemporaryDirectory();

        Assert.Empty(NewService(temp).GetHistory("never-seen").Entries);
    }

    [Fact]
    public void SavingLeavesNoTemporaryFilesBehind()
    {
        using var temp = new TemporaryDirectory();
        var service = NewService(temp);

        service.SaveSettings(Settings.Default.With(maxHistoryPerGame: 3));
        service.AddHistoryEntry("game-1", Entry("1.0", "2.0"));

        // R52 writes via a temp file and a move. If a crash left the temp file behind it would
        // accumulate in the data directory forever, and would confuse anyone reading it by hand.
        var leftovers = Directory
            .EnumerateFiles(service.Paths.Root, "*", SearchOption.AllDirectories)
            .Where(p => p.Contains(".tmp"))
            .ToArray();

        Assert.Empty(leftovers);
    }

    [Fact]
    public void CorruptSettingsAreReportedAndDefaultsAreUsed()
    {
        using var temp = new TemporaryDirectory();
        var service = NewService(temp);

        File.WriteAllText(service.Paths.SettingsPath, "{ this is not json");

        var result = service.Load();

        // R51: reported, not fatal.
        Assert.Equal(Settings.Default, result.Settings);
        var problem = Assert.Single(result.Problems);
        Assert.Equal("Settings", problem.Scope);
        Assert.Equal(service.Paths.SettingsPath, problem.Target);
        Assert.False(string.IsNullOrWhiteSpace(problem.Detail));

        // And the unreadable file is left alone for the user to look at, rather than being deleted
        // or overwritten with defaults behind their back.
        Assert.Equal("{ this is not json", File.ReadAllText(service.Paths.SettingsPath));
    }

    [Fact]
    public void OneCorruptHistoryFileDoesNotDiscardTheOthers()
    {
        using var temp = new TemporaryDirectory();
        var service = NewService(temp);

        service.AddHistoryEntry("good-1", Entry("1.0", "2.0"));
        service.AddHistoryEntry("good-2", Entry("1.0", "3.0"));

        // R5: a library configuration that cannot be parsed must not discard what was already found.
        Directory.CreateDirectory(service.Paths.HistoryDirectory);
        File.WriteAllText(Path.Combine(service.Paths.HistoryDirectory, "broken.json"), "{ nope");

        var result = NewService(temp).Load();

        Assert.Equal(2, result.Histories.Count);
        Assert.Single(result.Problems);
        Assert.Equal("History", result.Problems[0].Scope);

        // Kept aside, so the next swap of that game does not write a fresh history over it.
        Assert.False(File.Exists(Path.Combine(service.Paths.HistoryDirectory, "broken.json")));
        Assert.Single(Directory.GetFiles(service.Paths.HistoryDirectory, "broken.json.unreadable-*"));
    }

    [Fact]
    public void BackupsOfDroppedEntriesAreDeleted()
    {
        using var temp = new TemporaryDirectory();
        var service = NewService(temp);
        service.SaveSettings(Settings.Default.With(maxHistoryPerGame: 1));

        var backups = Path.Combine(service.Paths.BackupDirectory, "game");
        Directory.CreateDirectory(backups);
        var old = Path.Combine(backups, "old.dll");
        var current = Path.Combine(backups, "current.dll");
        File.WriteAllText(old, "x");
        File.WriteAllText(current, "x");

        service.AddHistoryEntry("game-1", Entry("1.0", "2.0",
            backups: [new BackupRecord(@"C:\G\a.dll", old, new string('a', 64))]));
        service.AddHistoryEntry("game-1", Entry("2.0", "3.0",
            backups: [new BackupRecord(@"C:\G\a.dll", current, new string('b', 64))]));

        // The first entry fell off the end of the history, and its saved copy with it.
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(current));
    }

    [Fact]
    public void AHistoryLimitOfZeroStillRecordsTheSwap()
    {
        using var temp = new TemporaryDirectory();
        var service = NewService(temp);
        service.SaveSettings(Settings.Default.With(maxHistoryPerGame: 0));

        // Used to throw, which left the swap applied with nothing to undo it.
        service.AddHistoryEntry("game-1", Entry("1.0", "2.0"));

        Assert.Single(service.GetHistory("game-1").Entries);
    }

    [Fact]
    public void HistoryFileNamesCannotEscapeTheHistoryDirectory()
    {
        using var temp = new TemporaryDirectory();
        var service = NewService(temp);

        // A game id full of path separators and drive letters must not be able to steer the write
        // somewhere else.
        service.AddHistoryEntry("../../evil/C:\\Windows\\System32\\config", Entry("1.0", "2.0"));

        var written = Directory.EnumerateFiles(service.Paths.HistoryDirectory).ToArray();
        var single = Assert.Single(written);

        Assert.Equal(service.Paths.HistoryDirectory, Path.GetDirectoryName(single));

        // And it still round-trips as one game.
        Assert.Single(NewService(temp).Load().Histories);
    }

    [Fact]
    public void SaveHistoryReplacesTheWholeEntry()
    {
        using var temp = new TemporaryDirectory();
        var service = NewService(temp);

        var history = new GameHistory("game-1", ImmutableList.Create(
            Entry("1.0", "2.0"),
            Entry("1.0", "1.5")));

        service.SaveHistory(history);

        // A fresh service has to load before it can answer anything about stored history.
        var reloaded = NewService(temp).Load();
        var stored = Assert.Single(reloaded.Histories);

        Assert.Equal(2, stored.Entries.Count);
        Assert.Equal("2.0", stored.Entries[0].NewVersion);
    }

    // ------------------------------------------------------------ Helpers

    private static PersistenceService NewService(TemporaryDirectory temp) =>
        new(new DataPaths(Path.Combine(temp.Root, "data")));

    private static HistoryEntry Entry(
        string previous,
        string current,
        bool wasDowngrade = false,
        bool wasUntrusted = false,
        RuntimeKind kind = RuntimeKind.Dlss,
        RuntimeTrust newTrust = RuntimeTrust.Trusted,
        IReadOnlyList<BackupRecord>? backups = null,
        IReadOnlyList<string>? createdFiles = null,
        DateTimeOffset? at = null) =>
        new(
            at ?? DateTimeOffset.UtcNow,
            "game-1", "Test Game", @"C:\Games\Test",
            kind,
            previous, current,
            RuntimeTrust.Trusted, newTrust,
            backups ?? [],
            createdFiles ?? [],
            wasDowngrade, wasUntrusted);
}
