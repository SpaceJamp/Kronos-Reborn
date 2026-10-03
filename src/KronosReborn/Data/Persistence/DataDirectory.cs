using System;
using System.IO;

namespace KronosReborn.Data.Persistence;

/// <summary>
/// Resolves the program's data directory.
/// </summary>
/// <remarks>
/// R43: the program's data directory is user-specific. A per-user installation and a machine-wide
/// installation write to the same place for a given user.
/// Uses LocalApplicationData (not Roaming) because this is machine-specific cache/history data.
/// </remarks>
public static class DataDirectory
{
    private const string AppFolderName = "KronosReborn";

    /// <summary>Gets the absolute path to the program's data directory, creating it if needed.</summary>
    public static string Get()
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var path = Path.Combine(baseDir, AppFolderName);

        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
        }

        return path;
    }

    /// <summary>Path to the settings file.</summary>
    public static string SettingsPath => Path.Combine(Get(), "settings.json");

    /// <summary>Path to the history directory (one file per game).</summary>
    public static string HistoryDirectory
    {
        get
        {
            var path = Path.Combine(Get(), "history");
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }
            return path;
        }
    }

    /// <summary>Path to the runtime catalog file.</summary>
    public static string CatalogPath => Path.Combine(Get(), "catalog.json");
}