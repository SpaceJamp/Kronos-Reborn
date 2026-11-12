using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KronosReborn.Data.Updates;
using Xunit;

namespace KronosReborn.Tests;

public class UpdateCheckerTests
{
    private const string InstallerUrl = "https://github.com/SpaceJamp/Kronos-Reborn/releases/download/v0.2.0/KronosReborn-0.2.0-installer.exe";
    private const string PageUrl = "https://github.com/SpaceJamp/Kronos-Reborn/releases/tag/v0.2.0";

    [Fact]
    public async Task ANewerTagIsReportedWithItsInstaller()
    {
        var hex = Sha256Hex("installer");
        using var http = Client(Release("v0.2.0", Asset("KronosReborn-0.2.0-installer.exe", "sha256:" + hex.ToUpperInvariant())));

        var result = await new UpdateChecker(http, "0.1.0").CheckAsync(CancellationToken.None);

        Assert.Null(result.Error);
        var update = Assert.IsType<AvailableUpdate>(result.Update);
        Assert.Equal("0.2.0", update.Version);
        Assert.True(update.CanInstall);
        Assert.Equal(hex, update.InstallerSha256);
        Assert.Equal(new Uri(PageUrl), update.PageUrl);
        Assert.Equal(new Uri(InstallerUrl), update.InstallerUrl);
    }

    [Theory]
    [InlineData("v0.1.0")]
    [InlineData("v0.0.9")]
    [InlineData("0.1.0")]
    public async Task TheSameOrAnOlderTagIsNoUpdate(string tag)
    {
        using var http = Client(Release(tag, Asset("KronosReborn-installer.exe", "sha256:" + Sha256Hex("x"))));

        var result = await new UpdateChecker(http, "0.1.0").CheckAsync(CancellationToken.None);

        Assert.Null(result.Update);
        Assert.Null(result.Error);
    }

    [Theory]
    [InlineData("no assets")]
    [InlineData("no digest")]
    [InlineData("not sha256")]
    public async Task AReleaseWithoutAUsableInstallerOffersOnlyThePage(string kind)
    {
        object[] assets = kind switch
        {
            "no assets" => [],
            "no digest" => [Asset("KronosReborn-0.2.0-installer.exe", null)],
            _ => [Asset("KronosReborn-0.2.0-installer.exe", "md5:abc")],
        };
        using var http = Client(Release("v0.2.0", assets));

        var result = await new UpdateChecker(http, "0.1.0").CheckAsync(CancellationToken.None);

        var update = Assert.IsType<AvailableUpdate>(result.Update);
        Assert.False(update.CanInstall);
        Assert.Equal(new Uri(PageUrl), update.PageUrl);
    }

    [Theory]
    [InlineData("403")]
    [InlineData("500")]
    [InlineData("throws")]
    [InlineData("not json")]
    public async Task CheckReportsFailureWithoutThrowingOnHttpError(string kind)
    {
        using var http = new HttpClient(new Fake(_ => kind switch
        {
            "403" => new HttpResponseMessage(HttpStatusCode.Forbidden),
            "500" => new HttpResponseMessage(HttpStatusCode.InternalServerError),
            "throws" => throw new HttpRequestException("no network"),
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>") },
        }));

        var result = await new UpdateChecker(http, "0.1.0").CheckAsync(CancellationToken.None);

        Assert.Null(result.Update);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public async Task ADownloadMatchingItsDigestIsKept()
    {
        using var temp = new TemporaryDirectory();
        using var http = Client(null, "installer");
        var update = Update(Sha256Hex("installer"));

        var path = await new UpdateChecker(http, "0.1.0").DownloadAsync(update, temp.Root, null, CancellationToken.None);

        Assert.Equal("installer", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(temp.Root, "*.partial"));
    }

    [Fact]
    public async Task ADownloadWhoseDigestDiffersIsDeletedAndRefused()
    {
        using var temp = new TemporaryDirectory();
        using var http = Client(null, "tampered");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new UpdateChecker(http, "0.1.0").DownloadAsync(Update(Sha256Hex("installer")), temp.Root, null, CancellationToken.None));

        Assert.Empty(Directory.GetFiles(temp.Root));
    }

    [Fact]
    public void TheRunningVersionDropsBuildMetadata()
    {
        Assert.DoesNotContain('+', UpdateChecker.CurrentVersion());
    }

    private static AvailableUpdate Update(string sha) =>
        new("0.2.0", new Uri(PageUrl), new Uri(InstallerUrl), "KronosReborn-0.2.0-installer.exe", sha);

    private static string Sha256Hex(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static object Asset(string name, string? digest) => new
    {
        name,
        browser_download_url = $"https://github.com/SpaceJamp/Kronos-Reborn/releases/download/v0.2.0/{name}",
        digest,
    };

    private static string Release(string tag, params object[] assets) =>
        JsonSerializer.Serialize(new { tag_name = tag, html_url = PageUrl, assets });

    /// <summary>The release JSON at the API address and the installer bytes at its download address.</summary>
    private static HttpClient Client(string? releaseJson, string installer = "installer") =>
        new(new Fake(request => request.RequestUri!.ToString() switch
        {
            UpdateChecker.LatestReleaseUrl when releaseJson is not null =>
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(releaseJson) },
            InstallerUrl => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(installer) },
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        }));

    private sealed class Fake(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(answer(request));
    }
}
