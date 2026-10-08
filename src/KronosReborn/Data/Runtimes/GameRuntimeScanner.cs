using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using KronosReborn.Data.Libraries;

namespace KronosReborn.Data.Runtimes;

/// <summary>
/// Scans a game installation for runtime DLLs and reads their version information.
/// </summary/// <remarks>
/// This is pure filesystem work and version resource reading. Every failure path produces a
/// DetectedRuntime with Exists=true but Version=null and a ReadError, which is exactly R10: the
/// version is unknown, not guessed. The caller gets a complete list and decides how to present it.
///
/// This type holds no state. It is kept as a type rather than made static because it names a unit
/// of work — scanning a game folder for runtime DLLs — and the view model constructs it as one. That
/// is a deliberate shape, not an oversight, which is why the "make members static" analyzer finding
/// is suppressed here.
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Performance",
    "CA1822:Mark members as static",
    Justification = "Stateless by design; kept as a type so it can name a unit of work.")]
public sealed class GameRuntimeScanner
{
    /// <summary>
    /// Scans one game's folder for all known runtime DLLs.
    /// </summary>
    /// <param name="game">The game to scan, from library discovery.</param>
    /// <param name="cancellation">Allows interrupting a scan of many games.</param>
    public GameRuntimeSnapshot Scan(GameRecord game, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(game);

        cancellation.ThrowIfCancellationRequested();

        var runtimes = new List<DetectedRuntime>();

        // Where each of this game's runtime DLLs actually is. Shared with the swap planner so that
        // detection and installation cannot disagree about a path.
        var located = RuntimeDllLocator.Find(game.InstallPath, cancellation: cancellation);

        foreach (var fileName in RuntimeDlls.AllFileNames)
        {
            cancellation.ThrowIfCancellationRequested();

            var fullPath = located.GetValueOrDefault(fileName)
                           ?? Path.Combine(game.InstallPath, fileName);

            var relativePath = Path.GetRelativePath(game.InstallPath, fullPath);

            var kind = RuntimeDlls.KindFor(fileName) ?? throw new InvalidOperationException($"File name {fileName} not in RuntimeDlls");
            runtimes.Add(ReadOne(kind, fileName, relativePath, fullPath));
        }

        return new GameRuntimeSnapshot(game.Id, game.DisplayName, game.InstallPath, runtimes);
    }

    /// <summary>
    /// Scans multiple games, returning snapshots and accumulated problems.
    /// </summary>
    /// <remarks>
    /// R8: discovery is safe to interrupt. Cancellation is checked between games and between DLLs
    /// within a game, so a cancelled scan leaves the program in a consistent state.
    /// </remarks>
    public IReadOnlyList<GameRuntimeSnapshot> ScanMany(
        IEnumerable<GameRecord> games,
        CancellationToken cancellation = default)
    {
        var results = new List<GameRuntimeSnapshot>();

        foreach (var game in games)
        {
            cancellation.ThrowIfCancellationRequested();

            if (!game.Exists)
            {
                // R7: missing games are still scanned but will have no runtimes. We record an empty
                // snapshot rather than skipping, so the UI can show "game not on disk" consistently.
                results.Add(new GameRuntimeSnapshot(game.Id, game.DisplayName, game.InstallPath, []));
                continue;
            }

            results.Add(Scan(game, cancellation));
        }

        return results;
    }

    private static DetectedRuntime ReadOne(
        RuntimeKind kind,
        string fileName,
        string relativePath,
        string fullPath)
    {
        if (!File.Exists(fullPath))
        {
            return new DetectedRuntime(kind, fileName, relativePath, fullPath, null, null, false);
        }

        try
        {
            var versionInfo = FileVersionInfo.GetVersionInfo(fullPath);

            var fileVersion = versionInfo.FileVersion?.Trim();
            var productVersion = versionInfo.ProductVersion?.Trim();

            // Empty version strings from the API mean the resource is there but empty. Treat as unknown.
            if (string.IsNullOrWhiteSpace(fileVersion))
            {
                fileVersion = null;
            }
            if (string.IsNullOrWhiteSpace(productVersion))
            {
                productVersion = null;
            }

            return new DetectedRuntime(kind, fileName, relativePath, fullPath, fileVersion, productVersion, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return new DetectedRuntime(kind, fileName, relativePath, fullPath, null, null, true, ex.Message);
        }
    }
}