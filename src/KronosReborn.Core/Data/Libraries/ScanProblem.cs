using System;
using System.Collections.Generic;
using System.Linq;

namespace KronosReborn.Data.Libraries;

/// <summary>
/// Something that went wrong while looking for games, described well enough to act on.
/// </summary>
/// <remarks>
/// This exists because a bare string is not enough to satisfy the error reporting requirements. R57
/// asks what was being attempted and what the underlying failure was; R58 asks which library, path or
/// game it concerns; R60 asks for a next action where there is an obvious one. All three are fields
/// here rather than sentences assembled at each call site, so two problems cannot be reported in two
/// different shapes.
///
/// It is a record rather than an exception. These are expected outcomes - a library that is not
/// installed, a configuration file that has been corrupted by something else - and discovery must
/// continue past them rather than unwinding.
/// </remarks>
/// <param name="Scope">What kind of thing was being attempted, such as "Steam".</param>
/// <param name="Target">
/// The specific library, path or file involved. Null when the problem concerns the scan as a whole
/// rather than one thing in it.
/// </param>
/// <param name="Summary">One line saying what failed.</param>
/// <param name="Detail">
/// The underlying reason, including the underlying exception text where there is one. Empty rather
/// than null when there is nothing more to say.
/// </param>
/// <param name="Hint">
/// What the user could do about it, where there is an obvious action. Null when there is not, because
/// a hint that guesses is worse than none.
/// </param>
public sealed record ScanProblem(
    string Scope,
    string? Target,
    string Summary,
    string? Detail = null,
    string? Hint = null)
{
    /// <summary>
    /// A single line for a summary list.
    /// </summary>
    public string OneLine => Target is null ? $"{Scope}: {Summary}" : $"{Scope} ({Target}): {Summary}";

    /// <summary>
    /// Everything known about this problem, for the detailed view.
    /// </summary>
    /// <remarks>
    /// Built here rather than at each call site so the detailed view has the same shape everywhere. The
    /// underlying exception goes in last and in full, because R61 requires it to be reachable and a
    /// summarised stack trace is the first thing anyone actually needs from it.
    /// </remarks>
    public string Describe()
    {
        var lines = new List<string> { $"{Scope}: {Summary}" };

        if (!string.IsNullOrWhiteSpace(Target))
        {
            lines.Add($"  Where: {Target}");
        }

        if (!string.IsNullOrWhiteSpace(Detail))
        {
            lines.Add($"  Reason: {Detail}");
        }

        if (!string.IsNullOrWhiteSpace(Hint))
        {
            lines.Add($"  Try: {Hint}");
        }

        return string.Join(System.Environment.NewLine, lines);
    }

    /// <summary>
    /// Builds a problem from an exception, keeping the exception's own message and type.
    /// </summary>
    /// <remarks>
    /// The exception type is included because "Access to the path is denied" means something quite
    /// different depending on whether it came from a permission problem or from a malformed path, and
    /// the type is often the only thing distinguishing them.
    /// </remarks>
    public static ScanProblem FromException(
        string scope,
        string? target,
        string summary,
        Exception exception,
        string? hint = null)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return new ScanProblem(
            scope,
            target,
            summary,
            $"{exception.GetType().Name}: {exception.Message}",
            hint);
    }
}

/// <summary>
/// The outcome of looking for games across every source.
/// </summary>
/// <remarks>
/// Games and problems are returned together rather than as either/or. A scan that finds nothing is
/// exactly the case where the user most needs to be told whether that is because there is nothing
/// there or because something could not be read, which is R59 and cannot be expressed by returning
/// an empty list alone.
/// </remarks>
/// <param name="Games">Everything that was found, already deduplicated.</param>
/// <param name="Problems">Everything that went wrong, possibly none.</param>
/// <param name="SourcesChecked">How many sources were looked at, found or not.</param>
public sealed record ScanResult(
    IReadOnlyList<GameRecord> Games,
    IReadOnlyList<ScanProblem> Problems,
    int SourcesChecked)
{
    public static ScanResult Empty { get; } = new([], [], 0);

    /// <summary>
    /// Whether anything went wrong, whether or not any games were found.
    /// </summary>
    public bool HasProblems => Problems.Count > 0;

    /// <summary>
    /// Whether every source could not be read.
    /// </summary>
    /// <remarks>
    /// The case where an empty game list is least trustworthy: nothing was readable, so nothing is
    /// known. An empty list with no problems, by contrast, is a confident answer.
    /// </remarks>

    /// <summary>
    /// Folds several scans into one, keeping every problem.
    /// </summary>
    /// <remarks>
    /// Games are concatenated and then deduplicated, because the same game can be reported by two
    /// libraries. Problems are simply accumulated: R63 asks for every affected item, so truncating
    /// this list loses exactly the information the user opened it for.
    /// </remarks>
    public static ScanResult Combine(IEnumerable<ScanResult> results)
    {
        var all = results.ToList();

        return new ScanResult(
            GameRecord.Deduplicate(all.SelectMany(x => x.Games)),
            all.SelectMany(x => x.Problems).ToList(),
            all.Sum(x => x.SourcesChecked));
    }
}
