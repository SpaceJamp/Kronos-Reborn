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
/// Tests for treating DLSS, DLSS Frame Generation and DLSS Ray Reconstruction as one runtime.
/// </summary>
/// <remarks>
/// <para>
/// These are three separate files with three separate names, and the program has always known them
/// apart. What it did not know was that they have to move together. NVIDIA ships them at matching
/// versions, and every game checked on a real library carried them in step — one on 310.2.1 with
/// Frame Generation 310.2.1, the rest on 310.9.1 with 310.9.1 — while none of them carried Ray
/// Reconstruction at all.
/// </para>
/// <para>
/// So swapping DLSS on its own is how a game ends up running Frame Generation built for a different
/// Super Resolution version. These tests pin down that the rest of the stack comes along, that a
/// part is never added to a game that did not ship with it, and that a part which cannot come along
/// is said so rather than passed over in silence.
/// </para>
/// </remarks>
public class DlssFamilyTests
{
    [Fact]
    public void TheThreeDlssKindsAreOneMatchedSet()
    {
        // The whole rule rests on this grouping, so it is asserted rather than assumed.
        Assert.Equal(
            [RuntimeKind.Dlss, RuntimeKind.DlssG, RuntimeKind.DlssR],
            RuntimeDlls.Related(RuntimeKind.Dlss));

        Assert.Equal(
            [RuntimeKind.Dlss, RuntimeKind.DlssG, RuntimeKind.DlssR],
            RuntimeDlls.Related(RuntimeKind.DlssG));

        Assert.Equal(
            [RuntimeKind.Dlss, RuntimeKind.DlssG, RuntimeKind.DlssR],
            RuntimeDlls.Related(RuntimeKind.DlssR));
    }

    [Fact]
    public void FsrAndXessAreSelfContained()
    {
        // FSR is two files, but they are one runtime, not three parts of a matched set. It stands
        // alone, as XeSS does.
        Assert.Equal([RuntimeKind.Fsr], RuntimeDlls.Related(RuntimeKind.Fsr));
        Assert.Equal([RuntimeKind.Xess], RuntimeDlls.Related(RuntimeKind.Xess));

        Assert.False(RuntimeDlls.AreRelated(RuntimeKind.Fsr, RuntimeKind.Dlss));
        Assert.True(RuntimeDlls.AreRelated(RuntimeKind.Dlss, RuntimeKind.DlssG));
    }

    [Fact]
    public void ChoosingADlssVersionBringsFrameGenerationWithIt()
    {
        using var fixture = new DlssFixture();
        fixture.GivenGameHas(RuntimeKind.Dlss, RuntimeKind.DlssG);
        fixture.GivenCollectionHas("310.9.1", RuntimeKind.Dlss, RuntimeKind.DlssG);

        var plans = fixture.PlanWithFamily(RuntimeKind.Dlss);

        // The defect this guards: only DLSS was planned, leaving Frame Generation on 310.2.1 beside a
        // Super Resolution on 310.9.1.
        Assert.Equal(2, plans.Count);
        Assert.Contains(plans, p => p.RuntimeKind == RuntimeKind.Dlss);
        Assert.Contains(plans, p => p.RuntimeKind == RuntimeKind.DlssG);

        // Both to the version that was chosen, not two different ones.
        Assert.All(plans, p => Assert.Equal("310.9.1", p.TargetVersion));
    }

    [Fact]
    public void RayReconstructionIsBroughtAlongTooWhenTheGameHasIt()
    {
        using var fixture = new DlssFixture();
        fixture.GivenGameHas(RuntimeKind.Dlss, RuntimeKind.DlssG, RuntimeKind.DlssR);
        fixture.GivenCollectionHas("310.9.1", RuntimeKind.Dlss, RuntimeKind.DlssG, RuntimeKind.DlssR);

        var plans = fixture.PlanWithFamily(RuntimeKind.Dlss);

        Assert.Equal(3, plans.Count);
        Assert.Contains(plans, p => p.RuntimeKind == RuntimeKind.DlssR);
    }

    [Fact]
    public void StartingFromFrameGenerationAlsoBringsTheRest()
    {
        using var fixture = new DlssFixture();
        fixture.GivenGameHas(RuntimeKind.Dlss, RuntimeKind.DlssG);
        fixture.GivenCollectionHas("310.9.1", RuntimeKind.Dlss, RuntimeKind.DlssG);

        // Whichever part the user happens to pick first, the same stack is planned.
        var plans = fixture.PlanWithFamily(RuntimeKind.DlssG);

        Assert.Equal(2, plans.Count);
        Assert.Contains(plans, p => p.RuntimeKind == RuntimeKind.Dlss);
        Assert.Contains(plans, p => p.RuntimeKind == RuntimeKind.DlssG);
    }

    [Fact]
    public void FrameGenerationIsNotAddedToAGameThatNeverHadIt()
    {
        using var fixture = new DlssFixture();
        fixture.GivenGameHas(RuntimeKind.Dlss);
        fixture.GivenCollectionHas("310.9.1", RuntimeKind.Dlss, RuntimeKind.DlssG);

        var plans = fixture.PlanWithFamily(RuntimeKind.Dlss);

        // Adding Frame Generation to a game that shipped without it is adding runtime support to that
        // game, which the specification puts out of scope — and would leave the title running a
        // feature its developer never enabled.
        Assert.Single(plans);
        Assert.Equal(RuntimeKind.Dlss, plans[0].RuntimeKind);

        // And nothing is reported as left behind, because nothing was supposed to be there.
        Assert.Empty(plans[0].Unmatched);
    }

    [Fact]
    public void APartTheCollectionLacksIsSaidToBeLeftBehindRatherThanIgnored()
    {
        using var fixture = new DlssFixture();
        fixture.GivenGameHas(RuntimeKind.Dlss, RuntimeKind.DlssG);
        fixture.GivenCollectionHas("310.9.1", RuntimeKind.Dlss);

        var plans = fixture.PlanWithFamily(RuntimeKind.Dlss);

        // The upgrade still happens — refusing outright would strand the game on the old DLSS until
        // the user manually imported something.
        Assert.Single(plans);

        // But the preview says what was left behind and what would fix it. Silently half-matching a
        // runtime is precisely the problem this rule was added to stop.
        var dlss = plans[0];
        Assert.Equal([RuntimeKind.DlssG], dlss.Unmatched);
        Assert.True(dlss.HasWarning);

        // The preview names the part that was left behind and the version that would fix it, so the
        // user is not left to infer the mismatch from two lines of numbers.
        Assert.Contains(
            RuntimeKindNames.Display(RuntimeKind.DlssG),
            dlss.Summary,
            StringComparison.Ordinal);
        Assert.Contains("310.9.1", dlss.Summary, StringComparison.Ordinal);
        Assert.Contains("stays on the version", dlss.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void APartAlreadyAtTheChosenVersionIsNotTreatedAsLeftBehind()
    {
        using var fixture = new DlssFixture();
        fixture.GivenGameHas(RuntimeKind.Dlss, RuntimeKind.DlssG);
        fixture.GivenCollectionHas("310.9.1", RuntimeKind.Dlss);

        // The game is already on 310.9.1 for Frame Generation; only DLSS is behind.
        fixture.GivenGameIsAt(RuntimeKind.DlssG, "310.9.1");
        fixture.GivenGameIsAt(RuntimeKind.Dlss, "310.2.1");

        var plans = fixture.PlanWithFamily(RuntimeKind.Dlss);

        Assert.Single(plans);

        // The point: Frame Generation is in step already, so nothing is reported as left behind.
        // (This plan does warn, for the unrelated reason that these test files are unsigned.)
        Assert.Empty(plans[0].Unmatched);
    }

    [Fact]
    public void EveryPartIsActuallyInstalledTogether()
    {
        using var fixture = new DlssFixture();
        fixture.GivenGameHas(RuntimeKind.Dlss, RuntimeKind.DlssG);
        fixture.GivenCollectionHas("310.9.1", RuntimeKind.Dlss, RuntimeKind.DlssG);

        var result = fixture.Execute(fixture.PlanWithFamily(RuntimeKind.Dlss));

        Assert.Equal(2, result.Succeeded);
        Assert.Equal(0, result.Failed);

        // Both files in the game now hold the new content, not just the one that was picked.
        foreach (var kind in new[] { RuntimeKind.Dlss, RuntimeKind.DlssG })
        {
            var file = Path.Combine(fixture.GameFolder, RuntimeDlls.ByKind[kind][0]);
            Assert.Equal(DlssFixture.NewContent(RuntimeDlls.ByKind[kind][0]), File.ReadAllText(file));
        }
    }

    [Fact]
    public void OnePartFailingRollsBackThePartsAlreadyAppliedToThatGame()
    {
        using var fixture = new DlssFixture();
        fixture.GivenGameHas(RuntimeKind.Dlss, RuntimeKind.DlssG);
        fixture.GivenCollectionHas("310.9.1", RuntimeKind.Dlss, RuntimeKind.DlssG);

        var plans = fixture.PlanWithFamily(RuntimeKind.Dlss);
        Assert.Equal(2, plans.Count);

        // Remove the collection's copy of the second part, so applying it cannot succeed. This is the
        // real shape of the failure: the collection had the file listed, then it went away.
        File.Delete(Path.Combine(
            fixture.CollectionFolderFor(RuntimeKind.DlssG),
            RuntimeDlls.ByKind[RuntimeKind.DlssG][0]));

        var result = fixture.Execute(plans);

        // The part that did apply must not survive on its own: the game would be running Frame
        // Generation from 310.2.1 against a Super Resolution from 310.9.1.
        Assert.Equal(0, result.Succeeded);

        foreach (var kind in new[] { RuntimeKind.Dlss, RuntimeKind.DlssG })
        {
            var file = Path.Combine(fixture.GameFolder, RuntimeDlls.ByKind[kind][0]);
            Assert.Equal(DlssFixture.OldContent(RuntimeDlls.ByKind[kind][0]), File.ReadAllText(file));
        }
    }

    [Fact]
    public void AFailedPartIsReportedWithAReasonRatherThanVanishing()
    {
        using var fixture = new DlssFixture();
        fixture.GivenGameHas(RuntimeKind.Dlss, RuntimeKind.DlssG);
        fixture.GivenCollectionHas("310.9.1", RuntimeKind.Dlss, RuntimeKind.DlssG);

        var plans = fixture.PlanWithFamily(RuntimeKind.Dlss);
        File.Delete(Path.Combine(
            fixture.CollectionFolderFor(RuntimeKind.DlssG),
            RuntimeDlls.ByKind[RuntimeKind.DlssG][0]));

        var result = fixture.Execute(plans);

        // R63: a summary accounts for every item. Both parts appear, and neither claims success.
        Assert.Equal(plans.Count, result.Results.Count);
        Assert.All(result.Results, r => Assert.False(r.Succeeded));

        // The part that was rolled back says why, so the user is not left guessing.
        Assert.Contains(result.Results, r =>
            r.ErrorMessage is not null &&
            r.ErrorMessage.Contains("rolled back", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void OneGameFailingDoesNotStopAnotherGame()
    {
        using var fixture = new DlssFixture();
        fixture.GivenGameHas(RuntimeKind.Dlss, RuntimeKind.DlssG);
        fixture.GivenCollectionHas("310.9.1", RuntimeKind.Dlss, RuntimeKind.DlssG);

        // A second game in the same batch, whose own DLSS file is held open so it cannot be written.
        fixture.AddSecondGame();
        using var locked = fixture.LockSecondGameDlss();

        var plans = fixture.PlanWithFamily(RuntimeKind.Dlss);

        // Both games, both parts, in one batch.
        Assert.Equal(4, plans.Count);

        var result = fixture.Execute(plans);

        // R39: a failure does not stop the batch. Per-game atomicity must not have become
        // all-or-nothing across games.
        Assert.True(result.Failed > 0, "the locked game should have failed");
        Assert.True(result.Succeeded > 0, "the unlocked game should still have been applied");

        // And the game that succeeded really did end up with both parts at the chosen version.
        foreach (var kind in new[] { RuntimeKind.Dlss, RuntimeKind.DlssG })
        {
            var file = Path.Combine(fixture.GameFolder, RuntimeDlls.ByKind[kind][0]);
            Assert.Equal(DlssFixture.NewContent(RuntimeDlls.ByKind[kind][0]), File.ReadAllText(file));
        }
    }

    /// <summary>
    /// A game and a collection that can hold several runtime kinds at once.
    /// </summary>
    /// <remarks>
    /// The existing <see cref="SwapFixture"/> is deliberately single-kind, which is what most swap
    /// behaviour needs. Multi-part behaviour needs a game carrying more than one runtime and a
    /// collection holding versions of each, so this builds that instead of loosening the other.
    /// </remarks>
    private sealed class DlssFixture : IDisposable
    {
        private readonly TemporaryDirectory _temp = new();
        private readonly Dictionary<RuntimeKind, string> _gameVersions = [];
        private readonly Dictionary<RuntimeKind, string?> _catalogVersion = [];
        private readonly string _dataRoot;

        public DlssFixture()
        {
            _dataRoot = Path.Combine(_temp.Root, "data");
            Paths = new DataPaths(_dataRoot);
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

        /// <summary>The version a swapped-in DLL reads as, so files can be told apart.</summary>
        public static string NewContent(string fileName) => $"NEW-{fileName}";

        /// <summary>The version a game's own DLL reads as, before any swap.</summary>
        public static string OldContent(string fileName) => $"OLD-{fileName}";

        public string CollectionFolderFor(RuntimeKind kind) =>
            Path.Combine(_temp.Root, "Collection", kind.ToString());

        /// <summary>The game already carries these runtimes, at the given version.</summary>
        public void GivenGameHas(params RuntimeKind[] kinds) => GivenGameIsAtAll(kinds, "310.2.1");

        public void GivenGameIsAt(RuntimeKind kind, string version) =>
            GivenGameIsAtAll([kind], version);

        private void GivenGameIsAtAll(IReadOnlyList<RuntimeKind> kinds, string version)
        {
            foreach (var kind in kinds)
            {
                var file = RuntimeDlls.ByKind[kind][0];

                File.WriteAllText(Path.Combine(GameFolder, file), OldContent(file));
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

                var file = RuntimeDlls.ByKind[kind][0];
                File.WriteAllText(Path.Combine(folder, file), NewContent(file));

                _catalogVersion[kind] = version;

                var detected = new DetectedRuntime(
                    kind, file, file, Path.Combine(folder, file), version, null, Exists: true);

                Catalog.AddFromDiscovery(detected, GameId, "Test Game", "Steam");
            }
        }

        /// <summary>Puts a second game with the same runtimes into the same batch.</summary>
        public void AddSecondGame()
        {
            foreach (var kind in _gameVersions.Keys)
            {
                var file = RuntimeDlls.ByKind[kind][0];
                File.WriteAllText(Path.Combine(SecondGameFolder, file), OldContent(file));
            }
        }

        /// <summary>
        /// Holds the second game's DLSS file open so it cannot be written.
        /// </summary>
        /// <remarks>
        /// Used to fail one game's plans without disturbing the other. Deleting the collection's copy
        /// would not do it: every game's plan for a kind reads from the same file in the collection, so
        /// that would fail all of them at once and prove nothing about per-game behaviour. Holding
        /// the game's own target file is the failure that is genuinely local to one game.
        /// </remarks>
        public FileStream LockSecondGameDlss()
        {
            var path = Path.Combine(SecondGameFolder, RuntimeDlls.ByKind[RuntimeKind.Dlss][0]);
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        }

        public IReadOnlyList<SwapPlan> PlanWithFamily(RuntimeKind kind)
        {
            var target = Catalog.Entries.First(e =>
                e.Kind == kind &&
                VersionNumber.AreEquivalent(e.Version, _catalogVersion[kind]));

            return SwapPlanner.PlanWithFamily(
                Games(),
                Catalog,
                kind,
                target.Id,
                Settings.Default.With(allowUntrustedRuntimes: true));
        }

        private List<SwapGameInfo> Games()
        {
            var games = new List<SwapGameInfo> { GameInfo(GameId, "Test Game", GameFolder) };

            if (Directory.Exists(SecondGameFolder) &&
                Directory.EnumerateFiles(SecondGameFolder).Any())
            {
                games.Add(GameInfo(SecondGameId, "Second Game", SecondGameFolder));
            }

            return games;
        }

        private SwapGameInfo GameInfo(string id, string name, string folder)
        {
            // Only the kinds this fixture was told the game has; the rest are reported absent, which
            // is how the planner decides not to add them.
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
