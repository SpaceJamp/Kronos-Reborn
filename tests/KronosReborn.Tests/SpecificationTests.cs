using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace KronosReborn.Tests;

/// <summary>
/// Guards the structure of docs/spec.md.
/// </summary>
/// <remarks>
/// The specification is the only artefact here that states what the program must do, so it is also the
/// one most likely to rot quietly. Nothing in the build would notice a duplicated requirement number,
/// a gap in the sequence, or a constraint written so loosely it cannot fail.
///
/// This checks structure only. Whether a requirement is correctly *implemented* is what the rest of
/// the suite is for; what is checked here is that the document is internally consistent, so that a
/// requirement can be referred to by number without ambiguity.
/// </remarks>
public class SpecificationTests
{
    static string SpecText()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && Directory.Exists(Path.Combine(dir.FullName, "src")) == false)
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);

        var path = Path.Combine(dir!.FullName, "docs", "spec.md");
        Assert.True(File.Exists(path), $"Could not find {path}");

        return File.ReadAllText(path);
    }

    /// <summary>Every "- **Xnn.**" heading marker in the document, in order.</summary>
    static List<string> RequirementIds()
    {
        return Regex.Matches(SpecText(), @"^- \*\*(?<id>[CR]\d+)\.\*\*", RegexOptions.Multiline)
            .Select(m => m.Groups["id"].Value)
            .ToList();
    }

    [Fact]
    public void EveryRequirementIdIsUnique()
    {
        // A duplicate means two different rules answer to the same name, and a test written against
        // that number is ambiguous about which one it is enforcing.
        var duplicates = RequirementIds()
            .GroupBy(x => x)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.True(duplicates.Count == 0, "Duplicate requirement ids: " + string.Join(", ", duplicates));
    }

    [Fact]
    public void RequirementIdsHaveNoGaps()
    {
        // A gap usually means a requirement was deleted and the rest were never renumbered, which
        // silently breaks any reference to the deleted one.
        var requirements = RequirementIds().Where(x => x.StartsWith("R", StringComparison.Ordinal)).ToList();
        AssertSequenceIsContiguous(requirements, "R");

        var constraints = RequirementIds().Where(x => x.StartsWith("C", StringComparison.Ordinal)).ToList();
        AssertSequenceIsContiguous(constraints, "C");
    }

    static void AssertSequenceIsContiguous(List<string> ids, string prefix)
    {
        var numbers = ids
            .Select(x => int.Parse(x.Substring(1), System.Globalization.CultureInfo.InvariantCulture))
            .OrderBy(x => x)
            .ToList();

        Assert.True(numbers.Count > 0, $"No {prefix} requirements found.");

        var expected = Enumerable.Range(1, numbers.Count).ToList();
        Assert.True(numbers.SequenceEqual(expected),
            $"{prefix} requirement numbers are not 1..{numbers.Count}. Found: {string.Join(",", numbers)}");
    }

    [Fact]
    public void EveryRequirementIsARequirementNotAConstraint()
    {
        // Guards against a requirement being filed as a constraint, which reads as though it were a
        // property of the platform rather than something the program must do.
        var text = SpecText();

        foreach (Match m in Regex.Matches(text, @"^- \*\*C\d+\.\*\*.*$", RegexOptions.Multiline))
        {
            // A constraint describes the environment or the build. A "must" is a requirement.
            Assert.False(
                m.Value.Contains(" must ", StringComparison.OrdinalIgnoreCase),
                $"Constraint reads like a requirement: {m.Value}");
        }
    }

    [Fact]
    public void TheSafetyPrinciplesAreStillPresentAndInOrder()
    {
        // They are the part of the document with the least redundancy: everything else failing is a
        // bug, but these failing is data loss. Worth a test that they were not quietly dropped.
        var text = SpecText();

        foreach (string phrase in new[]
                 {
                     "Never lose the original",
                     "Always reversible",
                     "Fail by doing nothing",
                     "Say what is known",
                     "Warn before",
                     "Never act silently",
                 })
        {
            Assert.True(
                text.Contains(phrase, StringComparison.OrdinalIgnoreCase),
                $"Safety principle missing from the specification: {phrase}");
        }
    }

    [Fact]
    public void TheDocumentStatesItWasWrittenFromRequirements()
    {
        // The claim that no previous implementation was consulted is the whole reason the licensing
        // position is sound. If the wording is ever weakened, that claim silently goes with it.
        var text = SpecText();

        Assert.Contains("written from requirements", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("No part of any previous implementation", text, StringComparison.OrdinalIgnoreCase);
    }
}