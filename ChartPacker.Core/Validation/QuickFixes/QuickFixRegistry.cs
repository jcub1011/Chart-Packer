using System.Diagnostics.CodeAnalysis;
using ChartPacker.Core.Charting;
using ChartPacker.Core.Charting.Commands;
using ChartPacker.Core.Charts;
using ChartPacker.Core.Metadata;

namespace ChartPacker.Core.Validation.QuickFixes;

/// <summary>
/// The quick fixes for metadata findings whose fix is simple and has one right answer (project spec §6), one
/// <see cref="IQuickFixStrategy"/> per rule.
/// </summary>
/// <remarks>
/// A fix is worked out from the metadata as drafted, at the time it's asked for, so it's never stale. To add one,
/// write a strategy and list it in <see cref="Strategies"/>. Chart lint's fixes, which change the chart rather than its
/// metadata, are listed apart, in <see cref="ChartStrategies"/>.
/// </remarks>
public static class QuickFixRegistry
{
    /// <summary>Every strategy, at most one per rule.</summary>
    public static IReadOnlyList<IQuickFixStrategy> Strategies { get; } =
    [
        new LenientBooleanFix(),
        new EmptyValueFix(),
        new ForbiddenValueCharacterFix(),
        new LegacyKeyFix(),
        new DuplicateKeyFix(),
    ];

    private static readonly Dictionary<string, IQuickFixStrategy> ByRule = Strategies.ToDictionary(s => s.RuleId, StringComparer.Ordinal);

    /// <summary>
    /// Every chart fix strategy, for chart lint findings, at most one per rule (charter-spec C16 item 3). They're kept
    /// apart from <see cref="Strategies"/>, so the metadata fixes (<see cref="IsSupported"/>, <see cref="FixKey"/>) don't
    /// see them. CM2 adds the timing and 5-fret rules' fixes; CM3 adds the drums and phrase rules'.
    /// </summary>
    public static IReadOnlyList<IChartQuickFixStrategy> ChartStrategies { get; } = Charting.Lint.ChartFixes.All;

    /// <summary>Whether findings of <paramref name="ruleId"/> can have a quick fix.</summary>
    public static bool IsSupported(string ruleId)
    {
        ArgumentNullException.ThrowIfNull(ruleId);
        return ByRule.ContainsKey(ruleId);
    }

    /// <summary>The fix for <paramref name="finding"/>, found in <paramref name="pairs"/>.</summary>
    /// <param name="finding">A finding about one key's pair, from <see cref="MetadataValidator"/>.</param>
    /// <param name="pairs">The metadata the finding was found in, as drafted.</param>
    /// <returns>The fix, or <see langword="null"/> when the rule has none or this finding has no single right one.</returns>
    public static QuickFix? Propose(Finding finding, IReadOnlyList<KeyValuePair<string, string>> pairs)
    {
        ArgumentNullException.ThrowIfNull(finding);
        ArgumentNullException.ThrowIfNull(pairs);
        return ByRule.TryGetValue(finding.RuleId, out var strategy) ? strategy.Propose(finding, pairs) : null;
    }

    /// <summary>Checks <paramref name="pairs"/> and proposes a fix for every finding that has one, in the findings' order.</summary>
    /// <param name="pairs">The metadata as drafted.</param>
    /// <param name="duplicateKeys">The keys a <c>song.ini</c> repeats, as for <see cref="MetadataValidator.Validate"/>.</param>
    public static IReadOnlyList<QuickFix> ProposeAll(
        IReadOnlyList<KeyValuePair<string, string>> pairs,
        IReadOnlyCollection<string>? duplicateKeys = null)
    {
        ArgumentNullException.ThrowIfNull(pairs);

        var fixes = new List<QuickFix>();
        foreach (var finding in MetadataValidator.Validate(pairs, duplicateKeys))
        {
            if (Propose(finding, pairs) is { } fix)
                fixes.Add(fix);
        }

        return fixes;
    }

    /// <summary>The fix for the chart lint finding <paramref name="finding"/>, from <see cref="ChartStrategies"/>.</summary>
    /// <param name="finding">A chart lint finding.</param>
    /// <param name="chart">The chart it was found in, as it is now.</param>
    /// <param name="options">The settings the chart is read with, as lint read it.</param>
    /// <returns>
    /// One undoable command to run through the chart's undo stack, or <see langword="null"/> when the rule has no fix or
    /// this finding has no single right one.
    /// </returns>
    public static IChartCommand? ProposeChartFix(Finding finding, EditableChart chart, ChartReadOptions options) => ProposeChartFix(finding, chart, options, ChartStrategies);

    /// <summary>The fix for the chart lint finding <paramref name="finding"/>, from the first of <paramref name="strategies"/> for its rule.</summary>
    /// <param name="finding">A chart lint finding.</param>
    /// <param name="chart">The chart it was found in, as it is now.</param>
    /// <param name="options">The settings the chart is read with, as lint read it.</param>
    /// <param name="strategies">The strategies to look in, such as <see cref="ChartStrategies"/>.</param>
    /// <returns>One undoable command, or <see langword="null"/> when none is for the rule, or its strategy has no single right fix.</returns>
    public static IChartCommand? ProposeChartFix(Finding finding, EditableChart chart, ChartReadOptions options, IEnumerable<IChartQuickFixStrategy> strategies)
    {
        ArgumentNullException.ThrowIfNull(finding);
        ArgumentNullException.ThrowIfNull(chart);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(strategies);
        foreach (var strategy in strategies)
        {
            if (string.Equals(strategy.RuleId, finding.RuleId, StringComparison.Ordinal))
                return strategy.Propose(finding, chart, options);
        }

        return null;
    }

    /// <summary>
    /// Fixes everything about <paramref name="key"/> that has a fix, in <paramref name="item"/>'s draft. Fixes are
    /// applied one at a time, each worked out from the metadata as the last left it, failures before hints, so two
    /// fixes to one pair (stripping a legacy key's value, then renaming it) don't undo each other.
    /// </summary>
    /// <param name="item">The song's saved metadata, the keys its <c>song.ini</c> repeats, and its draft, which the fixes go into.</param>
    /// <param name="key">The key whose findings to fix, ignoring case.</param>
    /// <param name="includeHints">Whether to fix hints, such as a legacy key, as well as failures.</param>
    /// <returns>How many fixes were applied.</returns>
    public static int FixKey(MetadataItem item, string key, bool includeHints = true)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(key);

        // Each fix clears its finding, so this is only a guard against two strategies undoing each other forever.
        int maxFixes = Strategies.Count * 2;
        int applied = 0;
        while (applied < maxFixes && NextFix(item, key, includeHints) is { } fix)
        {
            fix.ApplyTo(item);
            applied++;
        }

        return applied;
    }

    private static QuickFix? NextFix(MetadataItem item, string key, bool includeHints)
    {
        var pairs = item.Draft.ApplyTo(item.Original);
        var findings = MetadataValidator.Validate(pairs, item.RemainingDuplicateKeys())
            .Where(f => f.Key is not null && f.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
            .Where(f => includeHints || MetadataValidator.IsFailing(f))
            .OrderBy(f => MetadataValidator.IsFailing(f) ? 0 : 1);
        foreach (var finding in findings)
        {
            if (Propose(finding, pairs) is { } fix)
                return fix;
        }

        return null;
    }

    /// <summary>
    /// The finding's key and its value in <paramref name="pairs"/>: the first pair with the key, ignoring case, as
    /// the editor shows it.
    /// </summary>
    internal static bool TryGetValue(
        Finding finding,
        IReadOnlyList<KeyValuePair<string, string>> pairs,
        [NotNullWhen(true)] out string? key,
        [NotNullWhen(true)] out string? value)
    {
        if (finding.Key is { Length: > 0 } findingKey)
        {
            foreach (var pair in pairs)
            {
                if (pair.Key.Equals(findingKey, StringComparison.OrdinalIgnoreCase))
                {
                    key = pair.Key;
                    value = pair.Value;
                    return true;
                }
            }
        }

        key = null;
        value = null;
        return false;
    }
}
