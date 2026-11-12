using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using KronosReborn.Data.Runtimes;

namespace KronosReborn.Data.Updates;

/// <summary>A release newer than the one running.</summary>
/// <param name="Version">Its version, such as "0.2.0".</param>
/// <param name="PageUrl">The release's page on GitHub.</param>
/// <param name="InstallerUrl">Where its installer downloads from, or null when it has none.</param>
/// <param name="InstallerName">The installer's file name.</param>
/// <param name="InstallerSha256">The SHA-256 GitHub published for the installer, lower-case hex.</param>
public sealed record AvailableUpdate(
    string Version, Uri PageUrl, Uri? InstallerUrl, string? InstallerName, string? InstallerSha256)
{
    /// <summary>Whether the installer can be downloaded and checked; otherwise only the page is offered.</summary>
    public bool CanInstall => InstallerUrl is not null && InstallerSha256 is not null;
}

/// <summary>What a check found: an update, nothing newer (both null), or why it could not tell.</summary>
public sealed record UpdateCheckResult(AvailableUpdate? Update, string? Error);

/// <summary>
/// Asks GitHub for the latest release and downloads its installer.
/// </summary>
/// <remarks>
/// The builds are not code-signed, so the installer is checked against the SHA-256 digest GitHub
/// publishes for it. A file that does not match is deleted rather than run, and a release with no
/// digest offers only its page.
/// </remarks>
public sealed partial class UpdateChecker(HttpClient http, string currentVersion)
{
    /// <summary>The latest published release; GitHub leaves out drafts and pre-releases.</summary>
    public const string LatestReleaseUrl = "https://api.github.com/repos/SpaceJamp/Kronos-Reborn/releases/latest";

    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));

    /// <summary>The running version without build metadata: "0.1.0+abc1234" gives "0.1.0".</summary>
    public static string CurrentVersion() => BuildInfo.InformationalVersion.Split('+')[0];

    /// <summary>Asks GitHub whether a newer release is out. Never throws for network or GitHub failures.</summary>
    public async Task<UpdateCheckResult> CheckAsync(CancellationToken token)
    {
        try
        {
            using var response = await _http.GetAsync(LatestReleaseUrl, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new(null, $"GitHub answered {(int)response.StatusCode}");
            }

            await using var body = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var json = await JsonDocument.ParseAsync(body, cancellationToken: token).ConfigureAwait(false);
            var release = json.RootElement;

            var version = release.GetProperty("tag_name").GetString()!.TrimStart('v', 'V');
            if (!VersionNumber.IsOlder(currentVersion, version))
            {
                return new(null, null);
            }

            var page = new Uri(release.GetProperty("html_url").GetString()!);
            var installer = release.TryGetProperty("assets", out var assets)
                ? assets.EnumerateArray().FirstOrDefault(a =>
                    a.GetProperty("name").GetString()?.EndsWith("-installer.exe", StringComparison.OrdinalIgnoreCase) == true)
                : default;

            if (installer.ValueKind != JsonValueKind.Object)
            {
                return new(new AvailableUpdate(version, page, null, null, null), null);
            }

            var digest = installer.TryGetProperty("digest", out var d) ? d.GetString() : null;
            var sha = digest is not null && Sha256Digest().Match(digest) is { Success: true } m
                ? m.Groups[1].Value.ToLowerInvariant()
                : null;

            return new(new AvailableUpdate(
                version,
                page,
                new Uri(installer.GetProperty("browser_download_url").GetString()!),
                installer.GetProperty("name").GetString(),
                sha), null);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or KeyNotFoundException
                                       or InvalidOperationException or UriFormatException
                                   || (ex is TaskCanceledException && !token.IsCancellationRequested))
        {
            return new(null, ex.Message);
        }
    }

    /// <summary>Downloads the installer into <paramref name="folder"/> and checks it against its digest.</summary>
    /// <returns>The path of the checked installer.</returns>
    /// <exception cref="InvalidOperationException">The update has no installer that can be checked.</exception>
    /// <exception cref="InvalidDataException">The download does not match the digest; nothing is kept.</exception>
    public async Task<string> DownloadAsync(
        AvailableUpdate update, string folder, IProgress<double>? progress, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (!update.CanInstall)
        {
            throw new InvalidOperationException("This release has no installer that can be checked.");
        }

        Directory.CreateDirectory(folder);
        var name = Path.GetFileName(update.InstallerName ?? update.InstallerUrl!.AbsolutePath);
        var target = Path.Combine(folder, name);
        var partial = target + ".partial";

        try
        {
            using (var response = await _http.GetAsync(update.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                var length = response.Content.Headers.ContentLength;

                await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                await using var file = File.Create(partial);
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    done += read;
                    if (length > 0)
                    {
                        progress?.Report(100.0 * done / length.Value);
                    }
                }
            }

            string actual;
            await using (var check = File.OpenRead(partial))
            {
                actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(check, token).ConfigureAwait(false));
            }

            if (!string.Equals(actual, update.InstallerSha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"The download's SHA-256 is {actual}, but GitHub published {update.InstallerSha256}.");
            }

            File.Move(partial, target, overwrite: true);
            return target;
        }
        finally
        {
            // Whatever went wrong, a half-written or mismatched file is never left to be run.
            File.Delete(partial);
        }
    }

    [GeneratedRegex("^sha256:([0-9a-fA-F]{64})$")]
    private static partial Regex Sha256Digest();
}
