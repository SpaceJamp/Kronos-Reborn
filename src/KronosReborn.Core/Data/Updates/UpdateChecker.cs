using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using KronosReborn.Data.Runtimes;

namespace KronosReborn.Data.Updates;

/// <summary>A release newer than the one running.</summary>
/// <param name="Version">Its build number, such as "5".</param>
/// <param name="PageUrl">The release's page on GitHub.</param>
/// <param name="InstallerUrl">Where its installer downloads from, or null when it has none.</param>
/// <param name="InstallerName">The installer's file name.</param>
/// <param name="InstallerSha256">The SHA-256 GitHub published for the installer, lower-case hex.</param>
/// <param name="SignatureUrl">Where the maintainer's signature for the installer downloads from.</param>
public sealed record AvailableUpdate(
    string Version, Uri PageUrl, Uri? InstallerUrl, string? InstallerName, string? InstallerSha256, Uri? SignatureUrl = null)
{
    /// <summary>Whether the installer can be downloaded and checked; otherwise only the page is offered.</summary>
    public bool CanInstall => InstallerUrl is not null && InstallerSha256 is not null && SignatureUrl is not null;
}

/// <summary>What a check found: an update, nothing newer (both null), or why it could not tell.</summary>
public sealed record UpdateCheckResult(AvailableUpdate? Update, string? Error);

/// <summary>
/// Asks GitHub for the latest release and downloads its installer.
/// </summary>
/// <remarks>
/// <para>
/// An installer is run only if all of these hold, so getting a bad one through needs more than
/// control of the GitHub release:
/// </para>
/// <list type="bullet">
/// <item>Every link points at this repository on https://github.com.</item>
/// <item>The file's SHA-256 matches the digest GitHub published for it.</item>
/// <item>A signature over "KronosReborn|version|sha256" verifies against <see cref="ReleasePublicKey"/>.
/// The private key lives only on the maintainer's PC (see package/sign-release.ps1), never on GitHub,
/// and signing the version stops an older signed installer being passed off as a newer one.</item>
/// </list>
/// </remarks>
public sealed partial class UpdateChecker(HttpClient http, string currentVersion, string publicKey = UpdateChecker.ReleasePublicKey)
{
    /// <summary>The latest published release; GitHub leaves out drafts and pre-releases.</summary>
    public const string LatestReleaseUrl = "https://api.github.com/repos/SpaceJamp/Kronos-Reborn/releases/latest";

    /// <summary>The only place a release page, installer or signature may come from.</summary>
    private const string RepositoryUrl = "https://github.com/SpaceJamp/Kronos-Reborn/";

    /// <summary>
    /// The public half of the release signing key, P-256, as base64 SubjectPublicKeyInfo.
    /// </summary>
    /// <remarks>
    /// Printed by package/sign-release.ps1 -NewKey. Changing it means every installed copy refuses
    /// updates signed with the new key, so the release that changes it must be installed by hand.
    /// </remarks>
    public const string ReleasePublicKey =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEzhlMlt17oRypXQ9ubhrFHfg/gOICoFUSdx9jZYyZ7UGTMQ+IhurZ3fMzAuJnQwYBeB0XU+KjicBIRHs5+diCdg==";

    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));
    private readonly string _publicKey = publicKey;

    /// <summary>The running build number without build metadata: "4+abc1234" gives "4".</summary>
    public static string CurrentVersion() => BuildInfo.InformationalVersion.Split('+')[0];

    /// <summary>The exact bytes a release signature covers. package/sign-release.ps1 builds the same text.</summary>
    public static byte[] SignedMessage(string version, string sha256Hex) =>
        Encoding.UTF8.GetBytes($"KronosReborn|{version}|{sha256Hex}");

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

            var version = Text(release, "tag_name").TrimStart('v', 'V');
            if (!VersionNumber.IsOlder(currentVersion, version))
            {
                return new(null, null);
            }

            // Opened in the browser, so a link anywhere else could launch anything.
            if (InRepository(Text(release, "html_url")) is not { } page)
            {
                return new(null, "the release page is not in this app's repository");
            }

            var assets = release.TryGetProperty("assets", out var a) && a.ValueKind == JsonValueKind.Array
                ? a.EnumerateArray().ToArray()
                : [];
            var installer = assets.FirstOrDefault(x =>
                Text(x, "name").EndsWith("-installer.exe", StringComparison.OrdinalIgnoreCase));

            if (installer.ValueKind != JsonValueKind.Object)
            {
                return new(new AvailableUpdate(version, page, null, null, null), null);
            }

            var name = Text(installer, "name");
            var digest = installer.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
            var sha = digest is not null && Sha256Digest().Match(digest) is { Success: true } m
                ? m.Groups[1].Value.ToLowerInvariant()
                : null;
            var signature = assets.FirstOrDefault(x => string.Equals(Text(x, "name"), name + ".sig", StringComparison.OrdinalIgnoreCase));

            return new(new AvailableUpdate(
                version,
                page,
                InRepository(Text(installer, "browser_download_url")),
                name,
                sha,
                signature.ValueKind == JsonValueKind.Object ? InRepository(Text(signature, "browser_download_url")) : null), null);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or KeyNotFoundException
                                       or InvalidOperationException or UriFormatException
                                   || (ex is TaskCanceledException && !token.IsCancellationRequested))
        {
            return new(null, ex.Message);
        }
    }

    /// <summary>
    /// Downloads the installer into <paramref name="folder"/>, then checks its digest and the
    /// maintainer's signature.
    /// </summary>
    /// <returns>The path of the checked installer.</returns>
    /// <exception cref="InvalidOperationException">The update has no installer that can be checked.</exception>
    /// <exception cref="InvalidDataException">A check failed; nothing is kept.</exception>
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
            // Fetched first: a release without a readable signature is refused before 35 MB is downloaded.
            var signature = await ReadSignatureAsync(update.SignatureUrl!, token).ConfigureAwait(false);

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

            if (!IsSignedByMaintainer(update.Version, actual, signature))
            {
                throw new InvalidDataException(
                    $"The installer for build {update.Version} is not signed with Kronos Reborn's release key.");
            }

            File.Move(partial, target, overwrite: true);
            return target;
        }
        finally
        {
            // Whatever went wrong, a half-written or unverified file is never left to be run.
            File.Delete(partial);
        }
    }

    private async Task<byte[]> ReadSignatureAsync(Uri url, CancellationToken token)
    {
        using var response = await _http.GetAsync(url, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var text = (await response.Content.ReadAsStringAsync(token).ConfigureAwait(false)).Trim();

        try
        {
            var bytes = Convert.FromBase64String(text);
            return bytes.Length > 0 ? bytes : throw new InvalidDataException("The release signature is empty.");
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("The release signature could not be read.", ex);
        }
    }

    private bool IsSignedByMaintainer(string version, string sha256Hex, byte[] signature)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(_publicKey), out _);
            return key.VerifyData(SignedMessage(version, sha256Hex), signature, HashAlgorithmName.SHA256);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // A missing or broken built-in key verifies nothing.
            return false;
        }
    }

    /// <summary>The link if it is https on github.com under this repository, otherwise null.</summary>
    private static Uri? InRepository(string link) =>
        Uri.TryCreate(link, UriKind.Absolute, out var uri)
        && uri.AbsoluteUri.StartsWith(RepositoryUrl, StringComparison.OrdinalIgnoreCase)
        && !uri.AbsolutePath.Contains("..", StringComparison.Ordinal)
            ? uri
            : null;

    /// <summary>A string property; a missing or null one is malformed JSON, not a crash.</summary>
    private static string Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new JsonException($"GitHub's answer has no {property}.");

    [GeneratedRegex("^sha256:([0-9a-fA-F]{64})$")]
    private static partial Regex Sha256Digest();
}
