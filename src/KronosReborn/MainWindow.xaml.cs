using System.Windows;

namespace KronosReborn;

/// <summary>
/// The main window.
/// </summary>
/// <remarks>
/// Scaffolding. It exists so the build has something launchable, and so the metadata injection can be
/// seen working without opening a log file.
///
/// The two bound strings are the only behaviour here, and they are deliberately bound rather than
/// assigned in the constructor: a data-bound value is what a real view model will replace, so the
/// window is already shaped for that instead of being rewritten later.
/// </remarks>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // DataContext rather than a RelativeSource binding. It is the shape a view model will slot
        // into later, so the window does not have to be restructured when there is something to bind.
        DataContext = this;

        VersionText = BuildInfo.InformationalVersion;
        BuildText = DescribeBuild();
    }

    /// <summary>Version and commit, as shown under the title.</summary>
    public string VersionText { get; }

    /// <summary>
    /// Build date and branch, or a statement that there was none.
    /// </summary>
    /// <remarks>
    /// Says "unknown" rather than leaving the line out or printing a date that was never recorded.
    /// Both alternatives read as though the build carried information it did not.
    /// </remarks>
    public string BuildText { get; }

    string DescribeBuild()
    {
        var commit = BuildInfo.GitCommitShort ?? "unknown";
        var branch = BuildInfo.GitBranch;

        if (BuildInfo.HasBuildTimestamp == false)
        {
            return $"Build {commit}{(branch is null ? string.Empty : $", {branch}")}, date unknown";
        }

        var date = BuildInfo.BuildDateTime!.Value.ToString("g", System.Globalization.CultureInfo.CurrentCulture);

        return $"Build {commit}, {date}{(branch is null ? string.Empty : $", {branch}")}";
    }
}
