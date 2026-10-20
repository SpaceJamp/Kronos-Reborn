using System;
using System.Collections.Generic;
using System.IO;
#if WINDOWS
using Microsoft.Win32;
#endif

namespace KronosReborn.Data.Libraries;

/// <summary>
/// Where a GOG Galaxy installation is recorded, abstracted so it can be supplied in tests.
/// </summary>
public interface IGOGRegistry
{
    /// <summary>The installation path recorded for the current user, or null if there is none.</summary>
    string? GetUserInstallPath();

    /// <summary>The installation path recorded for the whole machine, or null if there is none.</summary>
    string? GetMachineInstallPath();
}

#if WINDOWS
/// <summary>
/// Reads GOG Galaxy paths from the real registry.
/// </summary>
public sealed class WindowsGOGRegistry : IGOGRegistry
{
    private const string UserKeyPath = @"SOFTWARE\GOG.com\GalaxyClient";
    private const string MachineKeyPath = @"SOFTWARE\WOW6432Node\GOG.com\GalaxyClient";
    private const string ValueName = "InstallPath";

    public string? GetUserInstallPath() => Read(Registry.CurrentUser, UserKeyPath, ValueName);
    public string? GetMachineInstallPath() => Read(Registry.LocalMachine, MachineKeyPath, ValueName);

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
#endif

/// <summary>
/// The GOG Galaxy installations this machine reports.
/// </summary>
/// <param name="InstallRoots">Installation folders found, best guess first.</param>
/// <param name="Problems">Anything that went wrong while looking.</param>
public sealed record GOGDiscoveryResult(IReadOnlyList<string> InstallRoots, IReadOnlyList<ScanProblem> Problems);

/// <summary>
/// Finds where GOG Galaxy is installed.
/// </summary>
/// <remarks>
/// The registry is authoritative where it exists. Conventional folders are checked as a fallback
/// because a portable or hand-installed copy is not registered.
/// </remarks>
public sealed class GOGInstallLocator(IGOGRegistry registry)
{
    private readonly IGOGRegistry _registry = registry ?? throw new ArgumentNullException(nameof(registry));

    public GOGDiscoveryResult Discover()
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
                "GOG Galaxy",
                null,
                "No GOG Galaxy installation was found, so no GOG games could be searched for.",
                "Neither the registry nor the usual installation folders held one.",
                "If GOG Galaxy is installed somewhere unusual, add the folder as a library by hand."));
        }

        return new GOGDiscoveryResult(found, problems);
    }

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
                candidates.Add(Path.Combine(root, "GOG Galaxy"));
                candidates.Add(Path.Combine(root, "GOG.com", "Galaxy"));
            }
        }
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            candidates.Add(Path.Combine(localAppData, "GOG.com", "Galaxy"));
        }
        return candidates.Where(PathNormalizer.DirectoryExists).ToList();
    }

    private static bool Same(string a, string b) =>
        string.Equals(PathNormalizer.Normalize(a), PathNormalizer.Normalize(b), StringComparison.OrdinalIgnoreCase);
}
