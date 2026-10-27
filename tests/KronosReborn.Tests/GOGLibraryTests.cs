using System.Collections.Generic;
using System.IO;
using KronosReborn.Data.Libraries;
using Xunit;

namespace KronosReborn.Tests;

public class GOGLibraryTests
{
    [Fact]
    public void EveryRecordedGameIsListedAndOneWithNoPathIsReported()
    {
        using var temp = new TemporaryDirectory();
        var installed = Path.Combine(temp.Root, "Witcher");
        Directory.CreateDirectory(installed);

        var result = GOGLibraryScanner.Scan(new FakeRegistry(
        [
            new GOGInstall("1207664663", "The Witcher 3", installed),
            new GOGInstall("1", "Uninstalled", Path.Combine(temp.Root, "Gone")),
            new GOGInstall("2", "Broken", null),
        ]));

        Assert.Equal(2, result.Games.Count);
        Assert.True(result.Games[0].Exists);
        Assert.Equal("GOG", result.Games[0].LibraryName);
        Assert.False(result.Games[1].Exists);
        Assert.Contains("Broken", Assert.Single(result.Problems).OneLine);
    }

    private sealed class FakeRegistry(IReadOnlyList<GOGInstall> games) : IGOGRegistry
    {
        public IReadOnlyList<GOGInstall> InstalledGames() => games;
    }
}
