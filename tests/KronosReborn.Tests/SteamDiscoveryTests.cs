using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KronosReborn.Data.Libraries;
using KronosReborn.Data.Valve;
using Xunit;

namespace KronosReborn.Tests;

/// <summary>
/// Tests for the Steam library discovery pipeline.
/// </summary>
/// <remarks>
/// These test the parts that are pure parsing and path logic, using real temporary directories
/// for the filesystem-dependent pieces. A mock filesystem would just reimplement the same logic;
/// a real temp directory exercises the actual Directory/File methods the scanner uses.
/// </remarks>
public class SteamDiscoveryTests
{
    [Fact]
    public void SteamLibraryFoldersExtractsOldLayout()
    {
        var text = """
            "libraryfolders"
            {
                "0"
                {
                    "path" "C:\\Steam"
                }
                "1"
                {
                    "path" "D:\\Games"
                }
            }
            """;

        var root = ValveKeyValues.Parse(text);
        var paths = SteamLibraryFolders.Extract(root!);

        Assert.Equal(2, paths.Count);
        Assert.Equal(@"C:\Steam", paths[0]);
        Assert.Equal(@"D:\Games", paths[1]);
    }

    [Fact]
    public void SteamLibraryFoldersExtractsNewLayout()
    {
        var text = """
            "libraryfolders"
            {
                "path"
                {
                    "0" "C:\\Steam"
                    "1" "D:\\Games"
                }
            }
            """;

        var root = ValveKeyValues.Parse(text);
        var paths = SteamLibraryFolders.Extract(root!);

        Assert.Equal(2, paths.Count);
        Assert.Equal(@"C:\Steam", paths[0]);
        Assert.Equal(@"D:\Games", paths[1]);
    }

    [Fact]
    public void SteamLibraryFoldersNewLayoutPreferredOverOld()
    {
        var text = """
            "libraryfolders"
            {
                "path"
                {
                    "0" "C:\\Steam"
                }
                "0"
                {
                    "path" "OLD"
                }
            }
            """;

        var root = ValveKeyValues.Parse(text);
        var paths = SteamLibraryFolders.Extract(root!);

        Assert.Single(paths);
        Assert.Equal(@"C:\Steam", paths[0]);
    }

    [Fact]
    public void SteamLibraryFoldersExtractsDuplicatePathsAsWritten()
    {
        // Extract returns what the file says. Deduplication against the Steam root happens in TryRead.
        var text = """
            "libraryfolders"
            {
                "0" { "path" "C:\\Steam" }
                "1" { "path" "C:\\Steam" }
            }
            """;

        var root = ValveKeyValues.Parse(text);
        var paths = SteamLibraryFolders.Extract(root!);

        Assert.Equal(2, paths.Count);
    }

    [Fact]
    public void SteamLibraryFoldersTryReadDeduplicatesAgainstSteamRoot()
    {
        using var temp = new TemporaryDirectory();

        var steamapps = Path.Combine(temp.Root, "steamapps");
        Directory.CreateDirectory(steamapps);

        // The VDF lists the same folder the steam root is in, so TryRead should dedup it
        var steamRootEscaped = temp.Root.Replace("\\", "\\\\");
        File.WriteAllText(
            Path.Combine(steamapps, "libraryfolders.vdf"),
            $$"""
            "libraryfolders"
            {
                "0" { "path" "{{steamRootEscaped}}" }
            }
            """);

        var ok = SteamLibraryFolders.TryRead(temp.Root, out var paths, out var problem);

        Assert.True(ok);
        Assert.Single(paths);
    }

    [Fact]
    public void SteamLibraryFoldersEmptyRootYieldsEmpty()
    {
        var text = "\"libraryfolders\" {}";

        var root = ValveKeyValues.Parse(text);
        var paths = SteamLibraryFolders.Extract(root!);

        Assert.Empty(paths);
    }

    // ------------------------------------------------------------ SteamAppManifest

    [Fact]
    public void SteamAppManifestParsesTypicalManifest()
    {
        var text = """
            "AppState"
            {
                "appid" "730"
                "name" "Counter-Strike 2"
                "installdir" "Counter-Strike Global Offensive"
            }
            """;

        var ok = SteamAppManifest.TryParse(text, out var manifest, out var error);

        Assert.True(ok);
        Assert.Equal("730", manifest!.AppId);
        Assert.Equal("Counter-Strike 2", manifest.Name);
        Assert.Equal("Counter-Strike Global Offensive", manifest.InstallDirectory);
        Assert.True(manifest.IsUsable);
        Assert.True(manifest.HasSafeInstallDirectory);
    }

    [Fact]
    public void SteamAppManifestBareKeysAreAccepted()
    {
        var text = """
            AppState
            {
                appid 730
                name Half-Life
                installdir Half-Life
            }
            """;

        var ok = SteamAppManifest.TryParse(text, out var manifest, out var error);

        Assert.True(ok);
        Assert.Equal("730", manifest!.AppId);
    }

    [Fact]
    public void SteamAppManifestMissingAppIdIsRejected()
    {
        var text = """
            "AppState"
            {
                "name" "No ID Game"
                "installdir" "SomeFolder"
            }
            """;

        var ok = SteamAppManifest.TryParse(text, out var manifest, out var error);

        Assert.False(ok);
        Assert.Null(manifest);
        Assert.NotNull(error);
    }

    [Fact]
    public void SteamAppManifestPathTraversalIsRejected()
    {
        var text = """
            "AppState"
            {
                "appid" "999"
                "installdir" "..\\..\\Windows"
            }
            """;

        var ok = SteamAppManifest.TryParse(text, out var manifest, out var error);

        Assert.True(ok);
        Assert.False(manifest!.HasSafeInstallDirectory);
    }

    [Fact]
    public void SteamAppManifestAbsolutePathIsRejected()
    {
        var text = """
            "AppState"
            {
                "appid" "999"
                "installdir" "C:\\Games\\Thing"
            }
            """;

        var ok = SteamAppManifest.TryParse(text, out var manifest, out var error);

        Assert.True(ok);
        Assert.False(manifest!.HasSafeInstallDirectory);
    }

    [Fact]
    public void SteamAppManifestFileNamePatternMatchesOnlyValid()
    {
        Assert.Equal("730", SteamAppManifest.TryGetAppIdFromFileName("appmanifest_730.acf"));
        Assert.Null(SteamAppManifest.TryGetAppIdFromFileName("appmanifest_notanumber.acf"));
        Assert.Null(SteamAppManifest.TryGetAppIdFromFileName("appmanifest_730.txt"));
        Assert.Null(SteamAppManifest.TryGetAppIdFromFileName("notamaniest_730.acf"));
    }

    // ------------------------------------------------------------ GameRecord dedup

    [Fact]
    public void GameRecordDeduplicateMergesLibraries()
    {
        var a = new GameRecord("Steam", "1", "Game", @"C:\Games\Game", true);
        var b = new GameRecord("Other", "2", "Game", @"C:\Games\Game", true);

        var merged = GameRecord.Deduplicate(new[] { a, b }).ToList();

        Assert.Single(merged);
        Assert.Equal("Steam, Other", merged[0].LibraryName);
    }

    [Fact]
    public void GameRecordDeduplicateIgnoresCaseAndTrailingSeparator()
    {
        var a = new GameRecord("A", "1", "G", @"C:\Games\Game", true);
        var b = new GameRecord("B", "2", "G", @"C:\Games\Game\", true);

        var merged = GameRecord.Deduplicate(new[] { a, b }).ToList();

        Assert.Single(merged);
    }

    [Fact]
    public void GameRecordDeduplicatePreservesOrder()
    {
        var a = new GameRecord("First", "1", "G", @"C:\Games\Game", true);
        var b = new GameRecord("Second", "2", "G", @"D:\Games\Game", true);

        var merged = GameRecord.Deduplicate(new[] { a, b }).ToList();

        Assert.Equal(2, merged.Count);
        Assert.Equal("First", merged[0].LibraryName);
        Assert.Equal("Second", merged[1].LibraryName);
    }

    // ------------------------------------------------------------ PathNormalizer

    [Fact]
    public void PathNormalizerNormalizeTrimsTrailingSeparator()
    {
        Assert.Equal(@"C:\Games", PathNormalizer.Normalize(@"C:\Games\"));
        Assert.Equal(@"C:\Games", PathNormalizer.Normalize(@"C:\Games"));
    }

    [Fact]
    public void PathNormalizerNormalizeKeepsDriveRootSeparator()
    {
        Assert.Equal(@"C:\", PathNormalizer.Normalize(@"C:\"));
        Assert.Equal(@"C:\", PathNormalizer.Normalize("C:"));
    }

    [Fact]
    public void PathNormalizerNormalizeHandlesWhitespace()
    {
        Assert.Equal(@"C:\Games", PathNormalizer.Normalize(@"  C:\Games  "));
    }

    [Fact]
    public void PathNormalizerDirectoryExistsReturnsFalseForMissing()
    {
        Assert.False(PathNormalizer.DirectoryExists(@"C:\ThisDoesNotExist\Probably"));
    }

    [Fact]
    public void PathNormalizerDirectoryExistsReturnsFalseForEmpty()
    {
        Assert.False(PathNormalizer.DirectoryExists(""));
        Assert.False(PathNormalizer.DirectoryExists("   "));
    }

    // ------------------------------------------------------------ SteamAppManifest file system scan

    [Fact]
    public void SteamLibraryScannerFindsGamesInTempDirectory()
    {
        using var temp = new TemporaryDirectory();

        // Create a library layout
        var steamapps = Path.Combine(temp.Root, "steamapps");
        var common = Path.Combine(steamapps, "common");
        Directory.CreateDirectory(common);

        // Game 1
        var game1Dir = Path.Combine(common, "GameOne");
        Directory.CreateDirectory(game1Dir);
        File.WriteAllText(
            Path.Combine(steamapps, "appmanifest_100.acf"),
            """
            "AppState"
            {
                "appid" "100"
                "name" "Game One"
                "installdir" "GameOne"
            }
            """);

        // Game 2
        var game2Dir = Path.Combine(common, "GameTwo");
        Directory.CreateDirectory(game2Dir);
        File.WriteAllText(
            Path.Combine(steamapps, "appmanifest_200.acf"),
            """
            "AppState"
            {
                "appid" "200"
                "name" "Game Two"
                "installdir" "GameTwo"
            }
            """);

        var scanner = new SteamLibraryScanner();
        var result = scanner.Scan(temp.Root);

        Assert.Equal(2, result.Games.Count);
        Assert.Equal(1, result.SourcesChecked);
        Assert.Equal("100", result.Games[0].Id);
        Assert.Equal("200", result.Games[1].Id);
        Assert.True(result.Games.All(g => g.Exists));
    }

    [Fact]
    public void SteamLibraryScannerReportsMissingGames()
    {
        using var temp = new TemporaryDirectory();

        var steamapps = Path.Combine(temp.Root, "steamapps");
        var common = Path.Combine(steamapps, "common");
        Directory.CreateDirectory(common);

        // Manifest present but folder deleted
        File.WriteAllText(
            Path.Combine(steamapps, "appmanifest_300.acf"),
            """
            "AppState"
            {
                "appid" "300"
                "name" "Gone"
                "installdir" "Missing"
            }
            """);

        var scanner = new SteamLibraryScanner();
        var result = scanner.Scan(temp.Root);

        Assert.Single(result.Games);
        Assert.False(result.Games[0].Exists);
        Assert.Equal("Gone", result.Games[0].DisplayName);
    }

    [Fact]
    public void SteamLibraryScannerSkipsUnreadableManifest()
    {
        using var temp = new TemporaryDirectory();

        var steamapps = Path.Combine(temp.Root, "steamapps");
        Directory.CreateDirectory(steamapps);

        // Corrupt manifest
        File.WriteAllText(
            Path.Combine(steamapps, "appmanifest_999.acf"),
            "this is not vdf at all {");

        var scanner = new SteamLibraryScanner();
        var result = scanner.Scan(temp.Root);

        Assert.Empty(result.Games);
        Assert.Single(result.Problems);
        Assert.Equal("Steam game", result.Problems[0].Scope);
    }

    [Fact]
    public void SteamLibraryScannerReportsMissingLibraryFolder()
    {
        using var temp = new TemporaryDirectory();

        var steamapps = Path.Combine(temp.Root, "steamapps");
        Directory.CreateDirectory(steamapps);

        // Library listed but folder doesn't exist
        File.WriteAllText(
            Path.Combine(steamapps, "libraryfolders.vdf"),
            """
            "libraryfolders"
            {
                "0"
                {
                    "path" "C:\\NotHere"
                }
            }
            """);

        var scanner = new SteamLibraryScanner();
        var result = scanner.Scan(temp.Root);

        Assert.Single(result.Problems);
        Assert.Equal("Steam library", result.Problems[0].Scope);
        Assert.Contains("not present", result.Problems[0].Summary, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// A temporary directory that cleans itself up.
/// </summary>
internal sealed class TemporaryDirectory : IDisposable
{
    public string Root { get; }

    public TemporaryDirectory()
    {
        Root = Path.Combine(Path.GetTempPath(), $"kronos_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort cleanup; the temp cleaner will get it eventually.
        }
    }
}