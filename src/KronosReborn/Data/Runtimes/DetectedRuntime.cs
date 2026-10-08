using System;

namespace KronosReborn.Data.Runtimes;

/// <summary>
/// A runtime DLL found inside a game's installation.
/// </summary>
/// <remarks>
/// This is the result of scanning a game folder. It records what was found, whether it could be
/// read, and the version information if readable. R10: a version that cannot be read is reported
/// as unknown, never guessed at.
/// </remarks>
/// <param name="Kind">Which runtime this is.</param>
/// <param name="FileName">The file name as it appears in the game folder.</param>
/// <param name="RelativePath">Path relative to the game root, e.g. "nvngx_dlss.dll" or "x64/nvngx_dlss.dll".</param>
/// <param name="FullPath">Absolute path to the file.</param>
/// <param name="Version">The file version from the DLL's version resource, or null if unreadable.</param>
/// <param name="ProductVersion">The product version, which sometimes differs from the file version.</param>
/// <param name="Exists">Whether the file was present when scanned.</param>
/// <param name="ReadError">Why it could not be read, if Exists is true but Version is null.</param>
public sealed record DetectedRuntime(
    RuntimeKind Kind,
    string FileName,
    string RelativePath,
    string FullPath,
    string? Version,
    string? ProductVersion,
    bool Exists,
    string? ReadError = null)
{
    /// <summary>Whether the version could be determined.</summary>
    public bool IsVersionKnown => Version is not null;

    /// <summary>A one-line description for display.</summary>
    public string Describe()
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

        if (!Exists)
        {
            return $"{kindName}: not present";
        }

        if (!IsVersionKnown)
        {
            return $"{kindName}: present (version unknown{(!string.IsNullOrWhiteSpace(ReadError) ? $", {ReadError}" : "")})";
        }

        return $"{kindName}: {Version}";
    }
}

/// <summary>
/// The complete set of runtimes found in one game.
/// </summary>
/// <param name="GameId">The game's identifier (from the library that found it).</param>
/// <param name="GameName">The game's display name.</param>
/// <param name="GamePath">The game's installation folder.</param>
/// <param name="Runtimes">Every runtime DLL found, whether present or not.</param>
/// <param name="LastChangedByUs">
/// When this game was last modified by this program, if known from history. Null means never
/// or unknown.
/// </param>
public sealed record GameRuntimeSnapshot(
    string GameId,
    string GameName,
    string GamePath,
    IReadOnlyList<DetectedRuntime> Runtimes,
    DateTimeOffset? LastChangedByUs = null)
{
    /// <summary>Whether any runtime in this game is missing its version info.</summary>
    public bool HasUnknownVersions => Runtimes.Any(r => r.Exists && !r.IsVersionKnown);

    /// <summary>Whether any runtime DLL is present at all.</summary>
    public bool HasAnyRuntime => Runtimes.Any(r => r.Exists);
}