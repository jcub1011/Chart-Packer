using ChartPacker.Core.Charts;
using ChartPacker.Core.Validation;

namespace ChartPacker.Core.Charting.Commands;

/// <summary>
/// Takes notes out of one track and puts others in, as one change: a paste, a recorded pass or a transform such as lefty
/// flip (charter-spec C15). A note put in may go where one taken out was, but not onto a note that stays. The chords'
/// forcing and tap are set as given, and a chord left with no note loses its marking, which would mark nothing.
/// </summary>
/// <remarks>It keeps only what it changed, not a copy of the track (charter-spec §11, the undo stack's budget).</remarks>
public sealed class ReplaceNotes : ChartCommand
{
    private readonly EditableNote[] _remove;
    private readonly EditableNote[] _add;
    private readonly FlaggedChord[] _flags;
    private readonly List<(long Tick, ChordFlags Previous)> _previous = [];

    /// <param name="remove">The notes taken out, each exactly as the track has it.</param>
    /// <param name="add">The notes put in, no two at one place (tick, lane and kick).</param>
    /// <param name="flags">
    /// The marking each of these chords gets, in place of what it has (<see cref="ChordFlags.None"/> to clear it); the
    /// chords of the other ticks keep theirs.
    /// </param>
    /// <exception cref="ArgumentException">There's nothing to take out, put in or mark.</exception>
    public ReplaceNotes(ChartInstrument part, ChartDifficulty difficulty, IEnumerable<EditableNote> remove, IEnumerable<EditableNote> add, IEnumerable<FlaggedChord>? flags = null)
    {
        ArgumentNullException.ThrowIfNull(remove);
        ArgumentNullException.ThrowIfNull(add);
        Part = part;
        Difficulty = difficulty;
        _remove = [.. remove];
        _add = [.. add];
        _flags = flags is null ? [] : [.. flags];
        if (_remove.Length == 0 && _add.Length == 0 && _flags.Length == 0)
            throw new ArgumentException("There's nothing to change.", nameof(add));
        NoteEdits.CheckDistinct(_remove, nameof(remove));
        NoteEdits.CheckDistinct(_add, nameof(add));
        if (_flags.Select(static f => f.Tick).Distinct().Count() != _flags.Length)
            throw new ArgumentException("Each chord is marked once.", nameof(flags));
    }

    public ChartInstrument Part { get; }

    public ChartDifficulty Difficulty { get; }

    /// <summary>The notes taken out, as the track had them.</summary>
    public IReadOnlyList<EditableNote> Removed => _remove;

    /// <summary>The notes put in.</summary>
    public IReadOnlyList<EditableNote> Added => _add;

    private protected override ChartChange Do(EditableChart chart)
    {
        var track = TrackOf(chart, Part, Difficulty);
        NoteEdits.CheckPresent(track, Part, _remove);
        var leaving = new HashSet<EditableNote>(_remove);
        foreach (var note in _add)
        {
            track.Notes.Check(note);
            int index = track.Notes.IndexOfPlace(note);
            if (index >= 0 && !leaving.Contains(track.Notes[index]))
                throw new InvalidOperationException(Invariant($"{Part} {Difficulty} already has {Describe(note)}."));
        }

        foreach (var chord in _flags.Where(static f => f.Flags != ChordFlags.None))
        {
            if (track.Kind == ChartInstrumentKind.Drums)
                throw new InvalidOperationException("Drum chords can't be forced or tapped.");
            bool hasNote = _add.Any(n => n.Tick == chord.Tick);
            foreach (var there in track.Notes.At(chord.Tick))
                hasNote |= !leaving.Contains(there);
            if (!hasNote)
                throw new InvalidOperationException(Invariant($"{Part} {Difficulty} has no chord at tick {chord.Tick} to mark."));
        }

        NoteEdits.Replace(track.Notes, _remove, _add);
        _previous.Clear();
        foreach (var chord in _flags)
            Mark(track, chord.Tick, chord.Flags);

        // A chord left with no note keeps no marking, which would mark nothing.
        foreach (long tick in _remove.Select(static n => n.Tick).Distinct())
        {
            if (track.Notes.At(tick).IsEmpty)
                Mark(track, tick, ChordFlags.None);
        }

        return NoteEdits.Spans(Part, Difficulty, _remove)
            .Merge(NoteEdits.Spans(Part, Difficulty, _add))
            .Merge(ChartChange.Of(_flags.Select(f => new ChartLocation(Part, Difficulty, f.Tick, f.Tick))));
    }

    private protected override void Undo(EditableChart chart)
    {
        var track = TrackOf(chart, Part, Difficulty);
        NoteEdits.Replace(track.Notes, _add, _remove);
        for (int i = _previous.Count - 1; i >= 0; i--)
            track.SetFlags(_previous[i].Tick, _previous[i].Previous);
        _previous.Clear();
    }

    private void Mark(EditableTrack track, long tick, ChordFlags flags)
    {
        var previous = track.SetFlags(tick, flags);
        if (previous != flags)
            _previous.Add((tick, previous));
    }
}

/// <summary>
/// Puts new contents in one track in place of all it had: its notes, its chords' forcing and tap, and, for drums, its
/// mixes (which hold disco flip). Reduce adds its draft this way (charter-spec C9 item 2).
/// </summary>
public sealed class ReplaceTrack : ChartCommand
{
    private readonly EditableNote[] _notes;
    private readonly FlaggedChord[] _flags;
    private readonly DrumMix[]? _mixes;
    private (EditableNote[] Notes, FlaggedChord[] Flags, DrumMix[] Mixes)? _old;

    /// <param name="mixes">The drum mixes, or null to leave the track's as they are.</param>
    public ReplaceTrack(ChartInstrument part, ChartDifficulty difficulty, IEnumerable<EditableNote> notes, IEnumerable<FlaggedChord> flags, IEnumerable<DrumMix>? mixes = null)
    {
        ArgumentNullException.ThrowIfNull(notes);
        ArgumentNullException.ThrowIfNull(flags);
        Part = part;
        Difficulty = difficulty;
        _notes = [.. notes];
        _flags = [.. flags];
        _mixes = mixes is null ? null : [.. mixes];
    }

    public ChartInstrument Part { get; }

    public ChartDifficulty Difficulty { get; }

    private protected override ChartChange Do(EditableChart chart)
    {
        var track = TrackOf(chart, Part, Difficulty);
        TrackFill.Check(track, _notes, _flags, _mixes);
        _old = TrackFill.Take(track);
        TrackFill.Fill(track, _notes, _flags, _mixes);
        return ChartChange.Of(Part, Difficulty, 0, long.MaxValue);
    }

    private protected override void Undo(EditableChart chart)
    {
        if (_old is not { } old)
            return;
        TrackFill.Fill(TrackOf(chart, Part, Difficulty), old.Notes, old.Flags, old.Mixes);
        _old = null;
    }
}

/// <summary>
/// Copies one difficulty of a part to another, in place of all it had (charter-spec C9 item 1): its notes, its chords'
/// forcing and tap, and its drum mixes, which hold disco flip. Tom markers and phrases belong to the part, not a
/// difficulty, so they're shared already. A 2× kick, which only Expert has, isn't copied below it.
/// </summary>
public sealed class CopyDifficulty : ChartCommand
{
    private (EditableNote[] Notes, FlaggedChord[] Flags, DrumMix[] Mixes)? _old;

    /// <exception cref="ArgumentException"><paramref name="from"/> and <paramref name="to"/> are one difficulty.</exception>
    public CopyDifficulty(ChartInstrument part, ChartDifficulty from, ChartDifficulty to)
    {
        if (from == to)
            throw new ArgumentException("A difficulty is copied to another.", nameof(to));
        Part = part;
        From = from;
        To = to;
    }

    public ChartInstrument Part { get; }

    public ChartDifficulty From { get; }

    public ChartDifficulty To { get; }

    /// <summary>How many 2× kicks the last apply left out, as <see cref="To"/> isn't Expert.</summary>
    public int DroppedDoubleKicks { get; private set; }

    private protected override ChartChange Do(EditableChart chart)
    {
        var part = PartOf(chart, Part);
        var source = part[From];
        var target = part[To];
        var notes = source.Notes.AsSpan().ToArray();
        int dropped = 0;
        if (To != ChartDifficulty.Expert)
        {
            dropped = notes.Count(static n => n.DoubleKick);
            if (dropped > 0)
                notes = [.. notes.Where(static n => !n.DoubleKick)];
        }

        var flags = source.FlaggedChords.AsSpan().ToArray();
        DrumMix[]? mixes = part.Kind == ChartInstrumentKind.Drums ? [.. source.Mixes] : null;
        TrackFill.Check(target, notes, flags, mixes);
        _old = TrackFill.Take(target);
        TrackFill.Fill(target, notes, flags, mixes);
        DroppedDoubleKicks = dropped;
        return ChartChange.Of(Part, To, 0, long.MaxValue);
    }

    private protected override void Undo(EditableChart chart)
    {
        if (_old is not { } old)
            return;
        TrackFill.Fill(TrackOf(chart, Part, To), old.Notes, old.Flags, old.Mixes);
        _old = null;
    }
}

/// <summary>What the commands that fill a whole track share.</summary>
internal static class TrackFill
{
    /// <summary>Throws, changing nothing, when <paramref name="track"/> can't hold what it's to be filled with.</summary>
    public static void Check(EditableTrack track, EditableNote[] notes, FlaggedChord[] flags, DrumMix[]? mixes)
    {
        // Filled into a track of its own first, which checks every item as the track would.
        var scratch = new EditableTrack(track.Kind, track.Difficulty);
        foreach (var note in notes)
        {
            if (!scratch.Notes.Add(note))
                throw new ArgumentException(FormattableString.Invariant($"Two of the notes are at tick {note.Tick} in lane {note.Lane}."), nameof(notes));
        }

        foreach (var chord in flags)
        {
            if (track.Kind == ChartInstrumentKind.Drums)
                throw new InvalidOperationException("Drum chords can't be forced or tapped.");
            if (!scratch.FlaggedChords.Add(chord))
                throw new ArgumentException(FormattableString.Invariant($"The chord at tick {chord.Tick} is marked twice."), nameof(flags));
            if (scratch.Notes.At(chord.Tick).IsEmpty)
                throw new InvalidOperationException(FormattableString.Invariant($"There's no chord at tick {chord.Tick} to mark."));
        }

        foreach (var mix in mixes ?? [])
            scratch.Mixes.Add(mix);
    }

    /// <summary>What <paramref name="track"/> holds, to put back.</summary>
    public static (EditableNote[] Notes, FlaggedChord[] Flags, DrumMix[] Mixes) Take(EditableTrack track) =>
        (track.Notes.AsSpan().ToArray(), track.FlaggedChords.AsSpan().ToArray(), track.Mixes.AsSpan().ToArray());

    /// <summary>Empties <paramref name="track"/> and fills it, leaving its mixes when <paramref name="mixes"/> is null.</summary>
    public static void Fill(EditableTrack track, EditableNote[] notes, FlaggedChord[] flags, DrumMix[]? mixes)
    {
        ChartTickSnapshot.Refill(track.Notes, notes);
        ChartTickSnapshot.Refill(track.FlaggedChords, flags);
        if (mixes is not null)
            ChartTickSnapshot.Refill(track.Mixes, mixes);
    }
}
