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
/// Tests for version normalisation.
/// </summary>
/// <remarks>
/// NVIDIA writes version resources with commas — "310,9,1,0" is what every DLSS file reports — while
/// AMD and Intel use dots. The comma form was carried through untouched, which had two consequences.
/// The interface showed "310,9,1" as though it were a version, and, far worse, version comparison
/// split on dots, so a comma-separated version arrived as one unparseable token and became zero. Every
/// DLSS version therefore compared equal to every other, and the downgrade warning R30 requires could
/// never fire for the most common runtime there is.
/// </remarks>
public class VersionNumberTests
{
    [Theory]
    [InlineData("310,9,1,0", "310.9.1")]
    [InlineData("310,9,1", "310.9.1")]
    [InlineData("1.0.2.38022", "1.0.2.38022")]
    [InlineData("3.8.0", "3.8.0")]
    [InlineData("2.0.2.53", "2.0.2.53")]
    public void CommaVersionsBecomeDotted(string input, string expected)
    {
        Assert.Equal(expected, VersionNumber.Normalize(input));
    }

    [Fact]
    public void AMissingVersionStaysMissing()
    {
        // R10: an unreadable version is unknown and is never guessed at. Returning an empty string
        // instead of null would make it look known, and a DLL with no version resource would appear
        // in the catalog as version "".
        Assert.Null(VersionNumber.Normalize(null));
        Assert.Null(VersionNumber.Normalize("   "));
        Assert.Null(VersionNumber.Normalize(string.Empty));
    }

    [Fact]
    public void OnlyATrailingZeroRevisionIsDropped()
    {
        // "310.9.1.0" reads as noise beside "310.9.1", but nothing else may be lost: an AMD build of
        // 1.0.2.38022 must keep all four components because the last one carries information.
        Assert.Equal("310.9.1", VersionNumber.Normalize("310,9,1,0"));
        Assert.Equal("1.0.2.38022", VersionNumber.Normalize("1.0.2.38022"));
        Assert.Equal("0.0.0", VersionNumber.Normalize("0.0.0.0"));
        Assert.Equal("1.0", VersionNumber.Normalize("1.0"));
    }

    [Fact]
    public void VersionsThatDifferOnlyByATrailingZeroRevisionAreEquivalent()
    {
        Assert.True(VersionNumber.AreEquivalent("310,9,1,0", "310.9.1"));
        Assert.True(VersionNumber.AreEquivalent("3.8.0", "3.8"));
        Assert.False(VersionNumber.AreEquivalent("310.9.1", "310.2.1"));
    }

    [Fact]
    public void EquivalentIsCaseInsensitive()
    {
        Assert.True(VersionNumber.AreEquivalent("3.8.0", "3.8.0"));
        Assert.True(VersionNumber.AreEquivalent("1.0A", "1.0a"));
    }

    [Fact]
    public void CommaVersionsCompareCorrectlyEvenWhenNotNormalised()
    {
        // The comparison splits on both separators, so a raw value that has not been normalised still
        // orders correctly. Without this a comma version becomes zero and compares equal to anything.
        Assert.False(VersionComparer.IsOlder("310,9,1,0", "310,2,1,0"));
        Assert.True(VersionComparer.IsOlder("310,2,1,0", "310,9,1,0"));
        Assert.False(VersionComparer.IsOlder("310,9,1,0", "310,9,1,0"));
    }

    [Fact]
    public void MixedFormsOfTheSameVersionCompareEqual()
    {
        Assert.False(VersionComparer.IsOlder("310.9.1", "310,9,1,0"));
        Assert.False(VersionComparer.IsOlder("310,9,1,0", "310.9.1"));
    }

    [Fact]
    public void ADowngradeIsRecognisedAcrossVendors()
    {
        // What the fix is for: with commas unhandled, none of these were ever true.
        Assert.True(VersionComparer.IsOlder("3.8.0", "310.9.1"));
        Assert.False(VersionComparer.IsOlder("310.9.1", "3.8.0"));
        Assert.True(VersionComparer.IsOlder("1.0.2.38022", "2.0.2.53"));
    }
}

/// <summary>
/// Tests for what the catalog holds after a scan of a real-shaped library.
/// </summary>
public class CatalogIdentityTests
{
    [Fact]
    public void OneVersionFoundInSeveralGamesIsOneCatalogEntry()
    {
        using var temp = new TemporaryDirectory();
        var catalog = new RuntimeCatalog(Path.Combine(temp.Root, "catalog.json"));

        // The same DLSS version in three games, as on a library where several titles were updated
        // together.
        foreach (var game in new[] { "The Witcher 3", "Portal with RTX", "Injustice 2" })
        {
            var folder = Path.Combine(temp.Root, game, "bin");
            Directory.CreateDirectory(folder);
            var dll = Path.Combine(folder, "nvngx_dlss.dll");
            File.WriteAllText(dll, "content");

            catalog.AddFromDiscovery(
                new DetectedRuntime(RuntimeKind.Dlss, "nvngx_dlss.dll", "bin/nvngx_dlss.dll",
                    dll, "310.9.1", null, true),
                game, game, "Steam");
        }

        // R13: a collection of versions. Three copies of one version is three entries the user cannot
        // tell apart, and choosing any of them swaps a file onto itself.
        var dlss = catalog.Entries.Where(e => e.Kind == RuntimeKind.Dlss).ToList();
        Assert.Single(dlss);
        Assert.Equal("310.9.1", dlss[0].Version);

        // Where it was found is still recorded, as R17 requires.
        Assert.Contains("discovered in", dlss[0].DisplayName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DifferentVersionsOfTheSameKindAreStillSeparateEntries()
    {
        using var temp = new TemporaryDirectory();
        var catalog = new RuntimeCatalog(Path.Combine(temp.Root, "catalog.json"));

        foreach (var version in new[] { "310.9.1", "310.2.1" })
        {
            var folder = Path.Combine(temp.Root, version);
            Directory.CreateDirectory(folder);
            var dll = Path.Combine(folder, "nvngx_dlss.dll");
            File.WriteAllText(dll, "content");

            catalog.AddFromDiscovery(
                new DetectedRuntime(RuntimeKind.Dlss, "nvngx_dlss.dll", "nvngx_dlss.dll",
                    dll, version, null, true),
                version, "Game", "Steam");
        }

        Assert.Equal(2, catalog.Entries.Count);
    }

    [Fact]
    public void AGameRunningAnUnknownVersionContributesNothingToTheCatalog()
    {
        using var temp = new TemporaryDirectory();
        var catalog = new RuntimeCatalog(Path.Combine(temp.Root, "catalog.json"));

        var dll = Path.Combine(temp.Root, "nvngx_dlss.dll");
        File.WriteAllText(dll, "content");

        var added = catalog.AddFromDiscovery(
            new DetectedRuntime(RuntimeKind.Dlss, "nvngx_dlss.dll", "nvngx_dlss.dll",
                dll, null, null, true),
            "game-1", "Game", "Steam");

        // R10: unknown is not guessed at, so it never becomes a version the user could pick.
        Assert.Null(added);
        Assert.Empty(catalog.Entries);
    }
}
