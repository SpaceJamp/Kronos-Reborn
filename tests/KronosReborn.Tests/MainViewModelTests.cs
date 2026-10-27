using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using KronosReborn.Data.Libraries;
using KronosReborn.Data.Persistence;
using KronosReborn.Data.Runtimes;
using KronosReborn.UI;
using Xunit;

namespace KronosReborn.Tests;

public class MainViewModelTests
{
    [Fact]
    public void ARefreshKeepsTheSelectedGameAndTheTicksOnTheNewRows()
    {
        using var fixture = new Fixture();
        fixture.Vm.Games = Rows("a", "b");
        fixture.Vm.SelectedGame = fixture.Vm.Games[0];
        fixture.Vm.Games[1].IsSelected = true;

        // A refresh builds every row again.
        var refreshed = Rows("a", "b");
        fixture.Vm.Games = refreshed;

        // The pane used to keep the discarded row, so a version chosen there changed nothing.
        Assert.Same(refreshed[0], fixture.Vm.SelectedGame);
        Assert.True(refreshed[1].IsSelected);
        Assert.False(refreshed[0].IsSelected);
    }

    [Fact]
    public void AGameThatIsGoneAfterARefreshIsNoLongerSelected()
    {
        using var fixture = new Fixture();
        fixture.Vm.Games = Rows("a");
        fixture.Vm.SelectedGame = fixture.Vm.Games[0];

        fixture.Vm.Games = Rows("b");

        Assert.Null(fixture.Vm.SelectedGame);
    }

    [Fact]
    public void TickingARefreshedRowUpdatesNewest()
    {
        using var fixture = new Fixture();
        fixture.Vm.Games = Rows("a", "b");

        var raised = 0;
        fixture.Vm.ChooseNewestCommand.CanExecuteChanged += (_, _) => raised++;

        // Rows arriving with a refresh never got their tick listener, so Newest stayed disabled.
        fixture.Vm.Games[0].IsSelected = true;

        Assert.True(raised > 0);
        Assert.True(fixture.Vm.ChooseNewestCommand.CanExecute(null));
    }

    [Fact]
    public void AVersionChosenSurvivesTheCollectionChanging()
    {
        var row = Rows("a")[0];
        CatalogEntry[] entries = [Entry("310.2.1"), Entry("310.9.1")];
        row.RebuildChoices(entries);

        var dlss = row.Choices.Single(c => c.Kind == RuntimeKind.Dlss);
        dlss.Selected = dlss.Options.Single(o => o.Version == "310.9.1");

        // A download or an import adds a version, which rebuilds every game's pickers.
        row.RebuildChoices([.. entries, Entry("310.10.0")]);

        var rebuilt = row.Choices.Single(c => c.Kind == RuntimeKind.Dlss);
        Assert.Equal("310.9.1", rebuilt.Selected?.Version);
        Assert.Equal(3, rebuilt.Options.Count);
    }

    [Fact]
    public void TheCardSaysWhetherTheNewestVersionHeldIsInstalled()
    {
        // Each row's game runs DLSS 310.2.1.
        var row = Rows("a")[0];

        row.RebuildChoices([Entry("310.2.1")]);
        Assert.Equal("Newest installed", row.StatusText);

        row.RebuildChoices([Entry("310.2.1"), Entry("310.9.1")]);
        Assert.Equal("Newer version available", row.StatusText);
    }

    private static ObservableCollection<GameRow> Rows(params string[] ids) =>
        [.. ids.Select(id => new GameRow(
            new GameRecord("Steam", id, $"Game {id}", Path.Combine("C:", id), true),
            new GameRuntimeSnapshot(id, $"Game {id}", Path.Combine("C:", id),
                [new DetectedRuntime(RuntimeKind.Dlss, "nvngx_dlss.dll", "nvngx_dlss.dll",
                    Path.Combine("C:", id, "nvngx_dlss.dll"), "310.2.1", null, true)])))];

    private static CatalogEntry Entry(string version) => new(
        CatalogId.For(RuntimeKind.Dlss, version, RuntimeSource.Imported), RuntimeKind.Dlss, version, null,
        RuntimeSource.Imported, "test", Path.Combine("C:", "collection", "nvngx_dlss.dll"),
        RuntimeTrust.Trusted, DateTimeOffset.UnixEpoch);

    private sealed class Fixture : IDisposable
    {
        private readonly TemporaryDirectory _temp = new();
        private readonly RuntimeCatalog _catalog;
        private readonly PersistenceService _persistence;

        public Fixture()
        {
            var paths = new DataPaths(Path.Combine(_temp.Root, "data"));
            _persistence = new PersistenceService(paths);
            _catalog = new RuntimeCatalog(paths.CatalogPath);
            Vm = new MainViewModel(_catalog, _persistence, Settings.Default, paths);
        }

        public MainViewModel Vm { get; }

        public void Dispose()
        {
            Vm.Dispose();
            _catalog.Dispose();
            _persistence.Dispose();
            _temp.Dispose();
        }
    }
}
