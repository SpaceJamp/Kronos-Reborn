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
    public void ARefreshKeepsTheSelectedGameOnTheNewRow()
    {
        using var fixture = new Fixture();
        fixture.Vm.Games = Rows("a", "b");
        fixture.Vm.SelectedGame = fixture.Vm.Games[0];

        // A refresh builds every row again.
        var refreshed = Rows("a", "b");
        fixture.Vm.Games = refreshed;

        // The pane used to keep the discarded row, so a version chosen there changed nothing.
        Assert.Same(refreshed[0], fixture.Vm.SelectedGame);
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
    public void NewestChoosesTheNewestVersionHeldForEveryGame()
    {
        using var fixture = new Fixture();
        fixture.Catalog.AddImport(RuntimeKind.Dlss, "310.9.1", fixture.ImportFolder(), "test", RuntimeTrust.Trusted);
        fixture.Vm.Games = Rows("a", "b");
        foreach (var row in fixture.Vm.Games)
        {
            row.RebuildChoices(fixture.Catalog.Entries);
        }

        // Nothing selected and nothing ticked: every game is brought up to the newest held.
        fixture.Vm.ChooseNewestCommand.Execute(null);

        Assert.All(fixture.Vm.Games, g =>
            Assert.Equal("310.9.1", g.Choices.Single(c => c.Kind == RuntimeKind.Dlss).Selected?.Version));
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

    [Fact]
    public void TheWindowMayNotCloseWhileASwapIsRunning()
    {
        using var fixture = new Fixture();
        Assert.True(fixture.Vm.CanClose());

        fixture.Vm.IsWorking = true;

        // Closing part way through left games changed with no undo record.
        Assert.False(fixture.Vm.CanClose());
        Assert.Contains("still running", fixture.Vm.StatusText);
    }

    [Fact]
    public void NothingThatWritesGameFilesStartsDuringARefresh()
    {
        using var fixture = new Fixture();
        fixture.Vm.Games = Rows("a");
        fixture.Vm.SelectedGame = fixture.Vm.Games[0];
        Assert.True(fixture.Vm.UndoCommand.CanExecute(null));

        fixture.Vm.IsScanning = true;

        Assert.False(fixture.Vm.UndoCommand.CanExecute(null));
        Assert.False(fixture.Vm.RedoCommand.CanExecute(null));
        Assert.False(fixture.Vm.ApplySwapCommand.CanExecute(null));
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
        private readonly DataPaths _paths;
        private readonly PersistenceService _persistence;

        public Fixture()
        {
            _paths = new DataPaths(Path.Combine(_temp.Root, "data"));
            _persistence = new PersistenceService(_paths);
            Catalog = new RuntimeCatalog(_paths.CatalogPath);
            Vm = new MainViewModel(Catalog, _persistence, Settings.Default, _paths);
        }

        public MainViewModel Vm { get; }
        public RuntimeCatalog Catalog { get; }

        /// <summary>A folder holding a DLSS DLL, for importing into the collection.</summary>
        public string ImportFolder()
        {
            var folder = Path.Combine(_temp.Root, "import");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "nvngx_dlss.dll"), "x");
            return folder;
        }

        public void Dispose()
        {
            Vm.Dispose();
            Catalog.Dispose();
            _persistence.Dispose();
            _temp.Dispose();
        }
    }
}
