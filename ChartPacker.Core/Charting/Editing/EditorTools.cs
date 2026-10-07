using ChartPacker.Core.Charting.Commands;
using ChartPacker.Core.Charts;

namespace ChartPacker.Core.Charting.Editing;

/// <summary>
/// The editing tools (charter-spec §4.5, C3 item 1, C5): what a click, a drag or a key does to the chart. Both canvases
/// turn the pointer into <see cref="ChartPoint"/>s and pass them here, so every tool behaves the same in both views
/// (AC C3.2), and the tools' tests run against this rather than either canvas.
/// </summary>
/// <remarks>
/// A press starts a gesture, drags change it, and the release ends it, as one step on the undo stack. While it goes on, the
/// gesture's change is applied to the chart as a preview (<see cref="EditorSession.Preview"/>), so every view draws it.
/// </remarks>
public sealed class EditorTools
{
    private readonly EditorSession _session;
    private Gesture? _gesture;

    internal EditorTools(EditorSession session) => _session = session;

    /// <summary>Whether a gesture is going on.</summary>
    public bool IsActive => _gesture is not null;

    private EditableChart Chart => _session.Chart;

    private SnapGrid Snap => _session.Snap;

    private TempoMap Map => _session.TempoMap;

    /// <summary>Starts a gesture at <paramref name="point"/> with the current tool.</summary>
    public void Press(ChartPoint point, EditorModifiers modifiers = EditorModifiers.None)
    {
        Cancel();
        _session.Focused = point.Track;
        if (_session.TrackOf(point.Track) is null)
            return;
        bool shift = modifiers.HasFlag(EditorModifiers.Shift);
        _gesture = _session.Tool switch
        {
            EditorTool.Select => PressSelect(point, shift),
            EditorTool.Place => PressPlace(point, shift),
            EditorTool.Erase => new EraseGesture(this, point),
            EditorTool.Sustain => PressSustain(point),
            EditorTool.Tempo => PressTempo(point),
            _ => null,
        };
    }

    /// <summary>Moves the gesture on to <paramref name="point"/>.</summary>
    public void Drag(ChartPoint point) => _gesture?.Drag(point);

    /// <summary>Ends the gesture at <paramref name="point"/>, as one undoable step.</summary>
    public void Release(ChartPoint point)
    {
        var gesture = _gesture;
        _gesture = null;
        gesture?.Release(point);
    }

    /// <summary>Takes back the gesture going on, if there's one, leaving the chart and the selection as they were.</summary>
    public void Cancel()
    {
        var gesture = _gesture;
        _gesture = null;
        gesture?.Cancel();
        _session.Preview(null);
    }

    /// <summary>
    /// Places a note in <paramref name="lane"/> at the cursor of the focused lane, or removes the one there (charter-spec
    /// §9: 1–5, and 0 for an open note or the kick).
    /// </summary>
    public void ToggleNoteAtCursor(int lane)
    {
        if (_session.Focused is not { } key || _session.TrackOf(key) is not { } track)
            return;
        long tick = _session.CursorTick;
        if (track.TryGetNote(tick, lane, doubleKick: false, out var note))
        {
            _session.Execute(new RemoveNotes(key.Instrument, key.Difficulty, [note]));
            return;
        }

        if (PlacementRefusal(track, [new EditableNote(tick, EditableNote.PlainLength, lane)]) is { } why)
        {
            _session.Status = why;
            return;
        }

        var placed = new EditableNote(tick, EditableNote.PlainLength, lane);
        if (_session.Execute(new AddNotes(key.Instrument, key.Difficulty, [placed])))
            _session.Selection.Select(key, [placed]);
    }

    /// <summary>Moves the cursor <paramref name="steps"/> grid steps later, or earlier when negative (charter-spec §9).</summary>
    public void StepCursor(int steps)
    {
        long tick = (long)Math.Round(Math.Max(0, _session.TickAt(_session.Viewport.Cursor)));
        _session.MoveCursorTo(Snap.Step(tick, steps, Map));
    }

    /// <summary>Moves the cursor <paramref name="measures"/> measures later, or earlier when negative (PgUp and PgDn).</summary>
    public void StepCursorMeasures(int measures)
    {
        long tick = (long)Math.Round(Math.Max(0, _session.TickAt(_session.Viewport.Cursor)));
        _session.MoveCursorTo(SnapGrid.StepMeasures(tick, measures, Map));
    }

    /// <summary>
    /// Moves the selected notes <paramref name="steps"/> grid steps in time and <paramref name="lanes"/> lanes across
    /// (charter-spec §9, Alt+arrows), as one step.
    /// </summary>
    public void MoveSelection(int steps, int lanes, LaneSlots slots)
    {
        ArgumentNullException.ThrowIfNull(slots);
        if (_session.Selection.Track is not { } key)
            return;
        var notes = _session.Selection.Notes;
        if (notes.Count == 0)
            return;
        var first = notes[0];
        long tickDelta = steps == 0 ? 0 : Snap.Step(first.Tick, steps, Map) - first.Tick;
        int laneDelta = LaneDelta(notes, lanes, slots);
        if (tickDelta == 0 && laneDelta == 0)
            return;
        var command = new MoveNotes(key.Instrument, key.Difficulty, notes, tickDelta, laneDelta);
        if (MoveRefusal(key, notes, tickDelta, laneDelta) is { } why)
        {
            _session.Status = why;
            return;
        }

        if (_session.Execute(command))
            _session.Selection.Select(key, command.Moved);
    }

    /// <summary>Selects every note of the focused lane (Ctrl+A).</summary>
    public void SelectAll()
    {
        if (_session.Focused is not { } key || _session.TrackOf(key) is not { } track)
            return;
        _session.Selection.Select(key, track.Notes);
    }

    /// <summary>Deletes the selected notes and tempo markers (Delete), as one step.</summary>
    public void DeleteSelection()
    {
        var selection = _session.Selection;
        var steps = new List<Func<(IChartCommand?, string?)>>();
        if (selection.Track is { } key && selection.Notes is { Count: > 0 } notes)
            steps.Add(() => (new RemoveNotes(key.Instrument, key.Difficulty, notes), null));

        // Each removal is worked out against the chart the ones before it leave.
        foreach (long tick in selection.TempoTicks.Reverse())
        {
            steps.Add(() =>
            {
                var edit = TempoMapEdits.RemoveMarker(Chart, tick, _session.Delay, _session.KeepTimes);
                return (edit.Command, edit.Refusal);
            });
        }

        _session.ExecuteAsOne(steps);
    }

    /// <summary>
    /// Turns <paramref name="flag"/> on for every selected chord, or off when every one already has it (charter-spec §9:
    /// H, G and T). Force HOPO and force strum exclude each other.
    /// </summary>
    public void ToggleChordFlag(ChordFlags flag)
    {
        var selection = _session.Selection;
        if (selection.Track is not { } key || _session.TrackOf(key) is not { } track)
            return;
        var ticks = selection.Notes.Select(static n => n.Tick).Distinct().ToList();
        if (ticks.Count == 0)
            return;

        bool on = !ticks.All(t => track.FlagsAt(t).HasFlag(flag));
        var commands = new List<IChartCommand> { new SetChordFlags(key.Instrument, key.Difficulty, ticks, flag, on) };
        var other = flag switch
        {
            ChordFlags.ForceHopo => ChordFlags.ForceStrum,
            ChordFlags.ForceStrum => ChordFlags.ForceHopo,
            _ => ChordFlags.None,
        };
        if (on && other != ChordFlags.None)
        {
            var clash = ticks.Where(t => track.FlagsAt(t).HasFlag(other)).ToList();
            if (clash.Count > 0)
                commands.Insert(0, new SetChordFlags(key.Instrument, key.Difficulty, clash, other, on: false));
        }

        _session.Execute(commands.Count == 1 ? commands[0] : new CompositeCommand(commands));
    }

    /// <summary>
    /// Why notes can't be placed in <paramref name="track"/> as <paramref name="notes"/> (charter-spec C5 item 2): an open
    /// note in a chord with frets, or a fret on an open. Null when they can.
    /// </summary>
    /// <param name="leaving">Notes leaving their places, as in a move, which don't count.</param>
    public static string? PlacementRefusal(EditableTrack track, IEnumerable<EditableNote> notes, IReadOnlyCollection<EditableNote>? leaving = null)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(notes);
        if (track.Kind == ChartInstrumentKind.Drums)
            return null;

        var leavingSet = leaving is null ? null : new HashSet<NotePlace>(leaving.Select(NotePlace.Of));
        foreach (var chord in notes.GroupBy(static n => n.Tick))
        {
            bool open = false, fret = false;
            foreach (var note in chord)
            {
                open |= note.Lane == ChartNote.OpenLane;
                fret |= note.Lane != ChartNote.OpenLane;
            }

            foreach (var there in track.Notes.At(chord.Key))
            {
                if (leavingSet?.Contains(NotePlace.Of(there)) == true)
                    continue;
                open |= there.Lane == ChartNote.OpenLane;
                fret |= there.Lane != ChartNote.OpenLane;
            }

            if (open && fret)
                return "An open note can't be in a chord with frets.";
        }

        return null;
    }

    /// <summary>The note whose start or tail is under <paramref name="point"/>, the start first, or null for none.</summary>
    public static (EditableNote Note, bool IsTail)? HitTest(EditableTrack track, ChartPoint point, int? across)
    {
        ArgumentNullException.ThrowIfNull(track);
        long from = (long)Math.Floor(point.Tick - point.Tolerance);
        long to = (long)Math.Ceiling(point.Tick + point.Tolerance);
        EditableNote? best = null, bestAcross = null;
        double bestDistance = double.MaxValue, bestAcrossDistance = double.MaxValue;
        foreach (var note in track.Notes.Between(Math.Max(0, from), to))
        {
            double distance = Math.Abs(note.Tick - point.Tick);
            if (note.Lane == point.Lane && distance < bestDistance)
                (best, bestDistance) = (note, distance);
            else if (note.Lane == across && distance < bestAcrossDistance)
                (bestAcross, bestAcrossDistance) = (note, distance);
        }

        if ((best ?? bestAcross) is { } head)
            return (head, false);

        // A tail: the latest note in the lane held past the pointer.
        if (point.Lane is not { } lane)
            return null;
        var notes = track.Notes;
        for (int i = notes.FirstAfter((long)Math.Floor(point.Tick)) - 1; i >= 0; i--)
        {
            var note = notes[i];
            if (note.Lane == lane && note.Length > EditableNote.PlainLength && note.Tick + note.Length >= point.Tick)
                return (note, true);
        }

        return null;
    }

    private Gesture? PressSelect(ChartPoint point, bool shift)
    {
        var track = _session.TrackOf(point.Track)!;
        var selection = _session.Selection;
        if (HitTest(track, point, point.Slots.Across) is { } hit)
        {
            if (hit.IsTail)
                return SustainOf(point, hit.Note);
            if (shift)
                selection.Toggle(point.Track, hit.Note);
            else if (!selection.Contains(point.Track, hit.Note))
                selection.Select(point.Track, [hit.Note]);
            return selection.Contains(point.Track, hit.Note) ? new MoveGesture(this, point, hit.Note) : null;
        }

        return new BoxGesture(this, point, shift);
    }

    private Gesture? PressPlace(ChartPoint point, bool shift)
    {
        var track = _session.TrackOf(point.Track)!;
        int? lane = shift ? ApartLane(point.Slots) : point.Lane;
        if (lane is not { } placed)
            return null;
        long tick = Snap.Nearest(point.Tick, Map);
        if (track.TryGetNote(tick, placed, doubleKick: false, out var there))
        {
            _session.Selection.Select(point.Track, [there]);
            return new MoveGesture(this, point, there);
        }

        var note = new EditableNote(tick, EditableNote.PlainLength, placed);
        if (PlacementRefusal(track, [note]) is { } why)
        {
            _session.Status = why;
            return null;
        }

        var gesture = new PlaceGesture(this, point.Track, note);
        return gesture.Start() ? gesture : null;
    }

    private Gesture? PressSustain(ChartPoint point)
    {
        var track = _session.TrackOf(point.Track)!;
        return HitTest(track, point, point.Slots.Across) is { } hit ? SustainOf(point, hit.Note) : null;
    }

    private SustainGesture SustainOf(ChartPoint point, EditableNote grabbed)
    {
        var selection = _session.Selection;
        var notes = selection.Contains(point.Track, grabbed) ? selection.Notes : [grabbed];
        return new SustainGesture(this, point.Track, notes);
    }

    private Gesture? PressTempo(ChartPoint point)
    {
        var map = Chart.TempoMap;
        long from = (long)Math.Floor(point.Tick - point.Tolerance), to = (long)Math.Ceiling(point.Tick + point.Tolerance);
        var near = map.Tempos.Between(Math.Max(0, from), to);
        if (!near.IsEmpty)
        {
            long tick = near[0].Tick;
            double best = Math.Abs(tick - point.Tick);
            foreach (var marker in near)
            {
                if (Math.Abs(marker.Tick - point.Tick) < best)
                    (tick, best) = (marker.Tick, Math.Abs(marker.Tick - point.Tick));
            }

            _session.Selection.SelectTempos([tick]);
            return new TempoDragGesture(this, tick, anchored: !map.Anchors.At(tick).IsEmpty);
        }

        long at = Snap.Nearest(point.Tick, Map);
        var edit = TempoMapEdits.AddMarker(Chart, at);
        if (edit.IsRefused)
        {
            _session.Status = edit.Refusal;
            return null;
        }

        if (_session.Execute(edit.Command!))
            _session.Selection.SelectTempos([at]);
        return null;
    }

    /// <summary>How many lanes <paramref name="notes"/> can move by, towards <paramref name="wanted"/>: none when an open note or a kick is among them, and never out of the lanes.</summary>
    private static int LaneDelta(IReadOnlyList<EditableNote> notes, int wanted, LaneSlots slots)
    {
        int apart = ApartLane(slots);
        if (wanted == 0 || notes.Any(n => n.Lane == apart || slots.IndexOf(n.Lane) < 0))
            return 0;
        int movable = slots.Across is null ? slots.Slots.Count - 1 : slots.Slots.Count;
        int lowest = notes.Min(n => slots.IndexOf(n.Lane));
        int highest = notes.Max(n => slots.IndexOf(n.Lane));

        // The lanes are numbered in their column order, so a move across columns is a move across lane numbers.
        return Math.Clamp(wanted, -lowest, movable - 1 - highest);
    }

    /// <summary>
    /// The lane notes move in and out of only in time: an open note, or the drums' kick. A highway draws it across the
    /// others; a timeline gives it the last row.
    /// </summary>
    public static int ApartLane(LaneSlots slots)
    {
        ArgumentNullException.ThrowIfNull(slots);
        return slots.Across ?? slots.Slots[^1];
    }

    private string? MoveRefusal(ChartTrackKey key, IReadOnlyList<EditableNote> notes, long tickDelta, int laneDelta)
    {
        var track = _session.TrackOf(key)!;
        var moved = notes.Select(n => n with { Tick = n.Tick + tickDelta, Lane = n.Lane + laneDelta });
        return PlacementRefusal(track, moved, notes as IReadOnlyCollection<EditableNote>);
    }

    /// <summary>A gesture of one tool, from a press to its release.</summary>
    private abstract class Gesture(EditorTools tools)
    {
        protected EditorTools Tools { get; } = tools;

        protected EditorSession Session => Tools._session;

        public abstract void Drag(ChartPoint point);

        public virtual void Release(ChartPoint point)
        {
            Drag(point);
            Session.CommitPreview();
        }

        public virtual void Cancel()
        {
        }
    }

    /// <summary>Drags selected notes in time (to the grid) and across lanes.</summary>
    private sealed class MoveGesture : Gesture
    {
        private readonly ChartTrackKey _track;
        private readonly ChartPoint _pressed;
        private readonly EditableNote _grabbed;
        private readonly IReadOnlyList<EditableNote> _notes;
        private readonly IReadOnlyCollection<NotePlace> _selected;
        private MoveNotes? _move;

        public MoveGesture(EditorTools tools, ChartPoint pressed, EditableNote grabbed)
            : base(tools)
        {
            _track = pressed.Track;
            _pressed = pressed;
            _grabbed = grabbed;
            _notes = Session.Selection.Notes;
            _selected = [.. Session.Selection.Places];
        }

        public override void Drag(ChartPoint point)
        {
            long tick = Tools.Snap.Nearest(_grabbed.Tick + (point.Tick - _pressed.Tick), Tools.Map);
            long tickDelta = Math.Max(tick - _grabbed.Tick, -_notes.Min(static n => n.Tick));
            int apart = ApartLane(point.Slots);
            int wanted = point.Lane is { } lane && _pressed.Lane is { } from && lane != apart && from != apart
                ? point.Slots.IndexOf(lane) - point.Slots.IndexOf(from)
                : 0;
            int laneDelta = LaneDelta(_notes, wanted, point.Slots);
            if (_move is { } current && current.TickDelta == tickDelta && current.LaneDelta == laneDelta)
                return;

            if (tickDelta == 0 && laneDelta == 0)
            {
                Session.Preview(null);
                _move = null;
                Session.Selection.SelectPlaces(_track, _selected);
                return;
            }

            if (Tools.MoveRefusal(_track, _notes, tickDelta, laneDelta) is { } why)
            {
                Session.Status = why;
                return;
            }

            var move = new MoveNotes(_track.Instrument, _track.Difficulty, _notes, tickDelta, laneDelta);
            if (Session.Preview(move))
            {
                _move = move;
                Session.Selection.Select(_track, move.Moved);
            }
        }

        public override void Release(ChartPoint point)
        {
            Drag(point);
            if (Session.CommitPreview() is MoveNotes move)
                Session.Selection.Select(_track, move.Moved);
        }

        public override void Cancel() => Session.Selection.SelectPlaces(_track, _selected);
    }

    /// <summary>Drags the tails of notes, setting their lengths to end on the grid.</summary>
    private sealed class SustainGesture(EditorTools tools, ChartTrackKey track, IReadOnlyList<EditableNote> notes) : Gesture(tools)
    {
        private long _end = -1;

        public override void Drag(ChartPoint point)
        {
            long end = Tools.Snap.Nearest(point.Tick, Tools.Map);
            if (end == _end)
                return;
            _end = end;
            var lengths = notes
                .Select(n => (Note: n, Length: Math.Max(EditableNote.PlainLength, end - n.Tick)))
                .Where(p => p.Length != p.Note.Length)
                .ToList();
            Session.Preview(lengths.Count == 0 ? null : new SetSustain(track.Instrument, track.Difficulty, lengths));
        }
    }

    /// <summary>Places a note, and drags out its sustain.</summary>
    private sealed class PlaceGesture(EditorTools tools, ChartTrackKey track, EditableNote note) : Gesture(tools)
    {
        private long _length = -1;

        public bool Start() => Preview(EditableNote.PlainLength);

        public override void Drag(ChartPoint point)
        {
            long end = Tools.Snap.Nearest(point.Tick, Tools.Map);
            Preview(end > note.Tick ? end - note.Tick : EditableNote.PlainLength);
        }

        public override void Release(ChartPoint point)
        {
            Drag(point);
            if (Session.CommitPreview() is AddNotes added)
                Session.Selection.Select(track, added.Notes);
        }

        private bool Preview(long length)
        {
            if (length == _length)
                return true;
            _length = length;
            return Session.Preview(new AddNotes(track.Instrument, track.Difficulty, [note with { Length = length }]));
        }
    }

    /// <summary>Removes each note the pointer passes over.</summary>
    private sealed class EraseGesture : Gesture
    {
        private readonly ChartTrackKey _track;
        private readonly List<EditableNote> _erased = [];

        public EraseGesture(EditorTools tools, ChartPoint pressed)
            : base(tools)
        {
            _track = pressed.Track;
            Drag(pressed);
        }

        public override void Drag(ChartPoint point)
        {
            if (point.Track != _track)
                return;

            // The notes erased so far are out of the chart, as the preview, so the hit is among those left.
            var track = Session.TrackOf(_track)!;
            if (HitTest(track, point, point.Slots.Across) is not { } hit)
                return;
            _erased.Add(hit.Note);
            Session.Preview(new RemoveNotes(_track.Instrument, _track.Difficulty, _erased));
        }
    }

    /// <summary>Drags a tempo marker: in ticks when it isn't anchored, in time when it is (charter-spec C4 item 2).</summary>
    private sealed class TempoDragGesture(EditorTools tools, long tick, bool anchored) : Gesture(tools)
    {
        private double _last = double.NaN;

        public override void Drag(ChartPoint point)
        {
            // Where the pointer is, in audio seconds for an anchored marker, or in ticks: as the chart is with the
            // preview, which the pointer was mapped through.
            double target = anchored ? Session.AudioAt(point.Tick) : Tools.Snap.Nearest(point.Tick, Tools.Map);
            if (target == _last)
                return;
            _last = target;

            // Each edit is worked out against the chart without the last preview.
            Session.Preview(null);
            TempoEdit edit;
            if (anchored)
            {
                if (Math.Abs(target - Session.AudioAt(tick)) < 1e-6)
                    return;
                edit = TempoMapEdits.DragAnchored(Session.Chart, tick, target, Session.Delay, Session.KeepTimes);
            }
            else
            {
                long to = (long)target;
                if (to == tick)
                    return;
                edit = TempoMapEdits.MoveMarker(Session.Chart, tick, to, Session.Delay, Session.KeepTimes);
            }

            if (edit.IsRefused)
                Session.Status = edit.Refusal;
            else
                Session.Preview(edit.Command);
        }

        public override void Release(ChartPoint point)
        {
            Drag(point);
            var command = Session.CommitPreview();
            if (command is ChangeTempoMap && !anchored)
                Session.Selection.SelectTempos([Tools.Snap.Nearest(point.Tick, Tools.Map)]);
        }
    }

    /// <summary>Drags a box over notes, selecting them and the span of ticks it covers; a click in an empty place moves the cursor.</summary>
    private sealed class BoxGesture : Gesture
    {
        private readonly ChartPoint _pressed;
        private readonly bool _add;
        private readonly IReadOnlyCollection<NotePlace> _before;
        private bool _moved;

        public BoxGesture(EditorTools tools, ChartPoint pressed, bool add)
            : base(tools)
        {
            _pressed = pressed;
            _add = add;
            _before = Session.Selection.Track == pressed.Track ? [.. Session.Selection.Places] : [];
        }

        public override void Drag(ChartPoint point)
        {
            if (point.Track != _pressed.Track)
                return;
            _moved |= Math.Abs(point.Tick - _pressed.Tick) > _pressed.Tolerance || point.Lane != _pressed.Lane;
            if (!_moved)
                return;

            var track = Session.TrackOf(_pressed.Track)!;
            long from = (long)Math.Round(Math.Max(0, Math.Min(point.Tick, _pressed.Tick)));
            long to = (long)Math.Round(Math.Max(0, Math.Max(point.Tick, _pressed.Tick)));
            var slots = point.Slots;
            int? a = _pressed.Lane is { } l1 ? slots.IndexOf(l1) : null;
            int? b = point.Lane is { } l2 ? slots.IndexOf(l2) : null;
            int lo = Math.Min(a ?? 0, b ?? 0), hi = Math.Max(a ?? slots.Slots.Count - 1, b ?? slots.Slots.Count - 1);
            if (a is null || b is null)
                (lo, hi) = (0, slots.Slots.Count - 1);

            var inBox = new List<NotePlace>();
            foreach (var note in track.Notes.Between(from, to))
            {
                int index = slots.IndexOf(note.Lane);
                if ((index >= lo && index <= hi) || (index < 0 && note.Lane == slots.Across))
                    inBox.Add(NotePlace.Of(note));
            }

            Session.Selection.SelectPlaces(_pressed.Track, _add ? _before.Concat(inBox) : inBox);
            Session.Selection.SetRange(from, to);
        }

        public override void Release(ChartPoint point)
        {
            Drag(point);
            if (_moved)
                return;

            // A click in an empty place.
            if (!_add)
                Session.Selection.Clear();
            Session.MoveCursorTo(Tools.Snap.Nearest(_pressed.Tick, Tools.Map));
        }
    }
}
