using ChartPacker.Core.Charting.Commands;
using ChartPacker.Core.Charts;

namespace ChartPacker.Core.Charting.Editing;

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
    /// Drags out a synchronized region on the pressed lane's track, both ends on the grid (the Sync tool). Once it's drawn,
    /// the tool picked before Sync comes back; when it's refused, Sync stays, to draw another. A click draws nothing.
    /// </summary>
    private sealed class SyncGesture(EditorTools tools, ChartPoint pressed) : Gesture(tools)
    {
        private bool _moved;

        public override void Drag(ChartPoint point)
        {
            _moved |= Math.Abs(point.Tick - pressed.Tick) > pressed.Tolerance;
            if (!_moved)
                return;
            var snap = Tools.Snap;
            long a = snap.Nearest(Math.Max(0, pressed.Tick), Tools.Map);
            long b = snap.Nearest(Math.Max(0, point.Tick), Tools.Map);
            Session.Sync.Draft = a == b ? null : (pressed.Track, Math.Min(a, b), Math.Max(a, b));
        }

        public override void Release(ChartPoint point)
        {
            Drag(point);
            var draft = Session.Sync.Draft;
            Session.Sync.Draft = null;
            if (draft is var (track, from, to) && Session.Sync.Define(track, from, to))
                Session.Tool = Session.ToolBeforeSync;
        }

        public override void Cancel() => Session.Sync.Draft = null;
    }
}
