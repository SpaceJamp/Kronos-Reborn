using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using KronosReborn.Data.Libraries;
using KronosReborn.Data.Persistence;
using Xunit;

namespace KronosReborn.Tests;

public class StoreLibrariesTests
{
    [Fact]
    public void EpicListsInstalledGamesAndSkipsUnfinishedAndUnreadableOnes()
    {
        using var temp = new TemporaryDirectory();
        var manifests = Path.Combine(temp.Root, "Manifests");
        var game = Path.Combine(temp.Root, "Games", "Alan Wake 2");
        Directory.CreateDirectory(manifests);
        Directory.CreateDirectory(game);

        File.WriteAllText(Path.Combine(manifests, "a.item"),
            $$"""{"DisplayName":"Alan Wake 2","AppName":"Dodo","InstallLocation":{{System.Text.Json.JsonSerializer.Serialize(game)}},"bIsIncompleteInstall":false}""");
        File.WriteAllText(Path.Combine(manifests, "b.item"),
            """{"DisplayName":"Downloading","AppName":"Half","InstallLocation":"C:\\x","bIsIncompleteInstall":true}""");
        File.WriteAllText(Path.Combine(manifests, "c.item"), "{ not json");

        var result = EpicLibrary.Scan(manifests);

        var found = Assert.Single(result.Games);
        Assert.Equal("Alan Wake 2", found.Name);
        Assert.Equal("epic:Dodo", found.Id);
        Assert.True(found.Exists);
        Assert.Single(result.Problems);
    }

    [Fact]
    public void EpicWithNoLauncherFindsNothingAndReportsNothing()
    {
        using var temp = new TemporaryDirectory();

        var result = EpicLibrary.Scan(Path.Combine(temp.Root, "NotThere"));

        Assert.Empty(result.Games);
        Assert.Empty(result.Problems);
    }

    [Fact]
    public void UbisoftReadsItsForwardSlashFolders()
    {
        var result = UbisoftLibrary.Scan(new FakeRegistry
        {
            Ubisoft = [new UbisoftInstall("5595", "C:/Program Files (x86)/Ubisoft/Games/Avatar/")],
        });

        var game = Assert.Single(result.Games);
        Assert.Equal(@"C:\Program Files (x86)\Ubisoft\Games\Avatar", game.InstallPath);
        Assert.Equal("Avatar", game.Name);
        Assert.Equal("ubisoft:5595", game.Id);
    }

    [Fact]
    public void BattleNetAndEaAreFoundByPublisherWithoutTheirLaunchers()
    {
        var registry = new FakeRegistry
        {
            Uninstall =
            [
                new UninstallEntry("Diablo IV", "Blizzard Entertainment", @"C:\Games\Diablo IV"),
                new UninstallEntry("Battle.net", "Blizzard Entertainment", @"C:\Battle.net"),
                new UninstallEntry("Battlefield 2042", "Electronic Arts", "\"C:\\Games\\Battlefield 2042\\\""),
                new UninstallEntry("EA app", "Electronic Arts", @"C:\EA app"),
                new UninstallEntry("Not a game", "Someone Else", @"C:\Other"),
                new UninstallEntry("No folder", "Electronic Arts", null),
            ],
        };

        var blizzard = Assert.Single(PublisherLibrary.BattleNet(registry).Games);
        Assert.Equal("Diablo IV", blizzard.Name);
        Assert.Equal("Battle.net", blizzard.LibraryName);

        var ea = Assert.Single(PublisherLibrary.Ea(registry).Games);
        Assert.Equal(@"C:\Games\Battlefield 2042", ea.InstallPath);
    }

    [Fact]
    public void XboxGamesAreReadFromTheDrivesGamingRootFile()
    {
        using var temp = new TemporaryDirectory();
        var drive = temp.Root;
        Directory.CreateDirectory(Path.Combine(drive, "XboxGames", "Forza Horizon 5", "Content"));
        Directory.CreateDirectory(Path.Combine(drive, "XboxGames", "Not installed"));

        // "RGBX", a count, then each folder as null-terminated UTF-16.
        var bytes = Encoding.ASCII.GetBytes("RGBX")
            .Concat(new byte[] { 1, 0, 0, 0 })
            .Concat(Encoding.Unicode.GetBytes("\\XboxGames\0"))
            .ToArray();
        File.WriteAllBytes(Path.Combine(drive, ".GamingRoot"), bytes);

        var game = Assert.Single(XboxLibrary.Scan([drive]).Games);

        Assert.Equal("Forza Horizon 5", game.Name);
        Assert.EndsWith(Path.Combine("Forza Horizon 5", "Content"), game.InstallPath);
    }

    [Fact]
    public void AGamingRootFileThatIsNotOneIsIgnored()
    {
        using var temp = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(temp.Root, ".GamingRoot"), "something else");

        Assert.Empty(XboxLibrary.Scan([temp.Root]).Games);
    }

    [Fact]
    public void HandAddedGamesAreKeptOnceAndCanBeRemoved()
    {
        using var temp = new TemporaryDirectory();
        var service = new PersistenceService(new DataPaths(Path.Combine(temp.Root, "data")));
        var folder = Path.Combine(temp.Root, "My Game");
        Directory.CreateDirectory(folder);

        Assert.True(service.SetManualGame(folder, add: true));
        Assert.False(service.SetManualGame(folder + "\\", add: true)); // the same folder twice is one game

        var game = Assert.Single(ManualLibrary.Scan(service.ManualGames).Games);
        Assert.Equal("My Game", game.Name);
        Assert.Equal(ManualLibrary.LibraryName, game.LibraryName);
        Assert.True(game.Exists);

        Assert.True(service.SetManualGame(folder, add: false));
        Assert.Empty(service.ManualGames);
    }

    private sealed class FakeRegistry : IStoreRegistry
    {
        public IReadOnlyList<UbisoftInstall> Ubisoft { get; init; } = [];
        public IReadOnlyList<UninstallEntry> Uninstall { get; init; } = [];

        public IReadOnlyList<UbisoftInstall> UbisoftInstalls() => Ubisoft;
        public IReadOnlyList<UninstallEntry> UninstallEntries() => Uninstall;
    }
}
