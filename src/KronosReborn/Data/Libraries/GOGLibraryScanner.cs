using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace KronosReborn.Data.Libraries;

/// <summary>
/// Finds the games a GOG Galaxy installation can see.
/// </summary>
/// <remarks>
/// GOG Galaxy records installed games under its storage folder. Each installed game has its own
/// subfolder with a <c>gameinfo.json</c> describing it. Every failure path produces a
/// <see cref="ScanProblem"/> and carries on, per R4/R5.
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Performance",
    "CA1822:Mark members as static",
    Justification = "Stateless by design; kept as a type so it can name a unit of work.")]
public sealed class GOGLibraryScanner
{
    /// <summary>Name used for the GOG Galaxy library.</summary>
    private const string LibraryName = "GOG Galaxy";

    /// <summary>Relative path from the Galaxy root to installed-game metadata.</summary>
    private const string InstalledStorageRelative = "storage/installed";

    /// <summary>
    /// Discovers every game a GOG Galaxy installation knows about.
    /// </summary>
    /// <param name="galaxyRoot">The GOG Galaxy installation directory.</param>
    /// <param name="cancellation">Lets an interrupted discovery stop promptly.</param>
    /// <returns>A scan result with every game found and any problems encountered.</returns>
    public ScanResult Scan(string galaxyRoot, CancellationToken cancellation = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(galaxyRoot);

        var problems = new List<ScanProblem>();
        var games = new List<GameRecord>();

        var installedRoot = Path.Combine(galaxyRoot, InstalledStorageRelative);

        if (!PathNormalizer.DirectoryExists(installedRoot))
        {
            // No installed games folder means GOG Galaxy isn't really set up yet. Report it
            // quietly — a freshly installed Galaxy with no games is normal.
            return new ScanResult([], [], 1);
        }

        try
        {
            foreach (var gameDir in Directory.GetDirectories(installedRoot))
            {
                cancellation.ThrowIfCancellationRequested();

                var game = ReadGameInfo(gameDir);
                if (game is not null)
                {
                    games.Add(game);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems.Add(ScanProblem.FromException(
                "GOG Galaxy",
                installedRoot,
                "The installed games could not be listed.",
                ex,
                "This usually means you do not have permission to read it, or the drive is not ready."));
        }

        return new ScanResult(GameRecord.Deduplicate(games), problems, 1);
    }

    /// <summary>
    /// Reads one game's metadata from its installed folder.
    /// </summary>
    /// <param name="gameDir">The game's folder under storage/installed.</param>
    /// <returns>A <see cref="GameRecord"/>, or null if the game's metadata is unusable.</returns>
    private static GameRecord? ReadGameInfo(string gameDir)
    {
        var infoFile = Path.Combine(gameDir, "gameinfo.json");
        if (!File.Exists(infoFile))
        {
            return null;
        }

        string text;
        try
        {
            text = File.ReadAllText(infoFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        GameInfo? info;
        try
        {
            info = JsonSerializer.Deserialize<GameInfo>(text);
        }
        catch (JsonException)
        {
            return null;
        }

        if (info is null || string.IsNullOrWhiteSpace(info.GameId))
        {
            return null;
        }

        var installPath = Path.Combine(gameDir, "game");
        if (!PathNormalizer.DirectoryExists(installPath))
        {
            // The metadata exists but the game files don't. Report it as missing rather than
            // silently dropping it — R7 says vanished games are flagged, not forgotten.
            return new GameRecord(
                LibraryName,
                info.GameId,
                info.Title ?? Path.GetFileName(gameDir),
                installPath,
                Exists: false);
        }

        return new GameRecord(
            LibraryName,
            info.GameId,
            info.Title ?? Path.GetFileName(gameDir),
            installPath,
            Exists: true);
    }

    /// <summary>
    /// One game's metadata as GOG Galaxy writes it.
    /// </summary>
    private sealed class GameInfo
    {
        public string? GameId { get; set; }
        public string? Title { get; set; }
    }
}
