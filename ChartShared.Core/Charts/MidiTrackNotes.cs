namespace ChartShared.Core.Charts;

/// <summary>A note of a MIDI track, from its note-on to the note-off it's paired with.</summary>
/// <param name="Velocity">Its note-on's velocity.</param>
/// <param name="EnhancedOpens">Whether <c>ENHANCED_OPENS</c> had turned on open notes when it was let go.</param>
/// <param name="OnIndex">Its note-on's index among the track's events.</param>
/// <param name="OffIndex">Its note-off's index among the track's events.</param>
internal readonly record struct MidiNote(int Pitch, int Channel, int Velocity, long Start, long End, bool EnhancedOpens, int OnIndex, int OffIndex);

/// <summary>A Phase Shift sysex phrase, from its on event to the off event it's paired with.</summary>
/// <param name="Difficulty">The difficulty it's for, 0 Easy to 3 Expert, or <see cref="MidiLayout.SysexAllDifficulties"/>.</param>
/// <param name="Code">What it marks, such as <see cref="MidiLayout.SysexOpen"/> or <see cref="MidiLayout.SysexTap"/>.</param>
internal readonly record struct MidiSysexSpan(int Difficulty, int Code, long Start, long End, int OnIndex, int OffIndex);

/// <summary>A text event of a MIDI track, its text as <see cref="ChartTextEvents.Normalize"/> gives it.</summary>
/// <param name="Type">Its meta event type: 0x01, or 0x04 to 0x09.</param>
/// <param name="Index">Its index among the track's events.</param>
internal readonly record struct MidiText(long Tick, byte Type, string Text, int Index);

/// <summary>
/// A MIDI track's notes, Phase Shift sysex phrases and text events, paired and read as YARG reads them
/// (<c>MidReader</c> in YARG.Core), with where each came from among the track's events.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>The track's first event, which is meant to be its name, is never read.</item>
/// <item>A note-on pairs with the next note-off (or note-on at velocity 0) of its pitch and channel; a second
/// note-on while one is held is ignored, and a note never let go is lost.</item>
/// <item>A Phase Shift sysex event is exactly <c>50 53 00 00</c>, the difficulty, the code, and 1 for on or 0 for
/// off; an on pairs with the next off of its difficulty and code, as a note does.</item>
/// <item>Text events are any text type but the copyright and the track name. For five-fret tracks,
/// <c>ENHANCED_OPENS</c> turns on open notes for those let go after it; for drums tracks,
/// <c>ENABLE_CHART_DYNAMICS</c> turns on accents and ghosts for those let go after it (see <see cref="HasChartDynamics"/>).</item>
/// </list>
/// </remarks>
internal sealed class MidiTrackNotes
{
    private HashSet<int>? _consumed;

    // Whether Notes was rented from ListPool, to be given back by Return.
    private readonly bool _rented;

    private MidiTrackNotes(int capacity, bool rent)
    {
        _rented = rent;
        Notes = rent ? ListPool<MidiNote>.Rent(capacity) : new List<MidiNote>(capacity);
    }

    /// <summary>The notes, in the order they're let go.</summary>
    public List<MidiNote> Notes { get; }

    /// <summary>The Phase Shift sysex phrases, in the order they're let go.</summary>
    public List<MidiSysexSpan> Sysex { get; } = [];

    /// <summary>The text events, in order, <c>ENHANCED_OPENS</c> and <c>ENABLE_CHART_DYNAMICS</c> among them.</summary>
    public List<MidiText> Texts { get; } = [];

    /// <summary>
    /// The index among the track's events of its first <c>ENABLE_CHART_DYNAMICS</c> text event, bracketed or not,
    /// or null if it has none. Only drums tracks read it.
    /// </summary>
    public int? ChartDynamics { get; private set; }

    /// <summary>
    /// Whether <c>ENABLE_CHART_DYNAMICS</c> had turned on accents and ghosts when <paramref name="note"/> was let go.
    /// YARG reads a note when it's let go, and the event switches how it reads the drums track's notes from there
    /// on, not for the whole track, so a note let go before it is read without them.
    /// </summary>
    public bool HasChartDynamics(MidiNote note) => ChartDynamics is { } at && note.OffIndex > at;

    /// <summary>
    /// The indexes of the events read here: the note-ons and note-offs paired, the sysex events paired, and the
    /// text events. The rest (the first event, notes ignored or never let go, any other event) weren't.
    /// </summary>
    public IReadOnlySet<int> Consumed => _consumed ??= ConsumedOf();

    /// <summary>Gives back <see cref="Notes"/>, if it was rented. Nothing may use the track after.</summary>
    public void Return()
    {
        if (_rented)
            ListPool<MidiNote>.Return(Notes);
    }

    /// <summary>
    /// Marks the events <see cref="Consumed"/> holds, by their indexes in <paramref name="marks"/>, without making the
    /// set: a track's worth of hashed ints for each part loaded.
    /// </summary>
    public void MarkConsumed(Span<bool> marks)
    {
        foreach (var note in Notes)
            marks[note.OnIndex] = marks[note.OffIndex] = true;
        foreach (var sysex in Sysex)
            marks[sysex.OnIndex] = marks[sysex.OffIndex] = true;
        foreach (var text in Texts)
            marks[text.Index] = true;
    }

    /// <summary>The notes on <paramref name="pitch"/>, of any channel, in the order they're let go.</summary>
    public List<TickSpan> Spans(int pitch)
    {
        var spans = new List<TickSpan>();
        foreach (var note in Notes)
        {
            if (note.Pitch == pitch)
                spans.Add(new TickSpan(note.Start, note.End));
        }

        return spans;
    }

    /// <summary>
    /// The sysex phrases of <paramref name="code"/> for <paramref name="difficulty"/>, then those for every
    /// difficulty, each in the order they're let go.
    /// </summary>
    public List<TickSpan> SysexFor(ChartDifficulty difficulty, int code)
    {
        var spans = new List<TickSpan>();
        foreach (int of in (ReadOnlySpan<int>)[(int)difficulty, MidiLayout.SysexAllDifficulties])
        {
            foreach (var sysex in Sysex)
            {
                if (sysex.Difficulty == of && sysex.Code == code)
                    spans.Add(new TickSpan(sysex.Start, sysex.End));
            }
        }

        return spans;
    }

    /// <param name="enhancedOpens">Whether an <c>ENHANCED_OPENS</c> event can turn on open notes: five-fret only.</param>
    /// <param name="rent">
    /// Whether <see cref="Notes"/> is rented from <see cref="ListPool{T}"/>, for a track read only while a chart is: the
    /// caller then gives it back with <see cref="Return"/>.
    /// </param>
    public static MidiTrackNotes Of(byte[] bytes, List<MidiEvent> events, bool enhancedOpens, bool rent = false)
    {
        // A note takes a note-on and a note-off.
        var track = new MidiTrackNotes(events.Count / 2, rent);
        var held = new List<(int Pitch, int Channel, int Velocity, long Start, int Index)>();
        var heldSysex = new List<(int Difficulty, int Code, long Start, int Index)>();
        bool opensOn = false;

        // YARG skips a track's first event, which is meant to be its name.
        for (int index = 1; index < events.Count; index++)
        {
            var e = events[index];
            if (e.Kind is 0x80 or 0x90)
            {
                int pitch = bytes[e.DataStart];
                int channel = e.Status & 0x0F;
                int velocity = bytes[e.DataStart + 1];
                int at = IndexOfHeld(held, pitch, channel);
                if (e.Kind == 0x90 && velocity > 0)
                {
                    if (at < 0)
                        held.Add((pitch, channel, velocity, e.Tick, index));
                }
                else if (at >= 0)
                {
                    var on = held[at];
                    track.Notes.Add(new MidiNote(pitch, channel, on.Velocity, on.Start, e.Tick, opensOn, on.Index, index));
                    held.RemoveAt(at);
                }
            }
            else if (e.IsSysex)
            {
                if (PhaseShiftOf(bytes, e) is not (int difficulty, int code, bool isOn))
                    continue;

                int at = IndexOfHeldSysex(heldSysex, difficulty, code);
                if (isOn)
                {
                    if (at < 0)
                        heldSysex.Add((difficulty, code, e.Tick, index));
                }
                else if (at >= 0)
                {
                    var on = heldSysex[at];
                    track.Sysex.Add(new MidiSysexSpan(difficulty, code, on.Start, e.Tick, on.Index, index));
                    heldSysex.RemoveAt(at);
                }
            }
            else if (TextOf(bytes, e) is var (type, text))
            {
                string normalized = ChartTextEvents.Normalize(text);
                if (normalized == "ENHANCED_OPENS" && enhancedOpens)
                    opensOn = true;
                else if (normalized == "ENABLE_CHART_DYNAMICS")
                    track.ChartDynamics ??= index;
                track.Texts.Add(new MidiText(e.Tick, type, normalized, index));
            }
        }

        return track;
    }

    /// <summary>
    /// A Phase Shift sysex event's difficulty, code and whether it's an on event, if <paramref name="e"/> is one:
    /// exactly <c>50 53 00 00</c>, the difficulty, the code, and 1 for on or 0 for off.
    /// </summary>
    public static (int Difficulty, int Code, bool On)? PhaseShiftOf(byte[] bytes, MidiEvent e)
    {
        if (!e.IsSysex)
            return null;
        var data = MidiFile.SysexOf(bytes, e);
        if (data.Length != 8 || data[0] != 0x50 || data[1] != 0x53 || data[2] != 0 || data[3] != 0 || data[6] > 1)
            return null;
        return (data[4], data[5], data[6] == 1);
    }

    private HashSet<int> ConsumedOf()
    {
        var consumed = new HashSet<int>();
        foreach (var note in Notes)
        {
            consumed.Add(note.OnIndex);
            consumed.Add(note.OffIndex);
        }

        foreach (var sysex in Sysex)
        {
            consumed.Add(sysex.OnIndex);
            consumed.Add(sysex.OffIndex);
        }

        foreach (var text in Texts)
            consumed.Add(text.Index);
        return consumed;
    }

    // Loops rather than FindIndex, whose lambda would capture the pitch: an allocation for every note event.
    private static int IndexOfHeld(List<(int Pitch, int Channel, int Velocity, long Start, int Index)> held, int pitch, int channel)
    {
        for (int i = 0; i < held.Count; i++)
        {
            if (held[i].Pitch == pitch && held[i].Channel == channel)
                return i;
        }

        return -1;
    }

    private static int IndexOfHeldSysex(List<(int Difficulty, int Code, long Start, int Index)> held, int difficulty, int code)
    {
        for (int i = 0; i < held.Count; i++)
        {
            if (held[i].Difficulty == difficulty && held[i].Code == code)
                return i;
        }

        return -1;
    }

    /// <summary>A text event's type and text: any text type but the copyright and the track name.</summary>
    private static (byte Type, string Text)? TextOf(byte[] bytes, MidiEvent e)
    {
        if (!e.IsMeta)
            return null;
        var (type, start, length) = MidiFile.MetaOf(bytes, e);
        return type is 0x01 or (>= 0x04 and <= 0x09) ? ((byte)type, MidiFile.DecodeText(bytes.AsSpan(start, length))) : null;
    }
}
