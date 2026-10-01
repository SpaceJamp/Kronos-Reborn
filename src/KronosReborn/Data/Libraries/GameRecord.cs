using System;
using System.Collections.Generic;

namespace KronosReborn.Data.Libraries;

/// <summary>
/// A game found on this machine.
/// </summary>
/// <remarks>
/// Deliberately a record with readonly values. A game discovered from disk is a fact about the disk,
/// and letting it be mutated in memory means two parts of the program can disagree about what was
/// found. Anything that needs to change about a game goes through the program, not the record.
/// </remarks>
/// <param name="LibraryName">Which library reported this game.</param>
/// <param name="Id">
/// The library's own identifier for the game, such as a Steam application id. Stable across renames,
/// which a folder name is not.
/// </param>
/// <param name="Name">The name the library reports, which may differ from the folder name.</param>
/// <param name="InstallPath">Absolute path to the game's folder.</param>
/// <param name="Exists">Whether that folder was actually there when it was looked for.</param>
public sealed record GameRecord(
    string LibraryName,
    string Id,
    string Name,
    string InstallPath,
    bool Exists)
{
    /// <summary>
    /// A name to show the user.
    /// </summary>
    /// <remarks>
    /// Falls back to the folder name when the library reports nothing usable, because an unnamed row
    /// in a list is less useful than a slightly wrong name, and the folder name is at least on screen.
    /// </remarks>
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? InstallPath : Name;

    /// <summary>
    /// Whether two records are the same game found twice.
    /// </summary>
    /// <remarks>
    /// Compared by install path rather than by id, because ids are only unique within a library and
    /// two different libraries can use the same numeric id for entirely different games. Paths that
    /// differ only in case or trailing separator are the same folder on Windows.
    /// </remarks>
    public static bool IsSameGame(GameRecord a, GameRecord b)
    {
        return string.Equals(
            PathNormalizer.Normalize(a.InstallPath),
            PathNormalizer.Normalize(b.InstallPath),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Collapses duplicates, keeping one record per distinct install path.
    /// </summary>
    /// <remarks>
    /// The same game installed on two libraries is one game, shown once. R3 also requires the other
    /// library to be mentioned rather than dropped, so a survivor that meets a duplicate is replaced
    /// by a copy naming both, which keeps the result immutable and still one row.
    ///
    /// The name of the first library seen leads, and enumeration order decides which that is, so the
    /// scanner supplies library order explicitly rather than letting the filesystem choose.
    /// </remarks>
    public static IReadOnlyList<GameRecord> Deduplicate(IEnumerable<GameRecord> games)
    {
        var order = new List<string>();
        var merged = new Dictionary<string, GameRecord>(StringComparer.OrdinalIgnoreCase);

        foreach (var game in games)
        {
            var key = PathNormalizer.Normalize(game.InstallPath);

            if (!merged.TryGetValue(key, out var existing))
            {
                order.Add(key);
                merged[key] = game;
                continue;
            }

            if (existing.LibraryName.Contains(game.LibraryName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            merged[key] = existing with { LibraryName = $"{existing.LibraryName}, {game.LibraryName}" };
        }

        return order.Select(key => merged[key]).ToList();
    }
}

/// <summary>
/// Path comparison that does not trip over the things Windows treats as equivalent.
/// </summary>
internal static class PathNormalizer
{
    /// <summary>
    /// A form of the path suitable for comparing two paths for equality.
    /// </summary>
    /// <remarks>
    /// Trims a trailing separator and collapses a doubled separator at the end, because
    /// "C:\Games\" and "C:\Games" are the same folder and a comparison that says otherwise produces
    /// duplicate rows in the list.
    ///
    /// Deliberately not a full canonicalisation. Resolving to a short name, resolving links or
    /// normalising away "..\" all involve touching the disk, and this is used in places that must stay
    /// cheap and must not fail when a path does not exist. Callers that need the disk checked use
    /// <see cref="DirectoryExists"/>.
    /// </remarks>
    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var trimmed = path.Trim().TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);

        // A bare drive root such as "C:\" trims down to "C:", which is the drive-relative path and
        // names something else entirely. Keep the separator for that one case.
        if (trimmed.Length == 2 && trimmed[1] == ':')
        {
            return trimmed + System.IO.Path.DirectorySeparatorChar;
        }

        return trimmed;
    }

    /// <summary>
    /// Whether a directory is there, treating any failure to tell as "not there".
    /// </summary>
    /// <remarks>
    /// An unreadable or permission-denied path is reported as not present rather than raising. The
    /// caller is building a list of what is installed, and a folder it cannot read is a folder it
    /// cannot offer to modify, so treating it as absent is the honest answer. Distinguishing the two
    /// would mean surfacing a distinction the user cannot act on.
    /// </remarks>
    public static bool DirectoryExists(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            return System.IO.Directory.Exists(path);
        }
        catch (System.IO.IOException)
        {
            return false;
        }
        catch (System.UnauthorizedAccessException)
        {
            return false;
        }
    }
}