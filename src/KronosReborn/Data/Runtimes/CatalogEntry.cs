using System;

namespace KronosReborn.Data.Runtimes;

/// <summary>
/// Where a runtime version in the catalog came from.
/// </summary>
/// <remarks>
/// R17: the source is recorded so the user can tell where a version came from. C3: a dependency on
/// someone else's server is visible rather than hidden.
/// </remarks>
public enum RuntimeSource
{
    /// <summary>Discovered inside an installed game on this machine.</summary>
    Discovered,

    /// <summary>Imported by the user from a file they obtained elsewhere.</summary>
    Imported,

    /// <summary>Bundled with the program (not used in this version).</summary>
    Bundled,
}

/// <summary>
/// Whether the program considers this runtime version trustworthy for installation.
/// </summary>
/// <remarks>
/// R16: imported runtimes are checked for a valid digital signature before being accepted,
/// unless the user has explicitly allowed untrusted versions (R42). R33: a warning is shown before
/// swapping in an untrusted version.
/// </remarks>
public enum RuntimeTrust
{
    /// <summary>Has a valid digital signature from a known publisher (NVIDIA, AMD, Intel).</summary>
    Trusted,

    /// <summary>No valid signature, or signature from an unknown publisher.</summary>
    Untrusted,

    /// <summary>Explicitly accepted by the user despite being untrusted.</summary>
    UserAccepted,
}

/// <summary>
/// A runtime version available for installation, held in the program's catalog.
/// </summary>
/// <remarks>
/// R13: the program holds a collection of runtime versions it can install. R17: the source is
/// recorded. R18: removing from the catalog does not remove from games. R48/R49: versions found in
/// games (even from before the program existed) are added to the catalog.
/// </remarks>
/// <param name="Id">Stable identifier for this catalog entry.</param>
/// <param name="Kind">Which runtime this is.</param>
/// <param name="Version">The version string (from the DLL's version resource).</param>
/// <param name="ProductVersion">The product version, if different.</param>
/// <param name="Source">Where this version came from.</param>
/// <param name="SourceDetail">
/// Additional context about the source. For Discovered: the game id and library. For Imported: the
/// file path or URL. For Bundled: the bundle name.
/// </param>
/// <param name="FilePath">Absolute path to the DLL file this entry represents.</param>
/// <param name="Trust">Whether this version is trusted for installation.</param>
/// <param name="AddedAt">When this entry was added to the catalog.</param>
/// <param name="SignatureInfo">Details of the digital signature, if any.</param>
public sealed record CatalogEntry(
    string Id,
    RuntimeKind Kind,
    string Version,
    string? ProductVersion,
    RuntimeSource Source,
    string SourceDetail,
    string FilePath,
    RuntimeTrust Trust,
    DateTimeOffset AddedAt,
    string? SignatureInfo = null)
{
    /// <summary>A display name like "DLSS 3.7.10 (discovered in GameName)"</summary>
    public string DisplayName
    {
        get
        {
            var kindName = Kind switch
            {
                RuntimeKind.Dlss => "DLSS",
                RuntimeKind.DlssG => "DLSS Frame Gen",
                RuntimeKind.DlssR => "DLSS Ray Recon",
                RuntimeKind.Fsr => "FSR",
                RuntimeKind.Xess => "XeSS",
                _ => Kind.ToString()
            };

            var sourceText = Source switch
            {
                RuntimeSource.Discovered => $"discovered in {SourceDetail}",
                RuntimeSource.Imported => $"imported from {SourceDetail}",
                RuntimeSource.Bundled => $"bundled ({SourceDetail})",
                _ => SourceDetail
            };

            var trustMark = Trust == RuntimeTrust.Untrusted ? " ⚠ untrusted" : "";

            return $"{kindName} {Version} ({sourceText}){trustMark}";
        }
    }

    /// <summary>
    /// Whether this entry is signed by a runtime vendor.
    /// </summary>
    /// <remarks>
    /// <c>UserAccepted</c> is deliberately not included. Accepting an untrusted file says the user is
    /// willing for it to be used; it does not make the file genuine. R33 asks for a warning before a
    /// runtime from an untrusted source is installed, and it does not carve out files the user
    /// previously accepted, so a runtime that was accepted explicitly still warns every time it is
    /// about to be put into a game.
    /// </remarks>
    public bool IsInstallableWithoutWarning => Trust == RuntimeTrust.Trusted;

    /// <summary>Whether this entry represents the same runtime as another (same kind and version).</summary>
    public bool Matches(CatalogEntry other) =>
        Kind == other.Kind && string.Equals(Version, other.Version, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Generates stable IDs for catalog entries.
/// </summary>
internal static class CatalogId
{
    /// <summary>
    /// Creates an ID from kind, version and where it came from.
    /// </summary>
    /// <remarks>
    /// The game a version was found in is deliberately not part of the ID. It used to be, and the
    /// effect was that DLSS 310.9.1 found in three games became three catalog entries, so the picker
    /// offered the same version three times under identical labels with no way to tell them apart —
    /// and choosing any of them swapped a file onto itself. R13 asks for a collection of versions, and
    /// one version present on this machine is one entry however many games carry it.
    ///
    /// The source kind is part of it because an imported copy is a different thing from one found in
    /// a game: it may be signed differently and R16 treats the two differently.
    /// </remarks>
    public static string For(RuntimeKind kind, string version, RuntimeSource source)
    {
        var basis = $"{kind}|{version}|{source}";
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(basis));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }
}
