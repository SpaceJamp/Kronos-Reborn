#if WINDOWS
using System;
using System.Globalization;
using KronosReborn;
using Xunit;

namespace KronosReborn.Tests;

/// <summary>
/// Guards the build metadata parsing.
/// </summary>
/// <remarks>
/// This is the first code in the project, so it is worth being explicit about why it is here at all.
///
/// BuildInfo exists so a bug report can be tied to a specific binary. "Version 0.1.0" identifies
/// nothing on its own once more than one build exists; a commit hash does. That only works if the
/// metadata is actually present and parsed correctly, and there is no way to check that by running the
/// application and looking at a window.
///
/// The rule that matters most here is that an absent timestamp must stay absent. Converting an unset
/// value gives the Unix epoch, and 1 January 1970 formats as a perfectly plausible build date, so the
/// failure mode is a lie rather than a blank.
/// </remarks>
public class BuildInfoTests
{
    [Fact]
    public void ParsesTheRoundTripFormatTheBuildWrites()
    {
        // Exactly what $[System.DateTime]::UtcNow.ToString("o") produces.
        var parsed = BuildInfo.ParseTimestamp("2026-09-30T10:15:30.1234567Z");

        Assert.NotNull(parsed);
        Assert.Equal(2026, parsed!.Value.Year);
        Assert.Equal(9, parsed.Value.Month);
        Assert.Equal(30, parsed.Value.Day);
        Assert.Equal(TimeSpan.Zero, parsed.Value.Offset);
    }

    [Fact]
    public void TreatsMissingMetadataAsAbsentRatherThanAsTheEpoch()
    {
        // The bug this guards. Any of these returning a value at all means a build with no recorded
        // date renders as 1 January 1970 and looks real.
        Assert.Null(BuildInfo.ParseTimestamp(null));
        Assert.Null(BuildInfo.ParseTimestamp(string.Empty));
        Assert.Null(BuildInfo.ParseTimestamp("   "));
    }

    [Fact]
    public void TreatsUnparseableMetadataAsAbsentRatherThanThrowing()
    {
        // A build that wrote a date nobody can read is no better off than one that wrote nothing,
        // and this is read during startup. Throwing would take the application down over a field
        // shown in the corner of a window.
        Assert.Null(BuildInfo.ParseTimestamp("not a date"));
        Assert.Null(BuildInfo.ParseTimestamp("1759301530"));
    }

    [Fact]
    public void HonoursTheUtcOffsetRatherThanAssumingLocalTime()
    {
        // Same instant, written two ways. If the offset were ignored, every build west of UTC would
        // report a different day than it actually happened on.
        var withOffset = BuildInfo.ParseTimestamp("2026-09-30T23:30:00.0000000+05:00");
        var inZulu = BuildInfo.ParseTimestamp("2026-09-30T18:30:00.0000000Z");

        Assert.NotNull(withOffset);
        Assert.NotNull(inZulu);
        Assert.Equal(inZulu!.Value.ToUnixTimeSeconds(), withOffset!.Value.ToUnixTimeSeconds());
    }

    [Fact]
    public void TheRunningBuildRecordedATimestamp()
    {
        // Proves the injection target in Directory.Build.props actually fires. If it stopped matching,
        // this fails rather than the failure being discovered in a bug report months later.
        Assert.True(BuildInfo.HasBuildTimestamp, "No build timestamp was injected into this assembly.");
        Assert.NotNull(BuildInfo.BuildDateTime);
    }

    [Fact]
    public void InformationalVersionIsNotUnknown()
    {
        Assert.NotEqual("unknown", BuildInfo.InformationalVersion);
    }
}
#endif
