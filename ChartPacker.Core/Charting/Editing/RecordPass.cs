using ChartPacker.Core.Charting.Commands;
using ChartPacker.Core.Charts;

namespace ChartPacker.Core.Charting.Editing;

/// <summary>How a recorded pass goes into the track (charter-spec C15 item 5).</summary>
public enum RecordMode
{
    /// <summary>Each chord recorded takes the place of the chord at its tick; the rest stays.</summary>
    Merge,

    /// <summary>Everything in the selected range, or the stretch the pass played over, is taken out first.</summary>
    Overwrite,
}

/// <summary>
/// One pass of record mode (charter-spec C15 item 5): while the song plays, number keys place notes in one lane at the
/// moment they're pressed, and holding a key holds a sustain on 5-fret and GHL. The session owns it
/// (<see cref="EditorSession.Recording"/>).
/// </summary>
/// <remarks>
/// <para>A press's time is in audio seconds as the editor draws the song: <c>SmoothPosition − latency × speed</c> when it
/// was pressed (§4.4.1), which the caller reads from the playback clock. It's quantised to the grid, or to an onset with
/// snap to onset on (<see cref="EditorSession.Quantize"/>). So a pass works at any speed: a fast passage can be recorded at
/// 50%.</para>
/// <para>The chart isn't changed while the pass goes on: its notes are drawn as a ghost over the lane
/// (<see cref="EditorSession.GhostTrack"/>), and the pass is added as one undoable step when it ends.</para>
/// </remarks>
public sealed class RecordPass
{
    private readonly EditorSession _session;
    private readonly Dictionary<NotePlace, EditableNote> _notes = [];
    private readonly Dictionary<int, NotePlace> _held = [];
    private EditableTrack? _ghost;

    internal RecordPass(EditorSession session, ChartTrackKey track, RecordMode mode, double startSeconds, (long From, long To)? punch)
    {
        _session = session;
        Track = track;
        Mode = mode;
        Punch = punch;
        StartTick = (long)Math.Max(0, Math.Floor(session.TickAt(startSeconds)));
    }

    /// <summary>The part and difficulty recorded into.</summary>
    public ChartTrackKey Track { get; }

    public RecordMode Mode { get; }

    /// <summary>The range the pass records into when overwriting: presses outside it are left out. Null for none.</summary>
    public (long From, long To)? Punch { get; }

    /// <summary>The tick the pass started at.</summary>
    public long StartTick { get; }

    /// <summary>How many notes the pass has recorded.</summary>
    public int Count => _notes.Count;

    /// <summary>The notes recorded so far, as a track of their own, for the lane to draw; no part of the chart.</summary>
    public EditableTrack Ghost => _ghost ??= GhostOf();

    private bool IsDrums => ChartInstruments.KindOf(Track.Instrument) == ChartInstrumentKind.Drums;

    /// <summary>
    /// A key pressed for the lane drawn in <paramref name="shownLane"/>, at <paramref name="audioSeconds"/> as the editor
    /// draws the song. A key held down and repeating is pressed once.
    /// </summary>
    /// <param name="doubleKick">Whether it's the 2× kick (Expert drums).</param>
    /// <returns>Whether a note was recorded.</returns>
    public bool Press(int shownLane, double audioSeconds, bool doubleKick = false)
    {
        int key = doubleKick ? int.MinValue : shownLane;
        if (_held.ContainsKey(key))
            return false;
        long tick = Math.Max(0, _session.Quantize(Math.Max(0, _session.TickAt(audioSeconds)), Track));
        if (Punch is { } punch && (tick < punch.From || tick > punch.To))
            return false;
        if (doubleKick && (!IsDrums || Track.Difficulty != ChartDifficulty.Expert))
            return false;

        int lane = _session.StoredLane(Track, tick, shownLane);
        if (!IsDrums)
        {
            // A fret takes the place of an open note pressed on its tick; an open note on frets' tick is left out.
            bool open = lane == ChartNote.OpenLane;
            var there = _notes.Values.Where(n => n.Tick == tick).ToList();
            if (open && there.Any(static n => n.Lane != ChartNote.OpenLane))
                return false;
            if (!open)
            {
                foreach (var note in there.Where(static n => n.Lane == ChartNote.OpenLane))
                    _notes.Remove(NotePlace.Of(note));
            }
        }

        var placed = new EditableNote(tick, EditableNote.PlainLength, lane, DoubleKick: doubleKick);
        var place = NotePlace.Of(placed);
        _notes[place] = placed;
        _held[key] = place;
        _ghost = null;
        _session.RaiseState();
        return true;
    }

    /// <summary>
    /// The key for the lane drawn in <paramref name="shownLane"/> let go at <paramref name="audioSeconds"/>: on 5-fret and
    /// GHL, the note is held until then, to the grid, or stays a plain note if that's less than a grid step.
    /// </summary>
    public void Release(int shownLane, double audioSeconds, bool doubleKick = false)
    {
        int key = doubleKick ? int.MinValue : shownLane;
        if (!_held.Remove(key, out var place) || IsDrums || !_notes.TryGetValue(place, out var note))
            return;
        long end = _session.Quantize(Math.Max(0, _session.TickAt(audioSeconds)), Track);
        double step = _session.Snap.IsOn ? _session.Snap.StepTicks(_session.Chart.Resolution) : 1;
        long length = end - note.Tick;
        if (length < step)
            return;
        _notes[place] = note with { Length = length };
        _ghost = null;
        _session.RaiseState();
    }

    /// <summary>Lets go of every key held, as when the window loses focus.</summary>
    public void ReleaseAll(double audioSeconds)
    {
        foreach (int key in _held.Keys.ToList())
            Release(key == int.MinValue ? 0 : key, audioSeconds, doubleKick: key == int.MinValue);
    }

    /// <summary>
    /// Ends the pass at <paramref name="stopSeconds"/>, adding what it recorded as one undoable step: over the selected
    /// range or the stretch played, in place of what's there, or merged with it.
    /// </summary>
    /// <returns>Whether anything was added.</returns>
    public bool Finish(double stopSeconds)
    {
        ReleaseAll(stopSeconds);
        _session.EndRecording(this);
        if (_notes.Count == 0 || _session.TrackOf(Track) is not { } track)
            return false;

        var notes = _notes.Values.ToList();
        notes.Sort(EditableNote.Order);
        long stop = (long)Math.Max(StartTick, Math.Ceiling(_session.TickAt(stopSeconds)));
        (long From, long To)? clear = Mode == RecordMode.Overwrite ? Punch ?? (StartTick, stop) : null;
        var plan = NoteMerge.Plan(track, notes, [], clear);
        if (plan.IsEmpty)
            return false;
        if (EditorTools.PlacementRefusal(track, plan.Add, plan.Remove as IReadOnlyCollection<EditableNote>) is { } why)
        {
            _session.Status = why;
            return false;
        }

        bool added = _session.Execute(new ReplaceNotes(Track.Instrument, Track.Difficulty, plan.Remove, plan.Add, plan.Flags));
        if (added)
            _session.Selection.Select(Track, notes);
        return added;
    }

    /// <summary>Ends the pass, adding nothing.</summary>
    public void Cancel() => _session.EndRecording(this);

    private EditableTrack GhostOf()
    {
        var ghost = new EditableTrack(ChartInstruments.KindOf(Track.Instrument), Track.Difficulty);
        foreach (var note in _notes.Values)
            ghost.Notes.Add(note);
        return ghost;
    }
}
