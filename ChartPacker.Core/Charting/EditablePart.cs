using ChartPacker.Core.Charts;

namespace ChartPacker.Core.Charting;

/// <summary>
/// A pro-drums tom marker (pitches 110 to 112), kept exactly as written. It makes the notes it covers on its pad toms
/// rather than cymbals, on every difficulty.
/// </summary>
/// <param name="Pad">The pad it covers, numbered as drum lanes are: 2 yellow, 3 blue, 4 green.</param>
/// <param name="Tick">Where it starts, in chart ticks.</param>
/// <param name="Length">How long it lasts, in ticks, exactly as the MIDI marker is held.</param>
public readonly record struct TomMarker(int Pad, long Tick, long Length)
{
    /// <summary>The first pad a tom marker can cover: yellow.</summary>
    public const int FirstPad = 2;

    /// <summary>The last pad a tom marker can cover: green.</summary>
    public const int LastPad = 4;

    /// <summary>The order of a part's tom markers: by tick, then pad, then length.</summary>
    internal static int Order(TomMarker a, TomMarker b)
    {
        int c = a.Tick.CompareTo(b.Tick);
        if (c == 0)
            c = a.Pad.CompareTo(b.Pad);
        return c != 0 ? c : a.Length.CompareTo(b.Length);
    }
}

/// <summary>
/// One instrument of a chart (charter-spec §4.1): a track for each difficulty, and what every difficulty shares, as
/// MIDI stores it: phrases, tom markers, the part's settings and its other events.
/// </summary>
public sealed class EditablePart
{
    private readonly EditableTrack[] _tracks;

    internal EditablePart(ChartInstrument instrument)
    {
        Instrument = instrument;
        _tracks = [.. Enum.GetValues<ChartDifficulty>().Select(d => new EditableTrack(Kind, d))];
        Phrases = new TickList<Phrase>(static p => p.Tick, Phrase.Order, check: CheckPhrase);
        TomMarkers = new TickList<TomMarker>(static m => m.Tick, TomMarker.Order, check: CheckTomMarker);
        TextEvents = new TickList<TextEvent>(static e => e.Tick, check: CheckText);
        RawEvents = new TickList<RawEvent>(static e => e.Tick, check: CheckRaw);
    }

    private EditablePart(EditablePart source)
    {
        Instrument = source.Instrument;
        _tracks = [.. source._tracks.Select(t => t.Clone())];
        Phrases = source.Phrases.Clone();
        TomMarkers = source.TomMarkers.Clone();
        EnhancedOpens = source.EnhancedOpens;
        ChartDynamics = source.ChartDynamics;
        TextEvents = source.TextEvents.Clone();
        RawEvents = source.RawEvents.Clone();
    }

    public ChartInstrument Instrument { get; }

    /// <summary>How the part's notes are laid out.</summary>
    public ChartInstrumentKind Kind => ChartInstruments.KindOf(Instrument);

    /// <summary>The part's tracks, Easy to Expert.</summary>
    public IReadOnlyList<EditableTrack> Tracks => _tracks;

    /// <summary>The part's track at <paramref name="difficulty"/>.</summary>
    public EditableTrack this[ChartDifficulty difficulty] => _tracks[(int)difficulty];

    /// <summary>
    /// The phrases, which every difficulty shares: star power, solos, drum fills, big rock endings, and rolls and
    /// trills, which reach down from Expert. In order of tick, then kind, length and lowest difficulty.
    /// </summary>
    public TickList<Phrase> Phrases { get; }

    /// <summary>Pro drums' tom markers, which every difficulty shares, in order of tick, then pad, then length.</summary>
    public TickList<TomMarker> TomMarkers { get; }

    /// <summary>Whether any difficulty has a note.</summary>
    public bool HasNotes => _tracks.Any(static t => t.Notes.Count > 0);

    /// <summary>Whether the track has <c>[ENHANCED_OPENS]</c>, which makes the notes below green opens.</summary>
    public bool EnhancedOpens { get; internal set; }

    /// <summary>Whether the track has <c>[ENABLE_CHART_DYNAMICS]</c>, without which accents and ghosts aren't written.</summary>
    public bool ChartDynamics { get; internal set; }

    /// <summary>
    /// Free text events on the part's track, in tick order, then the order they were added in. The events the model
    /// holds elsewhere (<c>[ENHANCED_OPENS]</c>, <c>[ENABLE_CHART_DYNAMICS]</c> and mix events) aren't among them.
    /// </summary>
    public TickList<TextEvent> TextEvents { get; }

    /// <summary>The part track's events the model doesn't understand, in tick order, then the order they were added in.</summary>
    public TickList<RawEvent> RawEvents { get; }

    /// <summary>A copy that changes independently of this part.</summary>
    internal EditablePart Clone() => new(this);

    private static void CheckPhrase(Phrase phrase)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(phrase.Tick, nameof(phrase));
        ArgumentOutOfRangeException.ThrowIfNegative(phrase.Length, nameof(phrase));
        if (!Enum.IsDefined(phrase.Kind))
            throw new ArgumentException("The phrase's kind isn't one there is.", nameof(phrase));
        if (Phrase.HasLowestDifficulty(phrase.Kind) != phrase.LowestDifficulty.HasValue)
            throw new ArgumentException("Rolls and trills, and only they, have a lowest difficulty.", nameof(phrase));
    }

    private static void CheckTomMarker(TomMarker marker)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(marker.Tick, nameof(marker));
        ArgumentOutOfRangeException.ThrowIfNegative(marker.Length, nameof(marker));
        if (marker.Pad is < TomMarker.FirstPad or > TomMarker.LastPad)
            throw new ArgumentException("A tom marker covers yellow, blue or green.", nameof(marker));
    }

    internal static void CheckText(TextEvent e)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(e.Tick, nameof(e));
        if (e.Text is null)
            throw new ArgumentException("A text event has text.", nameof(e));
    }

    internal static void CheckRaw(RawEvent e)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(e.Tick, nameof(e));
        if (e.Bytes.IsEmpty)
            throw new ArgumentException("A raw event has at least a status byte.", nameof(e));
    }
}
