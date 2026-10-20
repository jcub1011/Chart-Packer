using ChartShared.Core.Charting.Difficulty;
using ChartShared.Core.Charting.Lint;
using ChartShared.Core.Charts;
using ChartShared.Core.Validation;

namespace ChartShared.Core.Charting.Editing;

/// <summary>
/// What the Reduce dialog shows of a run's drafts (charter-spec C9): their lint findings, each difficulty's suggested rating
/// and peak notes per second, and where a lower difficulty isn't easier than the one above it.
/// </summary>
/// <param name="Findings">The findings on the drafts' difficulties, the fret, drum and phrase rules' and CHR081's.</param>
/// <param name="Estimates">The suggested rating of each difficulty of the part with notes, the drafts in place.</param>
/// <param name="OutOfOrder">Each difficulty rated as hard as the one with notes above it, or harder.</param>
public sealed record ReductionCheck(
    IReadOnlyList<Finding> Findings,
    IReadOnlyDictionary<ChartDifficulty, DifficultyEstimate> Estimates,
    IReadOnlyList<ChartDifficulty> OutOfOrder)
{
    /// <summary>
    /// The check of <paramref name="drafted"/>, a chart with the drafts of <paramref name="targets"/> of
    /// <paramref name="part"/> in it that nothing else changes, read with <paramref name="options"/>. It can run off the
    /// editing thread.
    /// </summary>
    public static ReductionCheck Of(EditableChart drafted, ChartInstrument part, IReadOnlyCollection<ChartDifficulty> targets, ChartReadOptions options)
    {
        ArgumentNullException.ThrowIfNull(drafted);
        ArgumentNullException.ThrowIfNull(targets);
        var linter = new ChartLinter(ChartLintRules.ForEditor.Where(static r => IsReductionRule(r.RuleId)));
        var findings = linter.LintAll(new ChartLintInput(drafted, options: options)).Findings
            .Where(f => f.Location is { } at && at.Part == part && at.Difficulty is { } d
                && (targets.Contains(d) || (f.RuleId == ChrRules.Chr081 && d < ChartDifficulty.Expert && targets.Contains(d + 1))))
            .OrderBy(static f => f.Location!.Tick)
            .ToList();

        var estimates = DifficultyEstimator.EstimateDifficulties(drafted, part, options);
        var outOfOrder = new List<ChartDifficulty>();
        DifficultyEstimate? above = null;
        foreach (var difficulty in Enum.GetValues<ChartDifficulty>().OrderDescending())
        {
            if (!estimates.TryGetValue(difficulty, out var estimate))
                continue;
            if (above is not null && estimate.Score >= above.Score)
                outOfOrder.Add(difficulty);
            above = estimate;
        }

        return new ReductionCheck(findings, estimates, outOfOrder);
    }

    // The rules a reduction can trip: the 5-fret and drum rules, the phrase rules, and CHR081.
    private static bool IsReductionRule(string ruleId) =>
        (string.CompareOrdinal(ruleId, ChrRules.Chr020) >= 0 && string.CompareOrdinal(ruleId, ChrRules.Chr065) <= 0) || ruleId == ChrRules.Chr081;
}
