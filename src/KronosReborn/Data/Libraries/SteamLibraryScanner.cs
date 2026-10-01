using System;
using System.Collections.Generic;
using System.IO;

namespace KronosReborn.Data.Libraries;

/// <summary>
/// Finds the games a Steam installation can see.
/// </summary>
/// <remarks>
/// Every failure path here produces a <see cref="ScanProblem"/> and carries on. That is not
/// defensiveness for its own sake, it is what R4 and R5 ask for: one unreadable library, or one
/// manifest that is half written, must not cost the user every other game they own.
/// </remarks>
public sealed class SteamLibraryScanner
{
    /// <summary>Name used for the library folder that is the Steam installation itself.</summary>
    private const string MainLibraryName = "Steam";

    /// <summary>
    /// Discovers every game across every library belonging to a Steam installation.
    /// </summary>
    /// <param name="steamRoot">The Steam installation directory.</param>
    /// <param name="cancellation">Lets an interrupted discovery stop promptly.</param>
    public ScanResult Scan(string steamRoot, CancellationToken cancellation = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(steamRoot);

        var problems = new List<ScanProblem>();
        var games = new List<GameRecord>();

        SteamLibraryFolders.TryRead(steamRoot, out var libraries, out var libraryProblem);

        if (libraryProblem is not null)
        {
            problems.Add(libraryProblem);
        }

        foreach (var library in libraries)
        {
            cancellation.ThrowIfCancellationRequested();

            ScanLibrary(library, steamRoot, games, problems, cancellation);
        }

        // R5: problems accumulated above are collected, never substituted for what was found. A
        // broken library reduces what is listed and says why, rather than emptying the list.
        return new ScanResult(GameRecord.Deduplicate(games), problems, libraries.Count);
    }

    /// <summary>
    /// Scans one library folder.
    /// </summary>
    private void ScanLibrary(
        string libraryRoot,
        string steamRoot,
        List<GameRecord> games,
        List<ScanProblem> problems,
        CancellationToken cancellation)
    {
        if (!PathNormalizer.DirectoryExists(libraryRoot))
        {
            // R4. The library is listed by Steam but is not on this machine, which is what a library
            // on a drive that is not connected looks like. Saying so is useful; silently skipping it
            // would leave the user wondering where their games went.
            problems.Add(new ScanProblem(
                "Steam library",
                libraryRoot,
                "This library folder is not present, so its games were not searched for.",
                Hint: "If the drive holding it is not connected, reconnect it and search again."));

            return;
        }

        var manifests = Path.Combine(libraryRoot, "steamapps");
        var libraryName = NameFor(libraryRoot, steamRoot);

        IEnumerable<string> files;

        try
        {
            files = Directory.EnumerateFiles(manifests, "appmanifest_*.acf");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems.Add(ScanProblem.FromException(
                "Steam library",
                manifests,
                "The games in this library could not be listed.",
                ex,
                "This usually means you do not have permission to read it, or the drive is not ready."));

            return;
        }

        // Enumeration is lazy, so the sort forces it to happen here where the failure can still be
        // caught and reported, rather than part way through the loop where it would escape.
        string[] ordered;

        try
        {
            ordered = files.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems.Add(ScanProblem.FromException(
                "Steam library",
                manifests,
                "The games in this library could not be listed.",
                ex));

            return;
        }

        foreach (var file in ordered)
        {
            cancellation.ThrowIfCancellationRequested();

            ReadManifest(file, libraryRoot, libraryName, games, problems);
        }
    }

    /// <summary>
    /// Reads one manifest and adds the game it describes.
    /// </summary>
    private void ReadManifest(
        string file,
        string libraryRoot,
        string libraryName,
        List<GameRecord> games,
        List<ScanProblem> problems)
    {
        var fileName = Path.GetFileName(file);

        // Matched on the file name so a stray file matching the wildcard is skipped rather than
        // reaching the id check with nothing to work with.
        if (SteamAppManifest.TryGetAppIdFromFileName(fileName) is null)
        {
            return;
        }

        string text;

        try
        {
            text = File.ReadAllText(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems.Add(ScanProblem.FromException(
                "Steam game",
                file,
                "This game's details could not be read.",
                ex));

            return;
        }

        if (!SteamAppManifest.TryParse(text, out var manifest, out var error))
        {
            problems.Add(new ScanProblem(
                "Steam game",
                file,
                "This game's details could not be understood, so the game was skipped.",
                error,
                "Steam may have been in the middle of updating this game. Searching again usually clears it."));

            return;
        }

        if (!manifest!.IsUsable)
        {
            problems.Add(new ScanProblem(
                "Steam game",
                file,
                "This game's details do not say where it is installed, so the game was skipped.",
                Hint: "Reinstalling the game in Steam would rewrite this."));

            return;
        }

        if (!manifest.HasSafeInstallDirectory)
        {
            problems.Add(new ScanProblem(
                "Steam game",
                file,
                "This game records an install location that is not inside its library, so the game was skipped.",
                $"Steam recorded the folder as \"{manifest.InstallDirectory}\".",
                "This entry is written by Steam. It is being ignored rather than followed, because following it would point outside the game library."));

            return;
        }

        var installPath = Path.Combine(libraryRoot, "steamapps", "common", manifest.InstallDirectory);

        // R7. The game is listed even when its folder is gone, flagged rather than dropped. Silently
        // forgetting it looks the same as the game never having existed.
        games.Add(new GameRecord(
            libraryName,
            manifest.AppId,
            manifest.Name,
            installPath,
            PathNormalizer.DirectoryExists(installPath)));
    }

    /// <summary>
    /// A name for a library folder.
    /// </summary>
    /// <remarks>
    /// The installation folder is always called Steam, whatever it is actually named on disk, so that
    /// the list reads the same for everyone. Other libraries are named for their folder, which is
    /// what the user gave them when they created the library.
    /// </remarks>
    private static string NameFor(string libraryRoot, string steamRoot)
    {
        if (string.Equals(
                PathNormalizer.Normalize(libraryRoot),
                PathNormalizer.Normalize(steamRoot),
                StringComparison.OrdinalIgnoreCase))
        {
            return MainLibraryName;
        }

        var name = Path.GetFileName(PathNormalizer.Normalize(libraryRoot));

        return string.IsNullOrWhiteSpace(name) ? libraryRoot : name;
    }
}