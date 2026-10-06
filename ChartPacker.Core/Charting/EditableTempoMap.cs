using ChartPacker.Core.Charts;

namespace ChartPacker.Core.Charting;

/// <summary>
/// A time-signature marker as MIDI writes it: the signature, and the two metronome bytes the games ignore, kept so that
/// loading and saving gives the same event.
/// </summary>
/// <param name="Denominator">The note value of a beat, a power of two: 4 for quarter notes, 8 for eighths.</param>
/// <param name="ClocksPerClick">MIDI clocks per metronome click.</param>
/// <param name="ThirtySecondsPerQuarter">Thirty-second notes per MIDI quarter note.</param>
public readonly record struct TimeSignatureMarker(long Tick, int Numerator, int Denominator, byte ClocksPerClick = 24, byte ThirtySecondsPerQuarter = 8)
{
    /// <summary>The signature, as <see cref="TempoMap"/> takes it.</summary>
    public TimeSignature ToTimeSignature() => new(Tick, Numerator, Denominator);
}

/// <summary>
/// A tempo marker pinned to a time in the audio (charter-spec C4): edits keep the marker at that time. Saved as a
/// <c>[chartpacker_anchor &lt;µs&gt;]</c> text event on the tempo track.
/// </summary>
/// <param name="Tick">The anchored marker's tick.</param>
/// <param name="AudioMicroseconds">Its time in the audio, in whole microseconds.</param>
public readonly record struct TempoAnchor(long Tick, long AudioMicroseconds);

/// <summary>
/// A chart's tempo map as the author wrote it (charter-spec §4.1): tempo markers, time-signature markers and anchors,
/// each kept exactly as loaded or entered. <see cref="ToTempoMap"/> turns it into the <see cref="TempoMap"/> that every
/// conversion between ticks and seconds, and every beat line, comes from.
/// </summary>
/// <remarks>
/// Unlike <see cref="TempoMap"/>, the lists aren't tidied: two markers at one tick are both kept, in the order they
/// came, and nothing is put at tick 0 that wasn't there. <see cref="TempoMap"/> tidies them as the games read them.
/// </remarks>
public sealed class EditableTempoMap
{
    /// <summary>The longest beat a MIDI tempo event can hold, in microseconds: three bytes' worth.</summary>
    public const long MaxMicrosecondsPerQuarter = 0xFF_FFFF;

    private Cached? _cached;

    internal EditableTempoMap(int resolution)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(resolution);
        Resolution = resolution;
        Tempos = new TickList<TempoChange>(static t => t.Tick, check: CheckTempo);
        TimeSignatures = new TickList<TimeSignatureMarker>(static s => s.Tick, check: CheckSignature);
        Anchors = new TickList<TempoAnchor>(static a => a.Tick, unique: true, check: CheckAnchor);
    }

    private EditableTempoMap(EditableTempoMap source)
    {
        Resolution = source.Resolution;
        Tempos = source.Tempos.Clone();
        TimeSignatures = source.TimeSignatures.Clone();
        Anchors = source.Anchors.Clone();
    }

    /// <summary>Ticks per beat.</summary>
    public int Resolution { get; }

    /// <summary>The tempo markers, in microseconds per quarter note as MIDI stores them, in tick order, then the order they came in.</summary>
    public TickList<TempoChange> Tempos { get; }

    /// <summary>The time-signature markers, in tick order, then the order they came in.</summary>
    public TickList<TimeSignatureMarker> TimeSignatures { get; }

    /// <summary>The anchors, at most one per tick.</summary>
    public TickList<TempoAnchor> Anchors { get; }

    /// <summary>
    /// The tempo map the markers make, for converting ticks to seconds and back and for beat lines. It's made once and
    /// kept until a tempo or time-signature marker changes.
    /// </summary>
    public TempoMap ToTempoMap()
    {
        // One reference, read and written whole, so a snapshot read on two threads at once still gets a right map.
        var cached = _cached;
        if (cached is not null && cached.TemposVersion == Tempos.Version && cached.SignaturesVersion == TimeSignatures.Version)
            return cached.Map;

        var map = new TempoMap(Resolution, Tempos, TimeSignatures.Select(s => s.ToTimeSignature()));
        _cached = new Cached(map, Tempos.Version, TimeSignatures.Version);
        return map;
    }

    /// <summary>The tempo, in beats per minute, of a beat <paramref name="microsecondsPerQuarter"/> long.</summary>
    public static double BpmOf(long microsecondsPerQuarter)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(microsecondsPerQuarter);
        return 60_000_000.0 / microsecondsPerQuarter;
    }

    /// <summary>
    /// The length of a beat at <paramref name="bpm"/>, in whole microseconds, as MIDI stores a tempo: rounded to the
    /// nearest, halves away from zero.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="bpm"/> isn't a positive number, or makes a beat MIDI can't hold.
    /// </exception>
    public static long MicrosecondsPerQuarterOf(double bpm)
    {
        if (!double.IsFinite(bpm) || bpm <= 0)
            throw new ArgumentOutOfRangeException(nameof(bpm), bpm, "A tempo is a positive number of beats per minute.");
        double microseconds = Math.Round(60_000_000.0 / bpm, MidpointRounding.AwayFromZero);
        if (microseconds < 1 || microseconds > MaxMicrosecondsPerQuarter)
            throw new ArgumentOutOfRangeException(nameof(bpm), bpm, "The tempo is too fast or too slow for MIDI to hold.");
        return (long)microseconds;
    }

    /// <summary>A copy that changes independently of this map.</summary>
    internal EditableTempoMap Clone() => new(this);

    private static void CheckTempo(TempoChange tempo)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(tempo.Tick, nameof(tempo));
        if (tempo.MicrosecondsPerQuarter is < 1 or > MaxMicrosecondsPerQuarter)
            throw new ArgumentException("A tempo marker's beat is 1 to 16,777,215 microseconds long.", nameof(tempo));
    }

    private static void CheckSignature(TimeSignatureMarker signature)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(signature.Tick, nameof(signature));
        if (signature.Numerator is < 1 or > byte.MaxValue || signature.Denominator < 1 || (signature.Denominator & (signature.Denominator - 1)) != 0)
            throw new ArgumentException("A time signature has 1 to 255 beats of a power-of-two note value.", nameof(signature));
    }

    private static void CheckAnchor(TempoAnchor anchor) => ArgumentOutOfRangeException.ThrowIfNegative(anchor.Tick, nameof(anchor));

    private sealed record Cached(TempoMap Map, int TemposVersion, int SignaturesVersion);
}
