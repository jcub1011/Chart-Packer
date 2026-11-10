using ChartShared.Core.Charts;

namespace ChartShared.Core.Charting;

/// <summary>
/// A phrase, or a drum disco flip section, as the span of chart it covers, for a view to draw behind a difficulty's notes.
/// </summary>
/// <param name="Kind">The kind of phrase, or null for a disco flip section.</param>
/// <param name="Tick">Where it starts, in chart ticks.</param>
/// <param name="EndTick">The tick after the last it covers; meaningless without <paramref name="HasEnd"/>.</param>
/// <param name="HasEnd">Whether it has an end: a disco flip section that is still on at the end of the chart doesn't.</param>
public readonly record struct PhraseSpan(PhraseKind? Kind, long Tick, long EndTick, bool HasEnd = true);

/// <summary>Finds the phrases a difficulty plays through, for the views that draw them behind its notes.</summary>
public static class PhraseSpans
{
    /// <summary>
    /// The phrases of <paramref name="part"/> that <paramref name="difficulty"/> plays through, and on drums its disco flip
    /// sections, in tick order. A roll or trill reaches down from Expert only as far as its lowest difficulty; the rest
    /// every difficulty shares.
    /// </summary>
    public static IReadOnlyList<PhraseSpan> Of(EditablePart part, ChartDifficulty difficulty)
    {
        ArgumentNullException.ThrowIfNull(part);
        var spans = new List<PhraseSpan>();
        foreach (var phrase in part.Phrases)
        {
            if (Phrase.HasLowestDifficulty(phrase.Kind) && phrase.LowestDifficulty is { } lowest && difficulty < lowest)
                continue;
            spans.Add(new PhraseSpan(phrase.Kind, phrase.Tick, phrase.CoverEnd));
        }

        if (part.Kind == ChartInstrumentKind.Drums)
        {
            foreach (var range in DiscoFlips.RangesOf(part[difficulty]))
                spans.Add(new PhraseSpan(null, range.Tick, range.EndTick ?? range.Tick, range.EndTick is not null));
        }

        spans.Sort(static (a, b) => a.Tick.CompareTo(b.Tick));
        return spans;
    }

    /// <summary>The spans of <paramref name="key"/>'s track in <paramref name="chart"/>; none when the chart hasn't the part.</summary>
    public static IReadOnlyList<PhraseSpan> Of(EditableChart chart, ChartTrackKey key)
    {
        ArgumentNullException.ThrowIfNull(chart);
        return chart.Parts.FirstOrDefault(p => p.Instrument == key.Instrument) is { } part ? Of(part, key.Difficulty) : [];
    }

    /// <summary>
    /// The spans of every track of the chart <paramref name="bytes"/> hold, read as the games read it (see
    /// <see cref="SongOpener.LoadChart"/>); none when the chart can't be read, which its highway shows without them.
    /// </summary>
    /// <param name="chartName">The chart file's name: <c>notes.mid</c> or <c>notes.chart</c>.</param>
    public static IReadOnlyDictionary<ChartTrackKey, IReadOnlyList<PhraseSpan>> Read(string chartName, byte[] bytes, IReadOnlyList<KeyValuePair<string, string>> pairs)
    {
        ArgumentNullException.ThrowIfNull(chartName);
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(pairs);
        EditableChart chart;
        try
        {
            chart = SongOpener.LoadChart(chartName, bytes, pairs).Load.Chart;
        }
        catch (Exception)
        {
            // The phrases only decorate the highway, so whatever way the chart fails to read, it goes without them.
            return new Dictionary<ChartTrackKey, IReadOnlyList<PhraseSpan>>();
        }

        var tracks = new Dictionary<ChartTrackKey, IReadOnlyList<PhraseSpan>>();
        foreach (var part in chart.Parts)
        {
            foreach (var difficulty in Enum.GetValues<ChartDifficulty>())
            {
                if (part[difficulty].Notes.Count > 0)
                    tracks[new ChartTrackKey(part.Instrument, difficulty)] = Of(part, difficulty);
            }
        }

        return tracks;
    }
}
