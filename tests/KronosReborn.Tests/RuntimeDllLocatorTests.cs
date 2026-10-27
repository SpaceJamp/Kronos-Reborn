using System;
using System.IO;
using System.Linq;
using KronosReborn.Data.Libraries;
using KronosReborn.Data.Runtimes;
using Xunit;

namespace KronosReborn.Tests;

/// <summary>
/// Tests for finding runtime DLLs inside a game folder.
/// </summary>
/// <remarks>
/// <para>
/// Every path used here was taken from a real Steam library rather than invented, because the
/// original lookup only checked a fixed list of folder names and the real ones disagreed with all of
/// it. The Witcher 3 keeps DLSS in <c>bin\x64_dx12</c>, Injustice in <c>Binaries\Retail</c> and Portal
/// with RTX in <c>bin\.trex</c>. With the old list all three reported no runtime at all, so the
/// program would have had nothing to swap in most of a typical library.
/// </para>
/// <para>
/// These tests are the reason the fix is considered complete: they fail against the old
/// implementation for every one of those layouts.
/// </para>
/// </remarks>
public class RuntimeDllLocatorTests
{
    /// <summary>Folder layouts observed in real installs, relative to the game folder.</summary>
    public static TheoryData<string> RealWorldLayouts => new()
    {
        // The common case: straight in the game root.
        "",
        // A single intermediate level.
        "x64",
        // The Witcher 3: two levels down, under bin.
        "bin/x64_dx12",
        // Injustice 2: capitalised, two levels down.
        "Binaries/Retail",
        // Portal with RTX: bin, then a dot-prefixed folder.
        "bin/.trex",
        // Hellraiser: the game folder nested inside its own named subfolder.
        "Hellraiser/Binaries/Win64",
    };

    [Theory]
    [MemberData(nameof(RealWorldLayouts))]
    public void FindsDllsInEveryLayoutSeenInRealInstalls(string layout)
    {
        using var temp = new TemporaryDirectory();
        var game = CreateGameWith(temp.Root, layout, "nvngx_dlss.dll");

        var found = RuntimeDllLocator.Find(game, cancellation: TestContext.Current.CancellationToken);

        Assert.True(found.ContainsKey("nvngx_dlss.dll"), $"Not found under '{layout}'");
        Assert.Equal(ExpectedLocation(game, layout, "nvngx_dlss.dll"), found["nvngx_dlss.dll"]);
    }

    [Fact]
    public void FindsEveryRuntimeAGameCarriesInOneFolder()
    {
        using var temp = new TemporaryDirectory();
        var game = CreateGameWith(temp.Root, "bin/x64_dx12", "amd_fidelityfx_dx12.dll", "nvngx_dlss.dll");

        var found = RuntimeDllLocator.Find(game, cancellation: TestContext.Current.CancellationToken);

        Assert.Equal(2, found.Count);
        Assert.Contains("amd_fidelityfx_dx12.dll", found.Keys);
        Assert.Contains("nvngx_dlss.dll", found.Keys);
    }

    [Fact]
    public void FindsTheUnrealEngineDlssPluginEightLevelsDown()
    {
        // The case that used to report no DLSS at all: the old four-level limit stopped short of it.
        using var temp = new TemporaryDirectory();
        var game = CreateGameWith(
            temp.Root, "Engine/Plugins/Runtime/Nvidia/DLSS/Binaries/ThirdParty/Win64", "nvngx_dlss.dll");

        var found = RuntimeDllLocator.Find(game, cancellation: TestContext.Current.CancellationToken);

        Assert.EndsWith(Path.Combine("Win64", "nvngx_dlss.dll"), found["nvngx_dlss.dll"]);
    }

    [Fact]
    public void FindsEveryCopyAndMatchesNamesWhateverTheirCase()
    {
        using var temp = new TemporaryDirectory();
        var game = CreateGameWith(temp.Root, "bin", "nvngx_dlss.dll");
        var second = Path.Combine(game, "plugins", "NVNGX_DLSS.DLL");
        Directory.CreateDirectory(Path.GetDirectoryName(second)!);
        File.WriteAllText(second, "x");

        var found = RuntimeDllLocator.FindAll(game, cancellation: TestContext.Current.CancellationToken);

        // Keyed by the canonical name, with both copies listed.
        Assert.Equal(2, found["nvngx_dlss.dll"].Count);
    }

    [Fact]
    public void FollowsALinkedFolderOnce()
    {
        using var temp = new TemporaryDirectory();
        var game = CreateGameWith(temp.Root, "real", "nvngx_dlss.dll");
        var link = Path.Combine(game, "linked");

        try
        {
            Directory.CreateSymbolicLink(link, Path.Combine(game, "real"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return; // creating links needs developer mode or elevation; nothing to check without one
        }

        var found = RuntimeDllLocator.FindAll(game, cancellation: TestContext.Current.CancellationToken);

        // Found once, through whichever path reached the folder first, rather than twice or never.
        Assert.Single(found["nvngx_dlss.dll"]);
    }

    [Fact]
    public void ReportsNothingForAGameWithNoRuntimes()
    {
        using var temp = new TemporaryDirectory();
        var game = Path.Combine(temp.Root, "Game");
        Directory.CreateDirectory(game);

        Assert.Empty(RuntimeDllLocator.Find(game, cancellation: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ReportsNothingForAMissingFolderRatherThanThrowing()
    {
        using var temp = new TemporaryDirectory();

        Assert.Empty(RuntimeDllLocator.Find(
            Path.Combine(temp.Root, "NotThere"),
            cancellation: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void StopsAtTheDepthLimit()
    {
        using var temp = new TemporaryDirectory();

        // One level beyond the bound, so deliberately not found.
        var game = CreateGameWith(temp.Root, "a/b/c/d/e/f/g/h/i/j/k", "nvngx_dlss.dll");

        Assert.Empty(RuntimeDllLocator.Find(game, cancellation: TestContext.Current.CancellationToken));

        // And the same file is found when the bound is raised, which shows the bound is the only
        // reason and not a failure to walk at all.
        Assert.Single(RuntimeDllLocator.Find(
            game, maxDepth: 12, cancellation: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void AFileTheGameDoesNotHaveBelongsInTheGameRoot()
    {
        using var temp = new TemporaryDirectory();
        var game = CreateGameWith(temp.Root, "bin/x64_dx12", "nvngx_dlss.dll");

        var located = RuntimeDllLocator.Find(game, cancellation: TestContext.Current.CancellationToken);

        // DLSS is where the game keeps it...
        Assert.Equal(
            Path.Combine(game, "bin", "x64_dx12", "nvngx_dlss.dll"),
            RuntimeDllLocator.TargetPathFor(game, "nvngx_dlss.dll", located));

        // ...and a DLL it has never heard of goes in the root rather than somewhere arbitrary.
        Assert.Equal(
            Path.Combine(game, "nvngx_dlssg.dll"),
            RuntimeDllLocator.TargetPathFor(game, "nvngx_dlssg.dll", located));
    }

    [Fact]
    public void PrefersTheShallowestCopyOfADll()
    {
        using var temp = new TemporaryDirectory();
        var game = Path.Combine(temp.Root, "Game");

        // Two copies, as happens when a patch leaves an old one behind.
        Directory.CreateDirectory(Path.Combine(game, "bin"));
        Directory.CreateDirectory(Path.Combine(game, "bin", "backup"));
        File.WriteAllText(Path.Combine(game, "nvngx_dlss.dll"), "root");
        File.WriteAllText(Path.Combine(game, "bin", "nvngx_dlss.dll"), "preferred");
        File.WriteAllText(Path.Combine(game, "bin", "backup", "nvngx_dlss.dll"), "buried");

        var found = RuntimeDllLocator.Find(game, cancellation: TestContext.Current.CancellationToken);

        // Breadth-first, so the shallowest is chosen deterministically rather than by directory order.
        Assert.Equal(Path.Combine(game, "nvngx_dlss.dll"), found["nvngx_dlss.dll"]);
    }

    [Fact]
    public void TheScannerAndThePlannerAgreeOnWhereADllBelongs()
    {
        using var temp = new TemporaryDirectory();
        var game = CreateGameWith(temp.Root, "bin/x64_dx12", "nvngx_dlss.dll");

        // What the scanner reports...
        var record = new GameRecord("Steam", "292030", "The Witcher 3", game, Exists: true);
        var snapshot = new GameRuntimeScanner().Scan(record, TestContext.Current.CancellationToken);

        var detected = snapshot.Runtimes.Single(r => r.Kind == RuntimeKind.Dlss);
        Assert.Equal(
            Path.Combine("bin", "x64_dx12", "nvngx_dlss.dll"),
            detected.RelativePath);

        // ...and where the planner would write a replacement.
        var catalogFolder = Path.Combine(temp.Root, "Collection");
        Directory.CreateDirectory(catalogFolder);
        File.WriteAllText(Path.Combine(catalogFolder, "nvngx_dlss.dll"), "newer");
        File.WriteAllText(Path.Combine(catalogFolder, "nvngx_dlssg.dll"), "newer");

        var catalog = new RuntimeCatalog(Path.Combine(temp.Root, "catalog.json"));
        var entry = catalog.AddFromDiscovery(
            new DetectedRuntime(RuntimeKind.Dlss, "nvngx_dlss.dll", "nvngx_dlss.dll",
                Path.Combine(catalogFolder, "nvngx_dlss.dll"), "9.9.9", null, true),
            "292030", "The Witcher 3", "Steam");

        var plans = KronosReborn.Data.Actions.SwapPlanner.Plan(
            [new KronosReborn.Data.Actions.SwapGameInfo("292030", "The Witcher 3", game, true, snapshot)],
            catalog, RuntimeKind.Dlss, entry!.Id,
            KronosReborn.Data.Persistence.Settings.Default.With(allowUntrustedRuntimes: true));

        var plan = Assert.Single(plans);

        // The bug: the planner wrote to the game root while the game loaded from bin\x64_dx12, so the
        // swap "succeeded" and the game kept running the old runtime.
        Assert.Equal(
            Path.Combine(game, "bin", "x64_dx12", "nvngx_dlss.dll"),
            Assert.Single(plan.ReplacedFiles));
    }

    [Fact]
    public void FindAllReturnsEveryCopyOfADll()
    {
        using var temp = new TemporaryDirectory();
        var game = Path.Combine(temp.Root, "Game");

        // Two copies, as happens when a patch leaves an old one behind.
        Directory.CreateDirectory(Path.Combine(game, "bin"));
        Directory.CreateDirectory(Path.Combine(game, "bin", "backup"));
        File.WriteAllText(Path.Combine(game, "nvngx_dlss.dll"), "root");
        File.WriteAllText(Path.Combine(game, "bin", "nvngx_dlss.dll"), "preferred");
        File.WriteAllText(Path.Combine(game, "bin", "backup", "nvngx_dlss.dll"), "buried");

        var found = RuntimeDllLocator.FindAll(game, cancellation: TestContext.Current.CancellationToken);

        Assert.True(found.ContainsKey("nvngx_dlss.dll"));
        Assert.Equal(3, found["nvngx_dlss.dll"].Count);
        // Shallowest first.
        Assert.Equal(Path.Combine(game, "nvngx_dlss.dll"), found["nvngx_dlss.dll"][0]);
    }

    [Fact]
    public void FindAllReportsNothingForAMissingFolder()
    {
        using var temp = new TemporaryDirectory();

        var found = RuntimeDllLocator.FindAll(
            Path.Combine(temp.Root, "NotThere"),
            cancellation: TestContext.Current.CancellationToken);

        Assert.Empty(found);
    }

    [Fact]
    public void DetectedRuntimeReportsMultipleFoundWhenAlternativesExist()
    {
        var runtime = new DetectedRuntime(
            RuntimeKind.Dlss, "nvngx_dlss.dll", "nvngx_dlss.dll",
            @"C:\game\nvngx_dlss.dll", "310.9.1", null, true,
            AlternativePaths: [@"C:\game\bin\nvngx_dlss.dll"]);

        Assert.True(runtime.MultipleFound);
        Assert.Single(runtime.AlternativePaths!);
    }

    [Fact]
    public void DetectedRuntimeReportsNotMultipleWhenNoAlternatives()
    {
        var runtime = new DetectedRuntime(
            RuntimeKind.Dlss, "nvngx_dlss.dll", "nvngx_dlss.dll",
            @"C:\game\nvngx_dlss.dll", "310.9.1", null, true);

        Assert.False(runtime.MultipleFound);
        Assert.Null(runtime.AlternativePaths);
    }
    private static string ExpectedLocation(string game, string layout, string fileName) =>
        layout.Length == 0
            ? Path.Combine(game, fileName)
            : Path.Combine(game, layout.Replace('/', Path.DirectorySeparatorChar), fileName);

    private static string CreateGameWith(string root, string layout, params string[] fileNames)
    {
        var game = Path.Combine(root, "Game");
        var directory = ExpectedLocation(game, layout, string.Empty)
            .TrimEnd(Path.DirectorySeparatorChar);

        Directory.CreateDirectory(directory);

        foreach (var fileName in fileNames)
        {
            File.WriteAllText(Path.Combine(directory, fileName), "content");
        }

        return game;
    }
}
