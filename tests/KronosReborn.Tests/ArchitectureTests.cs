using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using KronosReborn.Data.Libraries;
using KronosReborn.Data.Runtimes;
using Xunit;

namespace KronosReborn.Tests;

/// <summary>
/// Checks on the shape of the codebase itself.
/// </summary>
/// <remarks>
/// <para>
/// Most tests here read the repository's own source rather than calling into it. That sounds unusual
/// and it is deliberate: the bugs worth guarding against now are not wrong answers from a function
/// but whole categories of mistake that a behavioural test cannot see, because the behaviour looks
/// fine in isolation.
/// </para>
/// <para>
/// The example that motivated this file is the multi-DLL swap. The planner handled a runtime of one
/// DLL perfectly and quietly did half the work on a runtime of two. No test that happened to pick
/// DLSS would ever have noticed. What catches it is the rule below: every runtime kind must have
/// files, and every kind must be exercised by the swap tests, so a new kind cannot be added without
/// the same question being asked about it.
/// </para>
/// <para>
/// These tests locate the repository by walking up from the test binary. If they cannot find it they
/// fail loudly rather than passing vacuously — a guard that silently checks nothing is worse than no
/// guard, because it looks like coverage.
/// </para>
/// </remarks>
public class ArchitectureTests
{
    // ------------------------------------------------------------ Repository location

    [Fact]
    public void TheRepositoryCanBeFoundFromTheTestBinary()
    {
        // Everything else in this file depends on this. If it fails, the rest would pass for the
        // wrong reason, so it is asserted on its own.
        Assert.True(File.Exists(Path.Combine(RepositoryRoot, "KronosReborn.sln")));
    }

    private static string RepositoryRoot { get; } = FindRepositoryRoot();

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "KronosReborn.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Could not find KronosReborn.sln above {AppContext.BaseDirectory}. " +
            "These tests read the repository's own source and cannot run without it.");
    }

    private static IEnumerable<string> SourceFiles(string relativeDirectory) =>
        Directory.EnumerateFiles(
                Path.Combine(RepositoryRoot, relativeDirectory),
                "*.cs",
                SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    // ------------------------------------------------------------ Runtime kinds

    [Fact]
    public void EveryRuntimeKindHasAtLeastOneFile()
    {
        // A kind with no files would be offered to the user, produce a plan, and write nothing.
        foreach (var kind in Enum.GetValues<RuntimeKind>())
        {
            Assert.True(
                RuntimeDlls.ByKind.TryGetValue(kind, out var files) && files.Length > 0,
                $"{kind} has no files in RuntimeDlls.ByKind.");
        }
    }

    [Fact]
    public void EveryRuntimeKindAppearsInTheReverseLookup()
    {
        // The scanner and the planner use the forward mapping and the catalog the reverse one. A kind
        // missing from the reverse mapping is detected but never classified.
        foreach (var kind in Enum.GetValues<RuntimeKind>())
        {
            foreach (var fileName in RuntimeDlls.ByKind[kind])
            {
                Assert.True(
                    RuntimeDlls.ByFileName.TryGetValue(fileName, out var found) && found == kind,
                    $"{fileName} ({kind}) is not in RuntimeDlls.ByFileName as {kind}.");
            }
        }
    }

    [Fact]
    public void NoFileBelongsToTwoRuntimeKinds()
    {
        // A DLL claimed by two kinds would be reported under whichever was asked for first, and a
        // swap of one kind would silently overwrite the other's file.
        var seen = new Dictionary<string, RuntimeKind>(StringComparer.OrdinalIgnoreCase);

        foreach (var (kind, files) in RuntimeDlls.ByKind)
        {
            foreach (var fileName in files)
            {
                Assert.False(
                    seen.TryGetValue(fileName, out var owner),
                    $"{fileName} is listed under both {owner} and {kind}.");

                seen[fileName] = kind;
            }
        }
    }

    [Fact]
    public void EveryRuntimeKindIsExercisedByTheSwapTests()
    {
        // This is the guard for the class of bug the multi-DLL swap was. A runtime kind is a unit
        // that a swap must treat whole, so adding one without adding it to the theories means the
        // next person repeats the mistake.
        var testedKinds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in SourceFiles("tests"))
        {
            foreach (Match match in Regex.Matches(
                         File.ReadAllText(file), @"InlineData\(RuntimeKind\.(\w+)\)"))
            {
                testedKinds.Add(match.Groups[1].Value);
            }
        }

        foreach (var kind in Enum.GetValues<RuntimeKind>())
        {
            Assert.True(
                testedKinds.Contains(kind.ToString()),
                $"{kind} is not covered by any [InlineData(RuntimeKind.{kind})] test. " +
                "Every kind is a unit a swap must handle whole, so it needs the same tests as the rest.");
        }
    }

    // ------------------------------------------------------------ Global state

    [Fact]
    public void OnlyDataPathsBuildsTheProgramsOwnDataDirectory()
    {
        // The data directory used to be a static class reading LocalApplicationData, which meant the
        // test suite wrote history into the developer's own profile and read it back on later runs.
        // Injecting it fixed that, and this keeps it fixed.
        //
        // The rule is deliberately narrow. Reading LocalApplicationData is legitimate — the Steam
        // locator does exactly that to find where Steam is installed — so what is forbidden is
        // assembling *this program's* data directory anywhere but in DataPaths, which is what made
        // it a hidden global.
        var offenders = new List<string>();

        foreach (var file in SourceFiles("src"))
        {
            if (Path.GetFileName(file).Equals("DataPaths.cs", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (File.ReadAllText(file).Contains("DataPaths.AppFolderName", StringComparison.Ordinal))
            {
                offenders.Add(Path.GetRelativePath(RepositoryRoot, file));
            }
        }

        Assert.True(
            offenders.Count == 0,
            "These files assemble the program's own data directory. Use DataPaths instead:" +
            Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void NoTestWritesToTheRealDataDirectory()
    {
        // The old suite constructed `new PersistenceService()` with no argument, which quietly
        // defaulted to the current user's real profile and left history files there for the next run
        // to read.
        //
        // Only that one shape is forbidden. Creating a directory under a temporary path is exactly
        // what a well-behaved test does, and inspecting where ForCurrentUser points is harmless, so
        // a broader rule would flag correct tests and train everyone to ignore it. A parameterless
        // PersistenceService in a test, though, has no reading other than "use my real profile".
        var offenders = new List<string>();

        foreach (var file in SourceFiles("tests"))
        {
            // This file necessarily contains the text it searches for, in both the rule and its
            // explanation, so it is skipped rather than reported against itself.
            if (Path.GetFileName(file).Equals(
                    "ArchitectureTests.cs", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (File.ReadAllText(file).Contains("new PersistenceService()", StringComparison.Ordinal))
            {
                offenders.Add(Path.GetRelativePath(RepositoryRoot, file));
            }
        }

        Assert.True(
            offenders.Count == 0,
            "These tests construct a PersistenceService with no data path, which means the real user " +
            "profile. Pass a DataPaths built over a temporary directory:" + Environment.NewLine +
            string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void OnlyThePersistenceTestsMayLookAtTheUsersOwnDataDirectory()
    {
        // The one test allowed to name LocalApplicationData is the one asserting where the program's
        // data directory is, which can only be answered by asking. Reading the folder a second time
        // anywhere else is how a test ends up creating it, and how a debugging probe leaves real files
        // in the machine's profile.
        var offenders = new List<string>();

        foreach (var file in SourceFiles("tests"))
        {
            var name = Path.GetFileName(file);

            if (name.Equals("ArchitectureTests.cs", StringComparison.OrdinalIgnoreCase)
                || name.Equals("PersistenceTests.cs", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (File.ReadAllText(file).Contains(
                    "SpecialFolder.LocalApplicationData", StringComparison.Ordinal))
            {
                offenders.Add(Path.GetRelativePath(RepositoryRoot, file));
            }
        }

        Assert.True(
            offenders.Count == 0,
            "These tests read the user's local application data directory. Use a temporary directory:" +
            Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void EveryVisibilityBindingInTheWindowCanProduceAVisibility()
    {
        // This is the guard for a failure nothing else could see. The empty-state panel was bound to
        // Games.Count through a converter that returned a bool; WPF cannot turn a bool into a
        // Visibility, so the binding was discarded and the panel kept its default of Visible. It sat
        // over a fully populated list for ever, showing "No games found" over seven found games. The
        // status bar said the right thing the whole time, which is what made it look like discovery
        // was broken rather than the window.
        //
        // So: every Visibility binding must go through one of the two converters that return a
        // Visibility, and its source must be a boolean property rather than a count or a string.
        var xaml = File.ReadAllText(Path.Combine(RepositoryRoot, "src", "KronosReborn", "MainWindow.xaml"));

        var converters = new[] { "InverseBool", "BoolToVisibility" };
        var offenders = new List<string>();
        var bindings = new List<Match>();

        foreach (Match match in Regex.Matches(
                     xaml, @"Visibility=""\{Binding ([^,}]+)[^}]*Converter=\{StaticResource ([^}]+)\}\}"""))
        {
            bindings.Add(match);

            var source = match.Groups[1].Value.Trim();
            var converter = match.Groups[2].Value.Trim();

            if (!converters.Contains(converter, StringComparer.Ordinal))
            {
                offenders.Add($"'{source}' uses converter '{converter}', which does not return a Visibility");
                continue;
            }

            if (source.Contains('.', StringComparison.Ordinal))
            {
                offenders.Add(
                    $"'{source}' is a member access, not a boolean property; a count or a string " +
                    "cannot produce a Visibility");
            }
        }

        // Without this the test would pass if the pattern stopped matching the markup at all, which is
        // how a guard quietly becomes decoration. It failed exactly that way the first time.
        Assert.True(
            bindings.Count >= 6,
            $"Only {bindings.Count} visibility bindings were recognised in MainWindow.xaml. " +
            "The pattern itself has probably stopped matching, which would make this test check nothing.");

        Assert.True(
            offenders.Count == 0,
            "These visibility bindings cannot work:" + Environment.NewLine +
            string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void TheVisibilityConvertersReturnAVisibilityRatherThanABoolean()
    {
        // The other half of the same guard, at the source. Checking only the markup would miss a
        // converter that is used correctly by markup and still returns the wrong type.
        var converters = File.ReadAllText(
            Path.Combine(RepositoryRoot, "src", "KronosReborn", "UI", "Converters.cs"));

        foreach (var name in new[] { "BooleanToVisibilityConverter", "InverseBooleanConverter" })
        {
            Assert.True(
                converters.Contains(name, StringComparison.Ordinal),
                $"{name} is missing from Converters.cs.");

            Assert.True(
                converters.Contains("Visibility.Visible", StringComparison.Ordinal)
                && converters.Contains("Visibility.Collapsed", StringComparison.Ordinal),
                $"{name} does not appear to return a Visibility.");
        }
    }

    [Fact]
    public void TheGameListIsBoundDirectlyRatherThanThroughAResource()
    {
        // A CollectionViewSource declared in a resource dictionary is a Freezable and does not
        // reliably inherit the DataContext, which produces a view over nothing while the bound
        // collection is full. Binding the list to the collection and grouping its default view keeps
        // the data path explicit.
        var xaml = File.ReadAllText(Path.Combine(RepositoryRoot, "src", "KronosReborn", "MainWindow.xaml"));

        var listView = Regex.Match(xaml, @"<ListView\b.*?>", RegexOptions.Singleline).Value;

        Assert.Contains("ItemsSource=\"{Binding Games", listView, StringComparison.Ordinal);
        Assert.DoesNotContain("StaticResource GamesViewSource", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAbsentLibraryIsNotReportedAsAFailure()
    {
        // R59: "this could not be read" and "there is nothing in here" are different problems. A
        // leftover Steam folder with no steamapps in it is the second, and reporting the first tells
        // the user something false about a folder that is not a problem at all.
        using var temp = new TemporaryDirectory();

        var library = Path.Combine(temp.Root, "AbandonedSteam");
        Directory.CreateDirectory(library);

        var result = new SteamLibraryScanner().Scan(library, TestContext.Current.CancellationToken);

        Assert.Empty(result.Games);
        Assert.Empty(result.Problems);
    }

    [Fact]
    public void ALibraryThatCannotBeReadIsStillReported()
    {
        // The counterpart, so the fix above cannot be read as "report nothing ever".
        using var temp = new TemporaryDirectory();

        var steamRoot = Path.Combine(temp.Root, "Steam");
        var apps = Path.Combine(steamRoot, "steamapps");
        Directory.CreateDirectory(apps);

        // A library listed by Steam but not on this machine is worth telling the user about, because
        // it is what an unplugged drive looks like.
        File.WriteAllText(
            Path.Combine(apps, "libraryfolders.vdf"),
            "\"libraryfolders\"\n{\n    \"0\"\n    {\n        \"path\" \"" +
            Path.Combine(temp.Root, "NotConnected").Replace("\\", "\\\\") + "\"\n    }\n}\n");

        var result = new SteamLibraryScanner().Scan(steamRoot, TestContext.Current.CancellationToken);

        Assert.Contains(result.Problems, p => p.Summary.Contains("not present", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void VersionsAreNormalisedWhereTheyAreRead()
    {
        // NVIDIA writes version resources with commas, and the raw text was being used directly. That
        // showed in the interface as "310,9,1" and, worse, broke comparison: splitting on dots left one
        // unparseable token that became zero, so every DLSS version compared equal to every other and
        // the downgrade warning could never fire. Normalisation is done where the version is read so
        // that everything downstream sees one form.
        var readers = new[]
        {
            Path.Combine("src", "KronosReborn", "Data", "Runtimes", "GameRuntimeScanner.cs"),
            Path.Combine("src", "KronosReborn", "Data", "Runtimes", "RuntimeCatalog.cs"),
        };

        foreach (var relative in readers)
        {
            var text = File.ReadAllText(Path.Combine(RepositoryRoot, relative));

            Assert.True(
                text.Contains("VersionNumber.Normalize", StringComparison.Ordinal),
                $"{relative} reads a version without normalising it. " +
                "A comma-separated version then compares as zero.");
        }
    }

    // ------------------------------------------------------------ Specification

    [Fact]
    public void EveryRequirementCitedInCodeExistsInTheSpecification()
    {
        // Requirements are cited in comments throughout the source. A citation that does not exist is
        // a comment claiming the code satisfies something nobody wrote down, which is worse than no
        // citation because it cannot be checked.
        var specPath = Path.Combine(RepositoryRoot, "docs", "spec.md");
        var spec = File.ReadAllText(specPath);

        var defined = new HashSet<string>(
            Regex.Matches(spec, @"\bR(\d+)\b").Select(m => m.Groups[1].Value),
            StringComparer.Ordinal);

        Assert.True(defined.Count > 0, "No requirement ids found in the specification.");

        var unknown = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var file in SourceFiles("src").Concat(SourceFiles("tests")))
        {
            foreach (Match match in Regex.Matches(File.ReadAllText(file), @"\bR(\d+)\b"))
            {
                var id = match.Groups[1].Value;
                if (!defined.Contains(id))
                {
                    unknown.Add($"{id} ({Path.GetRelativePath(RepositoryRoot, file)})");
                }
            }
        }

        Assert.True(
            unknown.Count == 0,
            "Code cites requirements that the specification does not define:" +
            Environment.NewLine + string.Join(Environment.NewLine, unknown));
    }

    // ------------------------------------------------------------ Swap engine reachability

    [Fact]
    public void TheSwapEngineIsReachableFromTheUserInterface()
    {
        // The swap, undo and batch engines were written, tested and never called: the window bound
        // only a refresh, a group toggle and a theme toggle, so the program could list games and read
        // versions but could not change anything. Nothing about the engine's own tests would have
        // shown that, because they call it directly.
        var viewModel = File.ReadAllText(
            Path.Combine(RepositoryRoot, "src", "KronosReborn", "UI", "ViewModels.cs"));

        foreach (var type in new[] { "SwapExecutor", "UndoEngine", "SwapPlanner" })
        {
            Assert.True(
                viewModel.Contains(type, StringComparison.Ordinal),
                $"{type} is never used by the view model, so the program cannot perform a swap. " +
                "An engine that nothing can reach is the same as no engine at all.");
        }
    }

    [Fact]
    public void EveryCommandTheViewModelExposesIsBoundInTheWindow()
    {
        // The reverse direction of the same problem: a command that exists but is never bound is a
        // feature that cannot be reached. The group toggle was bound, worked, and did nothing to the
        // grouping, which is the same class of mistake.
        var viewModel = File.ReadAllText(
            Path.Combine(RepositoryRoot, "src", "KronosReborn", "UI", "ViewModels.cs"));

        var window = File.ReadAllText(
            Path.Combine(RepositoryRoot, "src", "KronosReborn", "MainWindow.xaml"));

        var exposed = Regex.Matches(viewModel, @"public ICommand (\w+)Command \{ get; \}")
            .Select(m => m.Groups[1].Value + "Command")
            .ToArray();

        Assert.True(exposed.Length > 0, "No commands found on the view model.");

        foreach (var command in exposed)
        {
            Assert.True(
                window.Contains(command, StringComparison.Ordinal),
                $"{command} exists on the view model but is not bound in MainWindow.xaml, " +
                "so it cannot be reached by the user.");
        }
    }
}
