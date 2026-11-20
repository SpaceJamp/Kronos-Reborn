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

    [Fact]
    public void ADownloadCutShortByClosingIsClearedOnTheNextStart()
    {
        using var temp = new TemporaryDirectory();
        var paths = new DataPaths(Path.Combine(temp.Root, "data"));
        var leftover = Path.Combine(paths.Root, "downloads", "0123", "nvngx_dlss.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(leftover)!);
        File.WriteAllText(leftover, "half a file");

        using var catalog = new RuntimeCatalog(paths.CatalogPath);
        using var http = new HttpClient();
        _ = new VendorDownloader(http, catalog, paths);

        Assert.False(Directory.Exists(Path.Combine(paths.Root, "downloads")));
    }

    private const string DlssTags = "https://api.github.com/repos/NVIDIA/DLSS/tags?per_page=100";

    private static string DlssRaw(string tag, string file) =>
        $"https://raw.githubusercontent.com/NVIDIA/DLSS/{tag}/lib/Windows_x86_64/rel/{file}";

    [Fact]
    public async Task ANewerDlssThanAnyHeldIsReported()
    {
        using var fixture = new Fixture();
        fixture.Responses[DlssTags] = Json("""[{"name":"v310.2.1"},{"name":"v310.5.0"}]""");
        fixture.Responses[DlssRaw("v310.5.0", "nvngx_dlss.dll")] = Json("x");
        fixture.Responses[DlssRaw("v310.5.0", "nvngx_dlssg.dll")] = Json("x");
        Hold(fixture, RuntimeKind.Dlss, "310.2.1");

        var newer = await fixture.Downloader.NewerAvailableAsync([RuntimeKind.Dlss, RuntimeKind.DlssG], TestContext.Current.CancellationToken);

        // Frame Generation is released with the same number, and none of it is held.
        Assert.Equal([RuntimeKind.Dlss, RuntimeKind.DlssG], newer);
    }

    [Fact]
    public async Task ANewestReleaseWithoutTheDllIsNotReported()
    {
        // Get newest would fall back to an older release already held, so the notice would never go.
        using var fixture = new Fixture();
        fixture.Responses[DlssTags] = Json("""[{"name":"v310.5.0"}]""");
        Hold(fixture, RuntimeKind.DlssR, "310.2.1");

        Assert.Empty(await fixture.Downloader.NewerAvailableAsync([RuntimeKind.DlssR], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AHeldVersionWhoseFileIsGoneDoesNotHideANewerOne()
    {
        using var fixture = new Fixture();
        fixture.Responses[DlssTags] = Json("""[{"name":"v310.5.0"}]""");
        fixture.Responses[DlssRaw("v310.5.0", "nvngx_dlss.dll")] = Json("x");
        var held = Hold(fixture, RuntimeKind.Dlss, "310.5.0");
        File.Delete(held);

        Assert.Equal([RuntimeKind.Dlss], await fixture.Downloader.NewerAvailableAsync([RuntimeKind.Dlss], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task NothingIsReportedWhenTheNewestIsHeld()
    {
        using var fixture = new Fixture();
        fixture.Responses[DlssTags] = Json("""[{"name":"v310.5.0"},{"name":"v310.2.1"}]""");
        Hold(fixture, RuntimeKind.Dlss, "310.5.0");

        Assert.Empty(await fixture.Downloader.NewerAvailableAsync([RuntimeKind.Dlss], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RuntimesWhoseTagsAreNotTheirVersionAreNeverReported()
    {
        // AMD's tags name the SDK, and XeSS Frame Generation and XeLL have their own numbers, so a
        // newer one cannot be told from the tags alone; guessing would be a false alarm.
        using var fixture = new Fixture();

        Assert.Empty(await fixture.Downloader.NewerAvailableAsync(
            [RuntimeKind.Fsr, RuntimeKind.FsrUpscaler, RuntimeKind.XessFg, RuntimeKind.Xell], TestContext.Current.CancellationToken));
        Assert.Empty(fixture.Requested);
    }

    [Fact]
    public async Task NothingIsReportedWhenGitHubCannotBeReached()
    {
        using var fixture = new Fixture();

        var ex = await Record.ExceptionAsync(() =>
            fixture.Downloader.NewerAvailableAsync([RuntimeKind.Dlss, RuntimeKind.Xess], TestContext.Current.CancellationToken));

        Assert.Null(ex);
    }

    [Fact]
    public async Task NewestStreamlineFallsBackPastAReleaseWithoutAnSdkZip()
    {
        using var fixture = new Fixture();
        fixture.Responses["https://api.github.com/repos/NVIDIA-RTX/Streamline/tags?per_page=100"] =
            Json("""[{"name":"v2.14.16"},{"name":"v2.14.1"}]""");

        var outcome = await fixture.Downloader.DownloadNewestAsync(RuntimeKind.Streamline, cancellation: TestContext.Current.CancellationToken);

        // Neither has a zip here, so both are tried, newest first, and nothing is added.
        Assert.False(outcome.Succeeded);
        Assert.Contains(fixture.Requested, u => u.EndsWith("/v2.14.16/streamline-sdk-v2.14.16.zip", StringComparison.Ordinal));
        Assert.Contains(fixture.Requested, u => u.EndsWith("/v2.14.1/streamline-sdk-v2.14.1.zip", StringComparison.Ordinal));
        Assert.Empty(fixture.Catalog.Entries);
    }

    [Fact]
    public async Task AServerThatSendsTheWholeSdkIsAFailedDownloadNotACrash()
    {
        using var fixture = new Fixture();
        // The fake ignores partial requests and answers with the whole file.
        fixture.Responses["https://github.com/NVIDIA-RTX/Streamline/releases/download/v2.14.16/streamline-sdk-v2.14.16.zip"] = Json("whole");

        var outcome = await fixture.Downloader.DownloadAsync(RuntimeKind.Streamline, "v2.14.16", cancellation: TestContext.Current.CancellationToken);

        Assert.False(outcome.Succeeded);
        Assert.Contains("270 MB", outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANewerStreamlineIsReportedWhenItsSdkZipIsPublished()
    {
        using var fixture = new Fixture();
        fixture.Responses["https://api.github.com/repos/NVIDIA-RTX/Streamline/tags?per_page=100"] = Json("""[{"name":"v2.14.16"}]""");
        fixture.Responses["https://github.com/NVIDIA-RTX/Streamline/releases/download/v2.14.16/streamline-sdk-v2.14.16.zip"] = Json("zip");
        Hold(fixture, RuntimeKind.Streamline, "2.14.1");

        Assert.Equal([RuntimeKind.Streamline], await fixture.Downloader.NewerAvailableAsync([RuntimeKind.Streamline], TestContext.Current.CancellationToken));
    }

    private static string Hold(Fixture fixture, RuntimeKind kind, string version)
    {
        var folder = Path.Combine(fixture.Paths.Root, "held-" + version);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, RuntimeDlls.ByKind[kind][0]), "x");
        var entry = fixture.Catalog.AddImport(kind, version, folder, "x", RuntimeTrust.Trusted);
        Assert.NotNull(entry);
        return entry.FilePath;
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
