using System;
using System.Collections.Generic;
using System.IO;
using KronosReborn.Data.Valve;

namespace KronosReborn.Data.Libraries;

/// <summary>
/// Reads the file that lists where a Steam installation keeps its libraries.
/// </summary>
/// <remarks>
/// This file has two layouts in the wild and both are still written by current Steam clients, so
/// both are read. The older one keys the libraries directly, "0", "1", and so on, each a block with a
/// "path" inside it. The newer one nests them under a single "path" key, still numbered, but as
/// plain values. Steam changed this without a version marker, and a client reading only one of them
/// silently loses every library the other layout describes.
/// </remarks>
public static class SteamLibraryFolders
{
    /// <summary>The file that lists library locations, relative to the Steam installation.</summary>
    public const string RelativePath = "steamapps/libraryfolders.vdf";

    /// <summary>
    /// Pulls the library paths out of an already parsed file.
    /// </summary>
    /// <remarks>
    /// Order is preserved as written, because that order decides which library gets credited for a
    /// game that appears in more than one, which R3 requires be shown rather than guessed.
    ///
    /// Paths are returned as written, not made absolute or checked. Whether one exists is a question
    /// for the caller, which can report a missing library properly rather than this returning nothing
    /// and letting an absent folder look the same as an unconfigured one.
    /// </remarks>
    public static IReadOnlyList<string> Extract(ValveNode root)
    {
        ArgumentNullException.ThrowIfNull(root);

        var paths = new List<string>();

        // The newer layout: a single "path" block whose children are numbered string values. Checked
        // first because a library block from the older layout never has a child called "path".
        var container = root.FindAll("path").FirstOrDefault(node => node.IsBlock);

        if (container is not null)
        {
            foreach (var entry in container.Children)
            {
                if (!string.IsNullOrWhiteSpace(entry.Value))
                {
                    paths.Add(entry.Value.Trim());
                }
            }

            return paths;
        }

        // The older layout: numbered blocks, each carrying its own "path".
        foreach (var library in root.Children)
        {
            var path = library.IsBlock ? library.GetString("path") : null;

            if (!string.IsNullOrWhiteSpace(path))
            {
                paths.Add(path.Trim());
            }
        }

        return paths;
    }

    /// <summary>
    /// Reads the library paths for a Steam installation.
    /// </summary>
    /// <remarks>
    /// Reports failure rather than throwing. A Steam installation with an unreadable or malformed
    /// library file is an expected state, not an exceptional one, and R4 requires it reported while
    /// discovery continues. The Steam root itself is always treated as a library, because a working
    /// installation with no library file at all still keeps its games there.
    /// </remarks>
    /// <param name="steamRoot">The Steam installation directory.</param>
    /// <param name="paths">The libraries found, the Steam root first.</param>
    /// <param name="problem">What went wrong, or null if nothing did.</param>
    public static bool TryRead(string steamRoot, out IReadOnlyList<string> paths, out ScanProblem? problem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(steamRoot);

        paths = Array.Empty<string>();
        problem = null;

        var file = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        var found = new List<string> { steamRoot };

        if (!File.Exists(file))
        {
            // Not a problem worth reporting. Plenty of installations have exactly one library and no
            // such file, and the games are still there to be found.
            paths = found;
            return true;
        }

        string text;

        try
        {
            text = File.ReadAllText(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problem = ScanProblem.FromException(
                "Steam",
                file,
                "The list of library folders could not be read, so only the main Steam folder was searched.",
                ex,
                "This usually means the file is locked by another program, or you do not have permission to read it.");

            paths = found;
            return false;
        }

        if (!ValveKeyValues.TryParse(text, out var root, out var error))
        {
            problem = new ScanProblem(
                "Steam",
                file,
                "The list of library folders could not be understood, so only the main Steam folder was searched.",
                error,
                "The file is written by Steam and should not need editing. If it was changed by hand, restoring it from a backup will fix this.");

            paths = found;
            return false;
        }

        foreach (var path in Extract(root!))
        {
            if (!found.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                found.Add(path);
            }
        }

        paths = found;
        return true;
    }
}
