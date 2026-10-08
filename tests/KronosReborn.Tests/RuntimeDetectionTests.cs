using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using KronosReborn.Data.Libraries;
using KronosReborn.Data.Runtimes;
using Xunit;

namespace KronosReborn.Tests;

/// <summary>
/// Tests for runtime DLL detection and version reading.
/// </summary>
public class RuntimeDetectionTests
{
    [Fact]
    public void RuntimeDllsByKindContainsExpectedFiles()
    {
        Assert.Equal(["nvngx_dlss.dll"], RuntimeDlls.ByKind[RuntimeKind.Dlss]);
        Assert.Equal(["nvngx_dlssg.dll"], RuntimeDlls.ByKind[RuntimeKind.DlssG]);
        Assert.Equal(["nvngx_dlssnr.dll"], RuntimeDlls.ByKind[RuntimeKind.DlssR]);
        Assert.Equal(["amd_fidelityfx_dx12.dll", "ffx_fsr2_api_dx12.dll"], RuntimeDlls.ByKind[RuntimeKind.Fsr]);
        Assert.Equal(["libxess.dll"], RuntimeDlls.ByKind[RuntimeKind.Xess]);
    }

    [Fact]
    public void RuntimeDllsByFileNameReverseLookupWorks()
    {
        Assert.Equal(RuntimeKind.Dlss, RuntimeDlls.ByFileName["nvngx_dlss.dll"]);
        Assert.Equal(RuntimeKind.DlssG, RuntimeDlls.ByFileName["nvngx_dlssg.dll"]);
        Assert.Equal(RuntimeKind.DlssR, RuntimeDlls.ByFileName["nvngx_dlssnr.dll"]);
        Assert.Equal(RuntimeKind.Fsr, RuntimeDlls.ByFileName["amd_fidelityfx_dx12.dll"]);
        Assert.Equal(RuntimeKind.Fsr, RuntimeDlls.ByFileName["ffx_fsr2_api_dx12.dll"]);
        Assert.Equal(RuntimeKind.Xess, RuntimeDlls.ByFileName["libxess.dll"]);
    }

    [Fact]
    public void RuntimeDllsIsCaseInsensitive()
    {
        Assert.True(RuntimeDlls.IsKnown("NVNGX_DLSS.DLL"));
        Assert.Equal(RuntimeKind.Dlss, RuntimeDlls.KindFor("nvngx_dlss.dll")!);
    }

    [Fact]
    public void RuntimeDllsAllFileNamesIsFlatList()
    {
        var all = RuntimeDlls.AllFileNames;
        Assert.Equal(6, all.Count);
        Assert.Contains("nvngx_dlss.dll", all);
        Assert.Contains("nvngx_dlssg.dll", all);
        Assert.Contains("nvngx_dlssnr.dll", all);
        Assert.Contains("amd_fidelityfx_dx12.dll", all);
        Assert.Contains("ffx_fsr2_api_dx12.dll", all);
        Assert.Contains("libxess.dll", all);
    }

    [Fact]
    public void DetectedRuntimeDescribeFormatsMissing()
    {
        var r = new DetectedRuntime(RuntimeKind.Dlss, "nvngx_dlss.dll", "nvngx_dlss.dll", "C:\\Game\\nvngx_dlss.dll", null, null, false);
        Assert.Equal("DLSS: not present", r.Describe());
    }

    [Fact]
    public void DetectedRuntimeDescribeFormatsPresentWithVersion()
    {
        var r = new DetectedRuntime(RuntimeKind.Fsr, "amd_fidelityfx_dx12.dll", "amd_fidelityfx_dx12.dll", "C:\\Game\\amd_fidelityfx_dx12.dll", "3.1.2", "3.1.2", true);
        Assert.Equal("FSR: 3.1.2", r.Describe());
    }

    [Fact]
    public void DetectedRuntimeDescribeFormatsPresentUnknownVersion()
    {
        var r = new DetectedRuntime(RuntimeKind.Xess, "libxess.dll", "libxess.dll", "C:\\Game\\libxess.dll", null, null, true, "Access denied");
        Assert.Equal("XeSS: present (version unknown, Access denied)", r.Describe());
    }

    [Fact]
    public void GameRuntimeSnapshotHasUnknownVersions()
    {
        var snap = new GameRuntimeSnapshot("1", "Game", "C:\\Game",
            new[]
            {
                new DetectedRuntime(RuntimeKind.Dlss, "nvngx_dlss.dll", "nvngx_dlss.dll", "C:\\Game\\nvngx_dlss.dll", "2.5.1", null, true),
                new DetectedRuntime(RuntimeKind.Fsr, "amd_fidelityfx_dx12.dll", "amd_fidelityfx_dx12.dll", "C:\\Game\\amd_fidelityfx_dx12.dll", null, null, true, "Failed"),
            });

        Assert.True(snap.HasUnknownVersions);
        Assert.True(snap.HasAnyRuntime);
    }

    // ------------------------------------------------------------ GameRuntimeScanner

    [Fact]
    public void GameRuntimeScannerFindsDllsInGameRoot()
    {
        using var temp = new TemporaryDirectory();

        // Create a game folder with some runtime DLLs
        var gameDir = temp.Root;
        var dlssPath = Path.Combine(gameDir, "nvngx_dlss.dll");
        var fsrPath = Path.Combine(gameDir, "amd_fidelityfx_dx12.dll");

        // Create dummy DLLs with version resources by copying a real signed DLL or using a test one
        // For this test we just create minimal PE files - the scanner will report them as present
        // but version unknown because they have no version resource.
        File.WriteAllBytes(dlssPath, CreateDummyDll());
        File.WriteAllBytes(fsrPath, CreateDummyDll());

        var game = new GameRecord("Steam", "123", "Test Game", gameDir, true);
        var scanner = new GameRuntimeScanner();
        var snap = scanner.Scan(game, TestContext.Current.CancellationToken);

        Assert.Equal("123", snap.GameId);
        Assert.Equal("Test Game", snap.GameName);
        Assert.Equal(6, snap.Runtimes.Count); // all known DLLs checked

        var dlss = snap.Runtimes.First(r => r.Kind == RuntimeKind.Dlss);
        Assert.True(dlss.Exists);
        Assert.False(dlss.IsVersionKnown); // dummy DLL has no version resource
        // ReadError is null because no exception was thrown - FileVersionInfo just returns empty version

        var fsr = snap.Runtimes.First(r => r.Kind == RuntimeKind.Fsr);
        Assert.True(fsr.Exists);

        var xess = snap.Runtimes.First(r => r.Kind == RuntimeKind.Xess);
        Assert.False(xess.Exists);
    }

    [Fact]
    public void GameRuntimeScannerChecksSubdirectories()
    {
        using var temp = new TemporaryDirectory();

        var gameDir = temp.Root;
        var x64 = Path.Combine(gameDir, "x64");
        Directory.CreateDirectory(x64);

        // DLL only in x64 subfolder
        var dlssPath = Path.Combine(x64, "nvngx_dlss.dll");
        File.WriteAllBytes(dlssPath, CreateDummyDll());

        var game = new GameRecord("Steam", "456", "Subdir Game", gameDir, true);
        var scanner = new GameRuntimeScanner();
        var snap = scanner.Scan(game, TestContext.Current.CancellationToken);

        var dlss = snap.Runtimes.First(r => r.Kind == RuntimeKind.Dlss);
        Assert.True(dlss.Exists);
        Assert.Equal("x64\\nvngx_dlss.dll", dlss.RelativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    [Fact]
    public void GameRuntimeScannerHandlesMissingGame()
    {
        using var temp = new TemporaryDirectory();

        // Game record points to non-existent folder
        var game = new GameRecord("Steam", "999", "Missing Game", Path.Combine(temp.Root, "NotHere"), false);
        var scanner = new GameRuntimeScanner();

        // ScanMany skips games that don't exist on disk, returning empty snapshots for them
        var results = scanner.ScanMany([game], TestContext.Current.CancellationToken);

        Assert.Single(results);
        var snap = results[0];
        Assert.Equal("999", snap.GameId);
        Assert.Empty(snap.Runtimes);
        Assert.False(snap.HasAnyRuntime);
    }

    [Fact]
    public void GameRuntimeScannerScanManyAccumulatesResults()
    {
        using var temp = new TemporaryDirectory();

        var game1Dir = Path.Combine(temp.Root, "Game1");
        Directory.CreateDirectory(game1Dir);
        File.WriteAllBytes(Path.Combine(game1Dir, "nvngx_dlss.dll"), CreateDummyDll());

        var game2Dir = Path.Combine(temp.Root, "Game2");
        Directory.CreateDirectory(game2Dir);
        // No DLLs in game 2

        var games = new[]
        {
            new GameRecord("Steam", "1", "Game One", game1Dir, true),
            new GameRecord("Steam", "2", "Game Two", game2Dir, true),
        };

        var scanner = new GameRuntimeScanner();
        var results = scanner.ScanMany(games, TestContext.Current.CancellationToken);

        Assert.Equal(2, results.Count);
        Assert.True(results[0].HasAnyRuntime);
        Assert.False(results[1].HasAnyRuntime);
    }

    [Fact]
    public void GameRuntimeScannerCancelsBetweenGames()
    {
        using var temp = new TemporaryDirectory();

        var games = Enumerable.Range(1, 5).Select(i =>
        {
            var dir = Path.Combine(temp.Root, $"Game{i}");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "nvngx_dlss.dll"), CreateDummyDll());
            return new GameRecord("Steam", i.ToString(System.Globalization.CultureInfo.InvariantCulture), $"Game {i}", dir, true);
        }).ToList();

        var scanner = new GameRuntimeScanner();
        var cts = new System.Threading.CancellationTokenSource();

        // Cancel after first game by using a token that cancels immediately on the second call
        // We can't easily test mid-scan cancellation without more infrastructure, but we can
        // verify the token is checked.
        var token = cts.Token;
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => scanner.ScanMany(games, token));
    }

    /// <summary>
    /// Creates a minimal PE file that FileVersionInfo will try to read but fail on gracefully.
    /// </summary>
    private static byte[] CreateDummyDll()
    {
        // A minimal valid PE file that isn't a real DLL. FileVersionInfo.GetVersionInfo
        // will return a FileVersionInfo with empty version strings for this.
        return [
            0x4D, 0x5A, // MZ header
            0x90, 0x00, 0x03, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0x00, 0x00,
            0xB8, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x40, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x80, 0x00,
            0x00, 0x00, 0x0E, 0x1F, 0xBA, 0x0E, 0x00, 0xB4, 0x09, 0xCD, 0x21, 0xB8, 0x01, 0x4C,
            0xCD, 0x21, 0x54, 0x68, 0x69, 0x73, 0x20, 0x70, 0x72, 0x6F, 0x67, 0x72, 0x61, 0x6D,
            0x20, 0x63, 0x61, 0x6E, 0x6E, 0x6F, 0x74, 0x20, 0x62, 0x65, 0x20, 0x72, 0x75, 0x6E,
            0x20, 0x69, 0x6E, 0x20, 0x44, 0x4F, 0x53, 0x20, 0x6D, 0x6F, 0x64, 0x65, 0x2E, 0x0D,
            0x0D, 0x0A, 0x24, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x50, 0x45, 0x00, 0x00 // PE signature at offset 0x80
        ];
    }
}
