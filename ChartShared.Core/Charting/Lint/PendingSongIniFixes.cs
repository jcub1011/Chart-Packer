using ChartShared.Core.Charting.Difficulty;
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
    /// The <c>diff_*</c> keys of <paramref name="part"/> that <paramref name="pairs"/> doesn't rate as
    /// <paramref name="estimates"/> suggest, each with its suggested tier, which using the suggested ratings would set
    /// (charter-spec C18 item 3); none for a part with no keys.
    /// </summary>
    /// <param name="estimates">The part's suggested ratings, from <see cref="DifficultyEstimator.EstimateKeys"/>.</param>
    public static IReadOnlyList<(string Key, int Tier)> RatingsToSet(ChartInstrument part, IReadOnlyList<KeyValuePair<string, string>> pairs, IReadOnlyList<KeyedEstimate> estimates)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        ArgumentNullException.ThrowIfNull(estimates);
        if (!NewChartSettings.Instruments.Contains(part))
            return [];
        return [.. estimates.Where(e => SongIniValues.Integer(SongIniValues.Last(pairs, e.Key)) != e.Estimate.Tier).Select(static e => (e.Key, e.Estimate.Tier))];
    }

    /// <summary>
    /// The suggested rating that fixes <paramref name="finding"/> (charter-spec C18 item 3), or null when it has none:
    /// CHR083's <c>diff_*</c> key, and its suggested tier, from the part's highest charted difficulty, rated by the model
    /// that rates that key.
    /// </summary>
    /// <param name="options">The settings the chart is read with, as lint read it.</param>
    public static (string Key, int Tier)? SuggestedRating(Finding finding, EditableChart chart, ChartReadOptions options)
    {
        ArgumentNullException.ThrowIfNull(finding);
        ArgumentNullException.ThrowIfNull(chart);
        if (finding.RuleId != ChrRules.Chr083 || finding.Key is not { } key || finding.Location is not { } location)
            return null;
        var instrument = DifficultyInstruments.OfKey(location.Part, key);
        return DifficultyEstimator.EstimatePart(chart, location.Part, options, instrument) is { } estimate ? (key, estimate.Tier) : null;
    }
}
