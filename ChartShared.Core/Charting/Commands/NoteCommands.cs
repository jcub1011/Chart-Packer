using ChartShared.Core.Charts;
using ChartShared.Core.Validation;

namespace ChartShared.Core.Charting.Commands;

/// <summary>Adds notes to one track. Each must go where the track has no note.</summary>
public sealed class AddNotes : ChartCommand
{
    private readonly EditableNote[] _notes;

    /// <param name="notes">The notes, at least one, no two at one place (tick, lane and kick).</param>
    public AddNotes(ChartInstrument part, ChartDifficulty difficulty, IEnumerable<EditableNote> notes)
    {
        Part = part;
        Difficulty = difficulty;
        _notes = AtLeastOne(notes, nameof(notes));
        NoteEdits.CheckDistinct(_notes, nameof(notes));
    }

    public ChartInstrument Part { get; }

    public ChartDifficulty Difficulty { get; }

    public IReadOnlyList<EditableNote> Notes => _notes;

    private protected override ChartChange Do(EditableChart chart)
    {
        var track = TrackOf(chart, Part, Difficulty);
        foreach (var note in _notes)
        {
            track.Notes.Check(note);
            if (track.Notes.IndexOfPlace(note) >= 0)
                throw new InvalidOperationException(Invariant($"{Part} {Difficulty} already has {Describe(note)}."));
        }

        foreach (var note in _notes)
            track.Notes.Add(note);
        return NoteEdits.Spans(Part, Difficulty, _notes);
    }

    private protected override void Undo(EditableChart chart)
    {
        var notes = TrackOf(chart, Part, Difficulty).Notes;
        foreach (var note in _notes)
            notes.Remove(note);
    }
}

/// <summary>
/// Removes notes from one track. A chord left with no note loses its forcing and tap, which would mark nothing, and gets
/// them back when the removal is undone.
/// </summary>
public sealed class RemoveNotes : ChartCommand
{
    private readonly EditableNote[] _notes;
    private readonly List<(long Tick, ChordFlags Flags)> _cleared = [];

    /// <param name="notes">The notes, at least one, each exactly as the track has it.</param>
    public RemoveNotes(ChartInstrument part, ChartDifficulty difficulty, IEnumerable<EditableNote> notes)
    {
        Part = part;
        Difficulty = difficulty;
        _notes = AtLeastOne(notes, nameof(notes));
        NoteEdits.CheckDistinct(_notes, nameof(notes));
    }

    public ChartInstrument Part { get; }

    public ChartDifficulty Difficulty { get; }

    public IReadOnlyList<EditableNote> Notes => _notes;

    private protected override ChartChange Do(EditableChart chart)
    {
        var track = TrackOf(chart, Part, Difficulty);
        NoteEdits.CheckPresent(track, Part, _notes);

        foreach (var note in _notes)
            track.Notes.Remove(note);
        _cleared.Clear();
        foreach (long tick in _notes.Select(static n => n.Tick).Distinct())
        {
            if (track.Notes.At(tick).IsEmpty)
            {
                var flags = track.SetFlags(tick, ChordFlags.None);
                if (flags != ChordFlags.None)
                    _cleared.Add((tick, flags));
            }
        }

        return NoteEdits.Spans(Part, Difficulty, _notes);
    }

    private protected override void Undo(EditableChart chart)
    {
        var track = TrackOf(chart, Part, Difficulty);
        foreach (var note in _notes)
            track.Notes.Add(note);
        foreach (var (tick, flags) in _cleared)
            track.SetFlags(tick, flags);
    }
}

/// <summary>
/// Moves notes of one track by a number of ticks and lanes, each keeping its length, accent or ghost and kick. None may
/// land on a note that isn't moving, or in a lane the part hasn't. An open note or a kick moves only in time, and no note
/// moves into the open or kick lane, as that would make it another kind of note.
/// </summary>
/// <remarks>
/// A chord's forcing and tap belong to its tick. When every note of a chord moves by the same number of ticks, its marking
/// moves with them, added to whatever the chord they land on is marked; when only some move, or they move apart, the
/// marking stays with the tick.
/// </remarks>
public sealed class MoveNotes : ChartCommand
{
    private readonly EditableNote[] _notes;
    private readonly long[] _tickDeltas;
    private readonly List<(long Tick, ChordFlags Previous)> _flags = [];
    private EditableNote[] _moved = [];

    /// <param name="notes">The notes, at least one, each exactly as the track has it.</param>
    /// <param name="tickDelta">How many ticks later they move; negative for earlier.</param>
    /// <param name="laneDelta">How many lanes up they move; negative for down.</param>
    public MoveNotes(ChartInstrument part, ChartDifficulty difficulty, IEnumerable<EditableNote> notes, long tickDelta, int laneDelta)
    {
        Part = part;
        Difficulty = difficulty;
        _notes = AtLeastOne(notes, nameof(notes));
        NoteEdits.CheckDistinct(_notes, nameof(notes));
        _tickDeltas = [.. _notes.Select(_ => tickDelta)];
        TickDelta = tickDelta;
        LaneDelta = laneDelta;
    }

    /// <summary>Moves each note in time by a number of ticks of its own, as Nudge to onset does (charter-spec C14 item 3).</summary>
    /// <param name="moves">The notes, at least one, each exactly as the track has it, and how many ticks later each moves.</param>
    public MoveNotes(ChartInstrument part, ChartDifficulty difficulty, IEnumerable<(EditableNote Note, long TickDelta)> moves)
    {
        ArgumentNullException.ThrowIfNull(moves);
        var list = moves.ToList();
        Part = part;
        Difficulty = difficulty;
        _notes = AtLeastOne(list.Select(static m => m.Note), nameof(moves));
        NoteEdits.CheckDistinct(_notes, nameof(moves));
        _tickDeltas = [.. list.Select(static m => m.TickDelta)];
        TickDelta = _tickDeltas[0];
        LaneDelta = 0;
    }

    public ChartInstrument Part { get; }

    public ChartDifficulty Difficulty { get; }

    /// <summary>The notes as they were before moving.</summary>
    public IReadOnlyList<EditableNote> Notes => _notes;

    /// <summary>How many ticks the first note moves; see <see cref="TickDeltas"/> for each.</summary>
    public long TickDelta { get; }

    /// <summary>How many ticks each note moves, in the order of <see cref="Notes"/>.</summary>
    public IReadOnlyList<long> TickDeltas => _tickDeltas;

    public int LaneDelta { get; }

    /// <summary>The notes where they moved to, in the order of <see cref="Notes"/>; empty until applied.</summary>
    public IReadOnlyList<EditableNote> Moved => _moved;

    private protected override ChartChange Do(EditableChart chart)
    {
        var track = TrackOf(chart, Part, Difficulty);
        NoteEdits.CheckPresent(track, Part, _notes);

        var moved = new EditableNote[_notes.Length];
        for (int i = 0; i < _notes.Length; i++)
        {
            var note = _notes[i];
            long tick = note.Tick + _tickDeltas[i];
            if (tick < 0)
                throw new InvalidOperationException(Invariant($"Moving {Describe(note)} {_tickDeltas[i]} ticks would put it before the start of the song."));
            moved[i] = note with { Tick = tick, Lane = note.Lane + LaneDelta };

            // Moving between an open (or a kick) and the other lanes would change what the note is, not where it is.
            if (LaneDelta != 0 && (IsApart(track.Kind, note.Lane) || IsApart(track.Kind, moved[i].Lane)))
                throw new ArgumentException(Invariant($"{Describe(note)} can't move to lane {moved[i].Lane}: an open note or a kick only moves in time."));
            try
            {
                track.Notes.Check(moved[i]);
            }
            catch (ArgumentException e)
            {
                throw new ArgumentException(Invariant($"{Describe(note)} can't move to lane {moved[i].Lane}. {e.Message}"), e);
            }
        }

        var moving = new HashSet<EditableNote>(_notes);
        for (int i = 0; i < moved.Length; i++)
        {
            int index = track.Notes.IndexOfPlace(moved[i]);
            if (index >= 0 && !moving.Contains(track.Notes[index]))
                throw new InvalidOperationException(Invariant($"{Describe(_notes[i])} can't move onto {Describe(track.Notes[index])} of {Part} {Difficulty}."));
        }

        var flags = FlagsAfter(track);

        foreach (var note in _notes)
            track.Notes.Remove(note);
        foreach (var note in moved)
            track.Notes.Add(note);
        _flags.Clear();
        foreach (var (tick, value) in flags)
        {
            var previous = track.SetFlags(tick, value);
            if (previous != value)
                _flags.Add((tick, previous));
        }

        _moved = moved;
        return NoteEdits.Spans(Part, Difficulty, _notes).Merge(NoteEdits.Spans(Part, Difficulty, moved));
    }

    private protected override void Undo(EditableChart chart)
    {
        var track = TrackOf(chart, Part, Difficulty);
        foreach (var note in _moved)
            track.Notes.Remove(note);
        foreach (var note in _notes)
            track.Notes.Add(note);
        foreach (var (tick, previous) in _flags)
            track.SetFlags(tick, previous);
    }

    /// <summary>Whether <paramref name="lane"/> is one a note can't move into or out of by lanes: a fret part's open, or the drums' kick.</summary>
    private static bool IsApart(ChartInstrumentKind kind, int lane) => lane == (kind == ChartInstrumentKind.Drums ? 0 : ChartNote.OpenLane);

    // The marking of each tick it changes, once the chords whose notes all move together have taken their marking with them.
    private List<(long Tick, ChordFlags Flags)> FlagsAfter(EditableTrack track)
    {
        var carried = new List<(long Tick, ChordFlags Flags, long Delta)>();
        foreach (var chord in Enumerable.Range(0, _notes.Length).GroupBy(i => _notes[i].Tick))
        {
            long delta = _tickDeltas[chord.First()];
            if (delta == 0 || chord.Any(i => _tickDeltas[i] != delta))
                continue;
            var flags = track.FlagsAt(chord.Key);
            if (flags != ChordFlags.None && track.Notes.At(chord.Key).Length == chord.Count())
                carried.Add((chord.Key, flags, delta));
        }

        var after = new Dictionary<long, ChordFlags>();

        // Every chord that moves leaves its tick first, so a chord landing where another left joins only what stays.
        foreach (var (tick, _, _) in carried)
            after[tick] = ChordFlags.None;
        foreach (var (tick, flags, delta) in carried)
        {
            long to = tick + delta;
            after[to] = (after.TryGetValue(to, out var there) ? there : track.FlagsAt(to)) | flags;
        }

        return [.. after.OrderBy(static p => p.Key).Select(static p => (p.Key, p.Value))];
    }
}

/// <summary>Sets how long notes of one track are held.</summary>
public sealed class SetSustain : ChartCommand
{
    private readonly EditableNote[] _notes;
    private readonly long[] _lengths;
    private EditableNote[] _changed = [];

    /// <summary>Gives every note the same length.</summary>
    /// <param name="notes">The notes, at least one, each exactly as the track has it.</param>
    /// <param name="length">The length, in ticks: <see cref="EditableNote.PlainLength"/> for no sustain.</param>
    public SetSustain(ChartInstrument part, ChartDifficulty difficulty, IEnumerable<EditableNote> notes, long length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        Part = part;
        Difficulty = difficulty;
        _notes = AtLeastOne(notes, nameof(notes));
        NoteEdits.CheckDistinct(_notes, nameof(notes));
        _lengths = [.. _notes.Select(_ => length)];
    }

    /// <summary>Gives each note its own length.</summary>
    /// <param name="lengths">Each note, exactly as the track has it, and its new length in ticks; at least one.</param>
    public SetSustain(ChartInstrument part, ChartDifficulty difficulty, IEnumerable<(EditableNote Note, long Length)> lengths)
    {
        var pairs = AtLeastOne(lengths, nameof(lengths));
        if (pairs.Any(static p => p.Length < 0))
            throw new ArgumentOutOfRangeException(nameof(lengths), "A note's length can't be negative.");
        Part = part;
        Difficulty = difficulty;
        _notes = [.. pairs.Select(static p => p.Note)];
        NoteEdits.CheckDistinct(_notes, nameof(lengths));
        _lengths = [.. pairs.Select(static p => p.Length)];
    }

    public ChartInstrument Part { get; }

    public ChartDifficulty Difficulty { get; }

    /// <summary>The notes as they were before.</summary>
    public IReadOnlyList<EditableNote> Notes => _notes;

    private protected override ChartChange Do(EditableChart chart)
    {
        var track = TrackOf(chart, Part, Difficulty);
        NoteEdits.CheckPresent(track, Part, _notes);
        var changed = new EditableNote[_notes.Length];
        for (int i = 0; i < _notes.Length; i++)
            changed[i] = _notes[i] with { Length = _lengths[i] };

        foreach (var note in changed)
            track.Notes.Check(note);

        NoteEdits.Replace(track.Notes, _notes, changed);
        _changed = changed;
        return NoteEdits.Spans(Part, Difficulty, _notes).Merge(NoteEdits.Spans(Part, Difficulty, changed));
    }

    private protected override void Undo(EditableChart chart) => NoteEdits.Replace(TrackOf(chart, Part, Difficulty).Notes, _changed, _notes);
}

/// <summary>Makes drum pad notes accents, ghosts or normal hits. A kick, which is always a normal hit, is refused.</summary>
public sealed class SetDynamics : ChartCommand
{
    private readonly EditableNote[] _notes;
    private EditableNote[] _changed = [];

    /// <param name="notes">The notes, at least one, each exactly as the drum track has it.</param>
    public SetDynamics(ChartInstrument part, ChartDifficulty difficulty, IEnumerable<EditableNote> notes, NoteDynamics dynamics)
    {
        if (!Enum.IsDefined(dynamics))
            throw new ArgumentOutOfRangeException(nameof(dynamics), dynamics, "A note is an accent, a ghost or neither.");
        Part = part;
        Difficulty = difficulty;
        _notes = AtLeastOne(notes, nameof(notes));
        NoteEdits.CheckDistinct(_notes, nameof(notes));
        Dynamics = dynamics;
    }

    public ChartInstrument Part { get; }

    public ChartDifficulty Difficulty { get; }

    /// <summary>The notes as they were before.</summary>
    public IReadOnlyList<EditableNote> Notes => _notes;

    public NoteDynamics Dynamics { get; }

    private protected override ChartChange Do(EditableChart chart)
    {
        var track = TrackOf(chart, Part, Difficulty);
        if (track.Kind != ChartInstrumentKind.Drums)
            throw new InvalidOperationException($"Only drum notes can be accents or ghosts, and {Part} isn't drums.");
        NoteEdits.CheckPresent(track, Part, _notes);
        var changed = new EditableNote[_notes.Length];
        for (int i = 0; i < _notes.Length; i++)
            changed[i] = _notes[i] with { Dynamics = Dynamics };

        foreach (var note in changed)
            track.Notes.Check(note);

        NoteEdits.Replace(track.Notes, _notes, changed);
        _changed = changed;
        return NoteEdits.Spans(Part, Difficulty, _notes);
    }

    private protected override void Undo(EditableChart chart) => NoteEdits.Replace(TrackOf(chart, Part, Difficulty).Notes, _changed, _notes);
}

/// <summary>
/// Turns force HOPO, force strum or tap on or off for chords of one track (charter-spec §4.1, Where flags live). The
/// other markings each chord has are left as they are.
/// </summary>
public sealed class SetChordFlags : ChartCommand
{
    private const ChordFlags AllChordFlags = ChordFlags.ForceHopo | ChordFlags.ForceStrum | ChordFlags.Tap;

    private readonly long[] _ticks;
    private ChordFlags[] _previous = [];

    /// <param name="ticks">The chords' ticks, at least one; each must have a note.</param>
    /// <param name="flags">The markings to turn on or off: one or more of force HOPO, force strum and tap.</param>
    /// <param name="on">Whether to turn them on.</param>
    public SetChordFlags(ChartInstrument part, ChartDifficulty difficulty, IEnumerable<long> ticks, ChordFlags flags, bool on)
    {
        if (flags == ChordFlags.None || (flags & ~AllChordFlags) != 0)
            throw new ArgumentException("A chord is marked force HOPO, force strum or tap.", nameof(flags));
        Part = part;
        Difficulty = difficulty;
        _ticks = AtLeastOne(ticks, nameof(ticks));
        if (_ticks.Distinct().Count() != _ticks.Length)
            throw new ArgumentException("Each chord is given once.", nameof(ticks));
        Flags = flags;
        On = on;
    }

    public ChartInstrument Part { get; }

    public ChartDifficulty Difficulty { get; }

    public IReadOnlyList<long> Ticks => _ticks;

    public ChordFlags Flags { get; }

    public bool On { get; }

    private protected override ChartChange Do(EditableChart chart)
    {
        var track = TrackOf(chart, Part, Difficulty);
        if (track.Kind == ChartInstrumentKind.Drums)
            throw new InvalidOperationException("Drum chords can't be forced or tapped.");
        foreach (long tick in _ticks)
        {
            if (track.Notes.At(tick).IsEmpty)
                throw new InvalidOperationException(Invariant($"{Part} {Difficulty} has no chord at tick {tick} to mark."));
        }

        var previous = new ChordFlags[_ticks.Length];
        for (int i = 0; i < _ticks.Length; i++)
        {
            var flags = track.FlagsAt(_ticks[i]);
            previous[i] = track.SetFlags(_ticks[i], On ? flags | Flags : flags & ~Flags);
        }

        _previous = previous;
        return ChartChange.Of(_ticks.Select(t => new ChartLocation(Part, Difficulty, t, t)));
    }

    private protected override void Undo(EditableChart chart)
    {
        var track = TrackOf(chart, Part, Difficulty);
        for (int i = 0; i < _ticks.Length; i++)
            track.SetFlags(_ticks[i], _previous[i]);
    }
}

/// <summary>What the note commands share.</summary>
internal static class NoteEdits
{
    /// <summary>Throws when two of <paramref name="notes"/> are at one place: one tick, lane and kick.</summary>
    public static void CheckDistinct(EditableNote[] notes, string name)
    {
        var places = new HashSet<(long, int, bool)>();
        foreach (var note in notes)
        {
            if (!places.Add((note.Tick, note.Lane, note.DoubleKick)))
                throw new ArgumentException(FormattableString.Invariant($"Two of the notes are at tick {note.Tick} in lane {note.Lane}."), name);
        }
    }

    /// <summary>Throws when <paramref name="track"/> hasn't each of <paramref name="notes"/> exactly.</summary>
    public static void CheckPresent(EditableTrack track, ChartInstrument part, EditableNote[] notes)
    {
        foreach (var note in notes)
        {
            if (track.Notes.IndexOf(note) < 0)
            {
                throw new InvalidOperationException(FormattableString.Invariant(
                    $"{part} {track.Difficulty} has no note at tick {note.Tick} in lane {note.Lane} of length {note.Length} ({note.Dynamics}{(note.DoubleKick ? ", 2x kick" : "")})."));
            }
        }
    }

    /// <summary>Puts each of <paramref name="to"/> in place of the note of <paramref name="from"/> at its place.</summary>
    public static void Replace(TickList<EditableNote> notes, EditableNote[] from, EditableNote[] to)
    {
        foreach (var note in from)
            notes.Remove(note);
        foreach (var note in to)
            notes.Add(note);
    }

    /// <summary>The ticks <paramref name="notes"/> cover, each from its start to its end.</summary>
    public static ChartChange Spans(ChartInstrument part, ChartDifficulty difficulty, IEnumerable<EditableNote> notes) =>
        ChartChange.Of(notes.Select(n => new ChartLocation(part, difficulty, n.Tick, n.Tick + n.Length)));
}
