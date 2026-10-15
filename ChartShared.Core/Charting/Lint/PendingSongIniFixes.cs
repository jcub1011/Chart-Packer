using ChartShared.Core.Charting.Lint.Rules;
using ChartShared.Core.Charts;
using ChartShared.Core.Validation;

namespace ChartShared.Core.Charting.Lint;

/// <summary>
/// The quick fixes that change <c>song.ini</c> rather than the chart, through Chart Builder's pending changes (charter-spec
/// C17 item 2): a fix restores a pending change the user reverted. They aren't undo steps, as reverting isn't.
/// </summary>
public static class PendingSongIniFixes
{
    /// <summary>
    /// The key of the reverted pending change that fixes <paramref name="finding"/>, or null when it has none: CHR084's
    /// <c>song_length</c>, when its correction was reverted.
    /// </summary>
    public static string? KeyToRestore(Finding finding, EditorSong song)
    {
        ArgumentNullException.ThrowIfNull(finding);
        ArgumentNullException.ThrowIfNull(song);
        if (finding.RuleId != ChrRules.Chr084 || !song.Reverted.Contains(SongLengthRule.Key))
            return null;
        return song.AllPendingChanges().Any(c => c.Key == SongLengthRule.Key) ? SongLengthRule.Key : null;
    }

    /// <summary>
    /// The <c>diff_*</c> keys of <paramref name="part"/> that <paramref name="pairs"/> doesn't rate <paramref name="tier"/>,
    /// which using a suggested rating of that tier would set (charter-spec C18 item 3); none for a part with no keys.
    /// </summary>
    public static IReadOnlyList<string> RatingKeysToSet(EditableChart chart, ChartInstrument part, IReadOnlyList<KeyValuePair<string, string>> pairs, int tier)
    {
        ArgumentNullException.ThrowIfNull(chart);
        ArgumentNullException.ThrowIfNull(pairs);
        if (!NewChartSettings.Instruments.Contains(part))
            return [];
        return [.. SongIniPartKeys.DifficultyKeysOf(part, chart.DrumsVariant).Where(k => SongIniValues.Integer(SongIniValues.Last(pairs, k)) != tier)];
    }

    /// <summary>
    /// The suggested rating that fixes <paramref name="finding"/> (charter-spec C18 item 3), or null when it has none:
    /// CHR083's <c>diff_*</c> key, and the part's suggested tier, from its highest charted difficulty.
    /// </summary>
    /// <param name="options">The settings the chart is read with, as lint read it.</param>
    public static (string Key, int Tier)? SuggestedRating(Finding finding, EditableChart chart, ChartReadOptions options)
    {
        ArgumentNullException.ThrowIfNull(finding);
        ArgumentNullException.ThrowIfNull(chart);
        if (finding.RuleId != ChrRules.Chr083 || finding.Key is not { } key || finding.Location is not { } location)
            return null;
        return Difficulty.DifficultyEstimator.EstimatePart(chart, location.Part, options) is { } estimate ? (key, estimate.Tier) : null;
    }
}
