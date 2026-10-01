using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace KronosReborn.Tests;

/// <summary>
/// Guards that the version cannot drift between the places it is declared.
/// </summary>
/// <remarks>
/// The version appears in three places: Directory.Build.props, package/config.cmd, and as a fallback
/// literal in package/Installer.iss.
///
/// Two of the three are only load-bearing some of the time, which is what makes drift possible.
/// package_Installer.cmd passes the real value to ISCC as a parameter, so the literal in the .iss is
/// never exercised during a normal build. And config.cmd drives the output filenames, so a stale
/// value there quietly produces an installer named after an older version.
///
/// Neither failure is a compile error, which is the point of testing it. The version is also bumped
/// often, and each bump is an opportunity for one of these three to be missed.
/// </remarks>
public class VersionConsistencyTests
{
    static string ReadRepoFile(params string[] segments)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && Directory.Exists(Path.Combine(dir.FullName, "src")) == false)
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);

        var path = Path.Combine(dir!.FullName, Path.Combine(segments));
        Assert.True(File.Exists(path), $"Could not find {path}");

        return File.ReadAllText(path);
    }

    /// <summary>Version declared in Directory.Build.props.</summary>
    static string PropsVersion()
    {
        var props = ReadRepoFile("Directory.Build.props");

        var match = Regex.Match(props, @"<Version>([^<]+)</Version>", RegexOptions.None, TimeSpan.FromSeconds(5));
        Assert.True(match.Success, "No <Version> element in Directory.Build.props.");

        return match.Groups[1].Value.Trim();
    }

    /// <summary>Version declared in package/config.cmd.</summary>
    static string ConfigVersion()
    {
        var config = ReadRepoFile("package", "config.cmd");

        var match = Regex.Match(config, @"^set app_version=(\S+)", RegexOptions.Multiline, TimeSpan.FromSeconds(5));
        Assert.True(match.Success, "No app_version assignment in package/config.cmd.");

        return match.Groups[1].Value.Trim();
    }

    /// <summary>Version declared as the fallback literal in package/Installer.iss.</summary>
    static string InstallerFallbackVersion()
    {
        var iss = ReadRepoFile("package", "Installer.iss");

        var match = Regex.Match(iss, @"#define\s+AppVersion\s+""?([^""\s]+)", RegexOptions.None, TimeSpan.FromSeconds(5));
        Assert.True(match.Success, "No AppVersion fallback literal in package/Installer.iss.");

        return match.Groups[1].Value.Trim();
    }

    [Fact]
    public void ConfigAndPropsAgree()
    {
        // The pair that actually matters. config.cmd produces the output filenames, so if this drifts
        // the build still succeeds and just emits the wrong name.
        Assert.Equal(PropsVersion(), ConfigVersion());
    }

    [Fact]
    public void InstallerFallbackAgreesWithTheProject()
    {
        // The .iss literal is only read when ISCC is invoked without /DAppVersion, which the normal
        // build never does. Left unchecked it can sit wrong indefinitely without anyone noticing,
        // until someone runs the script by hand and gets a differently named installer.
        Assert.Equal(PropsVersion(), InstallerFallbackVersion());
    }

    [Fact]
    public void TheVersionLooksLikeAVersion()
    {
        // Catches a half-finished edit such as an empty value or a stray character, which the
        // equality tests above would happily agree on if it happened in all three places at once.
        var version = PropsVersion();

        Assert.Matches(@"^\d+\.\d+\.\d+", version);
    }
}
