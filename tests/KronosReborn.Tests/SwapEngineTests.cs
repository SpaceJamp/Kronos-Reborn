using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using KronosReborn.Data.Actions;
using KronosReborn.Data.Libraries;
using KronosReborn.Data.Persistence;
using KronosReborn.Data.Runtimes;
using Xunit;

namespace KronosReborn.Tests;

/// <summary>
/// Tests for the swap and undo engine.
/// </summary>
/// <remarks>
/// These are the tests that matter most in the project, because this is the code that overwrites
/// files inside somebody's game install. Every test here runs against a temporary directory and an
/// injected <see cref="DataPaths"/>, so nothing touches the machine it runs on.
///
/// The two that matter most are <see cref="UndoRestoresEveryDllOfTheKind"/> and
/// <see cref="SwapInstallsEveryDllOfTheKind"/>. Both run over every <see cref="RuntimeKind"/>
/// rather than over a hand-picked example, because the bugs they cover were both of the form "works
/// for the one-DLL case, silently does the wrong thing for the two-DLL case". A test on DLSS alone
/// would have passed while FSR was left holding a new DLL paired against an old one.
/// </remarks>
public class SwapEngineTests
{
    // ------------------------------------------------------------ VersionComparer

    [Fact]
    public void VersionComparerDetectsOlder()
    {
        Assert.True(VersionComparer.IsOlder("3.7.10", "3.8.0"));
        Assert.True(VersionComparer.IsOlder("3.7.10", "3.7.11"));
        Assert.True(VersionComparer.IsOlder("2.0.0", "3.0.0"));
        Assert.True(VersionComparer.IsOlder("1.0.0", "1.0.1"));
        Assert.True(VersionComparer.IsOlder("1.0", "1.0.1"));
    }

    [Fact]
    public void VersionComparerDetectsNewerOrEqual()
    {
        Assert.False(VersionComparer.IsOlder("3.8.0", "3.7.10"));
        Assert.False(VersionComparer.IsOlder("3.7.10", "3.7.10"));
        Assert.False(VersionComparer.IsOlder("3.7.11", "3.7.10"));
    }

    [Fact]
    public void VersionComparerHandlesDifferentLengths()
    {
        Assert.True(VersionComparer.IsOlder("1.0", "1.0.1"));
        Assert.False(VersionComparer.IsOlder("1.0.1", "1.0"));
    }

    // ------------------------------------------------------------ SwapPlanner

    [Fact]
    public void SwapPlannerCreatesPlansForMatchingGames()
    {
        using var fixture = new SwapFixture(RuntimeKind.Dlss);
        fixture.InstallRuntime("3.8.0");

        var plans = fixture.Plan();

        var plan = Assert.Single(plans);
        Assert.Equal(fixture.GameId, plan.GameId);
        Assert.Equal(RuntimeKind.Dlss, plan.RuntimeKind);
        Assert.Equal("3.7.10", plan.CurrentVersion);
        Assert.False(plan.IsDowngrade); // an upgrade

        // The fixture's collection entry is unsigned, so it warns however the settings read. R42
        // governs whether an untrusted file may enter the collection; R33 governs whether the user is
        // told when one is about to be installed, and it does not exempt files accepted earlier.
        Assert.True(plan.IsUntrusted);
        Assert.True(plan.HasWarning);
    }

    [Fact]
    public void AnUntrustedRuntimeWarnsEvenWhenTheSettingsAcceptIt()
    {
        using var fixture = new SwapFixture(RuntimeKind.Dlss);
        fixture.InstallRuntime("3.8.0");
        fixture.AllowUntrusted = true;

        var plan = Assert.Single(fixture.Plan());

        // Before this, an untrusted source produced no warning at all once the setting was on, which
        // meant the one setting that made untrusted files usable also removed the only notice the
        // user got about them.
        Assert.True(plan.IsUntrusted);
        Assert.Contains("⚠", plan.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void SwapPlannerProducesNothingWhenTheVersionIsAlreadyInstalled()
    {
        using var fixture = new SwapFixture(RuntimeKind.Dlss);
        fixture.InstallRuntime("3.7.10"); // the version the game already runs

        // R38: a swap to the version already present has nothing to do. Offered as a plan it would
        // copy a file onto itself, and the picker lists a game's own version among the options.
        Assert.Empty(fixture.Plan());
    }

    [Fact]
    public void SwapPlannerProducesNothingWhenItWouldCopyAFileOntoItself()
    {
        using var temp = new TemporaryDirectory();

        // A catalog entry discovered inside the game itself, whose version could not be read.
        var game = Path.Combine(temp.Root, "Game");
        Directory.CreateDirectory(game);
        var dll = Path.Combine(game, "nvngx_dlss.dll");
        File.WriteAllText(dll, "content");

        var catalog = new RuntimeCatalog(Path.Combine(temp.Root, "catalog.json"));
        var entry = catalog.AddFromDiscovery(
            new DetectedRuntime(RuntimeKind.Dlss, "nvngx_dlss.dll", "nvngx_dlss.dll", dll, "3.9.0", null, true),
            "game-1", "Test Game", "Steam");
        Assert.NotNull(entry);

        // The game reports an unknown version, so the "already installed" rule cannot catch this one:
        // there is no version to compare. What is left is that the source file and the target file
        // are the same file, and copying it onto itself either throws or does nothing.
        var snapshot = new GameRuntimeSnapshot(
            "game-1", "Test Game", game,
            [new DetectedRuntime(RuntimeKind.Dlss, "nvngx_dlss.dll", "nvngx_dlss.dll", dll, null, null, true)]);

        var plans = SwapPlanner.Plan(
            [new SwapGameInfo("game-1", "Test Game", game, true, snapshot)],
            catalog, RuntimeKind.Dlss, entry!.Id,
            Settings.Default.With(allowUntrustedRuntimes: true));

        Assert.Empty(plans);
    }

    [Fact]
    public void SwapPlannerDetectsDowngrade()
    {
        using var fixture = new SwapFixture(RuntimeKind.Dlss);
        fixture.InstallRuntime("3.7.10");

        // The game reports a newer version than the one being installed.
        fixture.GameReportedVersion = "3.8.0";

        var plan = Assert.Single(fixture.Plan());
        Assert.True(plan.IsDowngrade);
        Assert.True(plan.HasWarning);
    }

    [Fact]
    public void SwapPlannerFlagsUntrustedWhenNotAllowed()
    {
        using var fixture = new SwapFixture(RuntimeKind.Dlss);
        fixture.InstallRuntime("3.8.0");

        // A dummy DLL has no signature, so the catalog entry is untrusted.
        Assert.Equal(RuntimeTrust.Untrusted, fixture.Entry!.Trust);

        fixture.AllowUntrusted = false;
        var plan = Assert.Single(fixture.Plan());

        Assert.True(plan.IsUntrusted);
        Assert.True(plan.HasWarning);
    }

    [Fact]
    public void SwapPlannerSkipsMissingGames()
    {
        using var fixture = new SwapFixture(RuntimeKind.Dlss);
        fixture.InstallRuntime("3.7.10");
        fixture.GameExistsOnDisk = false;

        Assert.Empty(fixture.Plan());
    }

    [Fact]
    public void SwapPlannerProducesNothingWhenTheCatalogLacksPartOfTheKind()
    {
        using var fixture = new SwapFixture(RuntimeKind.Fsr);
        fixture.InstallRuntime("3.2.0");

        // Remove one of the two DLLs an FSR runtime consists of.
        var missing = Path.Combine(fixture.CollectionFolder, RuntimeDlls.ByKind[RuntimeKind.Fsr][1]);
        File.Delete(missing);

        Assert.Empty(fixture.Plan());
    }

    [Theory]
    [InlineData(RuntimeKind.Dlss)]
    [InlineData(RuntimeKind.DlssG)]
    [InlineData(RuntimeKind.DlssR)]
    [InlineData(RuntimeKind.Fsr)]
    [InlineData(RuntimeKind.FsrVk)]
    [InlineData(RuntimeKind.Xess)]
    [InlineData(RuntimeKind.XessFg)]
    [InlineData(RuntimeKind.XessDx11)]
    [InlineData(RuntimeKind.Xell)]
    public void SwapPlannerTargetsEveryDllOfTheKind(RuntimeKind kind)
    {
        using var fixture = new SwapFixture(kind);
        fixture.InstallRuntime("3.2.0");

        var plan = Assert.Single(fixture.Plan());

        // The bug this guards: the plan covered only the first DLL of a multi-DLL kind, so FSR had
        // one file overwritten and the other left behind.
        Assert.Equal(RuntimeDlls.ByKind[kind].Length, plan.TargetFiles.Count);

        var names = plan.TargetFiles.Select(Path.GetFileName).ToArray();
        Assert.Equal(RuntimeDlls.ByKind[kind].OrderBy(n => n), names.OrderBy(n => n));
    }

    [Fact]
    public void SwapPlannerPlacesFilesWhereTheGameKeepsThem()
    {
        using var fixture = new SwapFixture(RuntimeKind.Dlss);
        fixture.InstallRuntime("3.8.0");

        // The game keeps its DLSS in a subdirectory, as Unreal titles do.
        var nested = Path.Combine(fixture.GameFolder, "Binaries", "Win64");
        Directory.CreateDirectory(nested);
        var existing = Path.Combine(nested, "nvngx_dlss.dll");
        File.Move(Path.Combine(fixture.GameFolder, "nvngx_dlss.dll"), existing);

        var plan = Assert.Single(fixture.Plan());

        // It must be replaced where it is, not written a second time into the game root.
        Assert.Equal(existing, Assert.Single(plan.TargetFiles));
    }

    // ------------------------------------------------------------ SwapExecutor

    [Theory]
    [InlineData(RuntimeKind.Dlss)]
    [InlineData(RuntimeKind.DlssG)]
    [InlineData(RuntimeKind.DlssR)]
    [InlineData(RuntimeKind.Fsr)]
    [InlineData(RuntimeKind.FsrVk)]
    [InlineData(RuntimeKind.Xess)]
    [InlineData(RuntimeKind.XessFg)]
    [InlineData(RuntimeKind.XessDx11)]
    [InlineData(RuntimeKind.Xell)]
    public void SwapInstallsEveryDllOfTheKind(RuntimeKind kind)
    {
        using var fixture = new SwapFixture(kind);
        fixture.InstallRuntime("3.2.0");

        var result = Assert.Single(fixture.Swap().Results);
        Assert.True(result.Succeeded, result.ErrorMessage);

        foreach (var fileName in RuntimeDlls.ByKind[kind])
        {
            var installed = Path.Combine(fixture.GameFolder, fileName);
            Assert.True(File.Exists(installed), $"{fileName} was not installed");
            Assert.Equal(SwapFixture.NewContent(fileName), File.ReadAllText(installed));
        }
    }

    [Fact]
    public void SwapKeepsBackupsOutOfTheGameFolder()
    {
        using var fixture = new SwapFixture(RuntimeKind.Dlss);
        fixture.InstallRuntime("3.8.0");

        var result = Assert.Single(fixture.Swap().Results);
        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.NotEmpty(result.Backups);

        // R24: nothing but the runtime files themselves is written into a game folder. Backups used
        // to be created beside the DLL, littering installs with nvngx_dlss.dll.kronos_bak_* files.
        var unexpected = Directory
            .EnumerateFileSystemEntries(fixture.GameFolder, "*", SearchOption.AllDirectories)
            .Where(p => !RuntimeDlls.ByKind[RuntimeKind.Dlss].Contains(Path.GetFileName(p)))
            .ToArray();

        Assert.Empty(unexpected);
    }

    [Fact]
    public void SwapRecordsTheStateItLeavesBehind()
    {
        using var fixture = new SwapFixture(RuntimeKind.Dlss);
        fixture.InstallRuntime("3.8.0");

        var result = Assert.Single(fixture.Swap().Results);
        var entry = Assert.IsType<HistoryEntry>(result.HistoryEntry);

        Assert.Equal(fixture.GameId, entry.GameId);
        Assert.Equal("3.7.10", entry.PreviousVersion);
        Assert.Equal("3.8.0", entry.NewVersion);
        Assert.Single(entry.Backups);

        // The hash is what lets undo tell "still as we left it" from "changed by something else".
        var backup = entry.Backups[0];
        Assert.False(string.IsNullOrWhiteSpace(backup.PostSwapSha256));
        Assert.Equal(
            FileFingerprint.Compute(backup.OriginalPath),
            backup.PostSwapSha256,
            ignoreCase: true);
    }

    [Fact]
    public void SwapRefusedWhenTheCollectionNoLongerHoldsTheWholeRuntime()
    {
        using var fixture = new SwapFixture(RuntimeKind.Fsr);
        fixture.InstallRuntime("3.2.0");

        var plan = Assert.Single(fixture.Plan());

        // The runtime is removed from the collection between planning and applying.
        fixture.Catalog.Remove(fixture.Entry!.Id);

        var result = Assert.Single(fixture.Execute(plan).Results);

        Assert.False(result.Succeeded);
        Assert.Equal(SwapFixture.OldContent("amd_fidelityfx_dx12.dll"),
            File.ReadAllText(Path.Combine(fixture.GameFolder, "amd_fidelityfx_dx12.dll")));
    }

    [Fact]
    public void FailedSwapRestoresEverythingItTouched()
    {
        using var fixture = new SwapFixture(RuntimeKind.Dlss);
        fixture.InstallRuntime("3.8.0");

        var plan = Assert.Single(fixture.Plan());

        // Make the write fail by turning the target into a directory that cannot be overwritten.
        var target = Path.Combine(fixture.GameFolder, "nvngx_dlss.dll");
        File.Delete(target);
        Directory.CreateDirectory(target);

        var result = Assert.Single(fixture.Execute(plan).Results);

        Assert.False(result.Succeeded);
        // Nothing changed anywhere else, and the failure names what went wrong (R57).
        Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
    }

    [Fact]
    public void CancelledBatchReportsWhatWasAlreadyDone()
    {
        using var fixture = new SwapFixture(RuntimeKind.Dlss);
        fixture.InstallRuntime("3.8.0");

        var plan = Assert.Single(fixture.Plan());

        // Cancel before anything starts. The batch must report zero work and say why, rather than
        // throwing and losing whatever it had already done. R40.
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var batch = fixture.ExecuteCancelled(cancellation.Token, plan);

        Assert.True(batch.WasCancelled);
        Assert.Equal(0, batch.Succeeded);
        Assert.Empty(batch.Results);
        Assert.Equal(1, batch.NotAttempted);

        // Nothing was applied.
        Assert.Equal(SwapFixture.OldContent("nvngx_dlss.dll"),
            File.ReadAllText(Path.Combine(fixture.GameFolder, "nvngx_dlss.dll")));
    }

    [Fact]
    public void BatchAccountsForEveryGameEvenWhenCancelled()
    {
        using var fixture = new SwapFixture(RuntimeKind.Dlss);
        fixture.InstallRuntime("3.8.0");

        var plan = Assert.Single(fixture.Plan());
        var plans = new[]
        {
            plan,
            plan with { GameId = "game-2", GameName = "Game 2" },
            plan with { GameId = "game-3", GameName = "Game 3" },
        };

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var batch = fixture.ExecuteCancelled(cancellation.Token, plans);

        // Every game is either done or explicitly counted as untouched, so a summary of the batch
        // can never silently lose one. R63: a truncated list loses what the user needed it for.
        Assert.Equal(plans.Length, batch.Results.Count + batch.NotAttempted);
        Assert.Empty(batch.Results);
        Assert.Equal(plans.Length, batch.NotAttempted);
        Assert.True(batch.WasCancelled);
    }

    [Fact]
    public void BatchContinuesPastAFailure()
    {
        using var fixture = new SwapFixture(RuntimeKind.Dlss);
        fixture.InstallRuntime("3.8.0");

        var good = Assert.Single(fixture.Plan());

        // A second plan that cannot succeed, because its collection entry is gone.
        var broken = good with
        {
            GameId = "other-game",
            GameName = "Other Game",
            TargetVersion = "9.9.9",
        };

        var batch = fixture.Execute(good, broken);

        Assert.Equal(2, batch.Results.Count);
        Assert.Equal(1, batch.Succeeded);
        Assert.Equal(1, batch.Failed);
        Assert.False(batch.AllSucceeded); // R39: one failure does not stop the rest
    }

    // ------------------------------------------------------------ UndoEngine

    [Theory]
    [InlineData(RuntimeKind.Dlss)]
    [InlineData(RuntimeKind.DlssG)]
    [InlineData(RuntimeKind.DlssR)]
    [InlineData(RuntimeKind.Fsr)]
    [InlineData(RuntimeKind.FsrVk)]
    [InlineData(RuntimeKind.Xess)]
    [InlineData(RuntimeKind.XessFg)]
    [InlineData(RuntimeKind.XessDx11)]
    [InlineData(RuntimeKind.Xell)]
    public void UndoRestoresEveryDllOfTheKind(RuntimeKind kind)
    {
        using var fixture = new SwapFixture(kind);
        fixture.InstallRuntime("3.8.0");

        Assert.True(Assert.Single(fixture.Swap().Results).Succeeded);

        var undo = fixture.Undo();

        // This is the regression test for the bug that made the feature unusable: undo compared the
        // live file against the backup and called any difference an external edit, so it refused
        // every single swap, including the ones it had just performed successfully.
        Assert.True(undo.Succeeded, undo.ErrorMessage);

        foreach (var fileName in RuntimeDlls.ByKind[kind])
        {
            var path = Path.Combine(fixture.GameFolder, fileName);
            Assert.Equal(SwapFixture.OldContent(fileName), File.ReadAllText(path));
        }
    }

    [Fact]
    public void UndoRemovesFilesTheSwapCreated()
    {
        using var fixture = new SwapFixture(RuntimeKind.Dlss);
        fixture.InstallRuntime("3.8.0");

        // The game has no DLSS at all, so the swap adds one.
        foreach (var name in RuntimeDlls.ByKind[RuntimeKind.Dlss])
        {
            File.Delete(Path.Combine(fixture.GameFolder, name));
        }

        var swap = Assert.Single(fixture.Swap().Results);
        Assert.True(swap.Succeeded, swap.ErrorMessage);
        Assert.True(File.Exists(Path.Combine(fixture.GameFolder, "nvngx_dlss.dll")));
        Assert.NotEmpty(swap.CreatedFiles);

        var undo = fixture.Undo();
        Assert.True(undo.Succeeded, undo.ErrorMessage);

        // Adding a runtime has to be reversible too, or the file is stranded for good.
        Assert.False(File.Exists(Path.Combine(fixture.GameFolder, "nvngx_dlss.dll")));
    }

    [Fact]
    public void UndoRefusesWhenTheGameWasChangedBySomethingElse()
    {
        using var fixture = new SwapFixture(RuntimeKind.Dlss);
        fixture.InstallRuntime("3.8.0");
        Assert.True(Assert.Single(fixture.Swap().Results).Succeeded);

        // Something else edits the file afterwards.
        var target = Path.Combine(fixture.GameFolder, "nvngx_dlss.dll");
        File.WriteAllText(target, "edited by a patcher");

        var undo = fixture.Undo();

        // R28: no entry describes a state the game can return to, so refuse rather than discard.
        Assert.False(undo.Succeeded);
        Assert.Contains("other than this program", undo.ErrorMessage);

        // The refusal changed nothing: not the file, not the backup.
        Assert.Equal("edited by a patcher", File.ReadAllText(target));
    }

    [Fact]
    public void UndoRefusesWhenTheSavedCopyIsMissing()
    {
        using var fixture = new SwapFixture(RuntimeKind.Dlss);
        fixture.InstallRuntime("3.8.0");

        var swap = Assert.Single(fixture.Swap().Results);
        Assert.True(swap.Succeeded, swap.ErrorMessage);

        // The backup is gone. R29: report as unrecoverable, do not claim to be restorable.
        foreach (var backup in swap.Backups)
        {
            File.Delete(backup.BackupPath);
        }

        var undo = fixture.Undo();

        Assert.False(undo.Succeeded);
        Assert.Contains("Nothing has been changed", undo.ErrorMessage);

        // Nothing was touched on the way to failing.
        Assert.Equal(SwapFixture.NewContent("nvngx_dlss.dll"),
            File.ReadAllText(Path.Combine(fixture.GameFolder, "nvngx_dlss.dll")));
    }

    [Fact]
    public void UndoCanItselfBeUndone()
    {
        using var fixture = new SwapFixture(RuntimeKind.Dlss);
        fixture.InstallRuntime("3.8.0");

        Assert.True(Assert.Single(fixture.Swap().Results).Succeeded);
        Assert.True(fixture.Undo().Succeeded);
        Assert.Equal(SwapFixture.OldContent("nvngx_dlss.dll"),
            File.ReadAllText(Path.Combine(fixture.GameFolder, "nvngx_dlss.dll")));

        // R26: each step is separately reversible. The undo used to write a history entry with no
        // backups, so the second undo reported "nothing to undo" and the change was permanent.
        var redo = fixture.Undo();

        Assert.True(redo.Succeeded, redo.ErrorMessage);
        Assert.Equal(SwapFixture.NewContent("nvngx_dlss.dll"),
            File.ReadAllText(Path.Combine(fixture.GameFolder, "nvngx_dlss.dll")));
    }

    [Fact]
    public void UndoWithNothingToUndoSaysSo()
    {
        using var fixture = new SwapFixture(RuntimeKind.Dlss);

        var undo = fixture.Undo();

        Assert.False(undo.Succeeded);
        Assert.Contains("Nothing to undo", undo.ErrorMessage);
    }

    // ------------------------------------------------------------ SwapPlan

    [Fact]
    public void SwapPlanSummaryIncludesWarningMark()
    {
        var plan = new SwapPlan(
            "123", "Test Game", @"C:\Game",
            RuntimeKind.Dlss, "3.8.0", "3.7.10", RuntimeTrust.Trusted,
            true, false, true, [], []);

        Assert.Contains("⚠", plan.Summary);
        Assert.Contains("3.8.0", plan.Summary);
        Assert.Contains("3.7.10", plan.Summary);
    }

    [Fact]
    public void SwapPlanSummaryShowsUntrusted()
    {
        var plan = new SwapPlan(
            "123", "Test Game", @"C:\Game",
            RuntimeKind.Dlss, "3.7.10", "3.8.0", RuntimeTrust.Untrusted,
            false, true, true, [], []);

        Assert.Contains("⚠", plan.Summary);
    }

    // ------------------------------------------------------------ BatchSwapResult

    [Fact]
    public void BatchSwapResultCountsSuccessAndFailure()
    {
        var results = new[]
        {
            SwapResult.Success("1", "G1", [Backup("bak1")], [], Entry("1")),
            SwapResult.Failed("2", "G2", "Error"),
            SwapResult.Success("3", "G3", [Backup("bak3")], [], Entry("3")),
        };

        var batch = new BatchSwapResult(results.ToImmutableList(), false, 0);

        Assert.Equal(2, batch.Succeeded);
        Assert.Equal(1, batch.Failed);
        Assert.False(batch.AllSucceeded);
    }

    private static BackupRecord Backup(string name) =>
        new($@"C:\G\{name}", name, new string('0', 64));

    private static HistoryEntry Entry(string id) => new(
        DateTimeOffset.UtcNow, id, "G", @"C:\G", RuntimeKind.Dlss, "1", "2",
        RuntimeTrust.Trusted, RuntimeTrust.Trusted, [], [], false, false);
}

/// <summary>
/// A game folder, a runtime collection and everything needed to swap one into the other.
/// </summary>
/// <remarks>
/// Written once and reused by every test here so that each test body says what it is checking
/// rather than how the fixture is assembled. The collection is deliberately a folder of real
/// files: the multi-DLL behaviour being tested is about which files get copied, and mocking the
/// filesystem would test the mock.
/// </remarks>
internal sealed class SwapFixture : IDisposable
{
    private readonly TemporaryDirectory _temp = new();
    private readonly string _dataRoot;

    public SwapFixture(RuntimeKind kind)
    {
        Kind = kind;
        GameId = "game-1";

        _dataRoot = Path.Combine(_temp.Root, "data");
        Paths = new DataPaths(_dataRoot);
        Persistence = new PersistenceService(Paths);

        Catalog = new RuntimeCatalog(Paths.CatalogPath);

        GameFolder = Path.Combine(_temp.Root, "Game");
        Directory.CreateDirectory(GameFolder);

        CollectionFolder = Path.Combine(_temp.Root, "Collection", Kind.ToString());
        Directory.CreateDirectory(CollectionFolder);

        GameVersions = new Dictionary<string, bool>();
    }

    public RuntimeKind Kind { get; }
    public string GameId { get; }
    public DataPaths Paths { get; }
    public PersistenceService Persistence { get; }
    public RuntimeCatalog Catalog { get; }
    public string GameFolder { get; }
    public string CollectionFolder { get; }
    public bool AllowUntrusted { get; set; } = true;
    public bool GameExistsOnDisk { get; set; } = true;

    /// <summary>The version the game is reported as already running.</summary>
    public string GameReportedVersion { get; set; } = "3.7.10";

    public Dictionary<string, bool> GameVersions { get; }
    public CatalogEntry? Entry { get; private set; }

    /// <summary>Content a given runtime DLL has before any swap.</summary>
    public static string OldContent(string fileName) => $"OLD-{fileName}-3.7.10";

    /// <summary>Content a given runtime DLL has after being swapped in.</summary>
    public static string NewContent(string fileName) => $"NEW-{fileName}-3.8.0";

    /// <summary>
    /// Puts an old copy of the runtime in the game and a new one in the collection.
    /// </summary>
    public void InstallRuntime(string collectionVersion)
    {
        foreach (var fileName in RuntimeDlls.ByKind[Kind])
        {
            File.WriteAllText(Path.Combine(GameFolder, fileName), OldContent(fileName));
            GameVersions[fileName] = true;

            File.WriteAllText(Path.Combine(CollectionFolder, fileName), NewContent(fileName));
        }

        var primary = Path.Combine(CollectionFolder, RuntimeDlls.ByKind[Kind][0]);
        var detected = new DetectedRuntime(
            Kind,
            RuntimeDlls.ByKind[Kind][0],
            RuntimeDlls.ByKind[Kind][0],
            primary,
            collectionVersion,
            null,
            Exists: true);

        Entry = Catalog.AddFromDiscovery(detected, GameId, "Test Game", "Steam");
    }

    /// <summary>Plans a swap of the collection's runtime into the game.</summary>
    public IReadOnlyList<SwapPlan> Plan()
    {
        var snapshot = new GameRuntimeSnapshot(
            GameId, "Test Game", GameFolder,
            RuntimeDlls.ByKind[Kind]
                .Select(name => new DetectedRuntime(
                    Kind, name, name, Path.Combine(GameFolder, name),
                    GameVersions.TryGetValue(name, out var present) && present ? GameReportedVersion : null,
                    null,
                    present))
                .ToArray());

        return SwapPlanner.Plan(
            [new SwapGameInfo(GameId, "Test Game", GameFolder, GameExistsOnDisk, snapshot)],
            Catalog,
            Kind,
            Entry!.Id,
            Settings.Default.With(allowUntrustedRuntimes: AllowUntrusted));
    }

    /// <summary>Plans and applies in one step, for tests whose interest is the outcome.</summary>
    public BatchSwapResult Swap() => Execute(Plan().ToArray());

    /// <summary>Executes one or more plans against the fixture's game.</summary>
    public BatchSwapResult Execute(params SwapPlan[] plans) =>
        new SwapExecutor(Persistence, Catalog, Paths).Execute(plans);

    /// <summary>Executes one or more plans, stopping work once the token is cancelled.</summary>
    public BatchSwapResult ExecuteCancelled(CancellationToken cancellation, params SwapPlan[] plans) =>
        new SwapExecutor(Persistence, Catalog, Paths).Execute(plans, cancellation);

    /// <summary>Undoes the most recent swap for this fixture's runtime kind.</summary>
    public SwapResult Undo() =>
        new UndoEngine(Persistence, Paths).Undo(GameId, Kind);

    public void Dispose() => _temp.Dispose();
}
