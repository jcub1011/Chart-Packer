using ChartShared.Core.Charting.Commands;
using ChartShared.Core.Charts;

namespace ChartShared.Core.Charting.Editing;

// Selecting across the lanes (charter-spec C15 item 2): a time range, a whole lane, a section; and what acts on a range.
public sealed partial class EditorTools
{
    private const string NoSectionAtCursor = "There's no section at the cursor to select.";

    /// <summary>Selects every note of <paramref name="key"/>'s lane.</summary>
    public void SelectLane(ChartTrackKey key)
    {
        if (_session.TrackOf(key) is not { } track)
            return;
        Cancel();
        _session.Selection.Select(key, track.Notes);
    }

    /// <summary>
    /// Selects the section the cursor is in, across the lanes shown (charter-spec C15 item 2, Ctrl+Shift+A): from its start
    /// to the tick before the next section's, or to the song's last note for the last.
    /// </summary>
    public void SelectSectionAtCursor()
    {
        long tick = (long)Math.Round(Math.Max(0, _session.TickAt(_session.Viewport.Cursor)));
        if (SectionSpanAt(tick) is not { } span)
        {
            _session.Status = NoSectionAtCursor;
            return;
        }

        Cancel();
        _session.Selection.SelectRange(span.From, span.To, LanesForRange());
    }

    /// <summary>
    /// The span of the section that <paramref name="tick"/> is in: from its start to the tick before the next section's,
    /// or to the end of the song's last note for the last. Null before the first section.
    /// </summary>
    public (long From, long To)? SectionSpanAt(long tick)
    {
        var starts = Chart.Events.Where(static e => SectionEvents.IsSection(e.Text)).Select(static e => e.Tick).Distinct().ToList();
        int index = starts.FindLastIndex(t => t <= tick);
        if (index < 0)
            return null;
        long to = index + 1 < starts.Count ? starts[index + 1] - 1 : Math.Max(starts[index], LastTick());
        return (starts[index], to);
    }

    /// <summary>The tracks a range selected across lanes covers: the lanes shown, or the focused one alone.</summary>
    private IReadOnlyList<ChartTrackKey> LanesForRange()
    {
        var shown = _session.VisibleTracks?.Invoke() ?? [];
        var tracks = shown.Where(k => _session.TrackOf(k) is not null).ToList();
        if (tracks.Count == 0 && _session.Focused is { } focused && _session.TrackOf(focused) is not null)
            tracks.Add(focused);
        return tracks;
    }

    /// <summary>Where the song's last note, phrase or event ends.</summary>
    private long LastTick()
    {
        long last = Chart.Events.Count > 0 ? Chart.Events[^1].Tick : 0;
        foreach (var part in Chart.Parts)
        {
            foreach (var track in part.Tracks)
            {
                if (track.Notes.Count > 0)
                    last = Math.Max(last, track.Notes.Max(static n => n.Tick + n.Length));
            }
        }

        return last;
    }

    /// <summary>Moves the notes of a range selected across lanes <paramref name="steps"/> grid steps in time, as one step, and the range with them.</summary>
    private void MoveRange(int steps)
    {
        var selection = _session.Selection;
        if (selection.Range is not { } range || steps == 0)
            return;
        long tickDelta = Snap.Step(range.From, steps, Map) - range.From;
        if (tickDelta == 0 || range.From + tickDelta < 0)
            return;
        var tracks = selection.RangeTracks.ToList();
        var moves = new List<Func<(IChartCommand?, string?)>>();
        foreach (var (key, notes) in selection.NotesByTrack())
        {
            moves.Add(() =>
            {
                var (command, _) = MoveOf(key, notes, tickDelta, 0);
                return (command, null);
            });
        }

        if (moves.Count == 0 || _session.ExecuteAsOne(moves))
            selection.SelectRange(range.From + tickDelta, range.To + tickDelta, tracks);
    }

    /// <summary>The step that removes the notes of a range selected across lanes, track by track.</summary>
    private IEnumerable<Func<(IChartCommand?, string?)>> RemoveRangeSteps()
    {
        foreach (var (key, notes) in _session.Selection.NotesByTrack())
            yield return () => (new RemoveNotes(key.Instrument, key.Difficulty, notes), null);
    }

    /// <summary>
    /// Turns <paramref name="flag"/> on or off for the chords of a range selected across lanes, each fret track as
    /// <see cref="ToggleChordFlag"/> does one; on unless every chord already has it.
    /// </summary>
    private void ToggleRangeChordFlag(ChordFlags flag)
    {
        var tracks = _session.Selection.NotesByTrack()
            .Where(t => ChartInstruments.KindOf(t.Track.Instrument) != ChartInstrumentKind.Drums)
            .Select(t => (t.Track, Track: _session.TrackOf(t.Track)!, Ticks: t.Notes.Select(static n => n.Tick).Distinct().ToList()))
            .ToList();
        if (tracks.Count == 0)
            return;
        bool on = !tracks.All(t => t.Ticks.All(tick => t.Track.FlagsAt(tick).HasFlag(flag)));
        var other = flag switch
        {
            ChordFlags.ForceHopo => ChordFlags.ForceStrum,
            ChordFlags.ForceStrum => ChordFlags.ForceHopo,
            _ => ChordFlags.None,
        };
        var commands = new List<IChartCommand>();
        foreach (var (key, track, ticks) in tracks)
        {
            if (on && other != ChordFlags.None && ticks.Where(t => track.FlagsAt(t).HasFlag(other)).ToList() is { Count: > 0 } clash)
                commands.Add(new SetChordFlags(key.Instrument, key.Difficulty, clash, other, on: false));
            commands.Add(new SetChordFlags(key.Instrument, key.Difficulty, ticks, flag, on));
        }

        _session.Execute(commands.Count == 1 ? commands[0] : new CompositeCommand(commands));
    }

    /// <summary>Drags a time range across the lanes shown (Alt+drag, charter-spec C15 item 2); a click clears the selection.</summary>
    private sealed class RangeGesture(EditorTools tools, ChartPoint pressed) : Gesture(tools)
    {
        private readonly IReadOnlyList<ChartTrackKey> _tracks = tools.LanesForRange();
        private bool _moved;

        public override void Drag(ChartPoint point)
        {
            _moved |= Math.Abs(point.Tick - pressed.Tick) > pressed.Tolerance;
            if (!_moved)
                return;
            var snap = Tools.Snap;
            long a = snap.Nearest(Math.Max(0, pressed.Tick), Tools.Map);
            long b = snap.Nearest(Math.Max(0, point.Tick), Tools.Map);
            Session.Selection.SelectRange(Math.Min(a, b), Math.Max(a, b), _tracks);
        }

        public override void Release(ChartPoint point)
        {
            Drag(point);
            if (!_moved)
                Session.Selection.Clear();
        }
    }

    /// <summary>
    /// Where a synchronized region's end dragged to <paramref name="pointer"/> goes, the other end at
    /// <paramref name="fixedEnd"/>, and the region reaching one after it (<paramref name="after"/>) or one before it: to
    /// the grid, or to exactly a 32nd note's length (<see cref="SyncRegion.MinLength"/>) when the pointer is nearer that,
    /// or has crossed it, so the shortest region is one 32nd note however the pointer moves. Never before the start.
    /// </summary>
    private long SyncEdgeAt(double pointer, long fixedEnd, bool after) =>
        EdgeAt(pointer, fixedEnd, after, SyncRegion.MinLength(Chart.Resolution));

    /// <summary>
    /// Where the loop region's end dragged to <paramref name="pointer"/> goes, as <see cref="SyncEdgeAt"/> has a
    /// synchronized region's: a loop is never shorter than a 32nd note either, nor before the start.
    /// </summary>
    private long LoopEdgeAt(double pointer, double fixedEnd, bool after) =>
        EdgeAt(pointer, fixedEnd, after, LoopMinLength);

    /// <summary>How short the loop region may be: one 32nd note's worth of ticks, as a synchronized region is.</summary>
    private long LoopMinLength => SyncRegion.MinLength(Chart.Resolution);

    /// <summary>
    /// Where an end dragged to <paramref name="pointer"/> goes, the other end at <paramref name="fixedEnd"/>, and the
    /// span reaching one after it (<paramref name="after"/>) or one before it: to the grid, or to exactly
    /// <paramref name="min"/> ticks from the other end when the pointer is nearer that, or has crossed it, so the
    /// shortest span is <paramref name="min"/> ticks however the pointer moves. Never before the start.
    /// </summary>
    private long EdgeAt(double pointer, double fixedEnd, bool after, long min)
    {
        double limit = Math.Max(0, after ? fixedEnd + min : fixedEnd - min);
        long limited = after ? (long)Math.Ceiling(limit) : (long)Math.Floor(limit);
        if (after ? pointer <= limit : pointer >= limit)
            return limited;

        // The minimum is a step of its own, which the end goes to when the pointer is nearer it than the grid's step.
        long at = Snap.Nearest(pointer, Map);
        at = after ? Math.Max(at, limited) : Math.Min(at, limited);
        return Math.Abs(pointer - limit) < Math.Abs(pointer - at) ? limited : at;
    }

    /// <summary>
    /// Drags out a synchronized region on the pressed lane's track, both ends on the grid (the Sync tool), or with Ctrl
    /// + Shift held, which picks the tool up for as long as they're held. Once it's drawn, the tool picked before Sync
    /// comes back; a click draws nothing. Pressed on a passage synced with the region, a click makes that passage the
    /// region's source instead (a drag there draws a new region).
    /// </summary>
    private sealed class SyncGesture(EditorTools tools, ChartPoint pressed, long? clickedMatch = null) : Gesture(tools)
    {
        private bool _moved;

        public override void Drag(ChartPoint point)
        {
            _moved |= Math.Abs(point.Tick - pressed.Tick) > pressed.Tolerance;
            if (!_moved)
                return;
            long a = Tools.Snap.Nearest(Math.Max(0, pressed.Tick), Tools.Map);
            long b = Tools.SyncEdgeAt(point.Tick, a, after: point.Tick >= pressed.Tick);
            if (a == b)
            {
                Session.Sync.Draft = null;
                return;
            }

            Session.Sync.Draft = (pressed.Track, Math.Min(a, b), Math.Max(a, b));
        }

        public override void Release(ChartPoint point)
        {
            Drag(point);
            var draft = Session.Sync.Draft;
            Session.Sync.Draft = null;
            if (draft is var (track, from, to))
            {
                if (Session.Sync.Define(track, from, to) && Session.Tool == EditorTool.Sync)
                    Session.Tool = Session.ToolBeforeSync;
            }
            else if (clickedMatch is { } start)
            {
                Session.Sync.MakeSource(start);
            }
        }

        public override void Cancel() => Session.Sync.Draft = null;
    }

    /// <summary>
    /// Drags the synchronized region along time, by its body, both ends keeping their distance on the grid. The
    /// passages matching it are found again where it lands. Nothing moves until the pointer has, so a click only
    /// picks the region up.
    /// </summary>
    private sealed class SyncMoveGesture(EditorTools tools, ChartPoint pressed) : Gesture(tools)
    {
        private readonly (long From, long To) _span = tools._session.Sync.Span!.Value;

        // The span last dragged to, so one the pointer hasn't left isn't made again.
        private (long From, long To)? _dragged;
        private bool _moved;

        public override void Drag(ChartPoint point)
        {
            _moved |= Math.Abs(point.Tick - pressed.Tick) > pressed.Tolerance;
            if (!_moved)
                return;
            long length = _span.To - _span.From;
            long from = Math.Max(0, Tools.Snap.Nearest(_span.From + (point.Tick - pressed.Tick), Tools.Map));
            if (_dragged == (from, from + length))
                return;
            _dragged = (from, from + length);
            Session.Sync.DragTo(from, from + length);
        }

        public override void Release(ChartPoint point)
        {
            Drag(point);
            if (_moved && Session.Sync.Span is var (from, to))
                Session.Sync.Redefine(from, to);
        }

        public override void Cancel()
        {
            if (_moved)
                Session.Sync.DragTo(_span.From, _span.To);
        }
    }

    /// <summary>
    /// Drags one end of the synchronized region to the grid, the other staying where it is. It never crosses the other
    /// end, and is never shorter than a 32nd note, which the end snaps down to. The passages matching it are found again
    /// as it's resized.
    /// </summary>
    private sealed class SyncResizeGesture(EditorTools tools, ChartPoint pressed, bool start) : Gesture(tools)
    {
        private readonly (long From, long To) _span = tools._session.Sync.Span!.Value;

        // The span last dragged to, so one the pointer hasn't left isn't made again.
        private (long From, long To)? _dragged;
        private bool _moved;

        public override void Drag(ChartPoint point)
        {
            _moved |= Math.Abs(point.Tick - pressed.Tick) > pressed.Tolerance;
            if (!_moved)
                return;
            long at = Tools.SyncEdgeAt(point.Tick, start ? _span.To : _span.From, after: !start);
            (long From, long To) span = start ? (at, _span.To) : (_span.From, at);
            if (span == _span || span == _dragged)
                return;
            _dragged = span;
            Session.Sync.DragTo(span.From, span.To);
        }

        public override void Release(ChartPoint point)
        {
            Drag(point);
            if (_moved && Session.Sync.Span is var (from, to))
                Session.Sync.Redefine(from, to);
        }

        public override void Cancel()
        {
            if (_moved)
                Session.Sync.DragTo(_span.From, _span.To);
        }
    }

    /// <summary>
    /// Drags out the loop region along the song, both ends on the grid, with the Loop tool picked or L held (charter-spec
    /// C10 item 2). Once it's drawn, the tool picked before Loop comes back; a click draws nothing.
    /// </summary>
    private sealed class LoopGesture(EditorTools tools, double tick, double tolerance) : Gesture(tools)
    {
        // The region it had before the drag, which is put back if the gesture is taken back.
        private readonly (double Start, double End)? _before = tools._session.Loop?.Span;

        // The span last dragged to, so one the pointer hasn't left isn't made again.
        private (long From, long To)? _drawn;
        private bool _moved;

        public override void Drag(ChartPoint point) => DragTo(point.Tick);

        public override void Drag(VocalPoint point) => DragTo(point.Tick);

        public override void Release(ChartPoint point) => ReleaseAt(point.Tick);

        public override void Release(VocalPoint point) => ReleaseAt(point.Tick);

        public override void Cancel() => Restore();

        private void DragTo(double pointer)
        {
            _moved |= Math.Abs(pointer - tick) > tolerance;
            if (!_moved)
                return;
            long from = Tools.Snap.Nearest(Math.Max(0, tick), Tools.Map);
            long to = Tools.LoopEdgeAt(pointer, from, after: pointer >= tick);
            (long From, long To) span = (Math.Min(from, to), Math.Max(from, to));
            if (span == _drawn)
                return;
            _drawn = span;
            Session.Loop?.DragTo(Session.AudioAt(span.From), Session.AudioAt(span.To));
        }

        private void ReleaseAt(double pointer)
        {
            DragTo(pointer);
            if (_drawn is not { } span)
                return;
            Session.DefineLoop(Session.AudioAt(span.From), Session.AudioAt(span.To));
            if (Session.Tool == EditorTool.Loop)
                Session.Tool = Session.ToolBeforeLoop;
        }

        // The region it had before, or none: the one drawn so far was only a preview, and looping was off without one.
        private void Restore()
        {
            if (_before is { } span)
                Session.Loop?.DragTo(span.Start, span.End);
            else
                Session.Loop?.Clear();
        }
    }

    /// <summary>
    /// Drags the loop region along time by its body, both ends keeping their distance on the grid (charter-spec C10 item
    /// 2). Nothing moves until the pointer has, so a click only picks the region up.
    /// </summary>
    private sealed class LoopMoveGesture : Gesture
    {
        private readonly double _tick;
        private readonly double _tolerance;
        private readonly (double Start, double End) _span;

        // Where the region starts, in ticks, as it's drawn rather than on the grid: it's only snapped once it's dragged.
        private readonly double _from;
        private readonly long _length;

        // The span last dragged to, so one the pointer hasn't left isn't made again.
        private (long From, long To)? _dragged;
        private bool _moved;

        public LoopMoveGesture(EditorTools tools, double tick, double tolerance)
            : base(tools)
        {
            _tick = tick;
            _tolerance = tolerance;
            _span = tools._session.Loop!.Span!.Value;
            _from = Session.TickAt(_span.Start);
            _length = Math.Max(Tools.LoopMinLength, (long)Math.Round(Session.TickAt(_span.End) - _from));
        }

        public override void Drag(ChartPoint point) => DragTo(point.Tick);

        public override void Drag(VocalPoint point) => DragTo(point.Tick);

        public override void Release(ChartPoint point) => ReleaseAt(point.Tick);

        public override void Release(VocalPoint point) => ReleaseAt(point.Tick);

        public override void Cancel()
        {
            if (_moved)
                Session.Loop?.DragTo(_span.Start, _span.End);
        }

        private void DragTo(double pointer)
        {
            _moved |= Math.Abs(pointer - _tick) > _tolerance;
            if (!_moved)
                return;
            long from = Math.Max(0, Tools.Snap.Nearest(_from + (pointer - _tick), Tools.Map));
            (long From, long To) span = (from, from + _length);
            if (span == _dragged)
                return;
            _dragged = span;
            Session.Loop?.DragTo(Session.AudioAt(span.From), Session.AudioAt(span.To));
        }

        private void ReleaseAt(double pointer)
        {
            if (!_moved)
                return;
            DragTo(pointer);
            if (Session.Loop?.Span is { } span)
                Session.DefineLoop(span.Start, span.End);
        }
    }

    /// <summary>
    /// Drags one end of the loop region to the grid, the other staying where it is (charter-spec C10 item 2). It never
    /// crosses the other end, and is never shorter than a 32nd note, which the end snaps down to.
    /// </summary>
    private sealed class LoopResizeGesture : Gesture
    {
        private readonly double _tick;
        private readonly double _tolerance;
        private readonly (double Start, double End) _span;

        // Its ends in ticks, as they're drawn rather than on the grid: only the end dragged is snapped.
        private readonly double _from;
        private readonly double _to;
        private readonly bool _start;

        // The span last dragged to, so one the pointer hasn't left isn't made again.
        private (long From, long To)? _dragged;
        private bool _moved;

        public LoopResizeGesture(EditorTools tools, double tick, double tolerance, bool start)
            : base(tools)
        {
            _tick = tick;
            _tolerance = tolerance;
            _start = start;
            _span = tools._session.Loop!.Span!.Value;
            _from = Session.TickAt(_span.Start);
            _to = Session.TickAt(_span.End);
        }

        public override void Drag(ChartPoint point) => DragTo(point.Tick);

        public override void Drag(VocalPoint point) => DragTo(point.Tick);

        public override void Release(ChartPoint point) => ReleaseAt(point.Tick);

        public override void Release(VocalPoint point) => ReleaseAt(point.Tick);

        public override void Cancel()
        {
            if (_moved)
                Session.Loop?.DragTo(_span.Start, _span.End);
        }

        private void DragTo(double pointer)
        {
            _moved |= Math.Abs(pointer - _tick) > _tolerance;
            if (!_moved)
                return;
            long at = Tools.LoopEdgeAt(pointer, _start ? _to : _from, after: !_start);
            (long From, long To) span = _start ? (at, (long)Math.Round(_to)) : ((long)Math.Round(_from), at);
            if (span == _dragged || (!_start && span.To <= span.From) || (_start && span.From >= span.To))
                return;
            _dragged = span;
            Session.Loop?.DragTo(Session.AudioAt(span.From), Session.AudioAt(span.To));
        }

        private void ReleaseAt(double pointer)
        {
            if (!_moved)
                return;
            DragTo(pointer);
            if (Session.Loop?.Span is { } span)
                Session.DefineLoop(span.Start, span.End);
        }
    }
}
