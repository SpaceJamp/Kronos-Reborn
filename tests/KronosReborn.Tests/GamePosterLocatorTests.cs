using System;
using System.IO;
using System.Linq;
using KronosReborn.Data.Libraries;
using Xunit;

namespace KronosReborn.Tests;

/// <summary>
/// Tests for finding cover art Steam has already downloaded.
/// </summary>
/// <remarks>
/// Both folder layouts used here were read off a real Steam installation rather than guessed, because
/// the two are written by different client generations and a program that looks in only one of them
/// shows posters on some machines and not others. The folder and file names below are the ones Steam
/// actually used.
/// </remarks>
public class GamePosterLocatorTests
{
    [Fact]
    public void FindsTheVerticalCapsuleInTheCurrentLayout()
    {
        using var temp = new TemporaryDirectory();
        var steam = SteamRoot(temp);

        // The layout a current client writes: named files inside a folder named for the image hash.
        WriteImage(steam, "appcache/librarycache/292030", "aa11bb22", "library_capsule.jpg");
        WriteImage(steam, "appcache/librarycache/292030", "aa11bb22", "library_header.jpg");
        WriteImage(steam, "appcache/librarycache/292030", "aa11bb22", "logo.png");
        WriteImage(steam, "appcache/librarycache/292030", "cc33dd44", "library_hero.jpg");

        var found = GamePosterLocator.Find("292030", [steam]);

        Assert.NotNull(found);
        Assert.Equal("library_capsule.jpg", Path.GetFileName(found));
    }

    [Fact]
    public void FindsArtworkInTheOlderLayout()
    {
        using var temp = new TemporaryDirectory();
        var steam = SteamRoot(temp);

        // The older layout puts the files directly in the app folder.
        WriteImage(steam, "appcache/librarycache/2012840", null, "library_600x900.jpg");
        WriteImage(steam, "appcache/librarycache/2012840", null, "header.jpg");
        WriteImage(steam, "appcache/librarycache/2012840", null, "logo.png");

        var found = GamePosterLocator.Find("2012840", [steam]);

        Assert.NotNull(found);
        Assert.Equal("library_600x900.jpg", Path.GetFileName(found));
    }

    [Fact]
    public void PrefersTheVerticalPosterOverAWideOne()
    {
        using var temp = new TemporaryDirectory();
        var steam = SteamRoot(temp);

        // Both present in the same folder. A narrow poster column wants the vertical one.
        WriteImage(steam, "appcache/librarycache/627270", null, "header.jpg");
        WriteImage(steam, "appcache/librarycache/627270", null, "library_capsule.jpg");

        var found = GamePosterLocator.Find("627270", [steam]);

        Assert.Equal("library_capsule.jpg", Path.GetFileName(found));
    }

    [Fact]
    public void TheCoverInAHashSubfolderWinsOverTheIconNamedForItsHash()
    {
        using var temp = new TemporaryDirectory();
        var steam = SteamRoot(temp);

        // The current layout, as Hogwarts Legacy has it: a 32x32 icon at the top named for its hash,
        // and each real image in a hash-named subfolder. The icon used to be taken as the cover.
        WriteImage(steam, "appcache/librarycache/990080", null, "a9ecb94f249768d0ee5ccecbffe8d8c06d9bed59.jpg");
        WriteImage(steam, "appcache/librarycache/990080", "471c718aa6d4e2111ae24d7a7427a7485c076f38", "library_hero.jpg");
        WriteImage(steam, "appcache/librarycache/990080", "dca9bb926befe37ff9754e3336cd9fe4c969c1e9", "library_capsule.jpg");

        var found = GamePosterLocator.Find("990080", [steam]);

        Assert.Equal("library_capsule.jpg", Path.GetFileName(found));
    }

    [Fact]
    public void TheIconNamedForItsHashIsNeverUsedAsTheCover()
    {
        using var temp = new TemporaryDirectory();
        var steam = SteamRoot(temp);

        WriteImage(steam, "appcache/librarycache/1938090", null, "8eaf32220060344996cbf11f697a4f4be943e5f3.jpg");

        Assert.Null(GamePosterLocator.Find("1938090", [steam]));
    }

    [Fact]
    public void LooksInTheOlderArtworkFolderToo()
    {
        using var temp = new TemporaryDirectory();
        var steam = SteamRoot(temp);

        WriteImage(steam, "steamapps/librarycache/431960", null, "library_600x900.jpg");

        var found = GamePosterLocator.Find("431960", [steam]);

        Assert.NotNull(found);
    }

    [Fact]
    public void AGameWithNoCachedArtIsSimplyReportedAsHavingNone()
    {
        using var temp = new TemporaryDirectory();
        var steam = SteamRoot(temp);

        WriteImage(steam, "appcache/librarycache/5309180", null, "library_capsule.jpg");

        // The specification defers artwork and requires games to be listed without it, so absence is
        // an ordinary outcome and must not be a failure.
        Assert.Null(GamePosterLocator.Find("9999999", [steam]));
    }

    [Fact]
    public void AMissingArtworkFolderIsNotAFailure()
    {
        using var temp = new TemporaryDirectory();
        var steam = SteamRoot(temp);

        Assert.Null(GamePosterLocator.Find("292030", [steam]));
        Assert.Null(GamePosterLocator.Find("292030", []));
        Assert.Null(GamePosterLocator.Find("292030", [Path.Combine(temp.Root, "NotEvenThere")]));
        Assert.Null(GamePosterLocator.Find("", [steam]));
        Assert.Null(GamePosterLocator.Find(null!, [steam]));
    }

    [Fact]
    public void NonImageFilesAreNotMistakenForArtwork()
    {
        using var temp = new TemporaryDirectory();
        var steam = SteamRoot(temp);

        WriteImage(steam, "appcache/librarycache/228980", null, "assetcache.vdf");
        WriteImage(steam, "appcache/librarycache/228980", null, "library_capsule.exe");

        Assert.Null(GamePosterLocator.Find("228980", [steam]));
    }

    [Fact]
    public void SearchingSeveralRootsFindsTheOneThatHasIt()
    {
        using var temp = new TemporaryDirectory();
        var first = SteamRoot(temp, "one");
        var second = SteamRoot(temp, "two");

        WriteImage(second, "appcache/librarycache/292030", null, "library_capsule.jpg");

        var found = GamePosterLocator.Find("292030", [first, second]);

        Assert.NotNull(found);
        Assert.StartsWith(second, found, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AScanAttachesPostersToTheGamesItFinds()
    {
        using var temp = new TemporaryDirectory();
        var steam = SteamRoot(temp);

        var apps = Path.Combine(steam, "steamapps");
        var common = Path.Combine(apps, "common", "The Witcher 3");
        Directory.CreateDirectory(common);
        File.WriteAllText(Path.Combine(common, "game.exe"), "binary");

        File.WriteAllText(
            Path.Combine(apps, "appmanifest_292030.acf"),
            "\"AppState\"\n{\n    \"appid\"      \"292030\"\n    \"name\"       \"The Witcher 3\"\n" +
            "    \"installdir\" \"The Witcher 3\"\n}\n");

        WriteImage(steam, "appcache/librarycache/292030", "aa11", "library_capsule.jpg");

        var result = new SteamLibraryScanner().Scan(steam, TestContext.Current.CancellationToken);

        var game = Assert.Single(result.Games);
        Assert.NotNull(game.PosterPath);
        Assert.EndsWith("library_capsule.jpg", game.PosterPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AGameWithNoPosterIsStillListed()
    {
        using var temp = new TemporaryDirectory();
        var steam = SteamRoot(temp);

        var apps = Path.Combine(steam, "steamapps");
        var common = Path.Combine(apps, "common", "No Art Game");
        Directory.CreateDirectory(common);
        File.WriteAllText(Path.Combine(common, "game.exe"), "binary");

        File.WriteAllText(
            Path.Combine(apps, "appmanifest_1111111.acf"),
            "\"AppState\"\n{\n    \"appid\"      \"1111111\"\n    \"name\"       \"No Art Game\"\n" +
            "    \"installdir\" \"No Art Game\"\n}\n");

        var result = new SteamLibraryScanner().Scan(steam, TestContext.Current.CancellationToken);

        // Artwork is never a reason to hide a game.
        var game = Assert.Single(result.Games);
        Assert.Null(game.PosterPath);
        Assert.Empty(result.Problems);
    }

    private static string SteamRoot(TemporaryDirectory temp, string name = "steam") =>
        Directory.CreateDirectory(Path.Combine(temp.Root, name)).FullName;

    private static void WriteImage(string steamRoot, string relativeFolder, string? subFolder, string fileName)
    {
        var folder = Path.Combine(steamRoot, relativeFolder);
        if (subFolder is not null)
        {
            folder = Path.Combine(folder, subFolder);
        }

        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, fileName), [0xFF, 0xD8, 0xFF, 0xE0]);
    }
}
