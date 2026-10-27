using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using KronosReborn.Data.Downloads;
using KronosReborn.Data.Persistence;
using KronosReborn.Data.Runtimes;
using Xunit;

namespace KronosReborn.Tests;

public class VendorDownloaderTests
{
    private const string Tags = "https://api.github.com/repos/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK/tags?per_page=100";

    [Fact]
    public async Task ReleasesAreListedNewestFirstAsNumbers()
    {
        using var fixture = new Fixture();
        fixture.Responses[Tags] = Json("""[{"name":"v1.1.4"},{"name":"v2.3.0"},{"name":"v1.10.0"},{"name":"v2.0.0"}]""");

        var releases = await fixture.Downloader.ListReleasesAsync(RuntimeKind.Fsr, TestContext.Current.CancellationToken);

        Assert.Equal(["v2.3.0", "v2.0.0", "v1.10.0", "v1.1.4"], releases);
    }

    [Fact]
    public async Task NewestSkipsReleasesWithoutTheFileAndRefusesAFileNotSignedByItsVendor()
    {
        using var fixture = new Fixture();
        fixture.Responses[Tags] = Json("""[{"name":"v2.3.0"},{"name":"v1.1.4"}]""");

        // SDK 2 has no amd_fidelityfx_dx12.dll; SDK 1 does. The test assembly stands in for the DLL,
        // and is not signed by AMD.
        fixture.Responses[Raw("v1.1.4", "PrebuiltSignedDLL/amd_fidelityfx_dx12.dll")] =
            new ByteArrayContent(File.ReadAllBytes(Assembly.GetExecutingAssembly().Location));

        var outcome = await fixture.Downloader.DownloadNewestAsync(
            RuntimeKind.Fsr, cancellation: TestContext.Current.CancellationToken);

        Assert.Contains(Raw("v2.3.0", "PrebuiltSignedDLL/amd_fidelityfx_dx12.dll"), fixture.Requested);
        Assert.False(outcome.Succeeded);
        Assert.Contains("not signed by its vendor", outcome.Message);
        Assert.Empty(fixture.Catalog.Entries);

        // Nothing is left behind from the download.
        var downloads = Path.Combine(fixture.Paths.Root, "downloads");
        Assert.True(!Directory.Exists(downloads) || Directory.GetFileSystemEntries(downloads).Length == 0);
    }

    [Fact]
    public async Task AVersionAlreadyHeldIsNotDownloadedAgain()
    {
        using var fixture = new Fixture();
        var folder = Path.Combine(fixture.Paths.Root, "held");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "nvngx_dlss.dll"), "x");
        Assert.NotNull(fixture.Catalog.AddImport(RuntimeKind.Dlss, "310.9.16", folder, "x", RuntimeTrust.Trusted));

        var outcome = await fixture.Downloader.DownloadAsync(
            RuntimeKind.Dlss, "v310.9.16", cancellation: TestContext.Current.CancellationToken);

        Assert.True(outcome.Succeeded);
        Assert.Contains("already in the collection", outcome.Message);
        Assert.Empty(fixture.Requested);
    }

    private static string Raw(string tag, string path) =>
        $"https://raw.githubusercontent.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK/{tag}/{path}";

    private static StringContent Json(string json) => new(json);

    private sealed class Fixture : IDisposable
    {
        private readonly TemporaryDirectory _temp = new();
        private readonly HttpClient _http;

        public Fixture()
        {
            Paths = new DataPaths(Path.Combine(_temp.Root, "data"));
            Catalog = new RuntimeCatalog(Paths.CatalogPath);
            _http = new HttpClient(new FakeGitHub(this));
            Downloader = new VendorDownloader(_http, Catalog, Paths);
        }

        public Dictionary<string, HttpContent> Responses { get; } = [];
        public List<string> Requested { get; } = [];
        public DataPaths Paths { get; }
        public RuntimeCatalog Catalog { get; }
        public VendorDownloader Downloader { get; }

        public void Dispose()
        {
            _http.Dispose();
            Catalog.Dispose();
            _temp.Dispose();
        }
    }

    /// <summary>Answers from the fixture's table, and 404 for anything not in it, as GitHub does.</summary>
    private sealed class FakeGitHub(Fixture fixture) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            fixture.Requested.Add(url);

            return Task.FromResult(fixture.Responses.TryGetValue(url, out var content)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = content }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
