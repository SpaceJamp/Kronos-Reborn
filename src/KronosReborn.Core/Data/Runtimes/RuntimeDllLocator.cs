using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
/// The search is bounded by depth, and follows a junction or symbolic link only to a folder it has
/// not already searched, so a link that leads back up the tree cannot make the walk unbounded.
/// </para>
/// </remarks>
public static class RuntimeDllLocator
{
    /// <summary>
    /// How many levels below the game folder to look.
    /// </summary>
    /// <remarks>
    /// Unreal Engine's DLSS plugin keeps its DLL eight levels down, in
    /// <c>Engine\Plugins\Runtime\Nvidia\DLSS\Binaries\ThirdParty\Win64</c>. Four levels, the old
    /// limit, missed it, so those games showed no DLSS at all. Ten leaves room for a game folder
    /// nested one or two levels inside the install.
    /// </remarks>
    public const int DefaultDepth = 10;

    /// <summary>
    /// Locates every known runtime DLL inside a game folder.
    /// </summary>
    /// <param name="gamePath">The game's installation folder.</param>
    /// <param name="maxDepth">How many levels below <paramref name="gamePath"/> to search.</param>
    /// <param name="cancellation">Allows interrupting a scan of many games.</param>
    /// <returns>
    /// Absolute path by file name. A name absent means the game does not have that file, not that
    /// there was an error. Only the shallowest (first found) path is returned per name.
    /// </returns>
    public static Dictionary<string, string> Find(
        string gamePath,
        int maxDepth = DefaultDepth,
        CancellationToken cancellation = default)
    {
        var all = FindAll(gamePath, maxDepth, cancellation);
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, paths) in all)
        {
            if (paths.Count > 0)
            {
                found[name] = paths[0];
            }
        }

        return found;
    }

    /// <summary>
    /// Locates every copy of every known runtime DLL inside a game folder.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="Find"/>, which keeps only the shallowest copy, this returns every location
    /// where a DLL with a known name appears. That is what lets the scanner report that a game carries
    /// the same runtime in two folders, which is the situation DLSS Swapper flags as
    /// MultipleDLSSFound rather than silently resolving to one of them.
    /// </remarks>
    /// <param name="gamePath">The game's installation folder.</param>
    /// <param name="maxDepth">How many levels below <paramref name="gamePath"/> to search.</param>
    /// <param name="cancellation">Allows interrupting a scan of many games.</param>
    /// <returns>
    /// A list of absolute paths per file name, in breadth-first (shallowest first) order. A name
    /// absent means the game does not have that file.
    /// </returns>
    public static Dictionary<string, List<string>> FindAll(
        string gamePath,
        int maxDepth = DefaultDepth,
        CancellationToken cancellation = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gamePath);

        var found = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        if (!Directory.Exists(gamePath))
        {
            return found;
        }

        var pending = new Queue<(string Directory, int Depth)>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.GetFullPath(gamePath) };
        pending.Enqueue((gamePath, 0));

        while (pending.Count > 0)
        {
            cancellation.ThrowIfCancellationRequested();

            var (directory, depth) = pending.Dequeue();

            // One listing per folder rather than one existence check per known name.
            foreach (var candidate in EnumerateDlls(directory))
            {
                var fileName = Path.GetFileName(candidate);
                if (RuntimeDlls.KindFor(fileName) is null)
                {
                    continue;
                }

                // Keyed by the canonical name, so callers indexing by RuntimeDlls names always match.
                var canonical = RuntimeDlls.AllFileNames.First(
                    name => name.Equals(fileName, StringComparison.OrdinalIgnoreCase));

                if (!found.TryGetValue(canonical, out var list))
                {
                    list = [];
                    found[canonical] = list;
                }
                list.Add(candidate);
            }

            if (depth >= maxDepth)
            {
                continue;
            }

            foreach (var child in EnumerateChildDirectories(directory, visited))
            {
                pending.Enqueue((child, depth + 1));
            }
        }

        return found;
    }

    /// <summary>
    /// Every place a DLL of a given name belongs in a game folder.
    /// </summary>
    /// <remarks>
    /// Every copy the game carries, so a game that keeps the runtime in two folders runs the chosen
    /// version whichever one it loads. A DLL the game does not have goes in the game root.
    /// </remarks>
    public static IReadOnlyList<string> TargetPathsFor(
        string gamePath,
        string fileName,
        IReadOnlyDictionary<string, List<string>> located)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gamePath);

        return located.TryGetValue(fileName, out var existing) && existing.Count > 0
            ? existing
            : [Path.Combine(gamePath, fileName)];
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
    /// game because of one locked folder would be worse than missing that folder. A junction or link is
    /// followed to where it points, but only if that folder has not been searched already.
    /// </remarks>
    private static IEnumerable<string> EnumerateChildDirectories(string directory, HashSet<string> visited)
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
            string resolved;
            try
            {
                var info = new DirectoryInfo(child);
                resolved = (info.Attributes & FileAttributes.ReparsePoint) != 0
                    ? info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? info.FullName
                    : info.FullName;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (visited.Add(resolved))
            {
                yield return child;
            }
        }
    }

    private static string[] EnumerateDlls(string directory)
    {
        try
        {
            return Directory.GetFiles(directory, "*.dll");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
