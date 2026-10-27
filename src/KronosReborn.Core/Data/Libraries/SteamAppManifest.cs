using System;
using System.IO;
using System.Text.RegularExpressions;
using KronosReborn.Data.Valve;

namespace KronosReborn.Data.Libraries;

/// <summary>
/// The contents of one Steam application manifest.
/// </summary>
/// <remarks>
/// Steam writes one of these per installed game, named for the game's id. It is the only place
/// Steam records the mapping from an id to a folder on disk, which is why discovery reads it rather
/// than trying to interpret folder names.
/// </remarks>
/// <param name="AppId">Steam's identifier for the game, such as 1245620.</param>
/// <param name="Name">The name Steam displays. May be empty, and is not the folder name.</param>
/// <param name="InstallDirectory">
/// The folder name under steamapps\common. A single path segment, not a full path. Steam writes a
/// full path here in some versions and an absolute path in some configurations, so it is treated as
/// untrusted and checked before use.
/// </param>
public sealed partial record SteamAppManifest(string AppId, string Name, string InstallDirectory)
{
    /// <summary>
    /// The file name Steam gives this game's manifest.
    /// </summary>
    public static string FileNameFor(string appId) => $"appmanifest_{appId}.acf";

    /// <summary>
    /// Whether this manifest can be used to find the game.
    /// </summary>
    /// <remarks>
    /// The folder is the only required part. The name is a display convenience and falls back to the
    /// folder name, so a manifest without one is still worth having rather than being discarded.
    /// </remarks>
    public bool IsUsable => !string.IsNullOrWhiteSpace(InstallDirectory);

    /// <summary>
    /// Whether the recorded folder is a single name rather than a path.
    /// </summary>
    /// <remarks>
    /// Steam normally writes one segment. A value containing a separator or a drive letter is
    /// rejected, because combining it with the library path would then produce a location outside the
    /// library entirely, and the program's whole job is writing into game folders. Reading such a
    /// value as an instruction to look elsewhere is not something a file found on disk gets to do.
    /// </remarks>
    public bool HasSafeInstallDirectory =>
        Path.GetFileName(InstallDirectory) == InstallDirectory
        && !InstallDirectory.Contains(':')
        && InstallDirectory is not ("." or "..");

    /// <summary>
    /// Reads a manifest's text.
    /// </summary>
    /// <remarks>
    /// Returns a problem rather than throwing. A manifest belonging to a game that has been
    /// uninstalled, or one Steam is part way through rewriting, must not stop discovery of every other
    /// game, which R5 requires.
    /// </remarks>
    public static bool TryParse(string text, out SteamAppManifest? manifest, out string? error)
    {
        manifest = null;
        error = null;

        if (!ValveKeyValues.TryParse(text, out var root, out error))
        {
            return false;
        }

        // Every key of interest lives under the AppState block. Some files wrap the whole thing in an
        // extra layer, so fall back to the root rather than reporting such a file as empty.
        var state = root!.FindAll("AppState").FirstOrDefault(node => node.IsBlock) ?? root;

        var appId = (state.GetString("appid") ?? string.Empty).Trim();
        var name = (state.GetString("name") ?? string.Empty).Trim();
        var installDirectory = (state.GetString("installdir") ?? string.Empty).Trim();

        if (appId.Length == 0)
        {
            error = "The manifest has no application id.";
            return false;
        }

        manifest = new SteamAppManifest(appId, name, installDirectory);
        return true;
    }

    /// <summary>
    /// The pattern Steam uses for manifest file names.
    /// </summary>
    /// <remarks>
    /// Matched against the file name rather than parsed out of it, so a folder containing
    /// appmanifest_notanumber.acf is skipped by enumeration instead of reaching the id check.
    /// ExplicitCapture is not used because the capture is unnamed; with ExplicitCapture it would
    /// not be captured and Groups[1] would be empty.
    /// </remarks>
    [GeneratedRegex(@"^appmanifest_(?<id>\d+)\.acf$", RegexOptions.CultureInvariant)]
    private static partial Regex FileNamePattern();

    /// <summary>
    /// Extracts the application id from a manifest file name, if it is one.
    /// </summary>
    public static string? TryGetAppIdFromFileName(string fileName)
    {
        var match = FileNamePattern().Match(fileName);

        return match.Success ? match.Groups["id"].Value : null;
    }
}
