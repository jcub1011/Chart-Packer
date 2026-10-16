using ChartShared.Core.Validation;

namespace ChartShared.Core.Charting.Lint;

/// <summary>
/// How much of the chart a <see cref="IChartLintRule"/> checks again after a change (charter-spec C16 item 1), so lint
/// keeps up while the author types.
/// </summary>
public enum LintScope
{
    /// <summary>
    /// Only the ticks the change touched, widened by the rule's <see cref="IChartLintRule.Reach"/> chords either side,
    /// such as the HOPO rules, which look one chord back and one forward.
    /// </summary>
    Range,

    /// <summary>Each part the change touched, whole, such as CHR080 and CHR081.</summary>
    Part,

    /// <summary>The whole chart after every change, such as CHR003, CHR083 and CHR084.</summary>
    Song,
}

/// <summary>
/// One chart lint rule (charter-spec C16, §6): it checks a snapshot of the chart and the song's settings, and finds
/// problems as <see cref="Finding"/>s with a CHR rule ID. <see cref="ChartLinter"/> runs it, over as much of the chart
/// as its <see cref="Scope"/> says.
/// </summary>
/// <remarks>
/// <para>
/// A rule reads only what its <see cref="ChartLintContext"/> holds: the snapshot, its game view, the song.ini pairs, the
/// read options and the audio length. It never reads the editor's state, such as the current snap, so it gives the same
/// result in Chart Builder and in Chart Packer's library (C16 item 5). It keeps no state between calls, as one rule
/// object serves every run.
/// </para>
/// <para>
/// Every finding of a <see cref="LintScope.Range"/> or <see cref="LintScope.Part"/> rule carries a
/// <see cref="Finding.Location"/>; the linter drops one that hasn't. Only a <see cref="LintScope.Song"/> rule's findings
/// may go without, such as one about song.ini, as it's checked again after every change.
/// </para>
/// <para>
/// A range rule's findings must each depend only on the chart within its own location, widened by
/// <see cref="Reach"/> chords either side (on its difficulty's track, or on every difficulty's when it has none), and on
/// what a change to which makes the linter check every rule again: the tempo map, the song's events, song.ini, the read
/// options and the audio length. A rule that compares difficulties, or reads a whole part, is a part rule. Given a
/// region, a range rule returns every one of its findings that overlaps the region; it may return more, as the linter
/// keeps only those. A part rule given a part returns every one of its findings in that part.
/// </para>
/// </remarks>
public interface IChartLintRule
{
    /// <summary>The rule's ID, e.g. <c>CHR020</c> from <see cref="ChrRules"/>: stable, and never reused.</summary>
    string RuleId { get; }

    /// <summary>How serious its findings are (charter-spec §6).</summary>
    Severity Severity { get; }

    /// <summary>How much of the chart it checks again after a change.</summary>
    LintScope Scope { get; }

    /// <summary>
    /// For a <see cref="LintScope.Range"/> rule, how many chords before and after a change it looks at: 1 for the HOPO
    /// rules, 0 for a rule about each note alone. Not used for other scopes.
    /// </summary>
    int Reach { get; }

    /// <summary>The rule's findings in what <paramref name="context"/> gives it to check.</summary>
    /// <param name="context">The snapshot, the song's settings, and the part or region to check.</param>
    IEnumerable<Finding> Check(ChartLintContext context);
}
