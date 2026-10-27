using System;
using System.Globalization;
using System.IO;
using System.Linq;

namespace KronosReborn.Data.Runtimes;

/// <summary>
/// Puts a runtime version into one comparable, readable form.
/// </summary>
/// <remarks>
/// <para>
/// NVIDIA writes its version resources with commas: the string in <c>nvngx_dlss.dll</c> is
/// "310,9,1,0", and every DLSS file reports its version that way. AMD and Intel use dots
/// ("1.0.2.38022"). Both are valid Windows version-resource text; only one of them is readable.
/// </para>
/// <para>
/// This was not merely cosmetic. Version comparison splits on dots, so a comma-separated version
/// arrived as a single unparseable token and became zero. Every DLSS version therefore compared equal
/// to every other, and the downgrade warning required by R30 could never fire for the most common
/// runtime there is — while the interface displayed "310,9,1" as though it were a version.
/// </para>
/// <para>
/// Normalisation happens where the version is read, so everything downstream — the catalog, the
/// display, the comparison and the stored history — sees one form and cannot disagree about it.
/// </para>
/// </remarks>
public static class VersionNumber
{
    /// <summary>
    /// The number of components in a Windows version resource.
    /// </summary>
    /// <remarks>
    /// Major, minor, build, revision. The revision is frequently zero and is dropped from the
    /// displayed form, since "310.9.1.0" reads as noise next to "310.9.1".
    /// </remarks>
    private const int ResourceComponentCount = 4;

    /// <summary>
    /// Turns a version resource string into dotted form.
    /// </summary>
    /// <remarks>
    /// Only the revision is trimmed, and only from a four-component resource, so "310,9,1,0"
    /// becomes "310.9.1" while "1.0.2.38022" and "3.8.0" are left alone. Trimming is safe for
    /// comparison, which treats missing components as zero, so "310.9.1" and "310.9.1.0" still
    /// compare equal.
    /// </remarks>
    /// <param name="version">The version as read, or null when there is none.</param>
    /// <returns>
    /// The normalised version, or the value unchanged when there is nothing to normalise. In
    /// particular a missing version stays missing: turning it into an empty string would make an
    /// unknown version look known, which is R10 — never guess.
    /// </returns>
    public static string? Normalize(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            // Whitespace is absence of a version, not a version made of spaces.
            return null;
        }

        var dotted = version.Trim().Replace(',', '.');

        var parts = dotted.Split('.', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == ResourceComponentCount
            && parts[^1].Length > 0
            && int.TryParse(parts[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var revision)
            && revision == 0)
        {
            dotted = string.Join('.', parts.Take(ResourceComponentCount - 1));
        }

        return dotted;
    }

    /// <summary>
    /// Whether two versions are the same, compared component by component.
    /// </summary>
    /// <remarks>
    /// Compared as numbers rather than as text, because "3.8" and "3.8.0" are the same version and
    /// "3.8" and "3.80" are not the same string. Used when deciding whether a catalog entry already
    /// exists, and by the planner to recognise a version a game already runs. Missing components count
    /// as zero, which is also how ordering treats them, so the two never disagree about equality.
    /// </remarks>
    public static bool AreEquivalent(string? left, string? right)
    {
        var a = Components(left);
        var b = Components(right);

        if (a.Length == 0 || b.Length == 0)
        {
            // One side is unknown. Two unknowns are not evidence that they match.
            return false;
        }

        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var av = i < a.Length ? a[i] : 0;
            var bv = i < b.Length ? b[i] : 0;

            if (av != bv)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether <paramref name="left"/> is an older version than <paramref name="right"/>.</summary>
    /// <remarks>False when either is unknown: an unknown version is never guessed to be older.</remarks>
    public static bool IsOlder(string? left, string? right)
    {
        var a = Components(left);
        var b = Components(right);

        if (a.Length == 0 || b.Length == 0)
        {
            return false;
        }

        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var av = i < a.Length ? a[i] : 0;
            var bv = i < b.Length ? b[i] : 0;

            if (av != bv)
            {
                return av < bv;
            }
        }

        return false;
    }

    /// <summary>The numeric components of a version, with anything unparseable treated as zero.</summary>
    private static int[] Components(string? version)
    {
        var normalized = Normalize(version);

        if (normalized is null)
        {
            return [];
        }

        return normalized
            .Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => int.TryParse(
                part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0)
            .ToArray();
    }

    /// <summary>
    /// Reads and normalises the version a file's own resources declare.
    /// </summary>
    /// <remarks>
    /// One place to do this, because the scanner, the catalog and the importer all need it and a
    /// file that was read one way in one of them and another way in another would produce a catalog
    /// entry the swap engine could not match.
    ///
    /// Returns false when the file could not be read at all, and true with a null version when it was
    /// read but declares none. That difference matters: R59 requires "could not be read" and "there
    /// is nothing here" to be reported differently.
    /// </remarks>
    public static bool TryRead(
        string path,
        out string? version,
        out string? productVersion,
        out string? error)
    {
        version = null;
        productVersion = null;
        error = null;

        try
        {
            var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);

            version = Normalize(info.FileVersion);
            productVersion = Normalize(info.ProductVersion);

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or System.ComponentModel.Win32Exception)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }
}
