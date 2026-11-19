using ChartShared.Core.Charting.Layout;
using ChartShared.Core.Charts;

namespace ChartShared.Core.Charting.Editing;

public sealed partial class EditorTools
{
    /// <summary>
    /// Starts scrubbing the pitch of the selected vocal notes, as the Properties panel's Pitch field is dragged: each is
    /// raised or lowered by the same semitones (<see cref="Scrub"/>), so notes of different pitches keep their intervals,
    /// shown as it goes and put on the undo stack as one step by <see cref="EndScrub"/>.
    /// </summary>
    /// <returns>Whether there were notes to scrub.</returns>
    public bool StartVocalPitchScrub()
    {
        Cancel();
        if (SelectedVocalContent() is not { } selected)
            return false;
        _gesture = new VocalPitchScrub(this, selected.Track, selected.Content);
        return true;
    }

    /// <summary>
    /// Starts scrubbing the length of the selected vocal notes, as the Properties panel's Length field is dragged: each is
    /// lengthened or shortened by the same ticks (<see cref="Scrub"/>), so notes of different lengths keep their own, and
    /// lasts at least a tick.
    /// </summary>
    /// <returns>Whether there were notes to scrub.</returns>
    public bool StartVocalLengthScrub()
    {
        Cancel();
        if (SelectedVocalContent() is not { } selected)
            return false;
        _gesture = new VocalLengthScrub(this, selected.Track, selected.Content, [.. _session.Selection.VocalPlaces]);
        return true;
    }

    /// <summary>
    /// Starts scrubbing the sustains of the selected notes, as the Properties panel's Length field is dragged: each is
    /// lengthened or shortened by the same ticks (<see cref="Scrub"/>), as the right button's drag does, so notes of
    /// different lengths keep their own.
    /// </summary>
    /// <returns>Whether there were notes to scrub.</returns>
    public bool StartNoteLengthScrub()
    {
        Cancel();
        var selection = _session.Selection;
        if (selection.Track is not { } key || _session.TrackOf(key) is null || selection.Notes is not { Count: > 0 } notes)
            return false;
        _gesture = new NoteLengthScrub(this, key, notes);
        return true;
    }

    /// <summary>
    /// Starts scrubbing where the selected notes are, as the Properties panel's Tick field is dragged: each is moved later
    /// or earlier by the same ticks (<see cref="Scrub"/>), so notes at different ticks keep their places around each other,
    /// as Alt and the arrows move them. None goes before the start.
    /// </summary>
    /// <returns>Whether there were notes to scrub.</returns>
    public bool StartNoteTickScrub()
    {
        Cancel();
        var selection = _session.Selection;
        if (selection.Track is not { } key || _session.TrackOf(key) is null || selection.Notes is not { Count: > 0 } notes)
            return false;
        _gesture = new NoteTickScrub(this, key, notes);
        return true;
    }

    /// <summary>
    /// Starts scrubbing where the selected vocal notes are, as the Properties panel's Tick field is dragged: each is moved
    /// later or earlier by the same ticks (<see cref="Scrub"/>), with its lyric, as Alt and the arrows move them. None goes
    /// before the start.
    /// </summary>
    /// <returns>Whether there were notes to scrub.</returns>
    public bool StartVocalTickScrub()
    {
        Cancel();
        if (SelectedVocalContent() is not { } selected)
            return false;
        _gesture = new VocalTickScrub(this, selected.Track, selected.Content);
        return true;
    }

    /// <summary>Whether a field scrub (<see cref="StartVocalPitchScrub"/> and the like) is going on.</summary>
    public bool IsScrubbing => _gesture is IFieldScrub;

    /// <summary>
    /// Moves the field scrub going on to <paramref name="steps"/> steps from where it started, up or down: semitones for a
    /// pitch; for a tick or a length, steps of the grid while snap is on, or ticks while it's off. It's shown on the chart
    /// without going on the undo stack.
    /// </summary>
    /// <returns>
    /// The steps the scrub is at, which stop short of <paramref name="steps"/> where going further changes nothing: a pitch
    /// at the highest or lowest sung, the first note at the start, or lengths all as short as they go. None with no scrub
    /// going on.
    /// </returns>
    public int Scrub(int steps) => _gesture is IFieldScrub scrub ? scrub.Scrub(steps) : 0;

    /// <summary>Ends the field scrub going on, putting what it shows on the undo stack as one step.</summary>
    public void EndScrub()
    {
        if (_gesture is not IFieldScrub scrub)
            return;
        _gesture = null;
        scrub.End();
    }

    // The vocal part the selected notes are of, as it is now; null with none, or when it can't be read.
    private (VocalTrack Track, VocalTrackContent Content)? SelectedVocalContent()
    {
        var selection = _session.Selection;
        if (selection.Vocal is not { } track || !selection.HasVocalNotes || VocalContentOrSay(track) is not { } content)
            return null;
        return (track, content);
    }

    // A length scrub's step, in ticks: a step of the grid while snap is on, or a tick while it's off.
    private double ScrubStep => Snap.IsOn ? Snap.StepTicks(Map.Resolution) : 1;

    // The ticks a length scrub of steps moves by, the whole grid steps worked out together so a custom grid doesn't drift.
    private long ScrubTicks(int steps) => (long)Math.Round(steps * ScrubStep);

    // The steps back that shorten something longest by at least longest: as far back as a length scrub needs to go.
    private int ScrubStepsBack(long longest) => longest <= 0 ? 0 : -(int)Math.Min(int.MaxValue, Math.Ceiling(longest / ScrubStep));

    // The ticks a tick scrub of steps moves notes whose first is at first by: with snap on, from it to the grid step that
    // many on or back, as Alt and the arrows step, so the first lands on the grid; with it off, that many ticks. Never
    // before the start.
    private long ScrubMove(long first, int steps)
    {
        if (steps == 0)
            return 0;
        return Snap.IsOn ? Snap.Step(first, steps, Map) - first : Math.Max(steps, -first);
    }

    // The steps back that take a note at first to the start: as far back as a tick scrub needs to go. Each time signature
    // before it starts the grid again, which can make a step more.
    private int ScrubStepsToStart(long first)
    {
        if (first <= 0)
            return 0;
        double steps = Snap.IsOn
            ? Math.Ceiling(first / ScrubStep) + Map.TimeSignatures.Count(s => s.Tick > 0 && s.Tick < first)
            : first;
        return -(int)Math.Min(int.MaxValue, steps);
    }

    /// <summary>A scrub of a Properties field, which takes steps from where it started rather than a pointer.</summary>
    private interface IFieldScrub
    {
        /// <summary>Moves to <paramref name="steps"/> steps from the start; the steps it's at.</summary>
        int Scrub(int steps);

        /// <summary>Puts what the scrub shows on the undo stack, as one step.</summary>
        void End();
    }

    /// <summary>
    /// Raises or lowers the selected vocal notes by the same semitones, with their lyrics, kept to the sung pitches as a
    /// group, so none is pushed out of them and their intervals are kept.
    /// </summary>
    private sealed class VocalPitchScrub : VocalGesture, IFieldScrub
    {
        private readonly IReadOnlyCollection<VocalNotePlace> _selected;
        private readonly int _lowest;
        private readonly int _highest;
        private int _pitches;

        public VocalPitchScrub(EditorTools tools, VocalTrack track, VocalTrackContent content)
            : base(tools, track, content)
        {
            _selected = [.. Session.Selection.VocalPlaces];
            var notes = VocalEdits.NotesAt(content, _selected);
            if (notes.Count > 0)
            {
                _lowest = Math.Min(0, VocalTrackContent.LowestPitch - notes.Min(static n => n.Pitch));
                _highest = Math.Max(0, VocalTrackContent.HighestPitch - notes.Max(static n => n.Pitch));
            }
        }

        public int Scrub(int steps)
        {
            int pitches = Math.Clamp(steps, _lowest, _highest);
            if (pitches == _pitches)
                return pitches;
            _pitches = pitches;

            if (pitches == 0)
            {
                Session.Preview(null);
                Session.Selection.SelectVocalNotes(Track, _selected);
                return 0;
            }

            IReadOnlyList<VocalNotePlace> moved = [];
            bool previewed = Preview(c => VocalEdits.Change(c, _selected, n => n with { Pitch = n.Pitch + pitches }, out moved));
            Session.Selection.SelectVocalNotes(Track, previewed && moved.Count > 0 ? moved : _selected);
            return pitches;
        }

        public void End() => Session.CommitPreview();

        public override void Cancel() => Session.Selection.SelectVocalNotes(Track, _selected);
    }

    /// <summary>Lengthens or shortens the selected vocal notes by the same ticks; each lasts at least a tick.</summary>
    private sealed class VocalLengthScrub : VocalGesture, IFieldScrub
    {
        private readonly IReadOnlyList<VocalNotePlace> _places;
        private readonly int _back;
        private int _steps;

        public VocalLengthScrub(EditorTools tools, VocalTrack track, VocalTrackContent content, IReadOnlyList<VocalNotePlace> places)
            : base(tools, track, content)
        {
            _places = places;
            var notes = VocalEdits.NotesAt(content, places);
            _back = notes.Count == 0 ? 0 : tools.ScrubStepsBack(notes.Max(static n => n.Length) - 1);
        }

        public int Scrub(int steps)
        {
            steps = Math.Max(steps, _back);
            if (steps == _steps)
                return steps;
            _steps = steps;
            long ticks = Tools.ScrubTicks(steps);
            if (ticks == 0)
                Session.Preview(null);
            else
                Preview(c => VocalEdits.Change(c, _places, n => n with { Length = Math.Max(1, n.Length + ticks) }, out _));
            return steps;
        }

        public void End() => Session.CommitPreview();
    }

    /// <summary>
    /// Lengthens or shortens the selected notes' sustains by the same ticks, as the right button's drag does: a sustain
    /// shortened past the shortest there is leaves a plain note.
    /// </summary>
    private sealed class NoteLengthScrub : Gesture, IFieldScrub
    {
        private readonly ChartTrackKey _track;
        private readonly IReadOnlyList<EditableNote> _notes;
        private readonly int _back;
        private int _steps;

        public NoteLengthScrub(EditorTools tools, ChartTrackKey track, IReadOnlyList<EditableNote> notes)
            : base(tools)
        {
            _track = track;
            _notes = notes;
            _back = tools.ScrubStepsBack(notes.Max(static n => SustainEnd(n) - n.Tick));
        }

        public override void Drag(ChartPoint point)
        {
        }

        public int Scrub(int steps)
        {
            if (Session.TrackOf(_track) is { Kind: ChartInstrumentKind.Drums })
            {
                if (steps != 0)
                    Session.Status = NoDrumSustain;
                return 0;
            }

            steps = Math.Max(steps, _back);
            if (steps == _steps)
                return steps;
            _steps = steps;
            long ticks = Tools.ScrubTicks(steps);
            Session.Preview(ticks == 0 ? null : SustainsOf(_track, _notes, n => Tools.LengthTo(n, SustainEnd(n) + ticks)));
            return steps;
        }

        public void End() => Session.CommitPreview();
    }

    /// <summary>
    /// Moves the selected vocal notes later or earlier by the same ticks, with their lyrics, the first to the grid while snap
    /// is on; none goes before the start.
    /// </summary>
    private sealed class VocalTickScrub : VocalGesture, IFieldScrub
    {
        private readonly IReadOnlyCollection<VocalNotePlace> _selected;
        private readonly long _first;
        private readonly int _back;
        private int _steps;

        public VocalTickScrub(EditorTools tools, VocalTrack track, VocalTrackContent content)
            : base(tools, track, content)
        {
            _selected = [.. Session.Selection.VocalPlaces];
            var notes = VocalEdits.NotesAt(content, _selected);
            _first = notes.Count == 0 ? 0 : notes.Min(static n => n.Tick);
            _back = tools.ScrubStepsToStart(_first);
        }

        public int Scrub(int steps)
        {
            steps = Math.Max(steps, _back);
            if (steps == _steps)
                return steps;
            _steps = steps;

            long ticks = Tools.ScrubMove(_first, steps);
            if (ticks == 0)
            {
                Session.Preview(null);
                Session.Selection.SelectVocalNotes(Track, _selected);
                return steps;
            }

            IReadOnlyList<VocalNotePlace> moved = [];
            bool previewed = Preview(c => VocalEdits.Change(c, _selected, n => n with { Tick = n.Tick + ticks }, out moved));
            Session.Selection.SelectVocalNotes(Track, previewed && moved.Count > 0 ? moved : _selected);
            return steps;
        }

        public void End() => Session.CommitPreview();

        public override void Cancel() => Session.Selection.SelectVocalNotes(Track, _selected);
    }

    /// <summary>
    /// Moves the selected notes later or earlier by the same ticks, each keeping its lane, as Alt and the arrows do: the first
    /// to the grid while snap is on, and none before the start. A move onto notes already there is refused, leaving the move
    /// before it shown.
    /// </summary>
    private sealed class NoteTickScrub : Gesture, IFieldScrub
    {
        private readonly ChartTrackKey _track;
        private readonly IReadOnlyList<EditableNote> _notes;
        private readonly IReadOnlyCollection<NotePlace> _selected;
        private readonly long _first;
        private readonly int _back;
        private int _steps;
        private IReadOnlyList<EditableNote>? _moved;

        public NoteTickScrub(EditorTools tools, ChartTrackKey track, IReadOnlyList<EditableNote> notes)
            : base(tools)
        {
            _track = track;
            _notes = notes;
            _selected = [.. Session.Selection.Places];
            _first = notes.Min(static n => n.Tick);
            _back = tools.ScrubStepsToStart(_first);
        }

        public override void Drag(ChartPoint point)
        {
        }

        public int Scrub(int steps)
        {
            steps = Math.Max(steps, _back);
            if (steps == _steps)
                return steps;
            _steps = steps;

            long ticks = Tools.ScrubMove(_first, steps);
            if (ticks == 0)
            {
                Session.Preview(null);
                _moved = null;
                Session.Selection.SelectPlaces(_track, _selected);
                return steps;
            }

            var (command, moved) = Tools.MoveOf(_track, _notes, ticks, 0);
            if (Session.Preview(command))
            {
                _moved = moved;
                Session.Selection.Select(_track, moved);
            }

            return steps;
        }

        public void End()
        {
            if (Session.CommitPreview() is not null && _moved is { } moved)
                Session.Selection.Select(_track, moved);
        }

        public override void Cancel() => Session.Selection.SelectPlaces(_track, _selected);
    }
}
