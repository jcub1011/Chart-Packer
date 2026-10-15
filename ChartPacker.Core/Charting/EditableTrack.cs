using ChartPacker.Core.Charts;

namespace ChartPacker.Core.Charting;

/// <summary>What's marked on a chord: every note on one tick of one difficulty.</summary>
[Flags]
public enum ChordFlags
{
    None = 0,

    /// <summary>Forced to a hammer-on or pull-off.</summary>
    ForceHopo = 1,

    /// <summary>Forced to a strum.</summary>
    ForceStrum = 2,

    /// <summary>A tap chord.</summary>
    Tap = 4,
}

/// <summary>
/// The flags marked on the chord at a tick. MIDI's markers cover every note on the ticks they span, so the flags belong
/// to the chord rather than to a note. A loaded file can mark one chord more than one way.
/// </summary>
public readonly record struct FlaggedChord(long Tick, ChordFlags Flags);

/// <summary>
/// One difficulty of a part (charter-spec §4.1): its notes, ordered by tick, then lane, then the kick before the 2×
/// kick, and what's marked per difficulty: forcing and tap per chord, and drum mixes, which hold disco flip.
/// </summary>
public sealed class EditableTrack
{
    private const ChordFlags AllChordFlags = ChordFlags.ForceHopo | ChordFlags.ForceStrum | ChordFlags.Tap;

    internal EditableTrack(ChartInstrumentKind kind, ChartDifficulty difficulty)
    {
        Kind = kind;
        Difficulty = difficulty;
        Notes = new TickList<EditableNote>(static n => n.Tick, EditableNote.Order, unique: true, note => CheckNote(note, kind, difficulty));
        FlaggedChords = new TickList<FlaggedChord>(static c => c.Tick, unique: true, check: CheckChord);
        Mixes = new TickList<DrumMix>(static m => m.Tick, check: mix => CheckMix(mix, kind));
    }

    private EditableTrack(EditableTrack source)
    {
        Kind = source.Kind;
        Difficulty = source.Difficulty;
        Notes = source.Notes.Clone();
        FlaggedChords = source.FlaggedChords.Clone();
        Mixes = source.Mixes.Clone();
    }

    public ChartDifficulty Difficulty { get; }

    /// <summary>How the part's notes are laid out, which sets the lanes a note can be in.</summary>
    public ChartInstrumentKind Kind { get; }

    /// <summary>The notes, at most one at each tick, lane and kind of kick.</summary>
    public TickList<EditableNote> Notes { get; }

    /// <summary>The chords marked forced or tap, one entry per tick, none of them <see cref="ChordFlags.None"/>.</summary>
    public TickList<FlaggedChord> FlaggedChords { get; }

    /// <summary>The drum mix events, in tick order, then the order they were added in.</summary>
    public TickList<DrumMix> Mixes { get; }

    /// <summary>The note at <paramref name="tick"/> in <paramref name="lane"/>, the 2× kick when <paramref name="doubleKick"/>.</summary>
    public bool TryGetNote(long tick, int lane, bool doubleKick, out EditableNote note)
    {
        int index = Notes.IndexOfPlace(new EditableNote(tick, 0, lane, DoubleKick: doubleKick));
        note = index >= 0 ? Notes[index] : default;
        return index >= 0;
    }

    /// <summary>What's marked on the chord at <paramref name="tick"/>.</summary>
    public ChordFlags FlagsAt(long tick)
    {
        int index = FlaggedChords.IndexOfPlace(new FlaggedChord(tick, ChordFlags.None));
        return index >= 0 ? FlaggedChords[index].Flags : ChordFlags.None;
    }

    /// <summary>Marks the chord at <paramref name="tick"/> with <paramref name="flags"/>, in place of what it had.</summary>
    /// <returns>What it had, for undoing.</returns>
    internal ChordFlags SetFlags(long tick, ChordFlags flags)
    {
        int index = FlaggedChords.IndexOfPlace(new FlaggedChord(tick, ChordFlags.None));
        var previous = index >= 0 ? FlaggedChords[index].Flags : ChordFlags.None;
        if (previous == flags)
            return previous;
        if (index >= 0)
            FlaggedChords.RemoveAt(index);
        if (flags != ChordFlags.None)
            FlaggedChords.Add(new FlaggedChord(tick, flags));
        return previous;
    }

    /// <summary>A copy that changes independently of this track.</summary>
    internal EditableTrack Clone() => new(this);

    // The checks are static, given what they need, so a copy's lists don't keep the track they were copied from.
    private static void CheckNote(EditableNote note, ChartInstrumentKind kind, ChartDifficulty difficulty)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(note.Tick, nameof(note));
        ArgumentOutOfRangeException.ThrowIfNegative(note.Length, nameof(note));
        if (note.Lane < EditableNote.FirstLane(kind) || note.Lane > EditableNote.LastLane(kind))
            throw new ArgumentException($"A {kind} note can't be in lane {note.Lane}.", nameof(note));
        // The games read accents and ghosts on the pads alone: a kick's would be shown but never played.
        if (note.Dynamics != NoteDynamics.None && (kind != ChartInstrumentKind.Drums || note.Lane == 0 || !Enum.IsDefined(note.Dynamics)))
            throw new ArgumentException("Only a drum pad note can be an accent or a ghost.", nameof(note));
        if (note.DoubleKick && (kind != ChartInstrumentKind.Drums || note.Lane != 0 || difficulty != ChartDifficulty.Expert))
            throw new ArgumentException("A 2x kick is a kick on Expert drums.", nameof(note));
    }

    private static void CheckChord(FlaggedChord chord)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(chord.Tick, nameof(chord));
        if (chord.Flags == ChordFlags.None || (chord.Flags & ~AllChordFlags) != 0)
            throw new ArgumentException("A flagged chord has one or more of force HOPO, force strum and tap.", nameof(chord));
    }

    private static void CheckMix(DrumMix mix, ChartInstrumentKind kind)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(mix.Tick, nameof(mix));
        if (kind != ChartInstrumentKind.Drums)
            throw new ArgumentException("Only drums have mix events.", nameof(mix));
        if (mix.Configuration is < 0 or > 5 || mix.Setting is null || !DrumMix.IsSetting(mix.Setting))
            throw new ArgumentException("A mix has a configuration from 0 to 5 and a setting of letters and digits.", nameof(mix));
    }
}
