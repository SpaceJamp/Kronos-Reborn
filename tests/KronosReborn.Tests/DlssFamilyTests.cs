using System;
using System.IO;
using System.Linq;
using KronosReborn.Data.Actions;
using KronosReborn.Data.Libraries;
using KronosReborn.Data.Persistence;
using KronosReborn.Data.Runtimes;
using Xunit;

namespace KronosReborn.Tests;

/// <summary>
/// Tests for DLSS, DLSS Frame Generation and DLSS Ray Reconstruction being three separate things.
/// </summary>
/// <remarks>
/// <para>
/// These are three files with three names, offered as three separate choices, and the user makes the
/// choice for each independently. Installing one never installs another. That is the behaviour these
/// tests pin down, because an earlier version of the program bundled them: picking a DLSS version
/// quietly wrote the Frame Generation file too, so the user could not choose a version for one part
/// without changing the other.
/// </para>
/// <para>
/// What is kept is the knowledge that NVIDIA supports the three only at matching versions. That is
/// reported in the preview rather than acted on, so a combination the user chose deliberately is not
/// overridden and not hidden.
/// </para>
/// </remarks>
public class DlssFamilyTests
{
    // -------------------------------------------------------------- Three separate choices

    [Fact]
    public void TheThreeDlssKindsAreDistinctChoices()
    {
        // Each is its own kind with its own file name, which is what makes them separately
        // selectable in the first place.
        Assert.Equal("nvngx_dlss.dll", RuntimeDlls.ByKind[RuntimeKind.Dlss][0]);
        Assert.Equal("nvngx_dlssg.dll", RuntimeDlls.ByKind[RuntimeKind.DlssG][0]);
        Assert.Equal("nvngx_dlssd.dll", RuntimeDlls.ByKind[RuntimeKind.DlssR][0]);

        // And each has a name of its own in the interface. If these were ever collapsed into one
        // label, the picker would show three identical entries with no way to tell them apart.
        Assert.Equal("DLSS", RuntimeKindNames.Display(RuntimeKind.Dlss));
        Assert.Equal("DLSS Frame Gen", RuntimeKindNames.Display(RuntimeKind.DlssG));
        Assert.Equal("DLSS Ray Recon", RuntimeKindNames.Display(RuntimeKind.DlssR));
    }

    [Fact]
    public void EachPartIsOfferedSeparatelyWhenTheGameHasAllThree()
    {
        using var fixture = new DlssFixture();
        fixture.GivenGameHas(RuntimeKind.Dlss, RuntimeKind.DlssG, RuntimeKind.DlssR);
        fixture.GivenCollectionHas("310.9.1", RuntimeKind.Dlss, RuntimeKind.DlssG, RuntimeKind.DlssR);

        // Every part of every version the collection holds is its own option, so the user can pick a
        // version for one part without being offered a bundle.
        var options = fixture.OfferedVersions();

        Assert.Equal(3, options.Count);
        Assert.Contains(options, o => o.Kind == RuntimeKind.Dlss);
        Assert.Contains(options, o => o.Kind == RuntimeKind.DlssG);
        Assert.Contains(options, o => o.Kind == RuntimeKind.DlssR);

        // All at the same version, but three separate entries rather than one.
        Assert.All(options, o => Assert.Equal("310.9.1", o.Version));
    }

    [Fact]
    public void ChoosingOnePartPlansOnlyThatPart()
    {
        using var fixture = new DlssFixture();
        fixture.GivenGameHas(RuntimeKind.Dlss, RuntimeKind.DlssG, RuntimeKind.DlssR);
        fixture.GivenCollectionHas("310.9.1", RuntimeKind.Dlss, RuntimeKind.DlssG, RuntimeKind.DlssR);

        var plans = fixture.Plan(RuntimeKind.Dlss);

        // The behaviour the user asked for. This was three plans before, and the two extra writes
        // happened without being chosen.
        Assert.Single(plans);
        Assert.Equal(RuntimeKind.Dlss, plans[0].RuntimeKind);
    }

    [Fact]
    public void ChoosingFrameGenerationChangesNothingElse()
    {
        using var fixture = new DlssFixture();
        fixture.GivenGameHas(RuntimeKind.Dlss, RuntimeKind.DlssG, RuntimeKind.DlssR);
        fixture.GivenCollectionHas("310.9.1", RuntimeKind.Dlss, RuntimeKind.DlssG, RuntimeKind.DlssR);

        var plans = fixture.Plan(RuntimeKind.DlssG);

        Assert.Single(plans);
        Assert.Equal(RuntimeKind.DlssG, plans[0].RuntimeKind);

        fixture.Execute(plans);

        // Only the chosen file was written.
        foreach (var kind in new[] { RuntimeKind.Dlss, RuntimeKind.DlssR })
        {
            var file = Path.Combine(fixture.GameFolder, RuntimeDlls.ByKind[kind][0]);
            Assert.Equal(
                DlssFixture.OldContent(RuntimeDlls.ByKind[kind][0]),
                File.ReadAllText(file));
        }
    }

    [Fact]
    public void ChoosingRayReconstructionInstallsRayReconstruction()
    {
        using var fixture = new DlssFixture();
        fixture.GivenGameHas(RuntimeKind.Dlss, RuntimeKind.DlssR);
        fixture.GivenCollectionHas("310.9.1", RuntimeKind.Dlss, RuntimeKind.DlssR);

        var plans = fixture.Plan(RuntimeKind.DlssR);
        fixture.Execute(plans);

        var rr = Path.Combine(fixture.GameFolder, RuntimeDlls.ByKind[RuntimeKind.DlssR][0]);
        Assert.Equal(DlssFixture.NewContent(RuntimeDlls.ByKind[RuntimeKind.DlssR][0]), File.ReadAllText(rr));
    }

    [Fact]
    public void AVersionOfOnePartDoesNotDependOnTheOthersBeingAvailable()
    {
        using var fixture = new DlssFixture();
        fixture.GivenGameHas(RuntimeKind.Dlss, RuntimeKind.DlssG);
        fixture.GivenCollectionHas("310.9.1", RuntimeKind.Dlss);

        // Only DLSS is in the collection. That is enough to swap DLSS: the missing Frame Generation
        // version is the user's business, not a reason to refuse.
        var plans = fixture.Plan(RuntimeKind.Dlss);

        Assert.Single(plans);
        Assert.Equal("310.9.1", plans[0].TargetVersion);
    }

    // -------------------------------------------------------------- The advisory note

    [Fact]
    public void APartLeftOnAnotherVersionIsReportedInThePreview()
    {
        using var fixture = new DlssFixture();
        fixture.GivenGameHas(RuntimeKind.Dlss, RuntimeKind.DlssG);
        fixture.GivenCollectionHas("310.9.1", RuntimeKind.Dlss);

        var plans = SwapPlanner.NotePartsLeftBehind(fixture.Plan(RuntimeKind.Dlss), fixture.Games());

        // Frame Generation would stay on 310.2.1 while DLSS goes to 310.9.1. That is the user's
        // choice to make, but it is said out loud rather than discovered later in the game.
        var plan = Assert.Single(plans);
        Assert.Equal([RuntimeKind.DlssG], plan.Unmatched);
        Assert.True(plan.HasWarning);
        Assert.Contains(
            RuntimeKindNames.Display(RuntimeKind.DlssG),
            plan.Summary,
            StringComparison.Ordinal);
    }

    [Fact]
    public void APartAlreadyAtTheChosenVersionIsNotReported()
    {
        using var fixture = new DlssFixture();
        fixture.GivenGameHas(RuntimeKind.Dlss, RuntimeKind.DlssG);
        fixture.GivenCollectionHas("310.9.1", RuntimeKind.Dlss);
        fixture.GivenGameIsAt(RuntimeKind.DlssG, "310.9.1");

        var plans = SwapPlanner.NotePartsLeftBehind(fixture.Plan(RuntimeKind.Dlss), fixture.Games());

        // Frame Generation is in step already, so there is nothing to say about it.
        Assert.Empty(Assert.Single(plans).Unmatched);
    }

    [Fact]
    public void APartTheGameDoesNotCarryIsNotReported()
    {
        using var fixture = new DlssFixture();
        fixture.GivenGameHas(RuntimeKind.Dlss);
        fixture.GivenCollectionHas("310.9.1", RuntimeKind.Dlss, RuntimeKind.DlssG);

        var plans = SwapPlanner.NotePartsLeftBehind(fixture.Plan(RuntimeKind.Dlss), fixture.Games());

        // Frame Generation is in the collection but not in the game. Adding it is out of scope, so it
        // is neither swapped in nor reported as left behind.
        Assert.Empty(Assert.Single(plans).Unmatched);
    }

    [Fact]
    public void FsrAndXeSSAreUnaffectedByAnyOfThis()
    {
        // They are different technologies entirely, with no cross-version coupling, so a plan for one
        // never mentions the other.
        using var fixture = new DlssFixture();
        fixture.GivenGameHas(RuntimeKind.Fsr, RuntimeKind.Xess);
        fixture.GivenCollectionHas("3.1.0", RuntimeKind.Fsr, RuntimeKind.Xess);

        var plans = SwapPlanner.NotePartsLeftBehind(fixture.Plan(RuntimeKind.Fsr), fixture.Games());

        Assert.Empty(Assert.Single(plans).Unmatched);
        Assert.Equal([RuntimeKind.Fsr], RuntimeDlls.Related(RuntimeKind.Fsr));
        Assert.Equal([RuntimeKind.Xess], RuntimeDlls.Related(RuntimeKind.Xess));
    }

    // -------------------------------------------------------------- Partial batches

    [Fact]
    public void OnePartOfAGameFailingRollsBackTheOthersAppliedToThatGame()
    {
        using var fixture = new DlssFixture();
        fixture.GivenGameHas(RuntimeKind.Dlss, RuntimeKind.DlssG);
        fixture.GivenCollectionHas("310.9.1", RuntimeKind.Dlss, RuntimeKind.DlssG);

        // A batch holding two parts of one game, which is what a caller asking for several changes
        // at once produces. The second cannot be applied.
        var plans = fixture.Plan(RuntimeKind.Dlss).Concat(fixture.Plan(RuntimeKind.DlssG)).ToList();
        Assert.Equal(2, plans.Count);

        File.Delete(Path.Combine(
            fixture.CollectionFolderFor(RuntimeKind.DlssG),
            RuntimeDlls.ByKind[RuntimeKind.DlssG][0]));

        var result = fixture.Execute(plans);

        // The part that did apply must not survive alone: the game would be running Frame Generation
        // from one version against a Super Resolution from another.
        Assert.Equal(0, result.Succeeded);
        Assert.Equal(2, result.Failed);

        foreach (var kind in new[] { RuntimeKind.Dlss, RuntimeKind.DlssG })
        {
            var file = Path.Combine(fixture.GameFolder, RuntimeDlls.ByKind[kind][0]);
            Assert.Equal(DlssFixture.OldContent(RuntimeDlls.ByKind[kind][0]), File.ReadAllText(file));
        }

        // R63: every item in the batch is accounted for, and the rolled-back one says why.
        Assert.Equal(plans.Count, result.Results.Count);
        Assert.Contains(result.Results, r =>
            r.ErrorMessage is not null &&
            r.ErrorMessage.Contains("rolled back", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void OneGameFailingDoesNotStopAnother()
    {
        using var fixture = new DlssFixture();
        fixture.GivenGameHas(RuntimeKind.Dlss, RuntimeKind.DlssG);
        fixture.GivenCollectionHas("310.9.1", RuntimeKind.Dlss, RuntimeKind.DlssG);
        fixture.AddSecondGame();

        // The second game's own file is held open so it cannot be written. Its collection copy is
        // shared, so removing that would fail every game at once and prove nothing about this.
        using var locked = fixture.LockSecondGameDlss();

        var plans = fixture.Plan(RuntimeKind.Dlss).Concat(fixture.Plan(RuntimeKind.DlssG)).ToList();

        var result = fixture.Execute(plans);

        // R39: a failure does not stop the batch. Per-game atomicity must not have become
        // all-or-nothing across games.
        Assert.True(result.Failed > 0, "the locked game should have failed");
        Assert.True(result.Succeeded > 0, "the unlocked game should still have been applied");
    }

    /// <summary>
    /// A game and a collection that can hold several runtime parts at once.
    /// </summary>
    /// <remarks>
    /// The existing <see cref="SwapFixture"/> is deliberately single-kind, which is what most swap
    /// behaviour needs. Anything involving more than one runtime part needs a game carrying several
    /// and a collection holding versions of each, so this builds that instead of loosening the other.
    /// </remarks>
    private sealed class DlssFixture : IDisposable
    {
        private readonly TemporaryDirectory _temp = new();
        private readonly Dictionary<RuntimeKind, string> _gameVersions = [];
        private readonly Dictionary<RuntimeKind, string> _catalogVersions = [];

        public DlssFixture()
        {
            Paths = new DataPaths(Path.Combine(_temp.Root, "data"));
            Persistence = new PersistenceService(Paths);
            Catalog = new RuntimeCatalog(Paths.CatalogPath);

            GameFolder = Path.Combine(_temp.Root, "Game");
            Directory.CreateDirectory(GameFolder);

            SecondGameFolder = Path.Combine(_temp.Root, "Game2");
            Directory.CreateDirectory(SecondGameFolder);
        }

        public const string GameId = "game-1";
        public const string SecondGameId = "game-2";

        public DataPaths Paths { get; }
        public PersistenceService Persistence { get; }
        public RuntimeCatalog Catalog { get; }
        public string GameFolder { get; }
        public string SecondGameFolder { get; }

        /// <summary>Content a swapped-in DLL holds, so files can be told apart.</summary>
        public static string NewContent(string fileName) => $"NEW-{fileName}";

        /// <summary>Content a game's own DLL holds, before any swap.</summary>
        public static string OldContent(string fileName) => $"OLD-{fileName}";

        public string CollectionFolderFor(RuntimeKind kind) =>
            Path.Combine(_temp.Root, "Collection", kind.ToString());

        /// <summary>The game already carries these runtimes, at the old version.</summary>
        public void GivenGameHas(params RuntimeKind[] kinds) => GivenAllAt(kinds, "310.2.1");

        public void GivenGameIsAt(RuntimeKind kind, string version) => GivenAllAt([kind], version);

        private void GivenAllAt(IReadOnlyList<RuntimeKind> kinds, string version)
        {
            foreach (var kind in kinds)
            {
                // Every file the kind consists of. FSR is two DLLs, and a collection holding only one
                // of them is a collection that cannot be installed from at all.
                foreach (var file in RuntimeDlls.ByKind[kind])
                {
                    File.WriteAllText(Path.Combine(GameFolder, file), OldContent(file));
                }

                _gameVersions[kind] = version;
            }
        }

        /// <summary>The collection holds these runtimes, at the given version.</summary>
        public void GivenCollectionHas(string version, params RuntimeKind[] kinds)
        {
            foreach (var kind in kinds)
            {
                var folder = CollectionFolderFor(kind);
                Directory.CreateDirectory(folder);

                foreach (var file in RuntimeDlls.ByKind[kind])
                {
                    File.WriteAllText(Path.Combine(folder, file), NewContent(file));
                }

                _catalogVersions[kind] = version;

                var primary = RuntimeDlls.ByKind[kind][0];
                Catalog.AddFromDiscovery(
                    new DetectedRuntime(
                        kind, primary, primary, Path.Combine(folder, primary), version, null, Exists: true),
                    GameId,
                    "Test Game",
                    "Steam");
            }
        }

        /// <summary>Puts a second game carrying the same runtimes into the batch.</summary>
        public void AddSecondGame()
        {
            foreach (var kind in _gameVersions.Keys)
            {
                foreach (var file in RuntimeDlls.ByKind[kind])
                {
                    File.WriteAllText(Path.Combine(SecondGameFolder, file), OldContent(file));
                }
            }
        }

        /// <summary>
        /// Holds the second game's DLSS file open so it cannot be written.
        /// </summary>
        /// <remarks>
        /// Used to fail one game's plans without disturbing the other. Deleting the collection's copy
        /// would not do it: every game's plan for a kind reads from the same file, so that would fail
        /// all of them at once and say nothing about per-game behaviour.
        /// </remarks>
        public FileStream LockSecondGameDlss()
        {
            var path = Path.Combine(SecondGameFolder, RuntimeDlls.ByKind[RuntimeKind.Dlss][0]);
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        }

        /// <summary>Every version the collection holds that applies to the game.</summary>
        public IReadOnlyList<(RuntimeKind Kind, string Version)> OfferedVersions() =>
        [
            .. Catalog.Entries
                .Where(e => _gameVersions.ContainsKey(e.Kind))
                .Select(e => (e.Kind, e.Version)),
        ];

        public IReadOnlyList<SwapPlan> Plan(RuntimeKind kind)
        {
            var target = Catalog.Entries.First(e =>
                e.Kind == kind && VersionNumber.AreEquivalent(e.Version, _catalogVersions[kind]));

            return SwapPlanner.Plan(
                Games(),
                Catalog,
                kind,
                target.Id,
                Settings.Default.With(allowUntrustedRuntimes: true));
        }

        /// <summary>Every game in the batch, with only the parts it actually carries.</summary>
        public List<SwapGameInfo> Games()
        {
            var games = new List<SwapGameInfo> { GameInfo(GameId, "Test Game", GameFolder) };

            if (Directory.EnumerateFiles(SecondGameFolder).Any())
            {
                games.Add(GameInfo(SecondGameId, "Second Game", SecondGameFolder));
            }

            return games;
        }

        private SwapGameInfo GameInfo(string id, string name, string folder)
        {
            var runtimes = Enum.GetValues<RuntimeKind>()
                .Select(kind => new DetectedRuntime(
                    kind,
                    RuntimeDlls.ByKind[kind][0],
                    RuntimeDlls.ByKind[kind][0],
                    Path.Combine(folder, RuntimeDlls.ByKind[kind][0]),
                    _gameVersions.GetValueOrDefault(kind),
                    null,
                    Exists: _gameVersions.ContainsKey(kind)))
                .ToArray();

            return new SwapGameInfo(
                id,
                name,
                folder,
                ExistsOnDisk: true,
                new GameRuntimeSnapshot(id, name, folder, runtimes));
        }

        public BatchSwapResult Execute(IReadOnlyList<SwapPlan> plans) =>
            new SwapExecutor(Persistence, Catalog, Paths).Execute(plans);

        public void Dispose()
        {
            Catalog.Dispose();
            _temp.Dispose();
        }
    }
}
