using ChartPacker.Core.Charts;
using ChartPacker.Core.Validation;

namespace ChartPacker.Core.Charting.Commands;

/// <summary>
/// Adds an empty part for one of the instruments v1 edits (charter-spec C2 item 5): any of
/// <see cref="NewChartSettings.Instruments"/> the chart hasn't.
/// </summary>
/// <remarks>
/// Drums need a variant. When the chart has one (song.ini set it, or the song had drums), the drums take it; otherwise
/// it must be given, as the editor asks for it. Undoing puts back the variant the chart had.
/// </remarks>
public sealed class AddPart : ChartCommand
{
    private DrumsVariant? _previousVariant;

    /// <param name="drums">For drums, their variant, needed when the chart has none; null for any other part.</param>
    public AddPart(ChartInstrument instrument, DrumsVariant? drums = null)
    {
        if (!NewChartSettings.Instruments.Contains(instrument))
            throw new ArgumentException($"{instrument} isn't a part Chart Builder edits.", nameof(instrument));
        if (drums is { } variant && (instrument != ChartInstrument.Drums || !Enum.IsDefined(variant)))
            throw new ArgumentException("Only drums have a variant, one of 4-lane, Pro and 5-lane.", nameof(drums));
        Instrument = instrument;
        Drums = drums;
    }

    public ChartInstrument Instrument { get; }

    /// <summary>The variant given for drums, or null.</summary>
    public DrumsVariant? Drums { get; }

    private protected override ChartChange Do(EditableChart chart)
    {
        if (chart.PartOf(Instrument) is not null)
            throw new InvalidOperationException($"The chart already has a {Instrument} part.");
        var song = SongChanges.Parts;
        if (Instrument == ChartInstrument.Drums)
        {
            if (chart.DrumsVariant is null && Drums is null)
                throw new InvalidOperationException("Adding drums needs their variant, as the song doesn't say how its drums are charted.");
            if (chart.DrumsVariant is { } current && Drums is { } given && current != given)
                throw new InvalidOperationException($"The song's drums are {current}, not {given}.");
            if (chart.DrumsVariant is null)
                song |= SongChanges.DrumsVariant;
        }

        _previousVariant = chart.DrumsVariant;
        chart.AddPart(Instrument);
        if (Instrument == ChartInstrument.Drums)
            chart.DrumsVariant ??= Drums;
        return ChartChange.Of([PartEdits.Whole(Instrument)], song);
    }

    private protected override void Undo(EditableChart chart)
    {
        chart.RemovePart(Instrument);
        chart.DrumsVariant = _previousVariant;
    }
}

/// <summary>
/// Removes a part and everything on its track (charter-spec C2 item 6), with the passthrough tracks the reader would take
/// for its instrument (a second <c>PART BASS</c>, say), which would otherwise be read as the part once it's gone. The part
/// and those tracks are kept, to put back as they were when the removal is undone. Removing drums clears the song's drum
/// variant, as a chart without drums has none, and undoing puts it back.
/// </summary>
public sealed class RemovePart(ChartInstrument instrument) : ChartCommand
{
    private readonly List<(int Index, PassthroughTrack Track)> _passthrough = [];
    private EditablePart? _removed;
    private DrumsVariant? _previousVariant;

    public ChartInstrument Instrument { get; } = instrument;

    private protected override ChartChange Do(EditableChart chart)
    {
        if (chart.PartOf(Instrument) is null)
            throw new InvalidOperationException($"The chart has no {Instrument} part.");
        var song = SongChanges.Parts;
        _previousVariant = chart.DrumsVariant;
        _removed = chart.RemovePart(Instrument);
        if (Instrument == ChartInstrument.Drums && chart.DrumsVariant is not null)
        {
            chart.DrumsVariant = null;
            song |= SongChanges.DrumsVariant;
        }

        // From the last, so the indexes of those still to remove don't change.
        _passthrough.Clear();
        for (int i = chart.Passthrough.Count - 1; i >= 0; i--)
        {
            if (chart.Passthrough[i].ReadAs() == Instrument)
                _passthrough.Add((i, chart.RemovePassthroughAt(i)));
        }

        return ChartChange.Of([PartEdits.Whole(Instrument)], song);
    }

    private protected override void Undo(EditableChart chart)
    {
        // From the first, so each goes back where it was.
        for (int i = _passthrough.Count - 1; i >= 0; i--)
            chart.InsertPassthrough(_passthrough[i].Index, _passthrough[i].Track);
        _passthrough.Clear();
        chart.AddPart(_removed!);
        chart.DrumsVariant = _previousVariant;
        _removed = null;
    }
}

//// <summary>
/// Changes how the song's drums are charted (charter-spec C1, Parts) while its drums part has no notes, or it has none.
/// A drums part with notes is converted instead (C6), which comes with CM3. Undoing puts back the variant it had.
/// </summary>
public sealed class SetDrumsVariant : ChartCommand
{
    private DrumsVariant? _previous;

    public SetDrumsVariant(DrumsVariant variant)
    {
        if (!Enum.IsDefined(variant))
            throw new ArgumentException("Drums are 4-lane, Pro or 5-lane.", nameof(variant));
        Variant = variant;
    }

    public DrumsVariant Variant { get; }

    private protected override ChartChange Do(EditableChart chart)
    {
        var drums = chart.PartOf(ChartInstrument.Drums);
        if (drums is not null && drums.HasNotes && chart.DrumsVariant != Variant)
            throw new InvalidOperationException("Drums with notes are converted to another variant, not switched.");
        _previous = chart.DrumsVariant;
        if (_previous == Variant)
            return ChartChange.None;
        chart.DrumsVariant = Variant;
        return drums is null
            ? ChartChange.Of(SongChanges.DrumsVariant)
            : ChartChange.Of([PartEdits.Whole(ChartInstrument.Drums)], SongChanges.DrumsVariant);
    }

    private protected override void Undo(EditableChart chart) => chart.DrumsVariant = _previous;
}

// <summary>What the part commands share.</summary>
internal static class PartEdits
{
    /// <summary>The whole of a part, on every difficulty.</summary>
    public static ChartLocation Whole(ChartInstrument instrument) => new(instrument, null, 0, long.MaxValue);
}
