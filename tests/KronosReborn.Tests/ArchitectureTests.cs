using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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