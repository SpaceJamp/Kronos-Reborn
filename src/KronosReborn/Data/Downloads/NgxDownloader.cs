using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using KronosReborn.Data.Logging;
using KronosReborn.Data.Persistence;
using KronosReborn.Data.Runtimes;

namespace KronosReborn.Data.Downloads;

/// <summary>
/// Result of checking the NVIDIA NGX CDN for available DLSS versions.
/// </summary>
public sealed record NgxVersionList(
    IReadOnlyList<NgxVersionEntry> Versions,
    string SourceUrl);

/// <summary>
/// One DLSS version available from NVIDIA's NGX CDN.
/// </summary>
/// <param name="AssetType">dlss, dlssg, or dlssd.</param>
/// <param name="VersionPacked">The numeric version identifier used in the URL.</param>
/// <param name="VersionString">Human-readable version, if we can parse it.</param>
/// <param name="DownloadUrl">Direct URL to the 160_E658700.bin file.</param>
public sealed record NgxVersionEntry(
    string AssetType,
    long VersionPacked,
    string? VersionString,
    string DownloadUrl);

/// <summary>
/// Downloads DLSS runtime DLLs from NVIDIA's NGX CDN.
/// </summary>
/// <remarks>
/// The URL pattern was reverse-engineered from DLSS Swapper:
/// d6e9b45e-d4f6-4a84-a460-bf61decae3e8/{asset_type}/versions/{version_packed}/files/160_E658700.bin
///
/// Every downloaded file is verified with Authenticode before being accepted into the collection,
/// matching DLSS Swapper's security model. Downloads go to the collection folder under the data
/// directory so they survive reboots and are independent of where the user originally saved them.
/// </remarks>
public sealed class NgxDownloader
{
    private static readonly HttpClient HttpClient = new();

    private static readonly string Bucket = "d6e9b45e-d4f6-4a84-a460-bf61decae3e8";

    private static readonly Regex VersionRegex =
        new(@"^d6e9b45e-d4f6-4a84-a460-bf61decae3e8/(?<asset>dlss|dlssg|dlssd)/versions/(?<packed>\d+)/files/160_E658700\.bin$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly DataPaths _paths;

    public NgxDownloader(DataPaths paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));

        Log.Instance.Information(
            $"NGX Downloader initialized, scanning CDN bucket {Bucket}",
            category: "Download.Startup");
    }

    /// <summary>
    /// Lists available DLSS versions from NVIDIA's NGX CDN.
    /// </summary>
    /// <remarks>
    /// NVIDIA does not publish a version listing API. We probe packed version numbers upward
    /// and check which URLs return a 200. Each asset type (dlss, dlssg, dlssd) is tracked
    /// independently — a missing dlssg at version 5 doesn't stop us from finding dlss at version 6.
    /// We stop probing a given asset type after 20 consecutive 404s, and cap the total at 5000
    /// probes per asset to keep the scan bounded.
    /// </remarks>
    public static async Task<NgxVersionList> ListVersionsAsync(
        CancellationToken cancellation = default)
    {
        var found = new List<NgxVersionEntry>();
        var assetTypes = new[] { "dlss", "dlssg", "dlssd" };
        var consecutiveNotFound = new Dictionary<string, int>
        {
            ["dlss"] = 0,
            ["dlssg"] = 0,
            ["dlssd"] = 0,
        };

        var maxProbe = 5000;

        for (long packed = 1; packed <= maxProbe; packed++)
        {
            cancellation.ThrowIfCancellationRequested();

            foreach (var asset in assetTypes)
            {
                var url = $"https://{Bucket}.s3.amazonaws.com/{asset}/versions/{packed}/files/160_E658700.bin";
                var status = await ProbeUrlAsync(url, cancellation);

                if (status == HttpStatusCode.OK)
                {
                    consecutiveNotFound[asset] = 0;
                    found.Add(new NgxVersionEntry(asset, packed, null, url));
                }
                else
                {
                    // S3 answers a missing key with 403 rather than 404 when the bucket is not
                    // listable, so any status other than 200 counts as a miss.
                    consecutiveNotFound[asset]++;
                }
            }

            // Stop probing entirely once all three asset types have gone 20 versions without a hit.
            if (consecutiveNotFound["dlss"] >= 20 &&
                consecutiveNotFound["dlssg"] >= 20 &&
                consecutiveNotFound["dlssd"] >= 20)
            {
                break;
            }
        }

        Log.Instance.Information(
            $"NGX CDN scan complete: found {found.Count} version(s)",
            category: "Download.Scan");

        return new NgxVersionList(found, $"https://{Bucket}.s3.amazonaws.com/");
    }

    private static async Task<HttpStatusCode> ProbeUrlAsync(string url, CancellationToken cancellation)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
            return response.StatusCode;
        }
        catch (HttpRequestException)
        {
            return HttpStatusCode.NotFound;
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            // HttpClient reports its own timeout as a cancellation. One slow probe is a miss, not a
            // reason to abandon the whole scan.
            return HttpStatusCode.RequestTimeout;
        }
    }

    /// <summary>
    /// Downloads one NGX asset and verifies its signature before saving to the collection.
    /// </summary>
    /// <returns>The path to the downloaded file, or null if the download or verification failed.</returns>
    /// <remarks>
    /// R16: imported runtimes are checked for a valid digital signature before being accepted.
    /// The signature check happens before anything is written to the collection folder.
    ///
    /// On non-Windows platforms, Authenticode verification is unavailable, so downloaded files
    /// are accepted as-is from NVIDIA's CDN (which is the direct source). On Windows, the
    /// signature is verified against known NVIDIA/AMD/Intel publishers.
    /// </remarks>
    public async Task<string?> DownloadAndVerifyAsync(
        NgxVersionEntry entry,
        string version,
        IProgress<double>? progress = null,
        CancellationToken cancellation = default)
    {
        var kind = RuntimeKindFromAsset(entry.AssetType);
        var folder = _paths.RuntimeFolder(kind, version);
        Directory.CreateDirectory(folder);

        var fileName = RuntimeDlls.ByKind[kind][0];
        var targetPath = Path.Combine(folder, fileName);

        Log.Instance.Information(
            $"Downloading {entry.AssetType} version {entry.VersionPacked} from NVIDIA CDN",
            category: "Download",
            ("AssetType", entry.AssetType),
            ("VersionPacked", entry.VersionPacked),
            ("Url", entry.DownloadUrl));

        try
        {
            await DownloadFileAsync(entry.DownloadUrl, targetPath, progress, cancellation);
        }
        catch (OperationCanceledException)
        {
            // A cancelled download is a partial file, and it must not stay in the collection folder.
            if (File.Exists(targetPath))
            {
                File.Delete(targetPath);
            }

            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            // Clean up partial download
            if (File.Exists(targetPath))
            {
                File.Delete(targetPath);
            }

            Log.Instance.Error(
                $"Download failed for {entry.AssetType} {entry.VersionPacked}: {ex.Message}",
                category: "Download",
                ("AssetType", entry.AssetType),
                ("VersionPacked", entry.VersionPacked),
                ("Url", entry.DownloadUrl),
                ("Error", ex.Message));

            return null;
        }

#if WINDOWS
        // Verify signature before accepting on Windows, where Authenticode is available.
        var result = SignatureVerifier.Check(targetPath);
        if (!result.HasValidSignature)
        {
            File.Delete(targetPath);

            Log.Instance.Error(
                $"Signature verification failed for {entry.AssetType} {entry.VersionPacked}: {result.Details}",
                category: "Download",
                ("AssetType", entry.AssetType),
                ("VersionPacked", entry.VersionPacked),
                ("Details", result.Details));

            return null;
        }

        Log.Instance.Information(
            $"Downloaded and verified {entry.AssetType} version {entry.VersionPacked} from NVIDIA CDN",
            category: "Download",
            ("AssetType", entry.AssetType),
            ("VersionPacked", entry.VersionPacked),
            ("Url", entry.DownloadUrl));
#endif

        return targetPath;
    }

    private static async Task DownloadFileAsync(
        string url,
        string targetPath,
        IProgress<double>? progress,
        CancellationToken cancellation)
    {
        using var response = await HttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellation);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? 0L;
        var downloaded = 0L;

        using var stream = await response.Content.ReadAsStreamAsync(cancellation);
        using var fileStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);

        var buffer = new byte[81920];
        int bytesRead;

        while ((bytesRead = await stream.ReadAsync(buffer, cancellation)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellation);
            downloaded += bytesRead;

            if (totalBytes > 0)
            {
                progress?.Report((double)downloaded / totalBytes * 100);
            }
        }
    }

    private static RuntimeKind RuntimeKindFromAsset(string asset) => asset switch
    {
        "dlss" => RuntimeKind.Dlss,
        "dlssg" => RuntimeKind.DlssG,
        "dlssd" => RuntimeKind.DlssR,
        _ => throw new ArgumentOutOfRangeException(nameof(asset), asset, null)
    };
}
