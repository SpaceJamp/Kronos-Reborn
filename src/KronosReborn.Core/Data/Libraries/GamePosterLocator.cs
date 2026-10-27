using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace KronosReborn.Data.Libraries;

/// <summary>
/// Finds the cover art Steam has already downloaded for a game.
/// </summary>
/// <remarks>
/// <para>
/// Steam keeps the artwork it shows in its own library for every app it knows about, so the poster is
/// already on this machine and nothing has to be fetched. That matters beyond convenience: the
/// specification requires the program to work with no network and no service operated by anyone
/// else (C3), and defers anything that would make a remote call part of showing a game. Reading what
/// Steam has already written satisfies both with nothing new to depend on.
/// </para>
/// <para>
/// There are two layouts in the wild and both are still produced. Older clients write the files
/// directly into a folder named for the app; newer ones put them in a subfolder named for the image
/// hash, keeping a hashed file of their own alongside. Both are searched, because a machine may have
/// either and which one is used is not something the user chose or can be asked about.
/// </para>
/// <para>
/// Artwork is a nicety and never a requirement: a game with no cached art is simply listed without a
/// poster, which is what the specification assumed when it deferred this.
/// </para>
/// </remarks>
public static class GamePosterLocator
{
    /// <summary>Where Steam keeps artwork, relative to the Steam installation folder.</summary>
    private static readonly string[] ArtworkFolders = ["appcache/librarycache", "steamapps/librarycache"];

    /// <summary>
    /// File names to look for, most preferred first.
    /// </summary>
    /// <remarks>
    /// Ordered by how well each suits a portrait cover. The capsule images are the vertical ones; the
    /// header and hero are wide, so they are only used when nothing better exists, the header first
    /// because it carries the game's title and the hero is often a dark banner without one.
    /// </remarks>
    private static readonly string[] PreferredNames =
    [
        "library_capsule.jpg",
        "library_600x900.jpg",
        "capsule.jpg",
        "p Capsule.jpg",
        "header.jpg",
        "library_header.jpg",
        "library_hero.jpg",
    ];

    /// <summary>
    /// Locates the poster for a Steam app.
    /// </summary>
    /// <param name="appId">The Steam application id, which is the artwork folder's name.</param>
    /// <param name="steamRoots">Steam installation folders to look in.</param>
    /// <returns>The artwork file, or null when none is cached.</returns>
    public static string? Find(string appId, IEnumerable<string> steamRoots)
    {
        if (string.IsNullOrWhiteSpace(appId) || steamRoots is null)
        {
            return null;
        }

        foreach (var root in steamRoots)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            foreach (var artworkFolder in ArtworkFolders)
            {
                var appFolder = Path.Combine(root, artworkFolder, appId);

                if (!Directory.Exists(appFolder))
                {
                    continue;
                }

                var found = Search(appFolder);
                if (found is not null)
                {
                    return found;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The best artwork inside one app folder.
    /// </summary>
    /// <remarks>
    /// Current clients keep each image in its own hash-named subfolder
    /// (<c>990080\dca9…\library_capsule.jpg</c>); older ones put them at the top. So each preferred
    /// name is looked for everywhere before the next is tried. The one unnamed image at the top, named
    /// for its hash, is a 32x32 icon: it was taken as the cover and shown stretched and blurred, and
    /// the initial drawn when there is no poster is better than that.
    /// </remarks>
    private static string? Search(string appFolder)
    {
        string[] folders = [appFolder, .. SafeEnumerateDirectories(appFolder)];

        foreach (var name in PreferredNames)
        {
            foreach (var folder in folders)
            {
                var candidate = Path.Combine(folder, name);
                if (IsImage(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string directory)
    {
        try
        {
            return Directory.EnumerateDirectories(directory).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Whether a path is a file whose extension is an image format.
    /// </summary>
    /// <remarks>
    /// Extension rather than content. These are Steam's own files in its own cache, and reading and
    /// validating the header of every candidate would cost more than it could tell: a file that Steam
    /// put in an artwork folder named for an app is the artwork for that app.
    /// </remarks>
    private static bool IsImage(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        return Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".webp";
    }
}
