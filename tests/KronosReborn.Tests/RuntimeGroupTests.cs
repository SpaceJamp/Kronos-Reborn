using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KronosReborn.Data.Libraries;
using KronosReborn.Data.Runtimes;
using KronosReborn.UI;
using Xunit;

namespace KronosReborn.Tests;

/// <summary>
/// Tests for how the runtime picker is arranged for a selected game.
/// </summary>
/// <remarks>
/// <para>
/// DLSS, DLSS Frame Generation and DLSS Ray Reconstruction are three separate things, and the user
/// makes the choice for each separately. The picker was one flat list of every version of every
/// runtime, so "310.2.1" appeared three times and only the lines around it said which of the three it
/// was — and picking the wrong line installed the wrong DLL. These tests pin down that each runtime
/// gets its own picker listing only its own versions.
/// </para>
/// </remarks>
public class RuntimeGroupTests
{
    [Fact]
    public void EachRuntimeTheGameCarriesGetsItsOwnGroup()
    {
        var groups = Groups(Dlss: true, DlssG: true, DlssR: true);

        Assert.Equal(3, groups.Count);
        Assert.Equal(
            [RuntimeKind.Dlss, RuntimeKind.DlssG, RuntimeKind.DlssR],
            groups.Select(g => g.Kind));
    }

    [Fact]
    public void AGroupListsOnlyItsOwnVersions()
    {
        var groups = Groups(
            versions: [(RuntimeKind.Dlss, "310.2.1"), (RuntimeKind.Dlss, "310.9.1"),
                        (RuntimeKind.DlssG, "310.2.1"), (RuntimeKind.DlssG, "310.9.1")]);

        var dlss = groups.Single(g => g.Kind == RuntimeKind.Dlss);
        var frameGen = groups.Single(g => g.Kind == RuntimeKind.DlssG);

        // The defect this guards: a DLSS version appearing in the Frame Generation picker, or the two
        // groups showing each other's versions. Choosing from the wrong group installs the wrong DLL.
        Assert.Equal(["310.2.1", "310.9.1"], dlss.Options.Select(o => o.Version));
        Assert.Equal(["310.2.1", "310.9.1"], frameGen.Options.Select(o => o.Version));
        Assert.All(dlss.Options, o => Assert.Equal(RuntimeKind.Dlss, o.Kind));
        Assert.All(frameGen.Options, o => Assert.Equal(RuntimeKind.DlssG, o.Kind));
    }

    [Fact]
    public void EachOptionSaysWhichRuntimeItIs()
    {
        var groups = Groups(versions: [(RuntimeKind.Dlss, "310.2.1"), (RuntimeKind.DlssG, "310.2.1")]);

        // Same version string for two different runtimes. The label has to disambiguate them, or the
        // picker shows two identical lines.
        var labels = groups.SelectMany(g => g.Options).Select(o => o.Label).ToList();

        Assert.Equal(labels.Count, labels.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(labels, l => l.Contains("DLSS", StringComparison.Ordinal));
        Assert.Contains(labels, l => l.Contains("Frame Gen", StringComparison.Ordinal));
    }

    [Fact]
    public void ARuntimeTheGameDoesNotCarryIsNotOffered()
    {
        // The game has DLSS and Frame Generation but no Ray Reconstruction. Offering it would install
        // a file the game never had, which is adding runtime support to the game rather than
        // upgrading what it has.
        var groups = Groups(Dlss: true, DlssG: true, DlssR: false);

        Assert.DoesNotContain(groups, g => g.Kind == RuntimeKind.DlssR);
        Assert.Equal(2, groups.Count);
    }

    [Fact]
    public void ARuntimeWithNoVersionsInTheCollectionIsNotOffered()
    {
        // The game carries Frame Generation, but the collection holds no version of it. An empty
        // picker is a control that looks usable and does nothing, so the runtime is left out instead;
        // the Import button is how a version gets added.
        var groups = Groups(versions: [(RuntimeKind.Dlss, "310.9.1")]);

        Assert.DoesNotContain(groups, g => g.Kind == RuntimeKind.DlssG);
        Assert.Equal(RuntimeKind.Dlss, Assert.Single(groups).Kind);
    }

    [Fact]
    public void AGameWithNothingShowableHasNoGroups()
    {
        // Steamworks Common Redistributables and similar carry no runtimes at all.
        Assert.Empty(Groups(Dlss: false, DlssG: false, DlssR: false, Xess: false, Fsr: false));
    }

    [Fact]
    public void EachGroupReportsWhatTheGameRunsNow()
    {
        var groups = Groups(
            currentDlssG: "310.9.1",
            versions: [(RuntimeKind.Dlss, "310.2.1"), (RuntimeKind.DlssG, "310.9.1")]);

        var frameGen = groups.Single(g => g.Kind == RuntimeKind.DlssG);
        var dlss = groups.Single(g => g.Kind == RuntimeKind.Dlss);

        // Shown next to the picker so the user can see what they are changing from without opening
        // anything.
        Assert.Equal("310.9.1", frameGen.CurrentVersion);
        Assert.Equal("310.9.1", frameGen.CurrentText);
        Assert.Equal("310.2.1", dlss.CurrentVersion);
    }

    [Fact]
    public void ARuntimeWithNoCurrentVersionSaysSoRatherThanShowingNothing()
    {
        var groups = Groups(currentDlss: null, versions: [(RuntimeKind.Dlss, "310.9.1")]);

        var dlss = groups.Single(g => g.Kind == RuntimeKind.Dlss);

        Assert.Null(dlss.CurrentVersion);
        Assert.Equal("not present", dlss.CurrentText);
    }

    [Fact]
    public void PickingAVersionShowsWhereItWouldChangeTo()
    {
        var dlss = Groups(currentDlss: "310.2.1", versions: [(RuntimeKind.Dlss, "310.9.1")])
            .Single(g => g.Kind == RuntimeKind.Dlss);

        dlss.Selected = dlss.Options.Single();
        Assert.Equal("310.2.1 → 310.9.1", dlss.StatusText);

        // Clearing the choice shows nothing, rather than leaving the previous comparison on screen.
        dlss.Selected = null;
        Assert.Equal(string.Empty, dlss.StatusText);
    }

    [Fact]
    public void PickingTheVersionAlreadyInstalledSaysSoRatherThanDoingNothing()
    {
        var dlss = Groups(currentDlss: "310.2.1", versions: [(RuntimeKind.Dlss, "310.2.1")])
            .Single(g => g.Kind == RuntimeKind.Dlss);

        dlss.Selected = dlss.Options.Single();

        // The planner skips this entirely, so selecting it would otherwise look like it had worked
        // and changed nothing.
        Assert.Equal("already installed", dlss.StatusText);
    }

    [Fact]
    public void VersionsThatDifferOnlyInSeparatorCountAsTheSameInstalledVersion()
    {
        // NVIDIA writes 310,2,1,0 in some builds and 310.9.1 in others. Both are the same version, so
        // choosing one of them must not be reported as a change.
        var dlss = Groups(currentDlss: "310,2,1,0", versions: [(RuntimeKind.Dlss, "310.2.1")])
            .Single(g => g.Kind == RuntimeKind.Dlss);

        dlss.Selected = dlss.Options.Single();

        Assert.Equal("already installed", dlss.StatusText);
    }

    [Fact]
    public void ChoosingAVersionTellsTheViewModelSo()
    {
        var dlss = Groups(versions: [(RuntimeKind.Dlss, "310.9.1")]).Single(g => g.Kind == RuntimeKind.Dlss);

        var told = 0;
        dlss.OnChanged += (_, _) => told++;

        dlss.Selected = dlss.Options.Single();

        // Without this the picker would change but the preview and Apply would carry on describing
        // the previous choice, which is worse than a picker that did nothing.
        Assert.Equal(1, told);
    }

    [Fact]
    public void GroupsAreOrderedByNameSoTheSameRuntimeIsAlwaysInTheSamePlace()
    {
        var groups = Groups(Fsr: true, Xess: true);

        Assert.Equal(
            groups.Select(g => g.Label).OrderBy(l => l, StringComparer.Ordinal),
            groups.Select(g => g.Label));
    }

    [Fact]
    public void GroupsAreNamedAfterTheirRuntimeNotTheirFiles()
    {
        var groups = Groups(DlssR: true);

        // The file names are near-identical (nvngx_dlss.dll, nvngx_dlssg.dll, nvngx_dlssr.dll) and
        // would be useless as labels; the user needs "DLSS" and "DLSS Frame Gen".
        var labels = groups.Select(g => g.Label).ToList();

        Assert.Equal(
            ["DLSS", "DLSS Frame Gen", "DLSS Ray Recon"],
            labels);
        Assert.DoesNotContain(labels, l => l.Contains(".dll", StringComparison.Ordinal));
        Assert.DoesNotContain(labels, l => l.Contains("nvngx", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AnUntrustedVersionIsMarkedInTheLabel()
    {
        var groups = Groups(versions: [(RuntimeKind.Dlss, "310.9.1")], untrusted: true);

        var label = groups.Single(g => g.Kind == RuntimeKind.Dlss).Options.Single().Label;

        // R42/R33: the source is shown where the choice is made, not only in the preview.
        Assert.Contains("untrusted", label, StringComparison.OrdinalIgnoreCase);
    }

    // -------------------------------------------------------------- Helpers

    /// <summary>
    /// Builds the groups a game with the given runtimes would be offered.
    /// </summary>
    /// <remarks>
    /// The version list is given explicitly rather than derived, because the point of most of these
    /// tests is what happens when two runtimes hold the same version, which a generated list would
    /// never produce by accident.
    /// </remarks>
    private static IReadOnlyList<RuntimeGroup> Groups(
        bool Dlss = true,
        bool DlssG = true,
        bool DlssR = false,
        bool Fsr = false,
        bool Xess = false,
        string? currentDlss = "310.2.1",
        string? currentDlssG = "310.2.1",
        IReadOnlyList<(RuntimeKind Kind, string Version)>? versions = null,
        bool untrusted = false)
    {
        // "present" here means the game carries a file for it, which is decided by the flag rather
        // than by whether a version string was supplied. currentDlss: null is a game that carries DLSS
        // whose file reports no version, which is a different case from a game that has no DLSS at
        // all and must not be offered one.
        var present = new Dictionary<RuntimeKind, string?>
        {
            [RuntimeKind.Dlss] = Dlss ? currentDlss : null,
            [RuntimeKind.DlssG] = DlssG ? currentDlssG : null,
            [RuntimeKind.DlssR] = DlssR ? "310.2.1" : null,
            [RuntimeKind.Fsr] = Fsr ? "3.1.0" : null,
            [RuntimeKind.Xess] = Xess ? "2.0.2" : null,
        };

        // Carried and version are separate facts: a game can carry DLSS whose file reports no version.
        var carried = new HashSet<RuntimeKind>();
        if (Dlss) { carried.Add(RuntimeKind.Dlss); }
        if (DlssG) { carried.Add(RuntimeKind.DlssG); }
        if (DlssR) { carried.Add(RuntimeKind.DlssR); }
        if (Fsr) { carried.Add(RuntimeKind.Fsr); }
        if (Xess) { carried.Add(RuntimeKind.Xess); }

        var runtimes = present
            .Select(kvp => new DetectedRuntime(
                kvp.Key,
                RuntimeDlls.ByKind[kvp.Key][0],
                RuntimeDlls.ByKind[kvp.Key][0],
                Path.Combine("C:", "game", RuntimeDlls.ByKind[kvp.Key][0]),
                kvp.Value,
                null,
                Exists: carried.Contains(kvp.Key)))
            .ToArray();

        var snapshot = new GameRuntimeSnapshot("game-1", "Test Game", @"C:\game", runtimes);

        var entries = (versions ?? DefaultVersions(carried))
            .Select(v => new CatalogEntry(
                CatalogId.For(v.Kind, v.Version, RuntimeSource.Imported),
                v.Kind,
                v.Version,
                null,
                RuntimeSource.Imported,
                $"{v.Version}.dll",
                Path.Combine("C:", "collection", RuntimeDlls.ByKind[v.Kind][0]),
                untrusted ? RuntimeTrust.Untrusted : RuntimeTrust.Trusted,
                DateTimeOffset.UnixEpoch,
                null))
            .ToArray();

        return RuntimeGroup.Build(snapshot, entries);
    }

    /// <summary>One version of every runtime the game carries.</summary>
    private static IReadOnlyList<(RuntimeKind, string)> DefaultVersions(IEnumerable<RuntimeKind> carried)
    {
        var versions = carried.Select(kind => (kind, "310.9.1")).ToList();

        // A game that carries nothing still needs the collection populated, so that the reason no group
        // appears is the game's own emptiness rather than an empty collection.
        if (versions.Count == 0)
        {
            versions.Add((RuntimeKind.Dlss, "310.9.1"));
        }

        return versions;
    }
}
