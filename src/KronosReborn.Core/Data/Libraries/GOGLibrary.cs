using System;
using System.Collections.Generic;
using System.IO;
#if WINDOWS
using Microsoft.Win32;
#endif

namespace KronosReborn.Data.Libraries;

/// <summary>One installed GOG game, as its installer recorded it.</summary>
/// <param name="Id">GOG's product id, the name of the registry key.</param>
/// <param name="Name">The game's name, when recorded.</param>
/// <param name="Path">Where the game is installed, when recorded.</param>
public sealed record GOGInstall(string Id, string? Name, string? Path);

/// <summary>Reads the games GOG has installed.</summary>
/// <remarks>An interface so discovery can be tested without a machine that has GOG games.</remarks>
public interface IGOGRegistry
{
    IReadOnlyList<GOGInstall> InstalledGames();
}

#if WINDOWS
/// <summary>
/// Reads <c>HKLM\SOFTWARE\WOW6432Node\GOG.com\Games</c>, where GOG's installers record every game.
/// </summary>
/// <remarks>
/// Every GOG game writes a key here with its <c>path</c>, whether it was installed through Galaxy or
/// from a standalone installer. The earlier code looked for a <c>storage\installed</c> folder inside
/// Galaxy instead, which Galaxy does not keep, so it never found a game.
/// </remarks>
public sealed class WindowsGOGRegistry : IGOGRegistry
{
    private const string GamesKeyPath = @"SOFTWARE\WOW6432Node\GOG.com\Games";

    public IReadOnlyList<GOGInstall> InstalledGames()
    {
        var games = new List<GOGInstall>();

        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(GamesKeyPath);
            if (root is null)
            {
                return games;
            }

            foreach (var id in root.GetSubKeyNames())
            {
                using var key = root.OpenSubKey(id);
                games.Add(new GOGInstall(
                    id,
                    (key?.GetValue("gameName") as string)?.Trim(),
                    (key?.GetValue("path") as string)?.Trim()));
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // An unreadable registry means no GOG games can be listed, not that discovery fails.
        }

        return games;
    }
}
#endif

/// <summary>Turns GOG's install records into games.</summary>
public static class GOGLibraryScanner
{
    /// <summary>Name used for the GOG library.</summary>
    public const string LibraryName = "GOG";

    public static ScanResult Scan(IGOGRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        var games = new List<GameRecord>();
        var problems = new List<ScanProblem>();

        foreach (var install in registry.InstalledGames())
        {
            if (string.IsNullOrWhiteSpace(install.Path))
            {
                problems.Add(new ScanProblem(
                    "GOG game",
                    install.Name ?? install.Id,
                    "GOG lists this game but does not say where it is installed, so it was skipped.",
                    Hint: "Reinstalling or repairing the game in GOG Galaxy would rewrite this."));
                continue;
            }

            games.Add(new GameRecord(
                LibraryName,
                install.Id,
                install.Name ?? install.Id,
                install.Path,
                PathNormalizer.DirectoryExists(install.Path)));
        }

        return new ScanResult(games, problems, 1);
    }
}
