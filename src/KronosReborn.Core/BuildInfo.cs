using System;
using System.Globalization;
using System.Reflection;

namespace KronosReborn;

/// <summary>
/// Build metadata, injected at compile time by the AddBuildMetadata target in
/// Directory.Build.props and read back here at startup.
/// </summary>
/// <remarks>
/// Two things make this worth having from the first commit rather than later.
///
/// The first is that an unset timestamp is indistinguishable from a real one once formatted.
/// <c>DateTimeOffset.FromUnixTimeSeconds(0)</c> is the epoch, so a build that recorded nothing used
/// to display "1 January 1970" and read as a plausible build date rather than as missing data. Every
/// value here is therefore nullable, and absence is shown as absence.
///
/// The second is that two builds of the same version are otherwise indistinguishable. When a user
/// reports a problem, "1.2.3" tells you nothing about which binary they are running; a commit hash
/// does. <see cref="GitCommitShort"/> exists for that.
///
/// Branch and tag are absent unless the packaging script passes them, because MSBuild cannot run git
/// while evaluating properties. That is the normal case for a plain build and is not an error.
/// </remarks>
internal static class BuildInfo
{
    const string KeyPrefix = "KronosReborn";

    static string? ReadMetadata(string name)
    {
        var value = typeof(BuildInfo).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(x => string.Equals(x.Key, KeyPrefix + name, StringComparison.Ordinal))?
            .Value;

        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>Branch the build came from, or null when git was not available.</summary>
    public static string? GitBranch { get; } = ReadMetadata("GitBranch");

    /// <summary>Full commit hash the build came from, or null.</summary>
    public static string? GitCommit { get; } = ReadMetadata("GitCommit");

    /// <summary>Tag the build came from, or null.</summary>
    public static string? GitTag { get; } = ReadMetadata("GitTag");

    /// <summary>When the build was made, or null when the build recorded no timestamp.</summary>
    public static DateTimeOffset? BuildTimestamp { get; } = ParseTimestamp(ReadMetadata("BuildTimestamp"));

    /// <summary>Whether a timestamp was recorded at all, as opposed to being absent.</summary>
    public static bool HasBuildTimestamp => BuildTimestamp.HasValue;

    /// <summary>
    /// First seven characters of the commit hash, or null.
    /// </summary>
    /// <remarks>
    /// Checked by length rather than assumed. The packaging scripts substitute the literal string
    /// "unknown" when git is unavailable, and slicing that to seven characters would look like a
    /// plausible hash rather than a missing one.
    /// </remarks>
    public static string? GitCommitShort
    {
        get
        {
            if (string.IsNullOrWhiteSpace(GitCommit) || GitCommit.Length < 7)
            {
                return null;
            }

            return GitCommit[..7];
        }
    }

    /// <summary>Build time in local time, or null when the build recorded none.</summary>
    public static DateTime? BuildDateTime => BuildTimestamp?.LocalDateTime;

    /// <summary>
    /// Parses an ISO 8601 round-trip timestamp, returning null for anything unparseable.
    /// </summary>
    /// <remarks>
    /// RoundtripKind is required so the offset in the string is honoured rather than assumed to be
    /// local time, which would report the wrong day for anything built west of UTC.
    ///
    /// Unparseable input is treated as absent rather than as an error: a build that recorded a
    /// timestamp nobody can read is no better off than one that recorded nothing, and throwing here
    /// would take the application down over a field shown in a corner of a window.
    /// </remarks>
    internal static DateTimeOffset? ParseTimestamp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var parsed)
                ? parsed
                : null;
    }

    /// <summary>The informational version, which the SDK builds from Version and SourceRevisionId.</summary>
    public static string InformationalVersion =>
        typeof(BuildInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "unknown";

    /// <summary>
    /// Whether this build came from a tagged commit.
    /// </summary>
    /// <remarks>
    /// Decides whether the UI offers a link to release notes. A build between releases has no tag,
    /// and there is nothing to link to.
    /// </remarks>
    public static bool IsFromTagBuild => string.IsNullOrWhiteSpace(GitTag) == false;
}
