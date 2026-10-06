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
/// Removes a part and everything on its track (charter-spec C2 item 6). The part is kept, to put back as it was when the
/// removal is undone. Removing drums clears the song's drum variant, as a chart without drums has none, and undoing puts
/// it back.
/// </summary>
public sealed class RemovePart : ChartCommand
{
    private EditablePart? _removed;
    private DrumsVariant? _previousVariant;

    public RemovePart(ChartInstrument instrument) => Instrument = instrument;

    public ChartInstrument Instrument { get; }

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

        return ChartChange.Of([PartEdits.Whole(Instrument)], song);
    }

    private protected override void Undo(EditableChart chart)
    {
        chart.AddPart(_removed!);
        chart.DrumsVariant = _previousVariant;
        _removed = null;
    }
}

/// <summary>What the part commands share.</summary>
internal static class PartEdits
{
    /// <summary>The whole of a part, on every difficulty.</summary>
    public static ChartLocation Whole(ChartInstrument instrument) => new(instrument, null, 0, long.MaxValue);
}
