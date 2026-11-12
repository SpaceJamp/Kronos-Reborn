using System;
using System.IO;
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

public sealed class UpdateCheckerTests : IDisposable
{
    private const string Base = "https://github.com/SpaceJamp/Kronos-Reborn/releases";
    private const string PageUrl = Base + "/tag/v0.2.0";
    private const string InstallerName = "KronosReborn-0.2.0-installer.exe";
    private const string InstallerUrl = Base + "/download/v0.2.0/" + InstallerName;
    private const string SignatureUrl = InstallerUrl + ".sig";

    /// <summary>Stands in for the release key, which lives only on the maintainer's PC.</summary>
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public void Dispose() => _key.Dispose();

    private string PublicKey => Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo());

    [Fact]
    public async Task ANewerTagIsReportedWithItsInstaller()
    {
        var hex = Sha256Hex("installer");
        using var http = Client(Release("v0.2.0", Asset(InstallerName, "sha256:" + hex.ToUpperInvariant()), Asset(InstallerName + ".sig", null)));

        var result = await Checker(http).CheckAsync(CancellationToken.None);

        Assert.Null(result.Error);
        var update = Assert.IsType<AvailableUpdate>(result.Update);
        Assert.Equal("0.2.0", update.Version);
        Assert.True(update.CanInstall);
        Assert.Equal(hex, update.InstallerSha256);
        Assert.Equal(new Uri(PageUrl), update.PageUrl);
        Assert.Equal(new Uri(InstallerUrl), update.InstallerUrl);
        Assert.Equal(new Uri(SignatureUrl), update.SignatureUrl);
    }

    [Theory]
    [InlineData("v0.1.0")]
    [InlineData("v0.0.9")]
    [InlineData("0.1.0")]
    public async Task TheSameOrAnOlderTagIsNoUpdate(string tag)
    {
        using var http = Client(Release(tag, Asset(InstallerName, "sha256:" + Sha256Hex("x"))));

        var result = await Checker(http).CheckAsync(CancellationToken.None);

        Assert.Null(result.Update);
        Assert.Null(result.Error);
    }

    [Theory]
    [InlineData("no assets")]
    [InlineData("no digest")]
    [InlineData("not sha256")]
    [InlineData("no signature")]
    [InlineData("installer elsewhere")]
    public async Task AReleaseWithoutAUsableInstallerOffersOnlyThePage(string kind)
    {
        var digest = "sha256:" + Sha256Hex("installer");
        var sig = Asset(InstallerName + ".sig", null);
        object[] assets = kind switch
        {
            "no assets" => [],
            "no digest" => [Asset(InstallerName, null), sig],
            "not sha256" => [Asset(InstallerName, "md5:abc"), sig],
            "no signature" => [Asset(InstallerName, digest)],
            _ => [Asset(InstallerName, digest, "https://evil.example/" + InstallerName), sig],
        };
        using var http = Client(Release("v0.2.0", assets));

        var result = await Checker(http).CheckAsync(CancellationToken.None);

        var update = Assert.IsType<AvailableUpdate>(result.Update);
        Assert.False(update.CanInstall);
        Assert.Equal(new Uri(PageUrl), update.PageUrl);
    }

    [Theory]
    [InlineData("https://evil.example/SpaceJamp/Kronos-Reborn/releases/tag/v0.2.0")]
    [InlineData("http://github.com/SpaceJamp/Kronos-Reborn/releases/tag/v0.2.0")]
    [InlineData("https://github.com/someone-else/Kronos-Reborn/releases/tag/v0.2.0")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    public async Task APageOutsideThisRepositoryIsNotOffered(string page)
    {
        using var http = Client(JsonSerializer.Serialize(new { tag_name = "v0.2.0", html_url = page, assets = Array.Empty<object>() }));

        var result = await Checker(http).CheckAsync(CancellationToken.None);

        // The page is opened in the browser, so a link anywhere else could launch anything.
        Assert.Null(result.Update);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Theory]
    [InlineData("403")]
    [InlineData("500")]
    [InlineData("throws")]
    [InlineData("not json")]
    [InlineData("null tag")]
    public async Task CheckReportsFailureWithoutThrowingOnHttpError(string kind)
    {
        using var http = new HttpClient(new Fake(_ => kind switch
        {
            "403" => new HttpResponseMessage(HttpStatusCode.Forbidden),
            "500" => new HttpResponseMessage(HttpStatusCode.InternalServerError),
            "throws" => throw new HttpRequestException("no network"),
            "null tag" => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"tag_name\":null,\"html_url\":null,\"assets\":[]}"),
            },
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>") },
        }));

        var result = await Checker(http).CheckAsync(CancellationToken.None);

        Assert.Null(result.Update);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public async Task AnInstallerSignedWithTheReleaseKeyIsKept()
    {
        using var temp = new TemporaryDirectory();
        using var http = Client(null, "installer", Sign("0.2.0", "installer"));

        var path = await Checker(http).DownloadAsync(Update("installer"), temp.Root, null, CancellationToken.None);

        Assert.Equal("installer", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(temp.Root, "*.partial"));
    }

    [Fact]
    public async Task ADownloadWhoseDigestDiffersIsDeletedAndRefused()
    {
        using var temp = new TemporaryDirectory();
        using var http = Client(null, "tampered", Sign("0.2.0", "installer"));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Checker(http).DownloadAsync(Update("installer"), temp.Root, null, CancellationToken.None));

        Assert.Empty(Directory.GetFiles(temp.Root));
    }

    [Fact]
    public async Task AReleaseWhoseFileAndDigestWereBothSwappedIsRefused()
    {
        using var temp = new TemporaryDirectory();

        // Someone with access to the release replaced the installer and its published digest together,
        // but cannot sign without the key on the maintainer's PC.
        using var forger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var forged = Convert.ToBase64String(forger.SignData(
            UpdateChecker.SignedMessage("0.2.0", Sha256Hex("malware")), HashAlgorithmName.SHA256));
        using var http = Client(null, "malware", forged);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Checker(http).DownloadAsync(Update("malware"), temp.Root, null, CancellationToken.None));

        Assert.Empty(Directory.GetFiles(temp.Root));
    }

    [Fact]
    public async Task AnOlderSignedInstallerPassedOffAsNewIsRefused()
    {
        using var temp = new TemporaryDirectory();

        // A genuine, signed 0.1.0 installer re-published as 0.2.0, to roll users back to a known bug.
        using var http = Client(null, "old installer", Sign("0.1.0", "old installer"));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Checker(http).DownloadAsync(Update("old installer"), temp.Root, null, CancellationToken.None));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not base64!")]
    public async Task AnUnreadableSignatureIsRefused(string signature)
    {
        using var temp = new TemporaryDirectory();
        using var http = Client(null, "installer", signature);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Checker(http).DownloadAsync(Update("installer"), temp.Root, null, CancellationToken.None));

        Assert.Empty(Directory.GetFiles(temp.Root));
    }

    [Fact]
    public void TheSignedTextIsFixed()
    {
        // package/sign-release.ps1 builds the same text; if this changes, that script must too.
        Assert.Equal("KronosReborn|0.2.0|abc", Encoding.UTF8.GetString(UpdateChecker.SignedMessage("0.2.0", "abc")));
    }

    [Fact]
    public void TheBuiltInReleaseKeyIsAValidP256PublicKey()
    {
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(UpdateChecker.ReleasePublicKey), out _);

        Assert.Equal(256, key.KeySize);
    }

    [Fact]
    public void TheRunningVersionDropsBuildMetadata()
    {
        Assert.DoesNotContain('+', UpdateChecker.CurrentVersion());
    }

    private UpdateChecker Checker(HttpClient http) => new(http, "0.1.0", PublicKey);

    private static AvailableUpdate Update(string content) =>
        new("0.2.0", new Uri(PageUrl), new Uri(InstallerUrl), InstallerName, Sha256Hex(content), new Uri(SignatureUrl));

    private string Sign(string version, string content) =>
        Convert.ToBase64String(_key.SignData(UpdateChecker.SignedMessage(version, Sha256Hex(content)), HashAlgorithmName.SHA256));

    private static string Sha256Hex(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static object Asset(string name, string? digest, string? url = null) => new
    {
        name,
        browser_download_url = url ?? $"{Base}/download/v0.2.0/{name}",
        digest,
    };

    private static string Release(string tag, params object[] assets) =>
        JsonSerializer.Serialize(new { tag_name = tag, html_url = PageUrl, assets });

    /// <summary>The release JSON, the installer bytes and its signature, each at its GitHub address.</summary>
    private static HttpClient Client(string? releaseJson, string installer = "installer", string signature = "") =>
        new(new Fake(request => request.RequestUri!.ToString() switch
        {
            UpdateChecker.LatestReleaseUrl when releaseJson is not null =>
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(releaseJson) },
            InstallerUrl => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(installer) },
            SignatureUrl => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(signature) },
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        }));

    private sealed class Fake(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(answer(request));
    }
}
