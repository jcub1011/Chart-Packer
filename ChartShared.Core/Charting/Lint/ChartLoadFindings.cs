using System.Globalization;
using ChartShared.Core.Resources;
using ChartShared.Core.Validation;

namespace ChartShared.Core.Charting.Lint;

/// <summary>
/// The chart's load issues as findings (CHR086, charter-spec §6): what <see cref="MidiChartLoader"/> left out of the
/// chart file, listed beside the lint findings until a save writes the file without it.
/// </summary>
/// <remarks>
/// They describe the file as it was loaded, so they aren't linted again as the chart is edited, and their ticks are the
/// file's.
/// </remarks>
public static class ChartLoadFindings
{
    /// <summary>
    /// A finding for each of <paramref name="issues"/>, in the order lint findings are shown: one on a part has its
    /// chart location, and one on the tempo track its tick. Each is Info, as the chart plays as it did.
    /// </summary>
    public static IReadOnlyList<Finding> Of(IReadOnlyList<ChartLoadIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(issues);
        var findings = new List<Finding>(issues.Count);
        foreach (var issue in issues)
        {
            string message = MessageOf(issue.Kind);
            if (issue.Pitch is { } pitch)
                message = string.Format(CultureInfo.CurrentCulture, FindingMessages.ChrLoadWithPitchFormat, message, pitch);

            findings.Add(issue.Part is { } part
                ? new Finding(ChrRules.Chr086, Severity.Info, message, Location: new ChartLocation(part, issue.Difficulty, issue.Tick, issue.Tick))
                : new Finding(ChrRules.Chr086, Severity.Info, message, Tick: issue.Tick));
        }

        findings.Sort(FindingOrder.Comparer);
        return findings;
    }

    /// <summary>
    /// <paramref name="lint"/> and <paramref name="load"/>, each already in the order lint findings are shown, put
    /// together in that order.
    /// </summary>
    public static IReadOnlyList<Finding> Merge(IReadOnlyList<Finding> lint, IReadOnlyList<Finding> load)
    {
        ArgumentNullException.ThrowIfNull(lint);
        ArgumentNullException.ThrowIfNull(load);
        if (load.Count == 0)
            return lint;
        if (lint.Count == 0)
            return load;
        return FindingOrder.Merge([.. lint], [.. load]);
    }

    private static string MessageOf(ChartLoadIssueKind kind) => kind switch
    {
        ChartLoadIssueKind.UnholdableTempo => FindingMessages.ChrLoadUnholdableTempo,
        ChartLoadIssueKind.UnholdableTimeSignature => FindingMessages.ChrLoadUnholdableTimeSignature,
        ChartLoadIssueKind.StrayAnchor => FindingMessages.ChrLoadStrayAnchor,
        ChartLoadIssueKind.FirstEventDropped => FindingMessages.ChrLoadFirstEventDropped,
        ChartLoadIssueKind.DuplicateNote => FindingMessages.ChrLoadDuplicateNote,
        ChartLoadIssueKind.DuplicateOpen => FindingMessages.ChrLoadDuplicateOpen,
        ChartLoadIssueKind.OpenBeforeEnhancedOpens => FindingMessages.ChrLoadOpenBeforeEnhancedOpens,
        ChartLoadIssueKind.UnpairedNoteEvent => FindingMessages.ChrLoadUnpairedNoteEvent,
        ChartLoadIssueKind.UnpairedSysexEvent => FindingMessages.ChrLoadUnpairedSysexEvent,
        ChartLoadIssueKind.UnusedMarker => FindingMessages.ChrLoadUnusedMarker,
        ChartLoadIssueKind.UnreadStarPower => FindingMessages.ChrLoadUnreadStarPower,
        ChartLoadIssueKind.DuplicateStarPower => FindingMessages.ChrLoadDuplicateStarPower,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}
