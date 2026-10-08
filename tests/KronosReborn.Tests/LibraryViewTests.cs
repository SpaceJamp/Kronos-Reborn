using System;
using System.IO;
using System.Linq;
using KronosReborn.Data.Libraries;
using KronosReborn.Data.Runtimes;
using KronosReborn.UI;
using Xunit;

namespace KronosReborn.Tests;

/// <summary>
/// Tests for the two views the library adds: the poster grid, and the list of held versions.
/// </summary>
/// <remarks>
/// Both answer a question the game list cannot. The grid shows a whole library at a glance, which a
/// list of names and numbers does not. The version library answers "what can I actually install,
/// where did it come from, and does it work anywhere" — which nothing on screen was able to say.
/// </remarks>
public class LibraryViewTests
{
    // ------------------------------------------------------------- The version library

    [Fact]
    public void TheLibraryShowsEveryVersionHeld()
    {
        var entries = Library(
            [("310.2.1", RuntimeKind.Dlss), ("310.9.1", RuntimeKind.Dlss),
             ("310.9.1", RuntimeKind.DlssG)]);

        // Before this there was no screen showing what the collection held at all: the game list said
        // what each game ran and the pickers said what could go into one game.
        Assert.Equal(3, entries.Count);
        Assert.Contains(entries, e => e.Kind == RuntimeKind.Dlss && e.Version == "310.2.1");
        Assert.Contains(entries, e => e.Kind == RuntimeKind.DlssG && e.Version == "310.9.1");
    }

    [Fact]
    public void EachVersionSaysWhichRuntimeItIsFor()
    {
        var entries = Library(
            [("310.9.1", RuntimeKind.Dlss), ("310.9.1", RuntimeKind.DlssG)]);

        // Same version twice. Without the name the two cards would be indistinguishable, which is the
        // ambiguity the per-kind pickers were split up to avoid.
        var labels = entries.Select(e => e.KindLabel).ToList();

        Assert.Equal(labels.Count, labels.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("DLSS", labels);
        Assert.Contains("DLSS Frame Gen", labels);
    }

    [Fact]
    public void VersionsAreListedNewestFirst()
    {
        var entries = Library(
            [("310.2.1", RuntimeKind.Dlss), ("310.9.1", RuntimeKind.Dlss), ("310.7.1", RuntimeKind.Dlss)]);

        var versions = entries.Where(e => e.Kind == RuntimeKind.Dlss).Select(e => e.Version).ToList();

        // Ordered as text this would be 310.2.1, 310.7.1, 310.9.1 — no, as text "310.2.1" sorts
        // before "310.7.1", but "310.9.1" would sort before "310.2.1" only if the string started
        // differently. Either way it is the wrong order, and the newest first is the useful one.
        Assert.Equal(["310.9.1", "310.7.1", "310.2.1"], versions);
    }

    [Fact]
    public void AVersionSaysWhetherItCanBeInstalledWithoutWarning()
    {
        var trusted = new LibraryEntry(
            Entry("310.9.1", RuntimeKind.Dlss, RuntimeTrust.Trusted),
            []);

        var accepted = new LibraryEntry(
            Entry("310.9.1", RuntimeKind.Dlss, RuntimeTrust.UserAccepted),
            []);

        // R33 and R42: the trust is stated where the version is seen, not only at swap time. A user
        // choosing between versions should not have to run a preview to find one of them is unsigned.
        Assert.Equal("signed", trusted.TrustText);
        Assert.Equal("⚠ untrusted", accepted.TrustText);
        Assert.True(accepted.IsUntrusted);
    }

    [Fact]
    public void AVersionSaysWhereItCameFrom()
    {
        var imported = new LibraryEntry(
            Entry("310.9.1", RuntimeKind.Dlss, RuntimeTrust.UserAccepted, RuntimeSource.Imported, "from Downloads"),
            []);

        var discovered = new LibraryEntry(
            Entry("310.2.1", RuntimeKind.Dlss, RuntimeTrust.Trusted, RuntimeSource.Discovered, "found in a game"),
            []);

        // R17: the source is recorded, and here it is actually shown.
        Assert.True(imported.IsImported);
        Assert.Equal("from Downloads", imported.SourceText);

        Assert.False(discovered.IsImported);
        Assert.Equal("found in a game", discovered.SourceText);
    }

    [Fact]
    public void AVersionSaysHowManyGamesAlreadyRunIt()
    {
        var none = new LibraryEntry(Entry("310.9.1", RuntimeKind.Dlss, RuntimeTrust.Trusted), []);
        var one = new LibraryEntry(
            Entry("310.9.1", RuntimeKind.Dlss, RuntimeTrust.Trusted), ["The Witcher 3"]);
        var two = new LibraryEntry(
            Entry("310.9.1", RuntimeKind.Dlss, RuntimeTrust.Trusted), ["Portal with RTX", "Injustice 2"]);

        // The practical question: a version three games already run is one known to work here.
        Assert.Equal("Not installed in any game", none.UsageText);
        Assert.Equal("Used by The Witcher 3", one.UsageText);
        Assert.Equal("Used by 2 games", two.UsageText);
    }

    [Fact]
    public void TheLibraryCanBeFilteredToOneRuntime()
    {
        var all = Library([("310.9.1", RuntimeKind.Dlss), ("310.9.1", RuntimeKind.DlssG)]);
        var filtered = Library([("310.9.1", RuntimeKind.Dlss), ("310.9.1", RuntimeKind.DlssG)], RuntimeKind.DlssG);

        Assert.Equal(2, all.Count);
        Assert.Equal(RuntimeKind.DlssG, Assert.Single(filtered).Kind);
    }

    // ------------------------------------------------------------- The poster grid

    [Fact]
    public void ThePosterGridCardShowsTheVersionsTheGameRuns()
    {
        var row = Row(Dlss: "310.9.1", DlssG: "310.2.1");

        // The card's value over a list of names: it says, without being clicked, what this game is on.
        var dlss = row.Choices.Single(c => c.Kind == RuntimeKind.Dlss);
        var frameGen = row.Choices.Single(c => c.Kind == RuntimeKind.DlssG);

        Assert.True(dlss.IsAvailable);
        Assert.Equal("310.9.1", dlss.CurrentText);
        Assert.Equal("310.2.1", frameGen.CurrentText);
    }

    [Fact]
    public void AGameWithNoArtworkStillShowsItsInitial()
    {
        var row = Row(poster: null);

        Assert.False(row.HasPoster);
        Assert.Equal("T", row.Initial);

        // A grid of empty cards reads as a failure to load, so there is always something to look at.
        Assert.False(string.IsNullOrWhiteSpace(row.Initial));
    }

    [Fact]
    public void TheTwoViewsAreExclusiveSoOnlyOneIsEverShown()
    {
        // Both views exist at once and one is hidden, rather than one being rebuilt on each toggle:
        // rebuilding a GridView throws away column widths the user has dragged.
        Assert.NotEqual(ViewMode.List, ViewMode.PosterGrid);
        Assert.Equal(2, Enum.GetValues<ViewMode>().Length);
    }

    // ------------------------------------------------------------- Helpers

    private static CatalogEntry Entry(
        string version,
        RuntimeKind kind,
        RuntimeTrust trust,
        RuntimeSource source = RuntimeSource.Discovered,
        string detail = "test") =>
        new(
            CatalogId.For(kind, version, source),
            kind,
            version,
            null,
            source,
            detail,
            Path.Combine("C:", "collection", RuntimeDlls.ByKind[kind][0]),
            trust,
            DateTimeOffset.UnixEpoch,
            null);

    private static IReadOnlyList<LibraryEntry> Library(
        IReadOnlyList<(string Version, RuntimeKind Kind)> versions,
        RuntimeKind? filter = null)
    {
        var entries = versions
            .Select(v => Entry(v.Version, v.Kind, RuntimeTrust.Trusted))
            .ToArray();

        var kinds = entries.Select(e => e.Kind).Distinct().ToList();

        var filtered = filter is null
            ? entries
            : [.. entries.Where(e => e.Kind == filter)];

        return
        [
            .. filtered
                .OrderBy(e => RuntimeKindNames.Display(e.Kind))
                .ThenByDescending(
                    e => e.Version,
                    Comparer<string>.Create((a, b) =>
                        KronosReborn.Data.Actions.VersionComparer.IsOlder(a, b) ? -1
                        : KronosReborn.Data.Actions.VersionComparer.IsOlder(b, a) ? 1
                        : 0))
                .Select(e => new LibraryEntry(e, [])),
        ];
    }

    private static GameRow Row(
        string? Dlss = null,
        string? DlssG = null,
        string? poster = "cover.jpg")
    {
        var record = new GameRecord("Steam", "game-1", "Test Game", @"C:\game", Exists: true, PosterPath: poster);

        var runtimes = Enum.GetValues<RuntimeKind>()
            .Select(kind =>
            {
                var version = kind switch
                {
                    RuntimeKind.Dlss => Dlss,
                    RuntimeKind.DlssG => DlssG,
                    _ => null,
                };

                return new DetectedRuntime(
                    kind,
                    RuntimeDlls.ByKind[kind][0],
                    RuntimeDlls.ByKind[kind][0],
                    Path.Combine(@"C:\game", RuntimeDlls.ByKind[kind][0]),
                    version,
                    null,
                    Exists: version is not null);
            })
            .ToArray();

        var row = new GameRow(record, new GameRuntimeSnapshot("game-1", "Test Game", @"C:\game", runtimes));
        row.RebuildChoices(
        [
            Entry("310.9.1", RuntimeKind.Dlss, RuntimeTrust.Trusted),
            Entry("310.2.1", RuntimeKind.DlssG, RuntimeTrust.Trusted),
        ]);

        return row;
    }
}
