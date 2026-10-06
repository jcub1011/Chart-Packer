using ChartPacker.Core.Charts;

namespace ChartPacker.Core.Charting;

/// <summary>
/// A chart as its author wrote it, for Chart Builder to edit (charter-spec §4.1): the tempo map, the global events,
/// the parts, and the tracks it doesn't edit, kept byte for byte (§4.2). Unlike <see cref="ChartDocument"/>, which is
/// what the games read, it keeps each flag where MIDI keeps it, so what's edited is what's saved.
/// </summary>
/// <remarks>
/// Anyone can read it; only the commands in this assembly change it, and every change keeps it ordered. Work off the
/// editing thread reads a <see cref="Clone"/> taken when it starts.
/// </remarks>
public sealed class EditableChart
{
    private readonly List<EditablePart> _parts;
    private readonly List<PassthroughTrack> _passthrough;

    /// <summary>An empty chart: no tempo or time-signature markers, events, parts or passthrough tracks.</summary>
    /// <param name="resolution">Ticks per beat.</param>
    internal EditableChart(int resolution)
    {
        TempoMap = new EditableTempoMap(resolution);
        Events = new TickList<TextEvent>(static e => e.Tick, check: EditablePart.CheckText);
        EventsRaw = new TickList<RawEvent>(static e => e.Tick, check: EditablePart.CheckRaw);
        TempoTrackRaw = new TickList<RawEvent>(static e => e.Tick, check: EditablePart.CheckRaw);
        _parts = [];
        _passthrough = [];
    }

    private EditableChart(EditableChart source)
    {
        Name = source.Name;
        TempoMap = source.TempoMap.Clone();
        Events = source.Events.Clone();
        EventsRaw = source.EventsRaw.Clone();
        TempoTrackRaw = source.TempoTrackRaw.Clone();
        DrumsVariant = source.DrumsVariant;
        _parts = [.. source._parts.Select(p => p.Clone())];
        _passthrough = [.. source._passthrough];
    }

    /// <summary>Ticks per beat.</summary>
    public int Resolution => TempoMap.Resolution;

    /// <summary>The tempo track's name, which is the song's; null when the loaded track has none.</summary>
    public string? Name { get; internal set; }

    /// <summary>The tempo and time-signature markers and the anchors.</summary>
    public EditableTempoMap TempoMap { get; }

    /// <summary>
    /// The text events on <c>EVENTS</c>: sections, song events and free text, in tick order, then the order they were
    /// added in.
    /// </summary>
    public TickList<TextEvent> Events { get; }

    /// <summary><c>EVENTS</c>' other events, such as notes, in tick order, then the order they were added in.</summary>
    public TickList<RawEvent> EventsRaw { get; }

    /// <summary>
    /// The tempo track's events other than tempo and time-signature markers, anchors and its name, such as key
    /// signatures, markers and text, in tick order, then the order they were added in.
    /// </summary>
    public TickList<RawEvent> TempoTrackRaw { get; }

    /// <summary>How the song's drums are charted; null when the song says nothing and has no drums part.</summary>
    public DrumsVariant? DrumsVariant { get; internal set; }

    /// <summary>The parts, in <see cref="ChartInstrument"/> order.</summary>
    public IReadOnlyList<EditablePart> Parts => _parts;

    /// <summary>
    /// The chunks of the loaded file that aren't edited, tracks and others, in the order the file has them. Copies of
    /// the chart share them, as they never change.
    /// </summary>
    public IReadOnlyList<PassthroughTrack> Passthrough => _passthrough;

    /// <summary>The part for <paramref name="instrument"/>, or null when the chart doesn't have it.</summary>
    public EditablePart? PartOf(ChartInstrument instrument)
    {
        int index = IndexOfPart(instrument);
        return index >= 0 ? _parts[index] : null;
    }

    /// <summary>
    /// A copy that changes independently of this chart, for work that reads the chart while it's being edited. Raw
    /// events' bytes and passthrough tracks are shared, as nothing changes them.
    /// </summary>
    public EditableChart Clone() => new(this);

    /// <summary>The chart a new song starts with, from what the new-chart wizard asked for (charter-spec C1).</summary>
    /// <remarks>
    /// A tempo marker and a time signature at tick 0, an empty part for each one asked for, and <c>EVENTS</c> holding
    /// <c>[music_start]</c> and <c>[end]</c>, both at tick 0 while there are no notes; saving moves <c>[end]</c> after
    /// the last note (CHR085). The tempo track is named after the song.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The resolution, tempo or time signature can't be written.</exception>
    public static EditableChart CreateNew(NewChartSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var chart = new EditableChart(settings.Resolution) { Name = settings.SongName };
        chart.TempoMap.Tempos.Add(new TempoChange(0, EditableTempoMap.MicrosecondsPerQuarterOf(settings.Bpm)));
        try
        {
            chart.TempoMap.TimeSignatures.Add(new TimeSignatureMarker(0, settings.Numerator, settings.Denominator));
        }
        catch (ArgumentException e)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), $"{settings.Numerator}/{settings.Denominator} isn't a time signature. {e.Message}");
        }

        foreach (var instrument in settings.Parts.Order())
            chart.AddPart(instrument);
        if (settings.Parts.Contains(ChartInstrument.Drums))
            chart.DrumsVariant = settings.Drums;
        chart.Events.Add(TextEvent.Of(0, "[music_start]"));
        chart.Events.Add(TextEvent.Of(0, "[end]"));
        return chart;
    }

    /// <summary>Adds an empty part for <paramref name="instrument"/>.</summary>
    /// <exception cref="InvalidOperationException">The chart already has the part.</exception>
    internal EditablePart AddPart(ChartInstrument instrument)
    {
        var part = new EditablePart(instrument);
        AddPart(part);
        return part;
    }

    /// <summary>Adds <paramref name="part"/>, as when undoing its removal.</summary>
    /// <exception cref="InvalidOperationException">The chart already has a part for its instrument.</exception>
    internal void AddPart(EditablePart part)
    {
        ArgumentNullException.ThrowIfNull(part);
        int index = IndexOfPart(part.Instrument);
        if (index >= 0)
            throw new InvalidOperationException($"The chart already has a {part.Instrument} part.");
        _parts.Insert(~index, part);
    }

    /// <summary>Removes the part for <paramref name="instrument"/>.</summary>
    /// <returns>The part removed, for undoing, or null when there wasn't one.</returns>
    internal EditablePart? RemovePart(ChartInstrument instrument)
    {
        int index = IndexOfPart(instrument);
        if (index < 0)
            return null;
        var part = _parts[index];
        _parts.RemoveAt(index);
        return part;
    }

    /// <summary>Adds <paramref name="track"/> after the passthrough tracks there are, as the loader finds them.</summary>
    internal void AddPassthrough(PassthroughTrack track) => InsertPassthrough(_passthrough.Count, track);

    /// <summary>Puts <paramref name="track"/> at <paramref name="index"/> among the passthrough tracks.</summary>
    internal void InsertPassthrough(int index, PassthroughTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        _passthrough.Insert(index, track);
    }

    /// <summary>Puts <paramref name="track"/> in place of the passthrough track at <paramref name="index"/>, as re-ticking does.</summary>
    /// <returns>The track it replaced, for undoing.</returns>
    internal PassthroughTrack SetPassthrough(int index, PassthroughTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        var previous = _passthrough[index];
        _passthrough[index] = track;
        return previous;
    }

    /// <summary>Removes the passthrough track at <paramref name="index"/>.</summary>
    /// <returns>The track removed, for undoing.</returns>
    internal PassthroughTrack RemovePassthroughAt(int index)
    {
        var track = _passthrough[index];
        _passthrough.RemoveAt(index);
        return track;
    }

    // The index of the part, or the bitwise complement of where it would go.
    private int IndexOfPart(ChartInstrument instrument)
    {
        int lo = 0, hi = _parts.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (_parts[mid].Instrument < instrument)
                lo = mid + 1;
            else
                hi = mid;
        }

        return lo < _parts.Count && _parts[lo].Instrument == instrument ? lo : ~lo;
    }
}
