using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace KronosReborn.Data.Runtimes;

/// <summary>
/// Finds where the runtime DLLs of a runtime kind live inside a game folder.
/// </summary>
/// <remarks>
/// <para>
/// One implementation, used by both the scanner and the swap planner, because the two must agree.
/// If they disagree, the scanner reports a runtime the program cannot see and the swap writes a DLL
/// somewhere the game never loads from, and both mistakes look like working software.
/// </para>
/// <para>
/// It searches a few levels down rather than checking a fixed list of folder names. The list it
/// replaced held "x64", "bin", "Win64" and "Binaries/Win64", and real installs disagreed with all of
/// them: The Witcher 3 keeps DLSS in <c>bin\x64_dx12</c>, Injustice in <c>Binaries\Retail</c>, and
/// Portal with RTX in <c>bin\.trex</c>. Every one of those games reported no runtime at all, so the
/// program could not have swapped anything in three of the four games on a typical library.
/// </para>
/// <para>
/// The search is bounded by depth and skips reparse points, so it cannot walk into a junction that
/// leads back up the tree or spend unbounded time on a very deep install.
/// </para>
/// </remarks>
public static class RuntimeDllLocator
{
    /// <summary>
    /// How many levels below the game folder to look.
    /// </summary>
    /// <remarks>
    /// Four reaches every layout seen so far with room to spare. Going deeper costs a full extra
    /// level of directory enumeration per game across every game on the machine, to find layouts that
    /// do not occur in practice.
    /// </remarks>
    public const int DefaultDepth = 4;

    /// <summary>
    /// Locates every known runtime DLL inside a game folder.
    /// </summary>
    /// <param name="gamePath">The game's installation folder.</param>
    /// <param name="maxDepth">How many levels below <paramref name="gamePath"/> to search.</param>
    /// <param name="cancellation">Allows interrupting a scan of many games.</param>
    /// <returns>
    /// Absolute path by file name. A name absent means the game does not have that file, not that
    /// there was an error.
    /// </returns>
    public static Dictionary<string, string> Find(
        string gamePath,
        int maxDepth = DefaultDepth,
        CancellationToken cancellation = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gamePath);

        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!Directory.Exists(gamePath))
        {
            return found;
        }

        var pending = new Queue<(string Directory, int Depth)>();
        pending.Enqueue((gamePath, 0));

        while (pending.Count > 0)
        {
            cancellation.ThrowIfCancellationRequested();

            var (directory, depth) = pending.Dequeue();

            foreach (var fileName in RuntimeDlls.AllFileNames)
            {
                if (found.ContainsKey(fileName))
                {
                    continue;
                }

                var candidate = Path.Combine(directory, fileName);
                if (File.Exists(candidate))
                {
                    found[fileName] = candidate;
                }
            }

            if (depth >= maxDepth)
            {
                continue;
            }

            foreach (var child in EnumerateChildDirectories(directory))
            {
                pending.Enqueue((child, depth + 1));
            }
        }

        return found;
    }

    /// <summary>
    /// Where a DLL of a given name belongs in a game folder.
    /// </summary>
    /// <remarks>
    /// The place the game already keeps it, so a replacement lands where the game loads from. A DLL
    /// the game does not have goes in the game root, which is where the majority of these sit.
    /// </remarks>
    public static string TargetPathFor(
        string gamePath,
        string fileName,
        IReadOnlyDictionary<string, string> located)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gamePath);

        return located.TryGetValue(fileName, out var existing)
            ? existing
            : Path.Combine(gamePath, fileName);
    }

    /// <summary>
    /// The immediate subdirectories of a folder that are worth searching.
    /// </summary>
    /// <remarks>
    /// An unreadable folder is skipped rather than reported: R4 says a source that cannot be read is
    /// reported, but this is one directory inside a game rather than a library, and failing the whole
    /// game because of one locked folder would be worse than missing that folder. Reparse points are
    /// skipped because a junction pointing back up the tree makes the walk unbounded.
    /// </remarks>
    private static IEnumerable<string> EnumerateChildDirectories(string directory)
    {
        string[] children;

        try
        {
            children = Directory.GetDirectories(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var child in children)
        {
            try
            {
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            yield return child;
        }
    }
}
