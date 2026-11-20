using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
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

    [Fact]
    public void NeedsUpdateShowsOnlyGamesWithANewerVersionHeld()
    {
        using var fixture = new Fixture();
        fixture.Vm.Games = [QualityOfLifeTests.Row("old", "310.2.1"), QualityOfLifeTests.Row("new", "310.9.1")];
        foreach (var row in fixture.Vm.Games)
        {
            row.RebuildChoices([QualityOfLifeTests.Entry("310.2.1"), QualityOfLifeTests.Entry("310.9.1")]);
        }

        fixture.Vm.ShowOnlyUpdates = true;

        Assert.Equal(1, fixture.Vm.UpdatableCount);
        Assert.Equal("old", Assert.Single(fixture.Vm.GameGroups.SelectMany(g => g.Games)).GameId);
    }

    [Fact]
    public void AHiddenGameLeavesTheGridAndNewestUntilShownAgain()
    {
        using var fixture = new Fixture();
        fixture.Catalog.AddImport(RuntimeKind.Dlss, "310.9.1", fixture.ImportFolder(), "test", RuntimeTrust.Trusted);
        fixture.Vm.Games = Rows("a", "b");
        foreach (var row in fixture.Vm.Games)
        {
            row.RebuildChoices(fixture.Catalog.Entries);
        }

        var hidden = fixture.Vm.Games[0];
        fixture.Vm.HideGameCommand.Execute(hidden);
        fixture.Vm.ChooseNewestCommand.Execute(null);

        Assert.DoesNotContain(hidden, fixture.Vm.GameGroups.SelectMany(g => g.Games));
        Assert.Same(hidden, Assert.Single(fixture.Vm.HiddenGames));
        Assert.Null(hidden.Choices.Single(c => c.Kind == RuntimeKind.Dlss).Selected);

        fixture.Vm.UnhideGameCommand.Execute(hidden);

        Assert.Contains(hidden, fixture.Vm.GameGroups.SelectMany(g => g.Games));
        Assert.Empty(fixture.Vm.HiddenGames);
    }

    [Fact]
    public void TheMagicBackgroundIsOnByDefaultAndTurningItOffIsSaved()
    {
        using var fixture = new Fixture();
        Assert.True(fixture.Vm.MagicBackground);

        var changed = new System.Collections.Generic.List<string?>();
        fixture.Vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        fixture.Vm.MagicBackground = false;

        Assert.Contains(nameof(MainViewModel.MagicBackground), changed);
        using var reloaded = new PersistenceService(fixture.Paths);
        Assert.False(reloaded.Load().Settings.MagicBackground);
    }

    [Fact]
    public void TheRedSchemeIsOffByDefaultAndTurningItOnIsSaved()
    {
        using var fixture = new Fixture();
        Assert.False(fixture.Vm.RedScheme);

        var changed = new System.Collections.Generic.List<string?>();
        fixture.Vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        fixture.Vm.RedScheme = true;

        Assert.Contains(nameof(MainViewModel.RedScheme), changed);
        using var reloaded = new PersistenceService(fixture.Paths);
        Assert.True(reloaded.Load().Settings.RedScheme);
    }

    [Fact]
    public void TheStatusBarSaysWhichBuildThisIs()
    {
        using var fixture = new Fixture();

        // Exact, so a commit hash that happens to start with the build's digits cannot pass for it.
        var build = System.Text.RegularExpressions.Regex.Escape(KronosReborn.Data.Updates.UpdateChecker.CurrentVersion());
        Assert.Matches($"^Build {build}( · |$)", fixture.Vm.BuildInfoText);
    }

    [Fact]
    public async Task AnImageThatCannotBeShownIsRefusedWithAReason()
    {
        using var fixture = new Fixture(new UnreadableImagePicker());
        fixture.Vm.Games = Rows("a");

        await fixture.Vm.ChooseCoverAsync(fixture.Vm.Games[0]);

        // Kept as a cover, it would have left a blank card that no automatic cover could replace.
        Assert.StartsWith("Could not use that cover:", fixture.Vm.StatusText);
        Assert.Null(KronosReborn.Data.Libraries.StorePosterFinder.Chosen(fixture.Paths.PostersDirectory, "a"));
    }

    [Fact]
    public async Task UpdateAllGetsTheNewestDownloadsFirstAndChangesNothingWhenAllAreCurrent()
    {
        var asked = new System.Collections.Generic.List<string>();
        using var fixture = new Fixture(http: new System.Net.Http.HttpClient(new Offline(asked)));
        fixture.Vm.Games = Rows("a");
        foreach (var row in fixture.Vm.Games)
        {
            row.RebuildChoices([Entry("310.2.1")]);
        }

        await fixture.Vm.UpdateAllAsync();

        // The vendors were asked for their newest first, then nothing needed changing.
        Assert.Contains(asked, url => url.Contains("NVIDIA/DLSS", StringComparison.OrdinalIgnoreCase));
        Assert.False(fixture.Vm.HasPreview);
        Assert.Contains("newest", fixture.Vm.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.True(fixture.Vm.UpdateAllCommand.CanExecute(null));
    }

    [Fact]
    public async Task UpdateAllCountsAsBusyWhileItDownloads()
    {
        var gate = new TaskCompletionSource();
        using var fixture = new Fixture(http: new System.Net.Http.HttpClient(new Held(gate.Task)));
        fixture.Vm.Games = Rows("a");

        var running = fixture.Vm.UpdateAllAsync();

        // A second click, Undo, or Refresh during the download used to run beside the apply.
        Assert.True(fixture.Vm.IsBusy);
        Assert.False(fixture.Vm.UpdateAllCommand.CanExecute(null));
        Assert.False(fixture.Vm.UndoLastApplyCommand.CanExecute(null));
        Assert.False(fixture.Vm.RefreshCommand.CanExecute(null));

        gate.SetResult();
        await running;
        Assert.False(fixture.Vm.IsBusy);
    }

    [Fact]
    public async Task UpdateAllLeavesYourOwnPicksForYouToReview()
    {
        var asked = new System.Collections.Generic.List<string>();
        using var fixture = new Fixture(http: new System.Net.Http.HttpClient(new Offline(asked)));
        fixture.Vm.Games = Rows("a");
        var row = fixture.Vm.Games[0];
        row.RebuildChoices([Entry("310.2.1"), Entry("310.1.0")]);
        var dlss = row.Choices.Single(c => c.Kind == RuntimeKind.Dlss);
        dlss.Selected = dlss.Options.Single(o => o.Version == "310.1.0");  // a deliberate downgrade

        await fixture.Vm.UpdateAllAsync();

        Assert.Empty(asked);
        Assert.Contains("own picks", fixture.Vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedDownloadIsSaidAndTheNoticeStays()
    {
        var asked = new System.Collections.Generic.List<string>();
        using var fixture = new Fixture(http: new System.Net.Http.HttpClient(new Offline(asked,
            ("https://api.github.com/repos/NVIDIA/DLSS/tags?per_page=100", """[{"name":"v310.9.1"}]"""),
            ("https://raw.githubusercontent.com/NVIDIA/DLSS/v310.9.1/lib/Windows_x86_64/rel/nvngx_dlss.dll", "not a dll"))));
        fixture.Vm.Games = Rows("a");
        await fixture.Vm.CheckForNewRuntimesAsync();
        Assert.True(fixture.Vm.HasNewRuntimes);

        await fixture.Vm.UpdateAllAsync();

        // The download did not give a new version, so the notice is still true.
        Assert.True(fixture.Vm.HasNewRuntimes);
        // And why is said, not hidden behind what was applied.
        Assert.Contains("nvngx_dlss.dll", fixture.Vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWebPageInsteadOfGitHubsAnswerDoesNotCrashUpdateAll()
    {
        // Such as a hotel or airport sign-in page answering in GitHub's place.
        var asked = new System.Collections.Generic.List<string>();
        using var fixture = new Fixture(http: new System.Net.Http.HttpClient(new Offline(asked,
            ("https://api.github.com/repos/NVIDIA/DLSS/tags?per_page=100", "<html>sign in</html>"))));
        fixture.Vm.Games = Rows("a");

        var ex = await Record.ExceptionAsync(fixture.Vm.UpdateAllAsync);

        Assert.Null(ex);
    }

    [Fact]
    public async Task ANewDlssReleaseIsAnnouncedWithAWayToGetIt()
    {
        var asked = new System.Collections.Generic.List<string>();
        using var fixture = new Fixture(http: new System.Net.Http.HttpClient(new Offline(asked,
            ("https://api.github.com/repos/NVIDIA/DLSS/tags?per_page=100", """[{"name":"v310.9.1"}]"""),
            ("https://raw.githubusercontent.com/NVIDIA/DLSS/v310.9.1/lib/Windows_x86_64/rel/nvngx_dlss.dll", "x"))));
        fixture.Vm.Games = Rows("a");
        Assert.False(fixture.Vm.HasNewRuntimes);

        await fixture.Vm.CheckForNewRuntimesAsync();

        Assert.True(fixture.Vm.HasNewRuntimes);
        Assert.Equal("New DLSS version: update all games", fixture.Vm.NewRuntimesText);
        Assert.True(fixture.Vm.UpdateAllCommand.CanExecute(null));
    }

    private sealed class Offline(System.Collections.Generic.List<string> asked, params (string Url, string Json)[] answers) : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, System.Threading.CancellationToken cancellationToken)
        {
            asked.Add(request.RequestUri!.AbsoluteUri);
            foreach (var (url, json) in answers)
            {
                if (url == request.RequestUri.AbsoluteUri)
                {
                    return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new System.Net.Http.StringContent(json) });
                }
            }

            return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }
    }

    /// <summary>Holds every request until the test lets it go, then answers 404.</summary>
    private sealed class Held(Task gate) : System.Net.Http.HttpMessageHandler
    {
        protected override async Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, System.Threading.CancellationToken cancellationToken)
        {
            await gate;
            return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
        }
    }

    private sealed class UnreadableImagePicker : IFilePicker
    {
        public Task<string?> AskForImageFileAsync(string title) =>
            throw new ArgumentException("cover.jpg is not an image Spellweaver can show.");

        public Task<string?> AskForRuntimeFileAsync() => throw new NotSupportedException();
        public Task<string?> AskWhereToSaveDiagnosticsAsync(string suggestedName) => throw new NotSupportedException();
        public Task<string?> AskForGameFolderAsync(string title) => throw new NotSupportedException();
        public Task SetClipboardTextAsync(string text) => throw new NotSupportedException();
        public Task OpenFolderAsync(string path) => throw new NotSupportedException();
        public Task OpenUrlAsync(Uri url) => throw new NotSupportedException();
        public void RunInstallerAndExit(string installerPath) => throw new NotSupportedException();
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

        public Fixture(IFilePicker? picker = null, System.Net.Http.HttpClient? http = null)
        {
            _paths = new DataPaths(Path.Combine(_temp.Root, "data"));
            _persistence = new PersistenceService(_paths);
            Catalog = new RuntimeCatalog(_paths.CatalogPath);
            Vm = new MainViewModel(Catalog, _persistence, Settings.Default, _paths, picker, http: http);
        }

        public MainViewModel Vm { get; }
        public RuntimeCatalog Catalog { get; }
        public DataPaths Paths => _paths;

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
