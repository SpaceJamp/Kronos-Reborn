using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KronosReborn.Data.Logging;
using KronosReborn.Data.Persistence;
using KronosReborn.Data.Runtimes;

namespace KronosReborn.Data.Downloads;

/// <summary>Where a vendor publishes a runtime's DLL.</summary>
/// <param name="Repository">The vendor's own GitHub repository, owner/name.</param>
/// <param name="Folder">The folder inside it that holds the signed DLL.</param>
public sealed record VendorSource(string Repository, string Folder);

/// <summary>What a download did.</summary>
/// <param name="Succeeded">Whether a version entered the collection, or was already there.</param>
/// <param name="Message">What happened, in terms the user can act on.</param>
/// <param name="Entry">The collection entry, when there is one.</param>
/// <param name="NotInRelease">True when the release simply does not include this file.</param>
public sealed record DownloadOutcome(bool Succeeded, string Message, CatalogEntry? Entry = null, bool NotInRelease = false);

/// <summary>
/// Downloads runtime DLLs from the vendors' own release repositories.
/// </summary>
/// <remarks>
/// <para>
/// NVIDIA, AMD and Intel each publish the signed DLLs in their SDK repositories on GitHub, tagged per
/// release, so that is where they are fetched from: no mirror and no third-party service. The NGX CDN
/// address this replaced does not exist (S3 answers NoSuchBucket), so that downloader never found a
/// version at all.
/// </para>
/// <para>
/// A download goes through <see cref="RuntimeImporter"/> like any other file, so it is held to the
/// same rule: its signature is checked and only a file signed by its vendor is accepted. A file from
/// the vendor's own repository that is not signed by the vendor is something wrong, not something to
/// ask about.
/// </para>
/// </remarks>
public sealed class VendorDownloader
{
    /// <summary>The DLL each runtime kind is published as, and where.</summary>
    internal static readonly IReadOnlyDictionary<RuntimeKind, VendorSource> Sources =
        new Dictionary<RuntimeKind, VendorSource>
        {
            [RuntimeKind.Dlss] = new("NVIDIA/DLSS", "lib/Windows_x86_64/rel"),
            [RuntimeKind.DlssG] = new("NVIDIA/DLSS", "lib/Windows_x86_64/rel"),
            [RuntimeKind.DlssR] = new("NVIDIA/DLSS", "lib/Windows_x86_64/rel"),
            [RuntimeKind.Fsr] = new("GPUOpen-LibrariesAndSDKs/FidelityFX-SDK", "PrebuiltSignedDLL"),
            [RuntimeKind.FsrVk] = new("GPUOpen-LibrariesAndSDKs/FidelityFX-SDK", "PrebuiltSignedDLL"),
            [RuntimeKind.FsrUpscaler] = new("GPUOpen-LibrariesAndSDKs/FidelityFX-SDK", "Kits/FidelityFX/signedbin"),
            [RuntimeKind.FsrFg] = new("GPUOpen-LibrariesAndSDKs/FidelityFX-SDK", "Kits/FidelityFX/signedbin"),
            [RuntimeKind.Xess] = new("intel/xess", "bin"),
            [RuntimeKind.XessFg] = new("intel/xess", "bin"),
            [RuntimeKind.XessDx11] = new("intel/xess", "bin"),
            [RuntimeKind.Xell] = new("intel/xess", "bin"),
        };

    /// <summary>How many releases "newest" looks back through for one that includes the file.</summary>
    /// <remarks>
    /// AMD's SDK 1 and SDK 2 releases hold different DLLs, so the newest release does not always
    /// include the one asked for; a few steps back reaches the newest that does.
    /// </remarks>
    internal const int NewestSearchDepth = 8;

    private readonly HttpClient _http;
    private readonly RuntimeCatalog _catalog;
    private readonly RuntimeImporter _importer;
    private readonly DataPaths _paths;
    private readonly Dictionary<string, IReadOnlyList<string>> _releases = new(StringComparer.OrdinalIgnoreCase);

    public VendorDownloader(HttpClient http, RuntimeCatalog catalog, DataPaths paths)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _importer = new RuntimeImporter(catalog, paths);

        // Only a download in progress uses this folder, and none is when the program starts, so what
        // is here was cut short by the program closing. Each can be a whole DLSS DLL.
        try
        {
            if (Directory.Exists(DownloadsFolder))
            {
                Directory.Delete(DownloadsFolder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Tried again on the next start.
        }
    }

    private string DownloadsFolder => Path.Combine(_paths.Root, "downloads");

    /// <summary>An HTTP client set up for GitHub, which refuses API requests without a user agent.</summary>
    public static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("KronosReborn");
        return http;
    }

    /// <summary>The releases of the repository a kind comes from, newest first.</summary>
    /// <remarks>Read once per repository per run; GitHub limits unauthenticated listing to 60 an hour.</remarks>
    public async Task<IReadOnlyList<string>> ListReleasesAsync(RuntimeKind kind, CancellationToken cancellation = default)
    {
        var repository = Sources[kind].Repository;

        lock (_releases)
        {
            if (_releases.TryGetValue(repository, out var cached))
            {
                return cached;
            }
        }

        using var response = await _http.GetAsync(
            new Uri($"https://api.github.com/repos/{repository}/tags?per_page=100"), cancellation).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false));

        var tags = json.RootElement.EnumerateArray()
            .Select(tag => tag.GetProperty("name").GetString())
            .OfType<string>()
            .ToList();

        // Newest by version number, since GitHub orders tags as text.
        tags.Sort((a, b) =>
            VersionNumber.IsOlder(VersionOf(a), VersionOf(b)) ? 1
            : VersionNumber.IsOlder(VersionOf(b), VersionOf(a)) ? -1
            : 0);

        lock (_releases)
        {
            _releases[repository] = tags;
        }

        return tags;
    }

    /// <summary>Downloads the newest release that includes this kind's DLL, unless it is already held.</summary>
    public async Task<DownloadOutcome> DownloadNewestAsync(
        RuntimeKind kind,
        IProgress<double>? progress = null,
        CancellationToken cancellation = default)
    {
        var releases = await ListReleasesAsync(kind, cancellation).ConfigureAwait(false);

        foreach (var release in releases.Take(NewestSearchDepth))
        {
            var outcome = await DownloadAsync(kind, release, progress, cancellation).ConfigureAwait(false);
            if (!outcome.NotInRelease)
            {
                return outcome;
            }
        }

        return new DownloadOutcome(false,
            $"None of the last {NewestSearchDepth} releases of {Sources[kind].Repository} include " +
            $"{RuntimeDlls.ByKind[kind][0]}.");
    }

    /// <summary>Downloads one release of a kind's DLL and adds it to the collection.</summary>
    public async Task<DownloadOutcome> DownloadAsync(
        RuntimeKind kind,
        string release,
        IProgress<double>? progress = null,
        CancellationToken cancellation = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(release);

        var name = RuntimeKindNames.Display(kind);

        // The tag names the version for NVIDIA's and Intel's DLLs, so a version already held is not
        // fetched again. AMD's tags name the SDK rather than the DLL, so those are always fetched and
        // the importer recognises a version already held.
        if (_catalog.Entries.FirstOrDefault(e =>
                e.Kind == kind && e.Trust == RuntimeTrust.Trusted && File.Exists(e.FilePath)
                && VersionNumber.AreEquivalent(e.Version, VersionOf(release))) is { } held)
        {
            return new DownloadOutcome(true, $"{name} {held.Version} is already in the collection.", held);
        }

        var source = Sources[kind];
        var fileName = RuntimeDlls.ByKind[kind][0];
        var url = new Uri($"https://raw.githubusercontent.com/{source.Repository}/{Uri.EscapeDataString(release)}/{source.Folder}/{fileName}");

        // Its own folder, so the importer sees only this file beside the one chosen.
        var folder = Path.Combine(DownloadsFolder, Guid.NewGuid().ToString("N"));
        var target = Path.Combine(folder, fileName);

        try
        {
            Directory.CreateDirectory(folder);

            using (var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false))
            {
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return new DownloadOutcome(false, $"Release {release} does not include {fileName}.", NotInRelease: true);
                }

                response.EnsureSuccessStatusCode();
                await CopyWithProgressAsync(response, target, progress, cancellation).ConfigureAwait(false);
            }

            var preview = RuntimeImporter.Inspect(target);
            if (preview.NeedsAcceptance)
            {
                Log.Instance.Error($"Downloaded {fileName} from {url} is not signed by its vendor", category: "Download");
                return new DownloadOutcome(false,
                    $"{fileName} from {source.Repository} {release} is not signed by its vendor, so it was not added.");
            }

            var result = _importer.Import(target, acceptUntrusted: false);
            if (!result.Succeeded)
            {
                return new DownloadOutcome(false, result.Error ?? $"{name} could not be added.");
            }

            Log.Instance.Information($"Downloaded {name} {result.Entry!.Version} from {url}", category: "Download");
            return new DownloadOutcome(true, $"Added {name} {result.Entry.Version} to the collection.", result.Entry);
        }
        catch (HttpRequestException ex)
        {
            return new DownloadOutcome(false, $"Could not download {name} {release}: {ex.Message}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new DownloadOutcome(false, $"Could not save {name} {release}: {ex.Message}");
        }
        finally
        {
            // The collection has its own copy by now; a cancelled or refused download leaves nothing.
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Left in the downloads folder; nothing reads from it.
            }
        }
    }

    /// <summary>A release tag as a version: "v310.9.16" is 310.9.16.</summary>
    internal static string VersionOf(string tag) =>
        tag.StartsWith('v') || tag.StartsWith('V') ? tag[1..] : tag;

    private static async Task CopyWithProgressAsync(
        HttpResponseMessage response, string target, IProgress<double>? progress, CancellationToken cancellation)
    {
        var total = response.Content.Headers.ContentLength ?? 0L;
        var done = 0L;
        var buffer = new byte[81920];

        await using var input = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
        await using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);

        int read;
        while ((read = await input.ReadAsync(buffer, cancellation).ConfigureAwait(false)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellation).ConfigureAwait(false);
            done += read;

            if (total > 0)
            {
                progress?.Report(done * 100.0 / total);
            }
        }
    }
}
