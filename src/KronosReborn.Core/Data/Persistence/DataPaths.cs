using System;
using System.IO;
using KronosReborn.Data.Runtimes;

namespace KronosReborn.Data.Persistence;

/// <summary>
/// The set of paths the program keeps its own data under.
/// </summary>
/// <remarks>
/// R43: the data directory is user-specific. A per-user installation and a machine-wide
/// installation write to the same place for a given user.
///
/// This is an instance rather than a static class on purpose. It used to be static, hard-wired to
/// <see cref="Environment.SpecialFolder.LocalApplicationData"/>, which meant a test could not point
/// it anywhere: running the suite wrote real history files into the developer's own profile, and
/// one run's leftovers were read back by the next. Every consumer now takes the paths it should
/// use, so tests pass a temporary directory and touch nothing else.
///
/// Production code obtains it from <see cref="ForCurrentUser"/>.
/// </remarks>
public sealed class DataPaths
{
    /// <summary>The folder name used under the user's local application data.</summary>
    public const string AppFolderName = "KronosReborn";

    /// <summary>Creates a set of paths rooted at <paramref name="root"/>.</summary>
    public DataPaths(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.GetFullPath(root);
    }

    /// <summary>The data directory itself. Not created by this call.</summary>
    public string Root { get; }

    /// <summary>The paths for the current user, as used by the application.</summary>
    public static DataPaths ForCurrentUser()
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new DataPaths(Path.Combine(baseDir, AppFolderName));
    }

    /// <summary>Path to the settings file.</summary>
    public string SettingsPath => Path.Combine(Root, "settings.json");

    /// <summary>Path to the runtime catalog file.</summary>
    public string CatalogPath => Path.Combine(Root, "catalog.json");

    /// <summary>The list of game folders the user added by hand.</summary>
    public string ManualGamesPath => Path.Combine(Root, "manual-games.json");

    /// <summary>The list of folders whose subfolders are each a game.</summary>
    public string GameFoldersPath => Path.Combine(Root, "game-folders.json");

    /// <summary>Directory holding one JSON file per game's history.</summary>
    public string HistoryDirectory => Path.Combine(Root, "history");

    /// <summary>
    /// Directory holding the originals a swap overwrote.
    /// </summary>
    /// <remarks>
    /// Backups live here rather than beside the file they protect. R24 says nothing but the runtime
    /// files themselves is written into a game folder, and a file named
    /// <c>nvngx_dlss.dll.kronos_bak_...</c> sitting in a Steam install is at the mercy of anything
    /// that verifies game files, shows all files in a launcher, or deletes unknown DLLs.
    /// </remarks>
    public string BackupDirectory => Path.Combine(Root, "backups");

    /// <summary>Covers fetched from Steam's store for games Steam's own cache has none for.</summary>
    public string PostersDirectory => Path.Combine(Root, "posters");

    /// <summary>
    /// Directory holding copies of the runtime versions the collection owns.
    /// </summary>
    /// <remarks>
    /// One folder per kind and version, each holding every file that kind consists of. An imported
    /// runtime is copied in rather than referenced where it was found, so the collection keeps working
    /// when the folder it came from is moved, deleted, or replaced by the launcher that provided it.
    /// </remarks>
    public string RuntimesDirectory => Path.Combine(Root, "runtimes");

    /// <summary>Where the collection's own copy of one runtime version lives.</summary>
    public string RuntimeFolder(RuntimeKind kind, string version) =>
        Path.Combine(RuntimesDirectory, kind.ToString(), SafeName(version));

    /// <summary>
    /// Renames a data file that could not be read, so the next save does not overwrite it.
    /// </summary>
    /// <remarks>
    /// Starting with empty data is right (R51), but saving that empty data over the unreadable file
    /// threw away everything in it for good. Kept aside, it can still be repaired by hand.
    /// </remarks>
    /// <returns>Where the file now is, or null if it could not be moved.</returns>
    public static string? SetAside(string path)
    {
        var aside = $"{path}.unreadable-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";

        try
        {
            File.Move(path, aside);
            return aside;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Creates the data directory if it is not already there.</summary>
    public string EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        return Root;
    }

    /// <summary>
    /// Turns a game id into something usable as a file or folder name.
    /// </summary>
    /// <remarks>
    /// Used for both history files and backup folders, so it has to be injective enough that two
    /// different games cannot collide. Invalid characters become underscores, and a hash of the
    /// original is appended so that ids which sanitise to the same string still get separate names.
    /// </remarks>
    public static string SafeName(string gameId)
    {
        var safe = string.Join("_", gameId.Split(Path.GetInvalidFileNameChars()));
        if (safe.Length > 64)
        {
            safe = safe[..64];
        }

        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(gameId));
        return $"{safe}_{Convert.ToHexString(hash)[..8].ToLowerInvariant()}";
    }
}
