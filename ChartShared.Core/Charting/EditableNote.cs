using ChartShared.Core.Charts;

namespace ChartShared.Core.Charting;

/// <summary>How hard a drum pad note is hit, as the author set it. A kick is always a normal hit, as the games read it.</summary>
public enum NoteDynamics
{
    /// <summary>A normal hit.</summary>
    None,

    /// <summary>An accent, hit harder: velocity 127 on a track with <c>[ENABLE_CHART_DYNAMICS]</c>.</summary>
    Accent,

    /// <summary>A ghost note, hit softer: velocity 1 on a track with <c>[ENABLE_CHART_DYNAMICS]</c>.</summary>
    Ghost,
}

/// <summary>A note's <see cref="NoteDynamics"/> as the games' <see cref="NoteFlags"/> have them, and back.</summary>
internal static class NoteDynamicsFlags
{
    public static NoteFlags ToFlags(this NoteDynamics dynamics) => dynamics switch
    {
        NoteDynamics.Accent => NoteFlags.Accent,
        NoteDynamics.Ghost => NoteFlags.Ghost,
        _ => NoteFlags.None,
    };

    /// <summary>The dynamics of <paramref name="flags"/>' accent or ghost; their other flags are ignored.</summary>
    public static NoteDynamics DynamicsOf(NoteFlags flags) =>
        (flags & NoteFlags.Accent) != 0 ? NoteDynamics.Accent : (flags & NoteFlags.Ghost) != 0 ? NoteDynamics.Ghost : NoteDynamics.None;
}

/// <summary>
/// One note as the author wrote it (charter-spec §4.1). Forcing and tap belong to the note's chord
/// (<see cref="EditableTrack.FlaggedChords"/>), and cymbal or tom to the part (<see cref="EditablePart.TomMarkers"/>).
/// </summary>
/// <param name="Tick">Where it starts, in chart ticks.</param>
/// <param name="Length">
/// How long it's held, in ticks, exactly as the MIDI note is held. The editor places plain notes
/// <see cref="PlainLength"/> long; 0 comes only from a loaded file. The sustain cutoff is applied only when the note is
/// shown.
/// </param>
/// <param name="Lane">
/// Its lane, numbered as <see cref="ChartNote.Lane"/> is: five-fret 0 to 4, green to orange; six-fret 0 to 5, the
/// three white frets then the three black; <see cref="ChartNote.OpenLane"/> an open note. Drums are numbered by pad:
/// 0 the kick, 1 red, 2 yellow, 3 blue, 4 the fourth pad (green, or five-lane orange), 5 the fifth (five-lane green).
/// </param>
/// <param name="Dynamics">Accent or ghost, on a drum pad only.</param>
/// <param name="DoubleKick">An Expert+ second kick: lane 0, on Expert drums only.</param>
public readonly record struct EditableNote(long Tick, long Length, int Lane, NoteDynamics Dynamics = NoteDynamics.None, bool DoubleKick = false)
{
    /// <summary>The length the editor gives a note without a sustain, as <see cref="ChartToMidi"/> writes one.</summary>
    public const long PlainLength = 1;

    /// <summary>
    /// The shortest hold Chart Builder treats as a sustain: shorter is a plain note, in the editor and in what it saves.
    /// </summary>
    public const long MinimumSustainLength = SustainCutoff.MinimumSustain;

    /// <summary>
    /// <paramref name="length"/> as Chart Builder gives a note: a plain note for a hold under
    /// <see cref="MinimumSustainLength"/>, otherwise the length itself.
    /// </summary>
    public static long SustainOrPlain(long length) => length < MinimumSustainLength ? PlainLength : length;

    /// <summary>
    /// <paramref name="length"/> as a loaded chart has it, with a hold under <see cref="MinimumSustainLength"/> made a
    /// plain note, keeping an already plain note's 0 as it is.
    /// </summary>
    public static long NormalizeSustain(long length) =>
        length > PlainLength && length < MinimumSustainLength ? PlainLength : length;

    /// <summary>The lowest lane an instrument of <paramref name="kind"/> has.</summary>
    public static int FirstLane(ChartInstrumentKind kind) => kind == ChartInstrumentKind.Drums ? 0 : ChartNote.OpenLane;

    /// <summary>The highest lane an instrument of <paramref name="kind"/> has.</summary>
    public static int LastLane(ChartInstrumentKind kind) => kind == ChartInstrumentKind.FiveFret ? 4 : 5;

    /// <summary>The order of a track's notes: by tick, then lane, then the kick before the 2× kick.</summary>
    internal static int Order(EditableNote a, EditableNote b)
    {
        int c = a.Tick.CompareTo(b.Tick);
        if (c == 0)
            c = a.Lane.CompareTo(b.Lane);
        return c != 0 ? c : a.DoubleKick.CompareTo(b.DoubleKick);
    }
}
