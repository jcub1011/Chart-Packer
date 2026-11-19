using ChartShared.Core.Charting.Commands;
using ChartShared.Core.Charts;
using ChartShared.Core.Validation;

namespace ChartShared.Core.Charting;

/// <summary>
/// Drum fills (activation lanes) and the big rock endings that share their pitches, 120 to 124 (charter-spec C6 item 5,
/// C8 item 4). On drums, which one a phrase is follows <c>EVENTS</c>' first <c>[coda]</c>: the games read those that
/// start at or after it as big rock endings, and those before it as fills.
/// </summary>
public static class DrumFills
{
    /// <summary>
    /// The tick of <c>EVENTS</c>' first <c>[coda]</c>, as <see cref="MidiChartLoader"/> finds it, or null when there's none.
    /// </summary>
    public static long? FirstCoda(EditableChart chart)
    {
        ArgumentNullException.ThrowIfNull(chart);
        foreach (var e in chart.Events)
        {
            if (IsCoda(e))
                return e.Tick;
        }

        return null;
    }

    /// <summary>Whether <paramref name="e"/> is a <c>[coda]</c> the games read.</summary>
    public static bool IsCoda(TextEvent e) => MidiChartLoader.IsReadText(e.Type) && ChartTextEvents.Normalize(e.Text) == "coda";

    /// <summary>What a drums phrase on pitches 120 to 124 starting at <paramref name="tick"/> is: a big rock ending at or after the first <c>[coda]</c>, a fill before it.</summary>
    public static PhraseKind KindAt(EditableChart chart, long tick) =>
        FirstCoda(chart) is { } coda && tick >= coda ? PhraseKind.BigRockEnding : PhraseKind.DrumFill;

    /// <summary>
    /// Makes each of the drums' fills and big rock endings the kind <see cref="KindAt"/> says, as the games will read it
    /// once <c>[coda]</c> has moved.
    /// </summary>
    /// <returns>What it changed: each phrase's span, on every difficulty.</returns>
    internal static ChartChange Reclassify(EditableChart chart, EditLog log)
    {
        if (chart.PartOf(ChartInstrument.Drums) is not { } drums)
            return ChartChange.None;
        long? coda = FirstCoda(chart);
        var wrong = drums.Phrases.Where(p => Classified(p, coda) != p).ToList();
        var regions = new List<ChartLocation>(wrong.Count);
        foreach (var phrase in wrong)
        {
            log.RemoveAt(drums.Phrases, drums.Phrases.IndexOf(phrase));
            log.Place(drums.Phrases, Classified(phrase, coda));
            regions.Add(new ChartLocation(ChartInstrument.Drums, null, phrase.Tick, phrase.Tick + phrase.Length));
        }

        return ChartChange.Of(regions);
    }

    /// <summary>
    /// <paramref name="phrase"/>, a drums phrase, as the kind its tick makes it against <paramref name="coda"/>
    /// (<see cref="FirstCoda"/>) when it's a fill or big rock ending; any other phrase as it is.
    /// </summary>
    internal static Phrase Classified(Phrase phrase, long? coda) =>
        phrase.Kind is PhraseKind.DrumFill or PhraseKind.BigRockEnding && phrase.Kind != KindAt(coda, phrase.Tick)
            ? phrase with { Kind = KindAt(coda, phrase.Tick) }
            : phrase;

    private static PhraseKind KindAt(long? coda, long tick) => tick >= coda ? PhraseKind.BigRockEnding : PhraseKind.DrumFill;
}
