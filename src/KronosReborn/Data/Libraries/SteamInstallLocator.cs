using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace KronosReborn.Data.Libraries;

/// <summary>
/// Where a Steam installation is recorded, abstracted so it can be supplied in tests.
/// </summary>
/// <remarks>
/// Reading the registry is the one part of discovery that cannot be exercised by laying files out in
/// a temporary directory, because it depends on the machine the tests run on. Isolating it behind
/// two questions keeps that dependency out of the tests instead of making them conditional on the
/// machine having Steam installed.
/// </remarks>
public interface ISteamRegistry
{
    /// <summary>
    /// The installation path recorded for the current user, or null if there is none.
    /// </summary>
    string? GetUserInstallPath();

    /// <summary>
    /// The installation path recorded for the whole machine, or null if there is none.
    /// </summary>
    string? GetMachineInstallPath();
}

/// <summary>
/// Reads the real registry.
/// </summary>
public sealed class WindowsSteamRegistry : ISteamRegistry
{
    /// <summary>Where a per-user installation records itself.</summary>
    private const string UserKeyPath = @"Software\Valve\Steam";

    /// <summary>Where a machine-wide installation records itself.</summary>
    private const string MachineKeyPath = @"SOFTWARE\WOW6432Node\Valve\Steam";

    /// <summary>The value a per-user installation writes.</summary>
    private const string UserValueName = "SteamPath";

    /// <summary>The value a machine-wide installation writes.</summary>
    private const string MachineValueName = "InstallPath";

    public string? GetUserInstallPath() => Read(Registry.CurrentUser, UserKeyPath, UserValueName);

    public string? GetMachineInstallPath() => Read(Registry.LocalMachine, MachineKeyPath, MachineValueName);

    /// <summary>
    /// Reads one value, treating every failure as "not recorded".
    /// </summary>
    /// <remarks>
    /// A missing key, a missing value, a value of the wrong type and permission denied all mean the
    /// same thing here: this is not where Steam says it lives. Nothing is gained by distinguishing
    /// them, because every case falls through to the same next place to look.
    /// </remarks>
    private static string? Read(RegistryKey hive, string keyPath, string valueName)
    {
        try
        {
            using var key = hive.OpenSubKey(keyPath);

            if (key?.GetValue(valueName) is not string value)
            {
                return null;
            }

            var trimmed = value.Trim();

            return trimmed.Length == 0 ? null : trimmed;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}

/// <summary>
/// The Steam installations this machine reports.
/// </summary>
/// <param name="InstallRoots">Installation folders found, best guess first.</param>
/// <param name="Problems">Anything that went wrong while looking, which may be empty.</param>
public sealed record SteamDiscoveryResult(IReadOnlyList<string> InstallRoots, IReadOnlyList<ScanProblem> Problems);

/// <summary>
/// Finds where Steam is installed.
/// </summary>
/// <remarks>
/// Three places, in order of how much they can be trusted. The registry is authoritative where it
/// exists, because it is Steam recording where it put itself and it survives the folder being
/// renamed. The conventional folders are a last resort, checked because a portable or hand installed
/// copy is not registered and is otherwise invisible.
/// </remarks>
public sealed class SteamInstallLocator(ISteamRegistry registry)
{
    private readonly ISteamRegistry _registry = registry ?? throw new ArgumentNullException(nameof(registry));

    /// <summary>
    /// Looks for Steam installations.
    /// </summary>
    /// <remarks>
    /// Findings are deduplicated by path but a location found twice is only reported as a problem
    /// when it turned out not to exist, since two records of the same real installation is not
    /// something the user needs told about.
    /// </remarks>
    public SteamDiscoveryResult Discover()
    {
        var problems = new List<ScanProblem>();
        var found = new List<string>();

        Add(found, _registry.GetUserInstallPath());
        Add(found, _registry.GetMachineInstallPath());

        foreach (var candidate in ConventionalFolders())
        {
            if (!found.Any(existing => Same(existing, candidate)))
            {
                Add(found, candidate);
            }
        }

        if (found.Count == 0)
        {
            problems.Add(new ScanProblem(
                "Steam",
                null,
                "No Steam installation was found, so no games could be searched for.",
                "Neither the registry nor the usual installation folders held one.",
                "If Steam is installed somewhere unusual, or has not been started since it was installed, add the folder as a library by hand."));
        }

        return new SteamDiscoveryResult(found, problems);
    }

    /// <summary>
    /// Adds a candidate if it looks like a path and is not already known.
    /// </summary>
    /// <remarks>
    /// Registry values are not trusted to be well formed. A path that does not exist is kept rather
    /// than discarded, so that the user is told Steam is recorded somewhere it is not, which is a
    /// real and fixable problem. Only an empty or whitespace value is dropped, because there is
    /// nothing to report about that.
    /// </remarks>
    private static void Add(List<string> found, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return;
        }

        var trimmed = candidate.Trim();

        if (found.Any(existing => Same(existing, trimmed)))
        {
            return;
        }

        found.Add(trimmed);
    }

    /// <summary>The folders Steam installs into when left to its own devices.</summary>
    /// <remarks>
    /// Only folders that are actually there are yielded. These are guesses rather than records, and
    /// a guess about a folder that does not exist contributes nothing except a problem saying it
    /// could not be read, so each one added noise to the problems list and a scan with nothing to
    /// do. A path the registry records is treated differently, because that is Steam stating a fact
    /// rather than this program speculating.
    /// </remarks>
    private static IReadOnlyList<string> ConventionalFolders()
    {
        var candidates = new List<string>();

        foreach (var root in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
        })
        {
            if (!string.IsNullOrWhiteSpace(root))
            {
                candidates.Add(Path.Combine(root, "Steam"));
            }
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            candidates.Add(Path.Combine(localAppData, "Steam"));
        }

        // Steam Deck / Linux-style locations.
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady))
        {
            candidates.Add(Path.Combine(drive.RootDirectory.FullName, "SteamLibrary"));
            candidates.Add(Path.Combine(drive.RootDirectory.FullName, "Steam"));
        }

        // Derive the folder from the executable, for an install the guesses missed.
        var steamExe = FindSteamExecutable();
        if (steamExe != null && Path.GetDirectoryName(steamExe) is { } folder)
        {
            candidates.Add(folder);
        }

        // Keep only the folders that are really there. See the remarks above.
        return candidates.Where(PathNormalizer.DirectoryExists).ToList();
    }

    /// <summary>The path if it names a folder that is there, otherwise nothing.</summary>

    private static string? FindSteamExecutable()
    {
        // Check registry for SteamExe
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (key?.GetValue("SteamExe") is string exePath && !string.IsNullOrWhiteSpace(exePath))
            {
                return exePath;
            }
        }
        catch { }

        // Check common locations
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam", "steam.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam", "steam.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Steam", "steam.exe"),
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private static bool Same(string a, string b) =>
        string.Equals(PathNormalizer.Normalize(a), PathNormalizer.Normalize(b), StringComparison.OrdinalIgnoreCase);
}
