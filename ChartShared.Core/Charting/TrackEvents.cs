using ChartShared.Core.Charts;

namespace ChartShared.Core.Charting;

/// <summary>
/// A track's events, put in order when it's written: by tick, then by their phase of it (<see cref="PartEventOrder"/>),
/// then by key, then as added.
/// </summary>
internal sealed class TrackEvents
{
    // The furthest tick a track can reach: a MIDI delta is at most four bytes of seven bits.
    internal const long MaxTick = 0x0FFF_FFFF;

    // A track's name comes before everything else at tick 0.
    private const int NamePhase = -1;

    // A Phase Shift sysex event's key in its phase: after every note event's.
    private const int SysexKey = 1 << 10;

    private readonly List<Placed> _events = [];

    /// <summary>A track whose first event, at tick 0, is its name.</summary>
    public TrackEvents(string name) => Add(0, NamePhase, 0, MidiMessage.Text(0, 0x03, name).Bytes);

    /// <summary>A track with no name of its own: its events say it, if anything does.</summary>
    public TrackEvents()
    {
    }

    /// <summary>The tick of the last event.</summary>
    public long LastTick { get; private set; }

    /// <summary>An event at <paramref name="tick"/>, in <paramref name="phase"/> of it, ordered by <paramref name="key"/> there, then as added.</summary>
    public void Add(long tick, int phase, int key, byte[] bytes)
    {
        _events.Add(new Placed(tick, phase, key, _events.Count, bytes));
        LastTick = Math.Max(LastTick, tick);
    }

    /// <summary>
    /// A note from <paramref name="tick"/> held for <paramref name="length"/>: its note-on among the note-ons by
    /// pitch, and its note-off among the note-offs where it ends, or straight after its note-on when it's held for no
    /// ticks.
    /// </summary>
    public void Note(long tick, long length, int pitch, int velocity = MidiLayout.NormalVelocity)
    {
        Add(tick, PartEventOrder.NoteOn, pitch * 2, MidiMessage.NoteOn(tick, pitch, velocity).Bytes);
        if (length == 0)
            Add(tick, PartEventOrder.NoteOn, (pitch * 2) + 1, MidiMessage.NoteOff(tick, pitch).Bytes);
        else
            Add(tick + length, PartEventOrder.NoteOff, pitch, MidiMessage.NoteOff(tick + length, pitch).Bytes);
    }

    /// <summary>A Phase Shift phrase on the tick at <paramref name="tick"/>: on there, after the note-ons, and off a tick later, after the note-offs.</summary>
    public void PhaseShift(long tick, int difficulty, int code)
    {
        Add(tick, PartEventOrder.NoteOn, SysexKey + difficulty, MidiMessage.PhaseShift(tick, difficulty, code, on: true).Bytes);
        Add(tick + 1, PartEventOrder.NoteOff, SysexKey + difficulty, MidiMessage.PhaseShift(tick + 1, difficulty, code, on: false).Bytes);
    }

    /// <summary>The track's chunk, its events in order and ended no earlier than <paramref name="endAtLeast"/>.</summary>
    /// <exception cref="ChartFormatException">An event is later than a MIDI file can say.</exception>
    public byte[] Chunk(long endAtLeast = 0)
    {
        if (Math.Max(LastTick, endAtLeast) > MaxTick)
            throw new ChartFormatException("An event of the chart is too late for a MIDI file.");
        _events.Sort(static (a, b) => (a.Tick, a.Phase, a.Key, a.Sequence).CompareTo((b.Tick, b.Phase, b.Key, b.Sequence)));

        // The writer keeps events at a tick in the order they're given.
        return MidiWriter.Track(_events.Select(static e => new MidiMessage(e.Tick, e.Bytes)), endAtLeast);
    }

    private readonly record struct Placed(long Tick, int Phase, int Key, int Sequence, byte[] Bytes);
}
