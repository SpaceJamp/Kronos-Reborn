using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
#if WINDOWS
using Microsoft.Win32;
#endif

namespace KronosReborn.Data.Libraries;

/// <summary>One program Windows lists under "Installed apps", as its uninstall entry records it.</summary>
public sealed record UninstallEntry(string? DisplayName, string? Publisher, string? InstallLocation);

/// <summary>One game Ubisoft Connect has installed.</summary>
public sealed record UbisoftInstall(string Id, string InstallDir);

/// <summary>The registry records the stores below are read from.</summary>
/// <remarks>An interface so discovery can be tested without a machine that has these stores.</remarks>
public interface IStoreRegistry
{
    IReadOnlyList<UbisoftInstall> UbisoftInstalls();

    IReadOnlyList<UninstallEntry> UninstallEntries();
}

#if WINDOWS
/// <summary>Reads the store records from the Windows registry.</summary>
public sealed class WindowsStoreRegistry : IStoreRegistry
{
    private const string UbisoftInstallsKey = @"SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs";

    private static readonly string[] UninstallKeys =
    [
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
    ];

    public IReadOnlyList<UbisoftInstall> UbisoftInstalls()
    {
        var installs = new List<UbisoftInstall>();

        ForEachSubKey(Registry.LocalMachine, UbisoftInstallsKey, (id, key) =>
        {
            if (key.GetValue("InstallDir") is string dir && !string.IsNullOrWhiteSpace(dir))
            {
                installs.Add(new UbisoftInstall(id, dir.Trim()));
            }
        });

        return installs;
    }

    public IReadOnlyList<UninstallEntry> UninstallEntries()
    {
        var entries = new List<UninstallEntry>();

        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            foreach (var path in UninstallKeys)
            {
                ForEachSubKey(hive, path, (_, key) => entries.Add(new UninstallEntry(
                    (key.GetValue("DisplayName") as string)?.Trim(),
                    (key.GetValue("Publisher") as string)?.Trim(),
                    (key.GetValue("InstallLocation") as string)?.Trim())));
            }
        }

        return entries;
    }

    private static void ForEachSubKey(RegistryKey hive, string path, Action<string, RegistryKey> read)
    {
        try
        {
            using var root = hive.OpenSubKey(path);
            if (root is null)
            {
                return;
            }

            foreach (var name in root.GetSubKeyNames())
            {
                using var key = root.OpenSubKey(name);
                if (key is not null)
                {
                    read(name, key);
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // An unreadable key means no games from it, not a failed discovery.
        }
    }
}
#endif

/// <summary>
/// Epic Games Store: one JSON manifest per installed game, in the launcher's data folder.
/// </summary>
public static class EpicLibrary
{
    public const string LibraryName = "Epic";

    /// <summary>Where the launcher writes its manifests.</summary>
    public static string DefaultManifestsFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Epic", "EpicGamesLauncher", "Data", "Manifests");

    public static ScanResult Scan(string manifestsFolder)
    {
        var games = new List<GameRecord>();
        var problems = new List<ScanProblem>();

        if (!Directory.Exists(manifestsFolder))
        {
            return new ScanResult(games, problems, 0);
        }

        string[] files;
        try
        {
            files = Directory.GetFiles(manifestsFolder, "*.item");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems.Add(ScanProblem.FromException("Epic", manifestsFolder, "The Epic game list could not be read.", ex));
            return new ScanResult(games, problems, 1);
        }

        foreach (var file in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var json = JsonDocument.Parse(File.ReadAllText(file));
                var root = json.RootElement;

                // A download still in progress is not a game yet.
                if (root.TryGetProperty("bIsIncompleteInstall", out var incomplete) && incomplete.ValueKind == JsonValueKind.True)
                {
                    continue;
                }

                var location = Text(root, "InstallLocation");
                var appName = Text(root, "AppName") ?? Path.GetFileNameWithoutExtension(file);

                if (string.IsNullOrWhiteSpace(location))
                {
                    continue;
                }

                games.Add(new GameRecord(
                    LibraryName,
                    $"epic:{appName}",
                    Text(root, "DisplayName") ?? appName,
                    location,
                    PathNormalizer.DirectoryExists(location)));
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                problems.Add(ScanProblem.FromException(
                    "Epic game", file, "This game's details could not be understood, so the game was skipped.", ex));
            }
        }

        return new ScanResult(GameRecord.Deduplicate(games), problems, 1);
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim()
            : null;
}

/// <summary>Ubisoft Connect: one registry key per installed game, holding its folder.</summary>
public static class UbisoftLibrary
{
    public const string LibraryName = "Ubisoft";

    public static ScanResult Scan(IStoreRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        var games = registry.UbisoftInstalls()
            .Select(install =>
            {
                // Ubisoft records the folder with forward slashes and a trailing one.
                var path = install.InstallDir.Replace('/', '\\').TrimEnd('\\');
                return new GameRecord(
                    LibraryName, $"ubisoft:{install.Id}", Path.GetFileName(path), path,
                    PathNormalizer.DirectoryExists(path));
            })
            .ToList();

        return new ScanResult(games, [], 1);
    }
}

/// <summary>
/// Battle.net and the EA app: games found by the publisher on their Windows uninstall entries.
/// </summary>
/// <remarks>
/// Neither launcher keeps a plain list of its games: Battle.net's is a binary database and the EA
/// app's is encrypted. Every game they install registers itself with Windows, though, with its
/// publisher and folder, and that record is plain.
/// </remarks>
public static class PublisherLibrary
{
    public const string BattleNetName = "Battle.net";
    public const string EaName = "EA";

    /// <summary>The launchers themselves, which register under the same publishers.</summary>
    private static readonly string[] Launchers = ["Battle.net", "EA app", "Origin", "EA Desktop"];

    public static ScanResult BattleNet(IStoreRegistry registry) =>
        Scan(registry, "Blizzard Entertainment", BattleNetName, "battlenet");

    public static ScanResult Ea(IStoreRegistry registry) =>
        Scan(registry, "Electronic Arts", EaName, "ea");

    private static ScanResult Scan(IStoreRegistry registry, string publisher, string libraryName, string idPrefix)
    {
        ArgumentNullException.ThrowIfNull(registry);

        var games = registry.UninstallEntries()
            .Where(e => string.Equals(e.Publisher, publisher, StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(e.InstallLocation)
                        && !string.IsNullOrWhiteSpace(e.DisplayName)
                        && !Launchers.Contains(e.DisplayName, StringComparer.OrdinalIgnoreCase))
            .Select(e =>
            {
                var path = e.InstallLocation!.Trim('"').TrimEnd('\\', '/');
                return new GameRecord(
                    libraryName, $"{idPrefix}:{e.DisplayName}", e.DisplayName!, path,
                    PathNormalizer.DirectoryExists(path));
            })
            .ToList();

        return new ScanResult(GameRecord.Deduplicate(games), [], 1);
    }
}

/// <summary>
/// Xbox app: games live in the folders each drive's <c>.GamingRoot</c> file names, one folder per
/// game with the game itself in <c>Content</c>.
/// </summary>
public static class XboxLibrary
{
    public const string LibraryName = "Xbox";

    public static ScanResult Scan(IEnumerable<string> driveRoots)
    {
        ArgumentNullException.ThrowIfNull(driveRoots);

        var games = new List<GameRecord>();

        foreach (var drive in driveRoots)
        {
            foreach (var root in GamingRoots(drive))
            {
                string[] folders;
                try
                {
                    folders = Directory.GetDirectories(root);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                foreach (var folder in folders)
                {
                    var content = Path.Combine(folder, "Content");
                    if (Directory.Exists(content))
                    {
                        games.Add(new GameRecord(
                            LibraryName, $"xbox:{Path.GetFileName(folder)}", Path.GetFileName(folder), content, true));
                    }
                }
            }
        }

        return new ScanResult(GameRecord.Deduplicate(games), [], 1);
    }

    /// <summary>The game folders a drive's <c>.GamingRoot</c> file lists.</summary>
    /// <remarks>
    /// The file is the four bytes "RGBX", a four-byte count, then each folder as a UTF-16 path
    /// relative to the drive, ending in a null.
    /// </remarks>
    internal static IEnumerable<string> GamingRoots(string drive)
    {
        var file = Path.Combine(drive, ".GamingRoot");

        byte[] bytes;
        try
        {
            if (!File.Exists(file))
            {
                return [];
            }

            bytes = File.ReadAllBytes(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        if (bytes.Length < 8 || Encoding.ASCII.GetString(bytes, 0, 4) != "RGBX")
        {
            return [];
        }

        return Encoding.Unicode.GetString(bytes, 8, (bytes.Length - 8) & ~1)
            .Split('\0', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(relative => Path.Combine(drive, relative.TrimStart('\\', '/')))
            .Where(Directory.Exists)
            .ToArray();
    }
}

/// <summary>Games the user added by choosing their folder.</summary>
public static class ManualLibrary
{
    public const string LibraryName = "Added by hand";

    /// <summary>The id a hand-added game gets, from its folder.</summary>
    public static string IdFor(string folder) => $"manual:{PathNormalizer.Normalize(folder)}";

    public static ScanResult Scan(IEnumerable<string> folders)
    {
        ArgumentNullException.ThrowIfNull(folders);

        var games = folders
            .Select(folder => new GameRecord(
                LibraryName, IdFor(folder), Path.GetFileName(folder.TrimEnd('\\', '/')), folder,
                PathNormalizer.DirectoryExists(folder)))
            .ToList();

        return new ScanResult(GameRecord.Deduplicate(games), [], 1);
    }
}
