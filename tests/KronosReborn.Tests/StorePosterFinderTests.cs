using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using KronosReborn.Data.Libraries;
using Xunit;

namespace KronosReborn.Tests;

public class StorePosterFinderTests
{
    private const string Search = "https://store.steampowered.com/api/storesearch/?l=english&cc=US&term=Hellraiser%20Revival";
    private const string Cover = "https://shared.steamstatic.com/store_item_assets/steam/apps/1551980/library_600x900.jpg";

    [Theory]
    // The repack folder that started this: "CB's" and the dash made Steam's search find nothing.
    [InlineData("CB's Hellraiser - Revival", "Hellraiser Revival")]
    [InlineData("Alan Wake 2 [FitGirl Repack] v1.2.3", "Alan Wake 2")]
    [InlineData("Cyberpunk 2077 (GOG)", "Cyberpunk 2077")]
    public void NamesAreCleanedIntoWordsSteamCanFind(string name, string expected)
    {
        Assert.Equal(expected, string.Join(' ', StorePosterFinder.SearchWords(name)));
    }

    [Fact]
    public void AResultCountsOnlyWhenItHasEveryWord()
    {
        string[] words = ["Hellraiser", "Revival"];

        Assert.True(StorePosterFinder.Matches(words, "Clive Barker's Hellraiser: Revival"));
        Assert.False(StorePosterFinder.Matches(words, "Hellraiser Collection"));
        Assert.False(StorePosterFinder.Matches([], "Anything"));
    }

    [Theory]
    // What Steam's search really returns for these: one word must be the whole name.
    [InlineData("Control", "CONTROL Resonant", false)]
    [InlineData("Tools", "Tools Up!", false)]
    [InlineData("Doom", "DOOM 3", false)]
    [InlineData("Portal", "Portal", true)]
    [InlineData("Prey", "PREY", true)]
    public void AOneWordNameMustBeTheWholeName(string word, string candidate, bool expected)
    {
        Assert.Equal(expected, StorePosterFinder.Matches([word], candidate));
    }

    [Fact]
    public void AOneWordFolderGetsNoCoverRatherThanAnotherGames()
    {
        using var fixture = new Fixture();
        fixture.Responses["https://store.steampowered.com/api/storesearch/?l=english&cc=US&term=Control"] = new StringContent("""
            {"total":2,"items":[{"id":3,"name":"CONTROL Resonant"},{"id":4,"name":"Airport Control 27"}]}
            """);

        Assert.Null(fixture.Find("folder:control", "Control"));
        Assert.DoesNotContain(fixture.Requested, url => url.Contains("steamstatic", StringComparison.Ordinal));
    }

    [Fact]
    public void TheGameItselfIsChosenOverItsDemoAndKeptForNextTime()
    {
        using var fixture = new Fixture();
        fixture.Responses[Search] = new StringContent("""
            {"total":3,"items":[
              {"id":5184670,"name":"Clive Barker's Hellraiser: Revival Demo"},
              {"id":1551980,"name":"Clive Barker's Hellraiser: Revival"},
              {"id":4664770,"name":"Clive Barker's Hellraiser: Revival - Deluxe Content"}]}
            """);
        fixture.Responses[Cover] = Image(1, 2, 3);

        var found = fixture.Find("folder:hellraiser", "CB's Hellraiser - Revival");

        Assert.NotNull(found);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(found));

        // Kept: the next refresh asks Steam for nothing.
        fixture.Requested.Clear();
        Assert.Equal(found, fixture.Find("folder:hellraiser", "CB's Hellraiser - Revival"));
        Assert.Empty(fixture.Requested);
    }

    [Fact]
    public void ANameWithNoMatchIsRememberedSoSteamIsNotAskedAgain()
    {
        using var fixture = new Fixture();

        Assert.Null(fixture.Find("folder:x", "Some Folder"));
        Assert.NotEmpty(fixture.Requested);

        fixture.Requested.Clear();
        Assert.Null(fixture.Find("folder:x", "Some Folder"));
        Assert.Empty(fixture.Requested);
    }

    [Fact]
    public void OnceSteamCannotBeReachedTheRestOfTheRefreshDoesNotWaitOnIt()
    {
        using var fixture = new Fixture { Offline = true };

        Assert.Null(fixture.Find("folder:a", "Hellraiser Revival"));
        // Each store is tried once: Steam, then the Microsoft Store.
        Assert.Equal(2, fixture.Requested.Count);

        // Where a store is blocked rather than offline, every one of these used to sit out its timeout.
        Assert.Null(fixture.Find("folder:b", "Alan Wake 2"));
        Assert.Null(fixture.Find("folder:c", "Cyberpunk 2077"));
        Assert.Equal(2, fixture.Requested.Count);
    }

    [Fact]
    public void WhenSteamCannotBeReachedTheNextRefreshTriesAgain()
    {
        using var fixture = new Fixture { Offline = true };

        Assert.Null(fixture.Find("folder:x", "Hellraiser Revival"));

        fixture.Offline = false;
        fixture.Requested.Clear();
        fixture.NextRefresh();
        fixture.Find("folder:x", "Hellraiser Revival");
        Assert.NotEmpty(fixture.Requested);
    }

    private const string MsSearch = "https://displaycatalog.mp.microsoft.com/v7.0/productFamilies/autosuggest?market=US&languages=en-US&query=Hydro%20Thunder%20Hurricane&productFamilyNames=Games,Apps";
    private const string MsProduct = "https://displaycatalog.mp.microsoft.com/v7.0/products?bigIds=9WZDNCRFHWFD&market=US&languages=en-US";
    private const string MsPoster = "https://store-images.s-microsoft.com/image/apps.50511.poster";

    [Fact]
    public void AGameSteamDoesNotSellGetsItsMicrosoftStorePoster()
    {
        using var fixture = new Fixture();
        fixture.Responses[MsSearch] = new StringContent("""
            {"Results":[{"Products":[{"Title":"Hydro Thunder Hurricane","ProductId":"9WZDNCRFHWFD","Type":"Game"}]}]}
            """);
        fixture.Responses[MsProduct] = new StringContent("""
            {"Products":[{"LocalizedProperties":[{"Images":[
              {"ImagePurpose":"Screenshot","Uri":"//store-images.microsoft.com/image/apps.1.shot"},
              {"ImagePurpose":"BoxArt","Uri":"//store-images.s-microsoft.com/image/apps.11444.box"},
              {"ImagePurpose":"Poster","Uri":"//store-images.s-microsoft.com/image/apps.50511.poster"}]}]}]}
            """);
        fixture.Responses[MsPoster] = Image(7, 8, 9);

        var found = fixture.Find("folder:hydro", "Hydro Thunder Hurricane");

        // The 2:3 poster, not the box art or a screenshot.
        Assert.NotNull(found);
        Assert.Equal([7, 8, 9], File.ReadAllBytes(found));
    }

    [Fact]
    public void TheMicrosoftStoreUsesTheSameStrictNameMatch()
    {
        using var fixture = new Fixture();
        fixture.Responses["https://displaycatalog.mp.microsoft.com/v7.0/productFamilies/autosuggest?market=US&languages=en-US&query=Minecraft%20Launcher&productFamilyNames=Games,Apps"] =
            new StringContent("""{"Results":[{"Products":[{"Title":"Minecraft Dungeons","ProductId":"X1"}]}]}""");

        Assert.Null(fixture.Find("xbox:launcher", "Minecraft Launcher"));
        Assert.DoesNotContain(fixture.Requested, url => url.Contains("bigIds", StringComparison.Ordinal));
    }

    [Fact]
    public void AnImageFromAnywhereButMicrosoftsImageServerIsNotDownloaded()
    {
        using var fixture = new Fixture();
        fixture.Responses[MsSearch] = new StringContent("""{"Results":[{"Products":[{"Title":"Hydro Thunder Hurricane","ProductId":"9WZDNCRFHWFD"}]}]}""");
        fixture.Responses[MsProduct] = new StringContent("""
            {"Products":[{"LocalizedProperties":[{"Images":[{"ImagePurpose":"Poster","Uri":"//evil.example/poster.jpg"}]}]}]}
            """);

        Assert.Null(fixture.Find("folder:hydro", "Hydro Thunder Hurricane"));
        Assert.DoesNotContain(fixture.Requested, url => url.Contains("evil.example", StringComparison.Ordinal));
    }

    [Fact]
    public void AMissIsAskedAgainAfterAWeek()
    {
        using var fixture = new Fixture();
        Assert.Null(fixture.Find("folder:x", "Some Folder"));

        // A game can reach a store after it is installed, as STAR WARS: Galactic Racer did.
        foreach (var miss in Directory.GetFiles(fixture.Posters, "*.miss"))
        {
            File.SetLastWriteTimeUtc(miss, DateTime.UtcNow.AddDays(-8));
        }

        fixture.Requested.Clear();
        fixture.NextRefresh();
        fixture.Find("folder:x", "Some Folder");
        Assert.NotEmpty(fixture.Requested);
    }

    [Fact]
    public void AMissRecordedByAnOlderVersionIsAskedAgain()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Posters);
        // Written before the Microsoft Store was searched, and never expiring.
        File.WriteAllText(Path.Combine(fixture.Posters, KronosReborn.Data.Persistence.DataPaths.SafeName("folder:x") + ".none"), "Some Folder");

        fixture.Find("folder:x", "Some Folder");

        Assert.NotEmpty(fixture.Requested);
        Assert.Empty(Directory.GetFiles(fixture.Posters, "*.none"));
    }

    [Fact]
    public void AChosenCoverIsUsedAndReplacesAnEarlierChoice()
    {
        using var fixture = new Fixture();
        var first = Path.Combine(fixture.Root, "first.png");
        var second = Path.Combine(fixture.Root, "second.jpg");
        File.WriteAllBytes(first, [1]);
        File.WriteAllBytes(second, [2]);

        Assert.Null(StorePosterFinder.Chosen(fixture.Posters, "xbox:launcher"));

        var one = StorePosterFinder.Choose(fixture.Posters, "xbox:launcher", first);
        var two = StorePosterFinder.Choose(fixture.Posters, "xbox:launcher", second);

        // A new name each time, so the window's decoded-image cache never shows the old one.
        Assert.NotEqual(one, two);
        Assert.Equal(two, StorePosterFinder.Chosen(fixture.Posters, "xbox:launcher"));
        Assert.Equal([2], File.ReadAllBytes(two));
        Assert.False(File.Exists(one));
    }

    [Theory]
    [InlineData("cover.txt")]
    [InlineData("cover.exe")]
    public void OnlyAnImageCanBeChosenAsACover(string name)
    {
        using var fixture = new Fixture();
        var path = Path.Combine(fixture.Root, name);
        File.WriteAllText(path, "x");

        Assert.Throws<ArgumentException>(() => StorePosterFinder.Choose(fixture.Posters, "folder:x", path));
    }

    [Fact]
    public void ABlockedMicrosoftStoreDoesNotStopSteamCovers()
    {
        using var fixture = new Fixture();
        fixture.OfflineHosts.Add("displaycatalog.mp.microsoft.com");
        fixture.Responses[Search] = new StringContent("""{"items":[{"id":1551980,"name":"Clive Barker's Hellraiser: Revival"}]}""");
        fixture.Responses[Cover] = Image(1);

        // Not on Steam, so it falls through to Microsoft, which is blocked.
        Assert.Null(fixture.Find("folder:other", "Some Folder"));

        // Then a game Steam does sell still gets its cover in the same refresh.
        Assert.NotNull(fixture.Find("folder:hellraiser", "CB's Hellraiser - Revival"));
    }

    [Fact]
    public void ABlockedSteamStillLetsTheMicrosoftStoreFindCovers()
    {
        using var fixture = new Fixture();
        fixture.OfflineHosts.Add("store.steampowered.com");
        fixture.Responses[MsSearch] = new StringContent("""{"Results":[{"Products":[{"Title":"Hydro Thunder Hurricane","ProductId":"9WZDNCRFHWFD"}]}]}""");
        fixture.Responses[MsProduct] = new StringContent("""{"Products":[{"LocalizedProperties":[{"Images":[{"ImagePurpose":"Poster","Uri":"//store-images.s-microsoft.com/image/apps.50511.poster"}]}]}]}""");
        fixture.Responses[MsPoster] = Image(7);

        Assert.NotNull(fixture.Find("folder:hydro", "Hydro Thunder Hurricane"));
    }

    [Fact]
    public void NoMissIsRememberedWhenAStoreCouldNotBeReached()
    {
        using var fixture = new Fixture();
        fixture.OfflineHosts.Add("displaycatalog.mp.microsoft.com");

        Assert.Null(fixture.Find("folder:x", "Some Folder"));

        // Microsoft was down, so the game was never really looked for: the next refresh asks again.
        Assert.Empty(Directory.Exists(fixture.Posters) ? Directory.GetFiles(fixture.Posters, "*.miss") : []);
    }

    [Fact]
    public void ALockedOldMarkerDoesNotFailTheScan()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Posters);
        var none = Path.Combine(fixture.Posters, KronosReborn.Data.Persistence.DataPaths.SafeName("folder:x") + ".none");
        File.WriteAllText(none, "Some Folder");
        using var locked = new FileStream(none, FileMode.Open, FileAccess.Read, FileShare.None);

        // Anything thrown here would fail the whole library refresh.
        var ex = Record.Exception(() => fixture.Find("folder:x", "Some Folder"));

        Assert.Null(ex);
    }

    [Theory]
    [InlineData("""{"items":[{"id":1,"name":null}]}""")]
    [InlineData("""{"items":[42, "x"]}""")]
    [InlineData("""[1,2,3]""")]
    public void OddAnswersFromSteamDoNotFailTheScan(string json)
    {
        using var fixture = new Fixture();
        fixture.Responses["https://store.steampowered.com/api/storesearch/?l=english&cc=US&term=Some%20Folder"] = new StringContent(json);

        var ex = Record.Exception(() => fixture.Find("folder:x", "Some Folder"));

        Assert.Null(ex);
    }

    [Fact]
    public void AWebPageInsteadOfAnImageIsNotKeptAsACover()
    {
        using var fixture = new Fixture();
        fixture.Responses[Search] = new StringContent("""{"items":[{"id":1551980,"name":"Clive Barker's Hellraiser: Revival"}]}""");
        // Such as a hotel or airport sign-in page answering in place of Steam.
        fixture.Responses[Cover] = new StringContent("<html>sign in</html>", System.Text.Encoding.UTF8, "text/html");

        Assert.Null(fixture.Find("folder:hellraiser", "CB's Hellraiser - Revival"));
        Assert.Empty(Directory.GetFiles(fixture.Posters, "*.jpg"));
    }

    [Fact]
    public void AHugeDownloadIsNotKeptAsACover()
    {
        using var fixture = new Fixture();
        fixture.Responses[Search] = new StringContent("""{"items":[{"id":1551980,"name":"Clive Barker's Hellraiser: Revival"}]}""");
        fixture.Responses[Cover] = Image(new byte[21 * 1024 * 1024]);

        Assert.Null(fixture.Find("folder:hellraiser", "CB's Hellraiser - Revival"));
    }

    [Fact]
    public void ANewChosenCoverIsKeptEvenIfTheOldOneCannotBeDeleted()
    {
        using var fixture = new Fixture();
        var first = Path.Combine(fixture.Root, "first.png");
        var second = Path.Combine(fixture.Root, "second.png");
        File.WriteAllBytes(first, [1]);
        File.WriteAllBytes(second, [2]);
        var old = StorePosterFinder.Choose(fixture.Posters, "folder:x", first);

        string kept;
        using (new FileStream(old, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            kept = StorePosterFinder.Choose(fixture.Posters, "folder:x", second);
        }

        Assert.Equal(kept, StorePosterFinder.Chosen(fixture.Posters, "folder:x"));
    }

    [Fact]
    public void ForgettingAChosenCoverGoesBackToTheAutomaticOne()
    {
        using var fixture = new Fixture();
        var image = Path.Combine(fixture.Root, "mine.png");
        File.WriteAllBytes(image, [1]);
        StorePosterFinder.Choose(fixture.Posters, "folder:x", image);

        StorePosterFinder.Unchoose(fixture.Posters, "folder:x");

        Assert.Null(StorePosterFinder.Chosen(fixture.Posters, "folder:x"));
    }

    private static ByteArrayContent Image(params byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
        return content;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TemporaryDirectory _temp = new();
        private readonly HttpClient _http;
        private StorePosterFinder _finder;

        public Fixture()
        {
            _http = new HttpClient(new FakeSteam(this));
            _finder = new StorePosterFinder(_http, Path.Combine(_temp.Root, "posters"));
        }

        public string Root => _temp.Root;
        public string Posters => Path.Combine(_temp.Root, "posters");
        public Dictionary<string, HttpContent> Responses { get; } = [];
        public HashSet<string> OfflineHosts { get; } = [];
        public List<string> Requested { get; } = [];
        public bool Offline { get; set; }

        /// <summary>Each refresh makes its own finder, as the view model does.</summary>
        public void NextRefresh() => _finder = new StorePosterFinder(_http, Path.Combine(_temp.Root, "posters"));

        public string? Find(string id, string name) =>
            _finder.Find(id, name, steamAppId: null, TestContext.Current.CancellationToken);

        public void Dispose()
        {
            _http.Dispose();
            _temp.Dispose();
        }
    }

    private sealed class FakeSteam(Fixture fixture) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            fixture.Requested.Add(url);

            if (fixture.Offline || fixture.OfflineHosts.Contains(request.RequestUri.Host))
            {
                throw new HttpRequestException("offline");
            }

            return Task.FromResult(fixture.Responses.TryGetValue(url, out var content)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = content }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
