#if WINDOWS
using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using KronosReborn.Data.Libraries;
using KronosReborn.Data.Runtimes;
using KronosReborn.UI;
using Xunit;

namespace KronosReborn.Tests;

/// <summary>
/// Tests for the value converters the window binds through.
/// </summary>
/// <remarks>
/// These exist because of a failure that no compiler, analyzer or unit test could have caught. The
/// visibility converter returned a <see cref="bool"/> to a <c>Visibility</c> property. WPF does not
/// convert between the two, so the binding produced nothing, was discarded, and the property kept
/// its default of <see cref="Visibility.Visible"/>. The empty-state panel therefore stayed on screen
/// permanently, covering a fully populated list with the message saying nothing had been found.
///
/// The build was green, the suite was green, and the program reported the right number of games in
/// its status bar while showing an empty window.
/// </remarks>
public class ConverterTests
{
    [Theory]
    [InlineData(true, Visibility.Visible)]
    [InlineData(false, Visibility.Collapsed)]
    public void BooleanToVisibilityShowsWhileTrue(bool value, Visibility expected)
    {
        var result = new BooleanToVisibilityConverter().Convert(value, typeof(Visibility), null, null!);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(true, Visibility.Collapsed)]
    [InlineData(false, Visibility.Visible)]
    public void InverseBooleanHidesWhileTrue(bool value, Visibility expected)
    {
        var result = new InverseBooleanConverter().Convert(value, typeof(Visibility), null, null!);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void VisibilityConvertersNeverReturnABoolean()
    {
        // Stated as its own test because that is the specific mistake: a bool cannot become a
        // Visibility, and returning one fails silently rather than loudly.
        foreach (var converter in new System.Windows.Data.IValueConverter[]
                 {
                     new BooleanToVisibilityConverter(),
                     new InverseBooleanConverter(),
                 })
        {
            var result = converter.Convert(true, typeof(Visibility), null, null!);

            Assert.IsAssignableFrom<Visibility>(result);
        }
    }

    [Fact]
    public void VisibilityConvertersTreatNullAsNotTrue()
    {
        // A binding that fails hands the converter null. It must not report "true" and hide the
        // element, which is how a broken binding turns into an invisible control.
        Assert.Equal(
            Visibility.Collapsed,
            new BooleanToVisibilityConverter().Convert(null, typeof(Visibility), null, null!));

        Assert.Equal(
            Visibility.Visible,
            new InverseBooleanConverter().Convert(null, typeof(Visibility), null, null!));
    }

    [Fact]
    public void VisibilityConvertersRoundTripTheOtherWayToo()
    {
        // Both directions work, so a binding that happens to be two-way is not quietly broken.
        var forward = new BooleanToVisibilityConverter();
        Assert.Equal(false, forward.ConvertBack(Visibility.Collapsed, typeof(bool), null, null!));
        Assert.Equal(true, forward.ConvertBack(Visibility.Visible, typeof(bool), null, null!));

        var inverse = new InverseBooleanConverter();
        Assert.Equal(true, inverse.ConvertBack(Visibility.Collapsed, typeof(bool), null, null!));
        Assert.Equal(false, inverse.ConvertBack(Visibility.Visible, typeof(bool), null, null!));
    }

    [Fact]
    public void TheColourConverterRefusesToConvertBackwards()
    {
        // A brush has no meaningful inverse, so this one does throw. Stated so the difference between
        // the converters is deliberate rather than accidental.
        Assert.Throws<NotSupportedException>(() =>
            new StringToBrushConverter().ConvertBack(Brushes.Black, typeof(object), null, null!));
    }
}

/// <summary>
/// Tests for the presence flags a row exposes to the window.
/// </summary>
/// <remarks>
/// The window's frame-generation and ray-reconstruction labels used to bind straight to the version
/// string, which is not a boolean and so could not produce a Visibility. The flags give the markup
/// something it can actually bind.
/// </remarks>
public class GameRowTests
{
    [Fact]
    public void PresenceFlagsFollowTheSnapshot()
    {
        var record = new GameRecord("Steam", "1", "Game", @"C:\Game", true);

        var withFrameGeneration = new GameRow(record, Snapshot(
            (RuntimeKind.Dlss, "3.8.0"),
            (RuntimeKind.DlssG, "3.8.0"),
            (RuntimeKind.DlssR, "3.8.0")));

        Assert.True(withFrameGeneration.HasDlssG);
        Assert.True(withFrameGeneration.HasDlssR);

        var without = new GameRow(record, Snapshot((RuntimeKind.Dlss, "3.7.10")));

        Assert.False(without.HasDlssG);
        Assert.False(without.HasDlssR);
    }

    [Fact]
    public void PresenceFlagsAreFalseWhenNothingIsKnown()
    {
        var row = new GameRow(new GameRecord("Steam", "1", "Game", @"C:\Game", true));

        Assert.False(row.HasDlssG);
        Assert.False(row.HasDlssR);
    }

    [Fact]
    public void ARowWithoutAnyRuntimeSaysSo()
    {
        var row = new GameRow(new GameRecord("Steam", "1", "Game", @"C:\Game", true), Snapshot());

        Assert.False(row.HasAnyRuntime);
        Assert.Equal("No runtimes", row.StatusText);
    }

    [Fact]
    public void AMissingGameSaysSoRatherThanReportingNoRuntimes()
    {
        var record = new GameRecord("Steam", "1", "Game", @"C:\Gone", Exists: false);
        var row = new GameRow(record, Snapshot());

        Assert.True(row.IsMissing);
        Assert.Equal("Missing", row.StatusText);
    }

    [Fact]
    public void ChangingTheSnapshotRefreshesThePresenceFlags()
    {
        var row = new GameRow(
            new GameRecord("Steam", "1", "Game", @"C:\Game", true),
            Snapshot());

        var raised = new System.Collections.Generic.List<string?>();
        row.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        row.Snapshot = Snapshot((RuntimeKind.DlssG, "3.8.0"));

        // A flag that never announces itself is a flag the window never hears about.
        Assert.Contains(nameof(GameRow.HasDlssG), raised);
        Assert.True(row.HasDlssG);
    }

    private static GameRuntimeSnapshot Snapshot(params (RuntimeKind Kind, string Version)[] runtimes) =>
        new("1", "Game", @"C:\Game",
            runtimes.Select(r => new DetectedRuntime(
                r.Kind, $"{r.Kind}.dll", $"{r.Kind}.dll", $@"C:\Game\{r.Kind}.dll",
                r.Version, null, true)).ToArray());
}
#endif
