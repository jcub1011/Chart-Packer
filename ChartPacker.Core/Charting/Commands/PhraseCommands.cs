using ChartPacker.Core.Charts;

namespace ChartPacker.Core.Charting.Commands;

/// <summary>
/// Adds a phrase to a part. Drum fills, rolls and trills are drums' only; on drums, a fill goes before <c>EVENTS</c>' first
/// <c>[coda]</c> and a big rock ending at or after it (<see cref="DrumFills.KindAt"/>). Overlapping phrases are allowed,
/// and saving merges them (see <see cref="MidiChartWriter.Normalize"/>).
/// </summary>
public sealed class AddPhrase(ChartInstrument part, Phrase phrase) : ChartCommand
{
    private int _index = -1;

    public ChartInstrument Part { get; } = part;

    public Phrase Phrase { get; } = phrase;

    private protected override ChartChange Do(EditableChart chart)
    {
        var part = PartOf(chart, Part);
        PhraseEdits.Check(chart, part, Phrase);
        _index = part.Phrases.Place(Phrase);
        return PhraseEdits.Span(Part, Phrase);
    }

    private protected override void Undo(EditableChart chart) => PartOf(chart, Part).Phrases.RemoveAt(_index);
}

/// <summary>Removes a phrase from a part.</summary>
/// <param name="phrase">The phrase, exactly as the part has it.</param>
public sealed class RemovePhrase(ChartInstrument part, Phrase phrase) : ChartCommand
{
    private int _index = -1;

    public ChartInstrument Part { get; } = part;

    public Phrase Phrase { get; } = phrase;

    private protected override ChartChange Do(EditableChart chart)
    {
        var phrases = PartOf(chart, Part).Phrases;
        int index = PhraseEdits.IndexOf(phrases, Part, Phrase);
        phrases.RemoveAt(index);
        _index = index;
        return PhraseEdits.Span(Part, Phrase);
    }

    private protected override void Undo(EditableChart chart) => PartOf(chart, Part).Phrases.Insert(_index, Phrase);
}

/// <summary>
/// Changes a phrase's start, length or lowest difficulty. Its kind stays, but for a drums fill and a big rock ending,
/// which share their pitches: one moved across <c>[coda]</c> becomes the other.
/// </summary>
public sealed class ChangePhrase : ChartCommand
{
    private int _from = -1;
    private int _to = -1;

    /// <param name="from">The phrase, exactly as the part has it.</param>
    /// <param name="to">What it becomes, of the same kind, or a fill made a big rock ending or back.</param>
    public ChangePhrase(ChartInstrument part, Phrase from, Phrase to)
    {
        if (from.Kind != to.Kind && !(PhraseEdits.IsFillPitches(from.Kind) && PhraseEdits.IsFillPitches(to.Kind)))
            throw new ArgumentException("A phrase changed keeps its kind; remove it and add another instead.", nameof(to));
        Part = part;
        From = from;
        To = to;
    }

    public ChartInstrument Part { get; }

    public Phrase From { get; }

    public Phrase To { get; }

    private protected override ChartChange Do(EditableChart chart)
    {
        var part = PartOf(chart, Part);
        int from = PhraseEdits.IndexOf(part.Phrases, Part, From);
        PhraseEdits.Check(chart, part, To);

        part.Phrases.RemoveAt(from);
        _to = part.Phrases.Place(To);
        _from = from;
        return PhraseEdits.Span(Part, From).Merge(PhraseEdits.Span(Part, To));
    }

    private protected override void Undo(EditableChart chart)
    {
        var phrases = PartOf(chart, Part).Phrases;
        phrases.RemoveAt(_to);
        phrases.Insert(_from, From);
    }
}

/// <summary>What the phrase commands share.</summary>
internal static class PhraseEdits
{
    /// <summary>Throws when <paramref name="part"/> of <paramref name="chart"/> can't have <paramref name="phrase"/>.</summary>
    public static void Check(EditableChart chart, EditablePart part, Phrase phrase)
    {
        part.Phrases.Check(phrase);
        if ((phrase.Kind is PhraseKind.DrumFill or PhraseKind.Roll or PhraseKind.Trill) && part.Kind != ChartInstrumentKind.Drums)
            throw new InvalidOperationException($"Only drums have {phrase.Kind} phrases, and {part.Instrument} isn't drums.");
        if (part.Kind == ChartInstrumentKind.Drums && IsFillPitches(phrase.Kind) && DrumFills.KindAt(chart, phrase.Tick) != phrase.Kind)
        {
            throw new InvalidOperationException(phrase.Kind == PhraseKind.DrumFill
                ? "On drums, a fill at or after [coda] is a big rock ending."
                : "On drums, a big rock ending before [coda] is a fill.");
        }
    }

    /// <summary>Whether a phrase of <paramref name="kind"/> is on pitches 120 to 124: a drum fill or a big rock ending.</summary>
    public static bool IsFillPitches(PhraseKind kind) => kind is PhraseKind.DrumFill or PhraseKind.BigRockEnding;

    /// <summary>The index of <paramref name="phrase"/> in <paramref name="phrases"/>.</summary>
    /// <exception cref="InvalidOperationException">The part hasn't it.</exception>
    public static int IndexOf(TickList<Phrase> phrases, ChartInstrument part, Phrase phrase)
    {
        int index = phrases.IndexOf(phrase);
        if (index < 0)
            throw new InvalidOperationException(FormattableString.Invariant($"{part} has no {phrase.Kind} phrase at tick {phrase.Tick} of length {phrase.Length}."));
        return index;
    }

    /// <summary>The ticks <paramref name="phrase"/> covers, on every difficulty.</summary>
    public static ChartChange Span(ChartInstrument part, Phrase phrase) => ChartChange.Of(part, null, phrase.Tick, phrase.Tick + phrase.Length);
}
