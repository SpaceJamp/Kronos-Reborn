using System;
using System.IO;
using System.Linq;
using KronosReborn.Data.Actions;
using KronosReborn.Data.Libraries;
using KronosReborn.Data.Persistence;
using KronosReborn.Data.Runtimes;
using KronosReborn.UI;
using Xunit;

namespace KronosReborn.Tests;

public class QualityOfLifeTests
{
    [Fact]
    public void NewestNeverPicksAnUnsignedVersion()
    {
        var row = Row("a", "310.2.1");
        row.RebuildChoices([Entry("310.5.0", RuntimeTrust.Trusted), Entry("310.9.1", RuntimeTrust.UserAccepted)]);
        var dlss = row.Choices.Single(c => c.Kind == RuntimeKind.Dlss);

        // ⬆ Newest acts on every game at once, so it must not spread a file nobody signed.
        Assert.True(dlss.ChooseNewest());
        Assert.Equal("310.5.0", dlss.Selected?.Version);
    }

    [Fact]
    public void NewestChoosesNothingWhenOnlyAnUnsignedVersionIsNewer()
    {
        var row = Row("a", "310.2.1");
        row.RebuildChoices([Entry("310.9.1", RuntimeTrust.Untrusted)]);

        Assert.False(row.Choices.Single(c => c.Kind == RuntimeKind.Dlss).ChooseNewest());
    }

    [Fact]
    public void TheCardListsTheVersionEachRuntimeRuns()
    {
        var row = Row("a", "310.2.1");

        Assert.Equal([$"{RuntimeKindNames.Display(RuntimeKind.Dlss)} 310.2.1"], row.RuntimeVersions);
    }

    [Fact]
    public void RestoreOriginalUndoesEverySwapOnTheGame()
    {
        using var fixture = new SwapFixture(RuntimeKind.Dlss);
        fixture.InstallRuntime("3.8.0");
        Assert.True(Assert.Single(fixture.Swap().Results).Succeeded);

        fixture.GameReportedVersion = "3.8.0";
        fixture.AddVersion("3.9.0", "NEWER");
        Assert.True(Assert.Single(fixture.Swap().Results).Succeeded);

        var (undone, error) = new UndoEngine(fixture.Persistence, fixture.Paths).RestoreOriginal(fixture.GameId);

        Assert.Null(error);
        Assert.Equal(2, undone);
        foreach (var name in RuntimeDlls.ByKind[RuntimeKind.Dlss])
        {
            Assert.Equal(SwapFixture.OldContent(name), File.ReadAllText(Path.Combine(fixture.GameFolder, name)));
        }
    }

    [Fact]
    public void SteamsLowerCaseForwardSlashPathIsTidiedAndItsRealCapitalsKept()
    {
        var result = new SteamInstallLocator(
            new Registry("c:/program files (x86)/steam", @"C:\Program Files (x86)\Steam")).Discover();

        Assert.Equal(@"C:\Program Files (x86)\Steam", result.InstallRoots[0]);
        Assert.Single(result.InstallRoots, r => string.Equals(r, @"C:\Program Files (x86)\Steam", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void HiddenGamesAndTheViewAreSavedWithTheSettings()
    {
        using var temp = new TemporaryDirectory();
        var paths = new DataPaths(temp.Root);
        using (var persistence = new PersistenceService(paths))
        {
            persistence.SaveSettings(Settings.Default.With(
                hiddenGames: ["steam-1"], searchText: "cyber", showOnlyUpdates: true, selectedTab: 2));
        }

        using var reloaded = new PersistenceService(paths);
        var settings = reloaded.Load().Settings;

        Assert.Equal(["steam-1"], settings.HiddenGames);
        Assert.Equal("cyber", settings.SearchText);
        Assert.True(settings.ShowOnlyUpdates);
        Assert.Equal(2, settings.SelectedTab);
    }

    internal static GameRow Row(string id, string version) => new(
        new GameRecord("Steam", id, $"Game {id}", Path.Combine("C:", id), true),
        new GameRuntimeSnapshot(id, $"Game {id}", Path.Combine("C:", id),
            [new DetectedRuntime(RuntimeKind.Dlss, "nvngx_dlss.dll", "nvngx_dlss.dll",
                Path.Combine("C:", id, "nvngx_dlss.dll"), version, null, true)]));

    internal static CatalogEntry Entry(string version, RuntimeTrust trust = RuntimeTrust.Trusted) => new(
        CatalogId.For(RuntimeKind.Dlss, version, RuntimeSource.Imported), RuntimeKind.Dlss, version, null,
        RuntimeSource.Imported, "test", Path.Combine("C:", "collection", "nvngx_dlss.dll"),
        trust, DateTimeOffset.UnixEpoch);

    private sealed class Registry(string? user, string? machine) : ISteamRegistry
    {
        public string? GetUserInstallPath() => user;
        public string? GetMachineInstallPath() => machine;
    }
}
