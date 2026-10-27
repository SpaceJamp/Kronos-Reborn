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
        fixture.Responses[Cover] = new ByteArrayContent([1, 2, 3]);

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
        Assert.Single(fixture.Requested);

        // Where Steam is blocked rather than offline, every one of these used to sit out its timeout.
        Assert.Null(fixture.Find("folder:b", "Alan Wake 2"));
        Assert.Null(fixture.Find("folder:c", "Cyberpunk 2077"));
        Assert.Single(fixture.Requested);
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

        public Dictionary<string, HttpContent> Responses { get; } = [];
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

            if (fixture.Offline)
            {
                throw new HttpRequestException("offline");
            }

            return Task.FromResult(fixture.Responses.TryGetValue(url, out var content)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = content }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
