using ChartPacker.Core.Charts;

namespace ChartPacker.Core.Charting.Commands;

/// <summary>
/// Adds a phrase to a part. Drum fills, rolls and trills are drums' only; overlapping phrases are allowed, and saving
/// merges them (see <see cref="MidiChartWriter.Normalize"/>).
/// </summary>
public sealed class AddPhrase(ChartInstrument part, Phrase phrase) : ChartCommand
{
    private int _index = -1;

    public ChartInstrument Part { get; } = part;

    public Phrase Phrase { get; } = phrase;

    private protected override ChartChange Do(EditableChart chart)
    {
        var part = PartOf(chart, Part);
        PhraseEdits.Check(part, Phrase);
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

/// <summary>Changes a phrase's start, length or lowest difficulty; its kind stays.</summary>
public sealed class ChangePhrase : ChartCommand
{
    private int _from = -1;
    private int _to = -1;

    /// <param name="from">The phrase, exactly as the part has it.</param>
    /// <param name="to">What it becomes, of the same kind.</param>
    public ChangePhrase(ChartInstrument part, Phrase from, Phrase to)
    {
        if (from.Kind != to.Kind)
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
        part.Phrases.Check(To);

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
    /// <summary>Throws when <paramref name="part"/> can't have <paramref name="phrase"/>.</summary>
    public static void Check(EditablePart part, Phrase phrase)
    {
        part.Phrases.Check(phrase);
        if ((phrase.Kind is PhraseKind.DrumFill or PhraseKind.Roll or PhraseKind.Trill) && part.Kind != ChartInstrumentKind.Drums)
            throw new InvalidOperationException($"Only drums have {phrase.Kind} phrases, and {part.Instrument} isn't drums.");
    }

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
