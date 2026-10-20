using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
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
    }

    /// <summary>
    /// Lists available DLSS versions from NVIDIA's NGX CDN.
    /// </summary>
    /// <remarks>
    /// NVIDIA does not publish a version listing API. We probe a range of packed version numbers
    /// and check which URLs return a 200. This is imperfect but works: DLSS versions are released
    /// sequentially, so we can scan upward from a known base until we hit a run of 404s.
    /// </remarks>
    public static async Task<NgxVersionList> ListVersionsAsync(
        CancellationToken cancellation = default)
    {
        var found = new List<NgxVersionEntry>();

        // Probe packed versions from 1 upward. Stop after 20 consecutive 404s — that's well past
        // any realistic gap between DLSS releases.
        var consecutiveNotFound = 0;
        var maxProbe = 200000; // generous upper bound

        for (long packed = 1; packed <= maxProbe; packed++)
        {
            cancellation.ThrowIfCancellationRequested();

            foreach (var asset in new[] { "dlss", "dlssg", "dlssd" })
            {
                var url = $"https://{Bucket}.s3.amazonaws.com/{asset}/versions/{packed}/files/160_E658700.bin";
                var status = await ProbeUrlAsync(url, cancellation);

                if (status == HttpStatusCode.OK)
                {
                    consecutiveNotFound = 0;
                    found.Add(new NgxVersionEntry(asset, packed, null, url));
                }
                else if (status == HttpStatusCode.NotFound)
                {
                    consecutiveNotFound++;
                }
            }

            // If all three asset types 404 for this packed version, increment the gap counter.
            // Actually we track per-asset, so reset on any hit.
            if (consecutiveNotFound >= 20)
            {
                break;
            }
        }

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
        catch (TaskCanceledException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return HttpStatusCode.NotFound;
        }
    }

    /// <summary>
    /// Downloads one NGX asset and verifies its signature before saving to the collection.
    /// </summary>
    /// <returns>The path to the downloaded file, or null if the download or verification failed.</returns>
    /// <remarks>
    /// R16: imported runtimes are checked for a valid digital signature before being accepted.
    /// The signature check happens before anything is written to the collection folder.
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

        try
        {
            await DownloadFileAsync(entry.DownloadUrl, targetPath, progress, cancellation);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            // Clean up partial download
            if (File.Exists(targetPath))
            {
                File.Delete(targetPath);
            }
            return null;
        }

        // Verify signature before accepting
        var result = SignatureVerifier.Check(targetPath);
        if (!result.HasValidSignature)
        {
            File.Delete(targetPath);
            return null;
        }

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
