using ChartShared.Core.Charting.Commands;
using ChartShared.Core.Charting.Layout;

namespace ChartShared.Core.Charting.Editing;

/// <summary>The pointer as the tools see it in a vocal part's lane (charter-spec C3 item 7).</summary>
/// <param name="Tick">The chart tick under the pointer.</param>
/// <param name="Pitch">The pitch of the row under the pointer, or null outside the rows.</param>
/// <param name="Tolerance">How many ticks either way the pointer reaches, for hitting what's there.</param>
/// <param name="InPhrases">Whether the pointer is in the strip the phrases are drawn in, above the rows.</param>
public readonly record struct VocalPoint(VocalTrack Track, double Tick, int? Pitch, double Tolerance, bool InPhrases = false);

/// <summary>Which part of a vocal note is grabbed: its body, to move it, or its end, to change its length.</summary>
public enum VocalNoteGrip
{
    Body,
    End,
}

/// <summary>A vocal note under the pointer (<see cref="EditorTools.HitTest(VocalPoint)"/>).</summary>
public readonly record struct VocalNoteHit(VocalNoteEvent Note, VocalNoteGrip Grip);

/// <summary>A vocal phrase's bar under the pointer (<see cref="EditorTools.HitTestPhrase(VocalPoint)"/>).</summary>
public readonly record struct VocalPhraseHit(VocalPhraseEvent Phrase, PhraseGrip Grip);

public sealed partial class EditorTools
{
    private const string VocalsUnreadable = "This vocal track can't be read, so it's kept as it is.";

    /// <summary>
    /// Starts a gesture at <paramref name="point"/> in a vocal part's lane with the current tool (charter-spec C3 item 7);
    /// in the phrase strip, with any tool, a gesture on the phrase there.
    /// </summary>
    public void Press(VocalPoint point, EditorModifiers modifiers = EditorModifiers.None)
    {
        Cancel();
        _modifiers = modifiers;

        // The Loop tool's press, or L held: the loop region covers the whole song, so it's drawn and taken hold of here
        // too, rather than the part's notes being edited. As in a highway's lane, only inside the rows: the phrase strip
        // and the margins beyond the highest and lowest pitch aren't its business.
        if (LoopActive)
        {
            _gesture = point.InPhrases || point.Pitch is null ? null : PressLoop(point.Tick, point.Tolerance);
            return;
        }

        if (VocalContentOrSay(point.Track) is not { } content)
            return;
        bool shift = modifiers.HasFlag(EditorModifiers.Shift);
        if (point.InPhrases)
        {
            _gesture = PressVocalPhrase(point, content, shift);
            return;
        }

        _gesture = _session.Tool switch
        {
            EditorTool.Select => PressVocalSelect(point, content, shift),
            EditorTool.Place => PressVocalPlace(point, content, shift),
            EditorTool.Erase => new VocalEraseGesture(this, point, content),
            EditorTool.Phrase => new VocalPhraseDrawGesture(this, point, content),
            EditorTool.Tempo => PressTempo(point.Tick, point.Tolerance),
            _ => null,
        };
    }

    /// <summary>
    /// Starts erasing at <paramref name="point"/> in a vocal part's lane, as the middle button does: each note the pointer
    /// passes over goes, with its lyrics, and in the phrase strip each phrase, as one step. With the Tempo tool, it removes
    /// the tempo marker there instead, and with the Loop tool active, it takes the loop region away.
    /// </summary>
    public void PressErase(VocalPoint point)
    {
        Cancel();
        _modifiers = EditorModifiers.None;
        if (LoopActive)
            _session.ClearLoop();
        else if (_session.Tool == EditorTool.Tempo)
            RemoveTempoNear(point.Tick, point.Tolerance);
        else if (VocalContentOrSay(point.Track) is { } content)
            _gesture = new VocalEraseGesture(this, point, content);
    }

    /// <summary>
    /// Starts the right button's gesture at <paramref name="point"/> in a vocal part's lane, as
    /// <see cref="PressContext(ChartPoint, EditorModifiers)"/> does in a highway's: a note there that isn't selected is
    /// selected alone, and a drag lengthens or shortens the selected notes, each by the same grid steps, or with Ctrl ends
    /// them all where the pointer is.
    /// </summary>
    public void PressContext(VocalPoint point, EditorModifiers modifiers = EditorModifiers.None)
    {
        Cancel();
        _modifiers = modifiers;
        if (point.InPhrases || VocalContentOrSay(point.Track) is not { } content)
            return;

        var selection = _session.Selection;
        if (HitTest(content, point) is { } hit && !selection.Contains(point.Track, hit.Note.Place))
            selection.SelectVocalNotes(point.Track, [hit.Note.Place]);
        if (selection.Vocal == point.Track && selection.HasVocalNotes)
            _gesture = new VocalLengthGesture(this, point.Track, content, [.. selection.VocalPlaces], point.Tick);
    }

    /// <summary>Moves the gesture on to <paramref name="point"/> in a vocal part's lane, with <paramref name="modifiers"/> held.</summary>
    public void Drag(VocalPoint point, EditorModifiers modifiers = EditorModifiers.None)
    {
        _modifiers = modifiers;
        _gesture?.Drag(point);
    }

    /// <summary>Ends the gesture at <paramref name="point"/> in a vocal part's lane, as one undoable step.</summary>
    public void Release(VocalPoint point)
    {
        var gesture = _gesture;
        _gesture = null;
        gesture?.Release(point);
    }

    /// <summary>
    /// The vocal note under <paramref name="point"/>, and whether its end is grabbed, or null for none. A note is hit on
    /// the row it's drawn on (a spoken note's is the middle row), up to <see cref="VocalPoint.Tolerance"/> ticks beyond its
    /// ends; as the rows are thin, the row either side of it hits it too, when no note is on the pointer's own.
    /// </summary>
    public VocalNoteHit? HitTest(VocalPoint point) =>
        _session.VocalContentOf(point.Track) is { } content ? HitTest(content, point) : null;

    /// <summary>The vocal phrase whose bar is under <paramref name="point"/> in the phrase strip, and which part of it, or null for none.</summary>
    public VocalPhraseHit? HitTestPhrase(VocalPoint point) =>
        _session.VocalContentOf(point.Track) is { } content ? HitTestPhrase(content, point) : null;

    /// <summary>Selects every note of the vocal part <paramref name="track"/> (Ctrl+A in its lane).</summary>
    public void SelectAllVocals(VocalTrack track)
    {
        if (VocalContentOrSay(track) is { } content)
            _session.Selection.SelectVocalNotes(track, content.Notes.Select(static n => n.Place));
    }

    /// <summary>
    /// Moves the selected vocal notes <paramref name="steps"/> grid steps in time, measured from the first, and
    /// <paramref name="semitones"/> in pitch, as one step, keeping them selected (charter-spec §9: Alt+arrows). They never
    /// go before the start or out of the sung pitches.
    /// </summary>
    public void MoveVocalSelection(int steps, int semitones)
    {
        var selection = _session.Selection;
        if (selection.Vocal is not { } track || selection.VocalNotes is not { Count: > 0 } notes)
            return;
        long first = notes[0].Tick;
        long ticks = steps == 0 ? 0 : Snap.Step(first, steps, Map) - first;
        int lowest = notes.Min(static n => n.Pitch), highest = notes.Max(static n => n.Pitch);
        int pitches = Math.Clamp(semitones, VocalTrackContent.LowestPitch - lowest, VocalTrackContent.HighestPitch - highest);
        if (ticks == 0 && pitches == 0)
            return;

        IReadOnlyList<VocalNotePlace> moved = [];
        if (EditVocals(track, c => VocalEdits.Change(c, selection.VocalPlaces, n => n with { Tick = n.Tick + ticks, Pitch = n.Pitch + pitches }, out moved)) is not null)
            selection.SelectVocalNotes(track, moved);
    }

    /// <summary>Sets the lyric of each selected vocal note to <paramref name="text"/>, as one step; blank takes it away.</summary>
    public void SetVocalLyric(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (SelectedVocalTicks() is { } selected)
            EditVocals(selected.Track, c => VocalEdits.SetLyric(c, selected.Ticks, text));
    }

    /// <summary>
    /// Gives the syllables of the typed <paramref name="line"/> to the selected vocal notes in order, a slide skipped, as
    /// one step (charter-spec C3 item 7: Type lyrics).
    /// </summary>
    /// <returns>How many syllables there were no note for; null when nothing was done.</returns>
    public int? SpreadVocalLyrics(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var selection = _session.Selection;
        if (selection.Vocal is not { } track || !selection.HasVocalNotes)
            return null;
        var syllables = VocalEdits.SplitLyrics(line);
        if (syllables.Count == 0)
            return null;
        int left = 0;
        return EditVocals(track, c => VocalEdits.SpreadLyrics(c, selection.VocalPlaces, syllables, out left)) is null ? null : left;
    }

    /// <summary>Makes the selected vocal notes slides, or not, as one step.</summary>
    public void SetVocalSlide(bool on)
    {
        if (SelectedVocalTicks() is { } selected)
            EditVocals(selected.Track, c => VocalEdits.SetSlide(c, selected.Ticks, on));
    }

    /// <summary>Makes the selected vocal notes spoken, or sung, as one step.</summary>
    public void SetVocalSpoken(bool on)
    {
        if (SelectedVocalTicks() is { } selected)
            EditVocals(selected.Track, c => VocalEdits.SetSpoken(c, selected.Ticks, on));
    }

    /// <summary>Sets the pitch of every selected vocal note, as one step, keeping them selected.</summary>
    public void SetVocalPitch(int pitch)
    {
        if (!VocalTrackContent.IsSung(pitch))
        {
            _session.Status = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"A sung note's pitch is {VocalTrackContent.LowestPitch} to {VocalTrackContent.HighestPitch}.");
            return;
        }

        ChangeSelectedVocalNotes(n => n with { Pitch = pitch });
    }

    /// <summary>Moves the selected vocal notes, which all start on one tick, to <paramref name="tick"/>, as one step.</summary>
    public void SetVocalTick(long tick)
    {
        if (tick < 0)
            return;
        ChangeSelectedVocalNotes(n => n with { Tick = tick });
    }

    /// <summary>Sets the length of every selected vocal note, as one step.</summary>
    public void SetVocalLength(long length)
    {
        if (length < 0)
            return;
        ChangeSelectedVocalNotes(n => n with { Length = length });
    }

    /// <summary>Moves the start of every selected vocal phrase to <paramref name="tick"/>, each keeping its end, as one step.</summary>
    public void SetVocalPhraseStart(long tick) =>
        ChangeSelectedVocalPhrases(p => p with { Tick = tick, Length = p.EndTick - tick });

    /// <summary>Moves the end of every selected vocal phrase to <paramref name="tick"/>, each keeping its start, as one step.</summary>
    public void SetVocalPhraseEnd(long tick) =>
        ChangeSelectedVocalPhrases(p => p with { Length = tick - p.Tick });

    // The step DeleteSelection takes for the selected vocal notes and phrases, with their lyrics.
    private Func<(IChartCommand?, string?)>? DeleteVocalsStep()
    {
        var selection = _session.Selection;
        if (selection.Vocal is not { } track || selection.IsEmpty)
            return null;
        var places = selection.VocalPlaces.ToList();
        var phrases = selection.VocalPhrases;
        if (places.Count == 0 && phrases.Count == 0)
            return null;
        return () =>
        {
            if (_session.VocalContentOf(track) is not { } content)
                return (null, VocalsUnreadable);
            var to = VocalEdits.RemovePhrases(VocalEdits.Remove(content, places), phrases);
            return (ReferenceEquals(to, content) ? null : new ChangeVocals(track, to), null);
        };
    }

    private (VocalTrack Track, IReadOnlyList<long> Ticks)? SelectedVocalTicks()
    {
        var selection = _session.Selection;
        if (selection.Vocal is not { } track || selection.VocalNotes is not { Count: > 0 } notes)
            return null;
        return (track, [.. notes.Select(static n => n.Tick).Distinct()]);
    }

    private void ChangeSelectedVocalNotes(Func<VocalNoteEvent, VocalNoteEvent> change)
    {
        var selection = _session.Selection;
        if (selection.Vocal is not { } track || !selection.HasVocalNotes)
            return;
        IReadOnlyList<VocalNotePlace> changed = [];
        if (EditVocals(track, c => VocalEdits.Change(c, selection.VocalPlaces, change, out changed)) is not null)
            selection.SelectVocalNotes(track, changed);
    }

    private void ChangeSelectedVocalPhrases(Func<VocalPhraseEvent, VocalPhraseEvent> change)
    {
        var selection = _session.Selection;
        if (selection.Vocal is not { } track || selection.VocalPhrases is not { Count: > 0 } phrases)
            return;
        var to = phrases.Select(change).ToList();
        if (to.Any(static p => p.Length < 1 || p.Tick < 0))
        {
            _session.Status = NoPhraseLength;
            return;
        }

        IReadOnlyList<VocalPhraseEvent> changed = [];
        if (EditVocals(track, c => VocalEdits.ChangePhrases(c, phrases, change, out changed)) is not null)
            selection.ReplaceVocalPhrases(track, changed);
    }

    /// <summary>
    /// Does <paramref name="edit"/> of the vocal part <paramref name="track"/> as one step, cancelling any gesture. A
    /// refusal is said in the status.
    /// </summary>
    /// <returns>The part as it is now, or null when it couldn't be read or the edit was refused.</returns>
    private VocalTrackContent? EditVocals(VocalTrack track, Func<VocalTrackContent, VocalTrackContent> edit)
    {
        Cancel();
        if (VocalContentOrSay(track) is not { } content)
            return null;
        VocalTrackContent to;
        try
        {
            to = edit(content);
        }
        catch (ArgumentException e)
        {
            _session.Status = e.Message;
            return null;
        }

        if (ReferenceEquals(to, content))
            return content;
        return _session.Execute(new ChangeVocals(track, to)) ? to : null;
    }

    // The vocal part, or null, said in the status when the chart has the track but it can't be read.
    private VocalTrackContent? VocalContentOrSay(VocalTrack track)
    {
        var content = _session.VocalContentOf(track);
        if (content is null && VocalTracks.IndexIn(Chart, track) >= 0)
            _session.Status = VocalsUnreadable;
        return content;
    }

    // The row a note is drawn on: its pitch's, or the middle row for one spoken.
    private static int DrawnPitch(VocalTrackContent content, VocalNoteEvent note) =>
        VocalEdits.IsSpoken(content, note.Tick) ? VocalLane.SpokenPitch : note.Pitch;

    private static VocalNoteHit? HitTest(VocalTrackContent content, VocalPoint point)
    {
        if (point.Pitch is not { } row)
            return null;
        VocalNoteEvent? best = null;
        (int Rows, double Distance, long Length) bestScore = (int.MaxValue, double.MaxValue, long.MaxValue);
        foreach (var note in content.Notes)
        {
            if (note.Tick > point.Tick + point.Tolerance)
                break;
            if (point.Tick > note.EndTick + point.Tolerance)
                continue;
            int rows = Math.Abs(DrawnPitch(content, note) - row);
            if (rows > 1)
                continue;
            double distance = point.Tick < note.Tick ? note.Tick - point.Tick : Math.Max(0, point.Tick - note.EndTick);
            var score = (rows, distance, note.Length);
            if (score.CompareTo(bestScore) < 0)
                (best, bestScore) = (note, score);
        }

        if (best is not { } hit)
            return null;
        var grip = hit.Length > 0 && PhraseRail.GripOf(point.Tick, point.Tolerance, hit.Tick, hit.EndTick) == PhraseGrip.End
            ? VocalNoteGrip.End
            : VocalNoteGrip.Body;
        return new VocalNoteHit(hit, grip);
    }

    private static VocalPhraseHit? HitTestPhrase(VocalTrackContent content, VocalPoint point)
    {
        VocalPhraseEvent? best = null;
        double bestDistance = double.MaxValue;
        foreach (var phrase in content.Phrases)
        {
            if (point.Tick < phrase.Tick - point.Tolerance || point.Tick > phrase.EndTick + point.Tolerance)
                continue;
            double distance = point.Tick < phrase.Tick ? phrase.Tick - point.Tick : Math.Max(0, point.Tick - phrase.EndTick);
            if (distance < bestDistance || (distance == bestDistance && phrase.Length < best!.Value.Length))
                (best, bestDistance) = (phrase, distance);
        }

        return best is { } hit ? new VocalPhraseHit(hit, PhraseRail.GripOf(point.Tick, point.Tolerance, hit.Tick, hit.EndTick)) : null;
    }

    private Gesture? PressVocalSelect(VocalPoint point, VocalTrackContent content, bool shift)
    {
        var selection = _session.Selection;
        if (HitTest(content, point) is not { } hit)
            return new VocalBoxGesture(this, point, content, shift);
        if (hit.Grip == VocalNoteGrip.End)
            return PressVocalSustain(point, content);

        var place = hit.Note.Place;
        if (shift)
            selection.ToggleVocalNote(point.Track, place);
        else if (!selection.Contains(point.Track, place))
            selection.SelectVocalNotes(point.Track, [place]);
        return selection.Contains(point.Track, place) ? new VocalMoveGesture(this, point, content, hit.Note) : null;
    }

    private Gesture? PressVocalPlace(VocalPoint point, VocalTrackContent content, bool shift)
    {
        if (HitTest(content, point) is { } hit)
        {
            // A note's end is grabbed as the select tool grabs it, so its length is set here too.
            if (hit.Grip == VocalNoteGrip.End)
                return PressVocalSustain(point, content);
            if (!_session.Selection.Contains(point.Track, hit.Note.Place))
                _session.Selection.SelectVocalNotes(point.Track, [hit.Note.Place]);
            return new VocalMoveGesture(this, point, content, hit.Note);
        }

        if (point.Pitch is not { } pitch)
            return null;
        long tick = Snap.Nearest(Math.Max(0, point.Tick), Map);

        // A note placed reaches no further than the next note's start, whatever its pitch.
        long reach = content.Notes.FirstOrDefault(n => n.Tick > tick) is { Length: >= 0 } next && next.Tick > tick ? next.Tick : long.MaxValue;
        var gesture = new VocalPlaceGesture(this, point.Track, content, new VocalNoteEvent(tick, 0, pitch), reach, shift ? VocalEdits.SlideText : null);
        return gesture.Start() ? gesture : null;
    }

    private Gesture? PressVocalSustain(VocalPoint point, VocalTrackContent content)
    {
        if (HitTest(content, point) is not { } hit)
            return null;
        var selection = _session.Selection;
        IReadOnlyList<VocalNotePlace> places = selection.Contains(point.Track, hit.Note.Place) ? [.. selection.VocalPlaces] : [hit.Note.Place];
        if (!selection.Contains(point.Track, hit.Note.Place))
            selection.SelectVocalNotes(point.Track, places);
        return new VocalSustainGesture(this, point.Track, content, places);
    }

    // A press in the phrase strip, with any tool: on a phrase's bar it selects the phrase (Shift adds it or takes it away),
    // and a drag moves it, or resizes it by the end grabbed. Elsewhere in the strip, the Phrase tool draws, and the others
    // select no phrases.
    private Gesture? PressVocalPhrase(VocalPoint point, VocalTrackContent content, bool shift)
    {
        var selection = _session.Selection;
        if (HitTestPhrase(content, point) is not { } hit)
        {
            if (_session.Tool == EditorTool.Phrase)
                return new VocalPhraseDrawGesture(this, point, content);
            if (selection.HasVocalPhrases)
                selection.ReplaceVocalPhrases(point.Track, []);
            return null;
        }

        if (shift)
        {
            selection.ToggleVocalPhrase(point.Track, hit.Phrase);
            return null;
        }

        if (!selection.Contains(point.Track, hit.Phrase))
            selection.SelectVocalPhrases(point.Track, [hit.Phrase]);
        return hit.Grip == PhraseGrip.Body
            ? new VocalPhraseMoveGesture(this, point, content, hit.Phrase)
            : new VocalPhraseResizeGesture(this, point, content, hit.Phrase, start: hit.Grip == PhraseGrip.Start);
    }

    /// <summary>A gesture in a vocal part's lane, worked out from the part as it was pressed.</summary>
    private abstract class VocalGesture(EditorTools tools, VocalTrack track, VocalTrackContent content) : Gesture(tools)
    {
        protected VocalTrack Track { get; } = track;

        /// <summary>The part as it was when the gesture started, which each preview is worked out from.</summary>
        protected VocalTrackContent Content { get; } = content;

        public sealed override void Drag(ChartPoint point)
        {
        }

        /// <summary>Previews the part as <paramref name="to"/>, or as it was when it's unchanged; whether it was taken.</summary>
        protected bool Preview(VocalTrackContent to)
        {
            if (ReferenceEquals(to, Content))
            {
                Session.Preview(null);
                return true;
            }

            if (Session.Preview(new ChangeVocals(Track, to)))
                return true;
            Session.Preview(null);
            return false;
        }

        /// <summary>Previews <paramref name="edit"/> of the part, a refusal said in the status leaving it as it was.</summary>
        protected bool Preview(Func<VocalTrackContent, VocalTrackContent> edit)
        {
            VocalTrackContent to;
            try
            {
                to = edit(Content);
            }
            catch (ArgumentException e)
            {
                Session.Status = e.Message;
                Session.Preview(null);
                return false;
            }

            return Preview(to);
        }
    }

    /// <summary>
    /// Drags the selected vocal notes in time, by the note grabbed, to the grid, and in pitch by the rows the pointer has
    /// crossed, with their lyrics. Nothing moves until the pointer has, so a click only selects, and leaves a note off the
    /// grid where it is.
    /// </summary>
    private sealed class VocalMoveGesture : VocalGesture
    {
        private readonly VocalPoint _pressed;
        private readonly int _pressedPitch;
        private readonly VocalNoteEvent _grabbed;
        private readonly IReadOnlyList<VocalNoteEvent> _notes;
        private readonly IReadOnlyCollection<VocalNotePlace> _selected;
        private (long Ticks, int Pitches) _delta;
        private bool _moved;

        public VocalMoveGesture(EditorTools tools, VocalPoint pressed, VocalTrackContent content, VocalNoteEvent grabbed)
            : base(tools, pressed.Track, content)
        {
            _pressed = pressed;
            _pressedPitch = pressed.Pitch ?? grabbed.Pitch;
            _grabbed = grabbed;
            _selected = [.. Session.Selection.VocalPlaces];
            _notes = VocalEdits.NotesAt(content, _selected);
        }

        public override void Drag(VocalPoint point)
        {
            if (point.Track != Track || _notes.Count == 0)
                return;
            int rows = (point.Pitch ?? _pressedPitch) - _pressedPitch;
            _moved |= Math.Abs(point.Tick - _pressed.Tick) > _pressed.Tolerance || rows != 0;
            if (!_moved)
                return;

            long tick = Tools.Snap.Nearest(_grabbed.Tick + (point.Tick - _pressed.Tick), Tools.Map);
            long ticks = Math.Max(tick - _grabbed.Tick, -_notes.Min(static n => n.Tick));
            int pitches = Math.Clamp(
                rows,
                VocalTrackContent.LowestPitch - _notes.Min(static n => n.Pitch),
                VocalTrackContent.HighestPitch - _notes.Max(static n => n.Pitch));
            if ((ticks, pitches) == _delta)
                return;
            _delta = (ticks, pitches);

            IReadOnlyList<VocalNotePlace> moved = [];
            bool previewed = Preview(c => VocalEdits.Change(c, _selected, n => n with { Tick = n.Tick + ticks, Pitch = n.Pitch + pitches }, out moved));
            Session.Selection.SelectVocalNotes(Track, previewed && moved.Count > 0 ? moved : _selected);
        }

        public override void Cancel() => Session.Selection.SelectVocalNotes(Track, _selected);
    }

    /// <summary>Drags the end of the selected vocal notes to the grid; each lasts at least a tick.</summary>
    private sealed class VocalSustainGesture(EditorTools tools, VocalTrack track, VocalTrackContent content, IReadOnlyList<VocalNotePlace> places)
        : VocalGesture(tools, track, content)
    {
        private long _end = -1;

        public override void Drag(VocalPoint point)
        {
            if (point.Track != Track)
                return;
            long end = Tools.Snap.Nearest(point.Tick, Tools.Map);
            if (end == _end)
                return;
            _end = end;
            Preview(c => VocalEdits.Change(c, places, n => n with { Length = Math.Max(1, end - n.Tick) }, out _));
        }
    }

    /// <summary>
    /// Lengthens or shortens vocal notes as the right button drags (<see cref="PressContext(VocalPoint, EditorModifiers)"/>):
    /// each by the ticks the pointer has moved on the grid, or with Ctrl each to end where the pointer is; each lasts at
    /// least a tick.
    /// </summary>
    private sealed class VocalLengthGesture(EditorTools tools, VocalTrack track, VocalTrackContent content, IReadOnlyList<VocalNotePlace> places, double pressed)
        : VocalGesture(tools, track, content)
    {
        private (long At, bool Match)? _last;

        public override void Drag(VocalPoint point)
        {
            if (point.Track != Track)
                return;
            long at = Tools.Snap.Nearest(point.Tick, Tools.Map);
            bool match = Tools._modifiers.HasFlag(EditorModifiers.Control);
            if (_last == (at, match))
                return;
            _last = (at, match);

            long delta = at - Tools.Snap.Nearest(pressed, Tools.Map);
            if (!match && delta == 0)
                Session.Preview(null);
            else
                Preview(c => VocalEdits.Change(c, places, n => n with { Length = Math.Max(1, match ? at - n.Tick : n.Length + delta) }, out _));
        }
    }

    /// <summary>
    /// Places a vocal note at the grid step pressed, a grid step long, and stretches it to the grid as the pointer drags;
    /// it never reaches past the next note's start.
    /// </summary>
    private sealed class VocalPlaceGesture(EditorTools tools, VocalTrack track, VocalTrackContent content, VocalNoteEvent note, long reach, string? lyric)
        : VocalGesture(tools, track, content)
    {
        private long _length = -1;

        public bool Start()
        {
            long step = Tools.Snap.Step(note.Tick, 1, Tools.Map) - note.Tick;
            return Place(Math.Max(1, step));
        }

        public override void Drag(VocalPoint point)
        {
            if (point.Track != Track)
                return;
            long end = Tools.Snap.Nearest(point.Tick, Tools.Map);
            if (end > note.Tick)
                Place(end - note.Tick);
        }

        public override void Release(VocalPoint point)
        {
            Drag(point);
            if (Session.CommitPreview() is ChangeVocals)
                Session.Selection.SelectVocalNotes(Track, [note.Place]);
        }

        private bool Place(long length)
        {
            length = Math.Max(1, Math.Min(length, reach - note.Tick));
            if (length == _length)
                return true;
            _length = length;
            return Preview(c => VocalEdits.Add(c, note with { Length = length }, lyric));
        }
    }

    /// <summary>Removes each vocal note the pointer passes over, with its lyrics, and in the phrase strip each phrase.</summary>
    private sealed class VocalEraseGesture : VocalGesture
    {
        private readonly HashSet<VocalNotePlace> _erased = [];
        private readonly HashSet<VocalPhraseEvent> _phrases = [];

        public VocalEraseGesture(EditorTools tools, VocalPoint pressed, VocalTrackContent content)
            : base(tools, pressed.Track, content) => Drag(pressed);

        public override void Drag(VocalPoint point)
        {
            if (point.Track != Track)
                return;
            bool added = point.InPhrases
                ? HitTestPhrase(Content, point) is { } phrase && _phrases.Add(phrase.Phrase)
                : HitTest(Content, point) is { } hit && _erased.Add(hit.Note.Place);
            if (added)
                Preview(c => VocalEdits.RemovePhrases(VocalEdits.Remove(c, _erased), _phrases));
        }
    }

    /// <summary>
    /// Selects the vocal notes in a box of ticks and rows, as the box is dragged out (Shift adding them to those selected).
    /// A click with no drag selects nothing.
    /// </summary>
    private sealed class VocalBoxGesture : VocalGesture
    {
        private readonly VocalPoint _pressed;
        private readonly bool _add;
        private readonly IReadOnlyCollection<VocalNotePlace> _before;
        private bool _moved;

        public VocalBoxGesture(EditorTools tools, VocalPoint pressed, VocalTrackContent content, bool add)
            : base(tools, pressed.Track, content)
        {
            _pressed = pressed;
            _add = add;
            _before = Session.Selection.Vocal == pressed.Track ? [.. Session.Selection.VocalPlaces] : [];
        }

        public override void Drag(VocalPoint point)
        {
            if (point.Track != Track)
                return;
            _moved |= Math.Abs(point.Tick - _pressed.Tick) > _pressed.Tolerance || point.Pitch != _pressed.Pitch;
            if (!_moved)
                return;

            long from = (long)Math.Round(Math.Max(0, Math.Min(point.Tick, _pressed.Tick)));
            long to = (long)Math.Round(Math.Max(0, Math.Max(point.Tick, _pressed.Tick)));
            int low = VocalTrackContent.LowestPitch, high = VocalTrackContent.HighestPitch;
            if (point.Pitch is { } a && _pressed.Pitch is { } b)
                (low, high) = (Math.Min(a, b), Math.Max(a, b));

            var inBox = Content.Notes
                .Where(n => n.Tick >= from && n.Tick <= to && DrawnPitch(Content, n) is var row && row >= low && row <= high)
                .Select(static n => n.Place);
            Session.Selection.SelectVocalNotes(Track, _add ? _before.Concat(inBox) : inBox);
            Session.Selection.SetRange(from, to);
        }

        public override void Release(VocalPoint point)
        {
            Drag(point);
            if (!_moved && !_add)
                Session.Selection.Clear();
        }
    }

    /// <summary>
    /// Draws out a vocal phrase along time, from the grid step pressed to the one the pointer is at, either way. A click
    /// without a drag adds nothing, and selects no phrases. It's written as 105, or as 105 and 106 when every phrase the
    /// part has is.
    /// </summary>
    private sealed class VocalPhraseDrawGesture : VocalGesture
    {
        private readonly long _from;
        private readonly VocalPhraseMarkers _markers;
        private VocalPhraseEvent? _tried;

        public VocalPhraseDrawGesture(EditorTools tools, VocalPoint pressed, VocalTrackContent content)
            : base(tools, pressed.Track, content)
        {
            _from = Tools.Snap.Nearest(Math.Max(0, pressed.Tick), Tools.Map);
            const VocalPhraseMarkers both = VocalPhraseMarkers.Primary | VocalPhraseMarkers.Secondary;
            _markers = content.Phrases.Count > 0 && content.Phrases.All(static p => p.Markers == both) ? both : VocalPhraseMarkers.Primary;
        }

        public override void Drag(VocalPoint point)
        {
            if (point.Track != Track)
                return;
            long to = Tools.Snap.Nearest(Math.Max(0, point.Tick), Tools.Map);
            VocalPhraseEvent? phrase = to == _from ? null : new VocalPhraseEvent(Math.Min(_from, to), Math.Abs(to - _from), _markers);
            if (phrase == _tried)
                return;
            _tried = phrase;
            if (phrase is { } added)
                Preview(c => VocalEdits.AddPhrase(c, added));
            else
                Session.Preview(null);
        }

        public override void Release(VocalPoint point)
        {
            Drag(point);
            if (Session.CommitPreview() is ChangeVocals && _tried is { } added)
                Session.Selection.SelectVocalPhrases(Track, [added]);
            else if (_tried is null)
                Session.Selection.ReplaceVocalPhrases(Track, []);
        }
    }

    /// <summary>
    /// Drags the selected vocal phrases in time, by the bar grabbed, to the grid, each keeping its length. Nothing moves
    /// until the pointer has, so a click only selects.
    /// </summary>
    private sealed class VocalPhraseMoveGesture : VocalGesture
    {
        private readonly VocalPoint _pressed;
        private readonly VocalPhraseEvent _grabbed;
        private readonly IReadOnlyList<VocalPhraseEvent> _phrases;
        private long _delta;
        private bool _moved;

        public VocalPhraseMoveGesture(EditorTools tools, VocalPoint pressed, VocalTrackContent content, VocalPhraseEvent grabbed)
            : base(tools, pressed.Track, content)
        {
            _pressed = pressed;
            _grabbed = grabbed;
            _phrases = Session.Selection.VocalPhrases;
        }

        public override void Drag(VocalPoint point)
        {
            _moved |= Math.Abs(point.Tick - _pressed.Tick) > _pressed.Tolerance;
            if (!_moved || point.Track != Track || _phrases.Count == 0)
                return;
            long tick = Tools.Snap.Nearest(_grabbed.Tick + (point.Tick - _pressed.Tick), Tools.Map);
            long delta = Math.Max(tick - _grabbed.Tick, -_phrases.Min(static p => p.Tick));
            if (delta == _delta)
                return;
            _delta = delta;

            IReadOnlyList<VocalPhraseEvent> moved = [];
            bool previewed = delta != 0 && Preview(c => VocalEdits.ChangePhrases(c, _phrases, p => p with { Tick = p.Tick + delta }, out moved));
            if (delta == 0)
                Session.Preview(null);
            Session.Selection.ReplaceVocalPhrases(Track, previewed ? moved : _phrases);
        }

        public override void Cancel() => Session.Selection.ReplaceVocalPhrases(Track, _phrases);
    }

    /// <summary>
    /// Drags one end of a vocal phrase to the grid, the other staying where it is; it never reaches the other end.
    /// Nothing changes until the pointer has moved, so a click only selects.
    /// </summary>
    private sealed class VocalPhraseResizeGesture : VocalGesture
    {
        private readonly VocalPoint _pressed;
        private readonly VocalPhraseEvent _phrase;
        private readonly bool _start;
        private readonly IReadOnlyList<VocalPhraseEvent> _selected;
        private VocalPhraseEvent _tried;
        private bool _moved;

        public VocalPhraseResizeGesture(EditorTools tools, VocalPoint pressed, VocalTrackContent content, VocalPhraseEvent phrase, bool start)
            : base(tools, pressed.Track, content)
        {
            _pressed = pressed;
            _phrase = phrase;
            _start = start;
            _selected = Session.Selection.VocalPhrases;
            _tried = phrase;
        }

        public override void Drag(VocalPoint point)
        {
            _moved |= Math.Abs(point.Tick - _pressed.Tick) > _pressed.Tolerance;
            if (!_moved || point.Track != Track)
                return;
            var snap = Tools.Snap;
            var map = Tools.Map;
            long at = snap.Nearest(Math.Max(0, point.Tick), map);
            long start = _phrase.Tick, end = _phrase.EndTick;
            if (_start)
            {
                if (at >= end)
                    at = snap.IsOn ? snap.Step(end, -1, map) : end - 1;
                if (at >= end || at < 0)
                    return;
                start = at;
            }
            else
            {
                end = Math.Max(at, snap.IsOn ? snap.Step(start, 1, map) : start + 1);
            }

            var resized = _phrase with { Tick = start, Length = end - start };
            if (resized == _tried)
                return;
            _tried = resized;

            bool previewed = Preview(c => VocalEdits.ChangePhrases(c, [_phrase], _ => resized, out _));
            var shown = previewed ? resized : _phrase;
            Session.Selection.ReplaceVocalPhrases(Track, _selected.Select(p => p == _phrase ? shown : p));
        }

        public override void Cancel() => Session.Selection.ReplaceVocalPhrases(Track, _selected);
    }
}
