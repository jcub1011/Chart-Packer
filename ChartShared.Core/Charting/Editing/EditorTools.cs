using ChartShared.Core.Charting.Commands;
using ChartShared.Core.Charts;

namespace ChartShared.Core.Charting.Editing;

/// <summary>
/// The editing tools (charter-spec §4.5, C3 item 1, C5): what a click, a drag or a key does to the chart. Both canvases
/// turn the pointer into <see cref="ChartPoint"/>s and pass them here, so every tool behaves the same in both views
/// (AC C3.2), and the tools' tests run against this rather than either canvas.
/// </summary>
/// <remarks>
/// A press starts a gesture, drags change it, and the release ends it, as one step on the undo stack. While it goes on, the
/// gesture's change is applied to the chart as a preview (<see cref="EditorSession.Preview"/>), so every view draws it.
/// </remarks>
public sealed partial class EditorTools
{
    private const string DoubleKickOffExpert = "A 2x kick is on Expert only.";
    private const string NoPhraseLength = "A phrase has to end after it starts.";
    private const string NoDiscoFlipLength = "A disco flip section has to end after it starts.";
    private const string DiscoFlipOffPro = "Only Pro drums have disco flip.";
    private const string DiscoFlipWithoutEnd = "This disco flip section never ends, as the file has it, so it can't be moved or have its start changed. Give it an end first.";
    private const string SyncToolKeeps = "The Sync tool doesn't delete anything; pick another tool to delete.";
    private const string LoopToolKeeps = "The Loop tool doesn't delete anything; pick another tool to delete.";

    // The markings a chord's type is one of, natural having none (SetChordType).
    private static readonly ChordFlags[] ChordTypeFlags = [ChordFlags.ForceHopo, ChordFlags.ForceStrum, ChordFlags.Tap];

    private readonly EditorSession _session;
    private Gesture? _gesture;

    // The modifier keys held as the gesture last moved, which some gestures read as they go.
    private EditorModifiers _modifiers;

    // The modifier keys held, as last taken in with no button down (HoldKeys), which say whether the Sync tool is held on.
    private EditorModifiers _held;

    // Whether a press with Shift held has started the selection afresh, so those after it, while Shift stays down, add to it.
    private bool _shiftSelecting;

    internal EditorTools(EditorSession session) => _session = session;

    /// <summary>Whether a gesture is going on.</summary>
    public bool IsActive => _gesture is not null;

    /// <summary>Whether the Sync tool is active: picked, or held on with Ctrl + Shift.</summary>
    public bool SyncActive => _session.Tool == EditorTool.Sync
        || (_held.HasFlag(EditorModifiers.Control) && _held.HasFlag(EditorModifiers.Shift));

    /// <summary>
    /// Whether a press with <paramref name="modifiers"/> held is the Sync tool's: the tool picked, or Ctrl + Shift held,
    /// which picks it up for as long as they're held. With Shift alone held, whatever the tool, the press is the select
    /// tool's.
    /// </summary>
    public bool IsSyncPress(EditorModifiers modifiers) =>
        (modifiers.HasFlag(EditorModifiers.Control) && modifiers.HasFlag(EditorModifiers.Shift))
        || (_session.Tool == EditorTool.Sync && !modifiers.HasFlag(EditorModifiers.Shift));

    /// <summary>
    /// Whether the Loop tool is active: picked, or held on with L (<see cref="EditorSession.LoopToolHeld"/>). It draws the
    /// loop region out, and takes hold of its ends to resize it or its body to move it (charter-spec C10 item 2).
    /// </summary>
    public bool LoopActive => _session.Tool == EditorTool.Loop || _session.LoopToolHeld;

    private EditableChart Chart => _session.Chart;

    private SnapGrid Snap => _session.Snap;

    /// <summary>
    /// Where a note placed or moved to <paramref name="tick"/> of <paramref name="key"/>'s track goes: the nearest onset
    /// within half a grid step with snap to onset on, otherwise the grid (charter-spec C14 item 2).
    /// </summary>
    private long SnapTick(double tick, ChartTrackKey key) => _session.Quantize(tick, key);

    /// <summary>
    /// Moves the selected notes to their nearest onsets, chord by chord, as one step (charter-spec C14 item 3), and keeps
    /// them selected where they land; chords with no onset near stay. Null when nothing is selected or there are no
    /// onsets.
    /// </summary>
    public NudgePlan? NudgeSelectionToOnsets()
    {
        if (_session.Selection.Track is not { } key || _session.Selection.Notes is not { Count: > 0 } notes)
            return null;
        if (_session.Onsets?.OnsetsFor(key) is not { Count: > 0 } onsets)
            return null;

        Cancel();
        var plan = OnsetNudge.Plan(_session, key, notes, onsets);
        if (plan.Command is { } command && _session.Execute(command))
            _session.Selection.Select(key, plan.Notes);
        return plan;
    }

    /// <summary>
    /// Puts the selected notes on <paramref name="grid"/>, chord by chord, as one step (<see cref="NoteQuantize"/>), and keeps
    /// them selected where they land. Null when nothing is selected.
    /// </summary>
    public QuantizePlan? QuantizeSelection(SnapGrid grid)
    {
        if (_session.Selection.Track is not { } key || _session.Selection.Notes is not { Count: > 0 } notes)
            return null;

        Cancel();
        var plan = NoteQuantize.Plan(_session, key, notes, grid);
        if (plan.Command is { } command && _session.Execute(command))
            _session.Selection.Select(key, plan.Notes);
        return plan;
    }

    private TempoMap Map => _session.TempoMap;

    /// <summary>
    /// Starts a gesture at <paramref name="point"/> with the current tool; in the phrase rail, with any tool but the erase
    /// tool, a gesture on the phrase there (charter-spec C8). Pressed on a selected note with Ctrl, a drag lengthens or
    /// shortens each selected note's sustain by the same grid steps; with Alt, it lays a run of each, as many steps as
    /// dragged. With Shift, any tool selects as the select tool does, the first press while Shift is held selecting afresh;
    /// Ctrl + Shift held instead picks up the Sync tool's press for as long as they're held, and L held the Loop tool's
    /// (<see cref="LoopActive"/>), which wins over every other tool's press since the region covers the whole song. The
    /// place tool puts a note down as <see cref="EditorSession.Placing"/> has it, Alt swapping a fret for an open note or
    /// the kick, and Ctrl a run for a sustain.
    /// </summary>
    public void Press(ChartPoint point, EditorModifiers modifiers = EditorModifiers.None)
    {
        Cancel();
        _modifiers = modifiers;
        _session.Focused = point.Track;
        bool shift = modifiers.HasFlag(EditorModifiers.Shift);
        if (!shift)
            _shiftSelecting = false;
        if (_session.TrackOf(point.Track) is null)
            return;

        // The Loop tool's press, or L held, which holds it on for as long as it's held. The region covers the whole song
        // rather than one lane, so a press outside the lanes or in the phrase rail isn't its business, and it wins over
        // the other tools' presses.
        if (LoopActive)
        {
            _gesture = point.Rail is null && point.Lane is not null ? PressLoop(point.Tick, point.Tolerance) : null;
            return;
        }

        if (point.Rail is not null)
        {
            // The erase tool erases there too, as it shows; a proposed phrase's ghost is still kept or dropped.
            bool erases = _session.Tool == EditorTool.Erase && !shift && _session.Proposals?.HitTest(point) is null;
            _gesture = erases ? new EraseGesture(this, point) : PressRail(point, shift);
            return;
        }

        // The Sync tool's press, or Ctrl + Shift held, which picks the Sync tool up for as long as they're held. It wins
        // over Shift's and Ctrl's presses of their own.
        if (IsSyncPress(modifiers))
        {
            _gesture = point.Lane is null ? null : PressSync(point);
            return;
        }

        if (PressSelected(point, modifiers) is { } edit)
        {
            _gesture = edit;
            return;
        }

        // Shift lends the other tools the select tool's press: a selection started afresh, added to while Shift stays down.
        if (shift && _session.Tool != EditorTool.Select)
        {
            if (!_shiftSelecting)
                _session.Selection.Clear();
            _shiftSelecting = true;
            _gesture = PressSelect(point, shift: true);
            return;
        }

        // Alt+drag selects a time range across the lanes (charter-spec C15 item 2).
        if (_session.Tool == EditorTool.Select && modifiers.HasFlag(EditorModifiers.Alt))
        {
            _gesture = new RangeGesture(this, point);
            return;
        }

        _gesture = _session.Tool switch
        {
            EditorTool.Select => PressSelect(point, shift),
            EditorTool.Place => PressPlace(point, alt: modifiers.HasFlag(EditorModifiers.Alt)),
            EditorTool.Erase => new EraseGesture(this, point),
            EditorTool.Phrase => PhraseDrawAt(point) is { } draw ? PressDraw(point.Track, draw) : null,
            EditorTool.Tempo => PressTempo(point),
            _ => null,
        };
    }

    /// <summary>
    /// A press with the Sync tool, or Ctrl + Shift held, in the lanes: on the synchronized region's end, a drag resizes
    /// it; on its body, a drag moves it; on a passage synced with it, a click makes that passage the region's source;
    /// anywhere else, a drag draws a new region in place of any drawn.
    /// </summary>
    private Gesture PressSync(ChartPoint point) => SyncHitOf(point) switch
    {
        { Grip: SyncGrip.Match } hit => new SyncGesture(this, point, hit.Start),
        { Grip: SyncGrip.Start } => new SyncResizeGesture(this, point, start: true),
        { Grip: SyncGrip.End } => new SyncResizeGesture(this, point, start: false),
        { Grip: SyncGrip.Body } => new SyncMoveGesture(this, point),
        _ => new SyncGesture(this, point),
    };

    /// <summary>
    /// What a press at <paramref name="point"/> takes hold of on the synchronized region: an end to resize it, its body
    /// to move it, or a passage synced with it to make the region's source (<see cref="SyncGrip.Match"/>, at the
    /// passage's start). Null where there's nothing to take hold of: a press draws a new region.
    /// </summary>
    public SyncHit? SyncHitOf(ChartPoint point)
    {
        var sync = _session.Sync;
        if (!sync.IsActive || point.Track != sync.Track || sync.Span is not var (from, to))
            return null;
        long length = to - from;
        foreach (long start in sync.Matches)
        {
            if (point.Tick >= start && point.Tick < start + length)
                return new SyncHit(SyncGrip.Match, start);
        }

        if (Math.Abs(point.Tick - from) <= point.Tolerance)
            return new SyncHit(SyncGrip.Start, from);
        if (Math.Abs(point.Tick - to) <= point.Tolerance)
            return new SyncHit(SyncGrip.End, to);
        return point.Tick > from && point.Tick < to ? new SyncHit(SyncGrip.Body, from) : null;
    }

    /// <summary>
    /// Where a press at <paramref name="point"/>, with <paramref name="modifiers"/> held, starts drawing a new
    /// synchronized region: the grid step nearest it. Null where a press does something else: one that takes hold of the
    /// region or of a passage synced with it (<see cref="SyncHitOf"/>), one in the rail, or one that isn't the Sync
    /// tool's press (<see cref="IsSyncPress"/>).
    /// </summary>
    public long? SyncDrawStart(ChartPoint point, EditorModifiers modifiers = EditorModifiers.None)
    {
        if (!IsSyncPress(modifiers) || point.Lane is null || Chart.PartOf(point.Track.Instrument) is null)
            return null;
        return SyncHitOf(point) is null ? Snap.Nearest(point.Tick, Map) : null;
    }

    /// <summary>
    /// A press with the Loop tool, or L held, in a lane: on the loop region's end, a drag resizes it; on its body, a drag
    /// moves it; anywhere else, a drag draws a new region in place of any drawn, which the song repeats (charter-spec C10
    /// item 2).
    /// </summary>
    private Gesture? PressLoop(double tick, double tolerance) => _session.Loop is null ? null : LoopGripAt(tick, tolerance) switch
    {
        LoopGrip.Start => new LoopResizeGesture(this, tick, tolerance, start: true),
        LoopGrip.End => new LoopResizeGesture(this, tick, tolerance, start: false),
        LoopGrip.Body => new LoopMoveGesture(this, tick, tolerance),
        _ => new LoopGesture(this, tick, tolerance),
    };

    /// <summary>
    /// What a press at <paramref name="tick"/> takes hold of on the loop region: an end to resize it, or its body to move
    /// it. Null where there's nothing to take hold of, so a press draws a new region, or with no region drawn.
    /// </summary>
    public LoopGrip? LoopGripAt(double tick, double tolerance)
    {
        if (_session.Loop?.Span is not { } span)
            return null;
        double from = _session.TickAt(span.Start), to = _session.TickAt(span.End);
        if (Math.Abs(tick - from) <= tolerance)
            return LoopGrip.Start;
        if (Math.Abs(tick - to) <= tolerance)
            return LoopGrip.End;
        return tick > from && tick < to ? LoopGrip.Body : null;
    }

    /// <summary>
    /// Where a press at <paramref name="tick"/>, with the Loop tool picked or L held, starts drawing a new loop region: the
    /// grid step nearest it. Null where a press does something else: one that takes hold of the region
    /// (<see cref="LoopGripAt"/>), or one that isn't the Loop tool's press (<see cref="LoopActive"/>).
    /// </summary>
    public long? LoopDrawStart(double tick, double tolerance)
    {
        if (!LoopActive || _session.Loop is null)
            return null;
        return LoopGripAt(tick, tolerance) is null ? Snap.Nearest(tick, Map) : null;
    }

    /// <summary>
    /// Starts erasing at <paramref name="point"/>, as the middle button does: each note the pointer passes over goes, and
    /// in the phrase rail each phrase or disco flip section, as one step. With the Tempo tool, it removes the tempo marker
    /// there instead. With the Sync tool active, it takes the synchronized region away rather than erasing anything, and
    /// with the Loop tool active, the loop region.
    /// </summary>
    public void PressErase(ChartPoint point)
    {
        Cancel();
        _modifiers = EditorModifiers.None;
        _session.Focused = point.Track;
        if (_session.TrackOf(point.Track) is null)
            return;

        // The Loop tool's middle button takes the loop region away, as the Sync tool's does the synchronized region.
        if (LoopActive)
        {
            _session.ClearLoop();
            return;
        }

        if (SyncActive)
        {
            _session.Sync.Clear();
            return;
        }

        if (_session.Tool == EditorTool.Tempo)
            RemoveTempoNear(point.Tick, point.Tolerance);
        else
            _gesture = new EraseGesture(this, point);
    }

    /// <summary>
    /// Starts the right button's gesture at <paramref name="point"/>, whatever the tool. A note there that isn't selected
    /// is selected alone; then a drag lengthens or shortens the selected notes' sustains, each by the same grid steps so
    /// they keep their own lengths, or with Ctrl ends them all where the pointer is, as one step. With Alt, the drag lays
    /// a run of each note on the grid instead, up to the pointer, so the runs end together. A click with no drag,
    /// cancelled rather than released, leaves the selection for the note menu.
    /// </summary>
    public void PressContext(ChartPoint point, EditorModifiers modifiers = EditorModifiers.None)
    {
        Cancel();
        _modifiers = modifiers;
        _session.Focused = point.Track;
        if (_session.TrackOf(point.Track) is null || point.Rail is not null)
            return;

        var selection = _session.Selection;
        if (HitTest(point) is { } hit && !selection.Contains(point.Track, hit.Note))
            selection.Select(point.Track, [hit.Note]);
        if (selection.Track == point.Track && selection.Notes is { Count: > 0 } notes)
            _gesture = new NoteLengthGesture(this, point.Track, notes, point.Tick, laysRun: null);
    }

    /// <summary>
    /// Takes note of the modifier keys held while no button is down: once Shift is let go, the next press with Shift
    /// selects afresh, and whether the Sync tool is held on with Ctrl + Shift is kept up.
    /// </summary>
    public void HoldKeys(EditorModifiers modifiers)
    {
        _held = modifiers;
        if (!modifiers.HasFlag(EditorModifiers.Shift))
            _shiftSelecting = false;
    }

    /// <summary>
    /// The left button's gesture on a selected note with Ctrl or Alt held, with the select or place tool: with Ctrl, each
    /// selected note's sustain lengthened or shortened by the same grid steps; with Alt, a run of each, as many steps as
    /// dragged. Null anywhere else, or with neither held.
    /// </summary>
    private NoteLengthGesture? PressSelected(ChartPoint point, EditorModifiers modifiers)
    {
        bool ctrl = modifiers.HasFlag(EditorModifiers.Control);
        if ((!ctrl && !modifiers.HasFlag(EditorModifiers.Alt)) || _session.Tool is not (EditorTool.Select or EditorTool.Place))
            return null;
        var selection = _session.Selection;
        if (HitTest(point) is not { } hit || !selection.Contains(point.Track, hit.Note))
            return null;
        return new NoteLengthGesture(this, point.Track, selection.Notes, point.Tick, laysRun: !ctrl);
    }

    /// <summary>Moves the gesture on to <paramref name="point"/>, with <paramref name="modifiers"/> held.</summary>
    public void Drag(ChartPoint point, EditorModifiers modifiers = EditorModifiers.None)
    {
        _modifiers = modifiers;
        _gesture?.Drag(point);
    }

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
    /// Places a note in <paramref name="shown"/>'s column at the cursor of the focused lane, or removes the one there
    /// (charter-spec §9: 1–5, and 0 for an open note or the kick). Inside a disco flip section, it's the pad drawn in that
    /// column (C6 item 7), so 2 places red. A note placed leaves nothing selected. One there isn't removed while the Sync
    /// or Loop tool is active.
    /// </summary>
    public void ToggleNoteAtCursor(int shown)
    {
        if (_session.Focused is not { } key || _session.TrackOf(key) is not { } track)
            return;
        long tick = _session.CursorTick;
        int lane = _session.StoredLane(key, tick, shown);
        if (track.TryGetNote(tick, lane, doubleKick: false, out var note))
        {
            if (SyncActive || LoopActive)
            {
                _session.Status = SyncActive ? SyncToolKeeps : LoopToolKeeps;
                return;
            }

            _session.Execute(new RemoveNotes(key.Instrument, key.Difficulty, [note]));
            return;
        }

        var placed = new EditableNote(tick, EditableNote.PlainLength, lane);
        if (_session.Execute(new AddNotes(key.Instrument, key.Difficulty, [placed])))
            _session.Selection.Clear();
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
    /// Moves the selected notes <paramref name="steps"/> grid steps in time and <paramref name="lanes"/> columns across
    /// (charter-spec §9, Alt+arrows), as one step; across the columns as they're drawn (<see cref="MoveOf"/>).
    /// </summary>
    public void MoveSelection(int steps, int lanes, LaneSlots slots)
    {
        ArgumentNullException.ThrowIfNull(slots);
        if (_session.Selection.HasRangeAcrossLanes)
        {
            MoveRange(steps);
            return;
        }

        if (_session.Selection.Track is not { } key)
            return;
        var notes = _session.Selection.Notes;
        if (notes.Count == 0)
            return;
        var first = notes[0];
        long tickDelta = steps == 0 ? 0 : Snap.Step(first.Tick, steps, Map) - first.Tick;
        int laneDelta = LaneDelta(ShownLanes(key, notes), lanes, slots);
        if (tickDelta == 0 && laneDelta == 0)
            return;
        var (command, moved) = MoveOf(key, notes, tickDelta, laneDelta);
        if (_session.Execute(command))
            _session.Selection.Select(key, moved);
    }

    /// <summary>Selects every note of the focused lane (Ctrl+A).</summary>
    public void SelectAll()
    {
        if (_session.Focused is not { } key || _session.TrackOf(key) is not { } track)
            return;
        _session.Selection.Select(key, track.Notes);
    }

    /// <summary>
    /// Deletes the selected notes, phrases, disco flip sections and tempo markers (Delete), or a vocal part's notes and
    /// phrases, as one step. Nothing is deleted while the Sync or Loop tool is active.
    /// </summary>
    public void DeleteSelection()
    {
        if (SyncActive || LoopActive)
        {
            _session.Status = SyncActive ? SyncToolKeeps : LoopToolKeeps;
            return;
        }

        var selection = _session.Selection;
        var steps = new List<Func<(IChartCommand?, string?)>>();
        if (DeleteVocalsStep() is { } vocals)
            steps.Add(vocals);
        if (selection.Track is { } key && selection.Notes is { Count: > 0 } notes)
            steps.Add(() => (new RemoveNotes(key.Instrument, key.Difficulty, notes), null));
        else if (selection.HasRangeAcrossLanes)
            steps.AddRange(RemoveRangeSteps());
        if (selection.PhrasePart is { } part)
        {
            foreach (var phrase in selection.Phrases)
                steps.Add(() => (new RemovePhrase(part, phrase), null));
        }

        foreach (var section in selection.DiscoFlips)
            steps.Add(() => (new RemoveDiscoFlip(section.Difficulty, section.Range), null));

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
    /// Adds a star power phrase over the selection (charter-spec §9: S), or removes the one there already, which starts
    /// and ends exactly where it would. Over selected notes, it runs from the first one's tick to the end of the last to
    /// end, a tick past it for a note without a sustain, so it covers every one as the games read markers; otherwise over
    /// the span selected, in the focused lane's part.
    /// </summary>
    public void ToggleStarPowerOverSelection()
    {
        var selection = _session.Selection;
        ChartInstrument part;
        long from, to;
        if (selection.Track is { } key && selection.Notes is { Count: > 0 } notes)
        {
            part = key.Instrument;
            from = notes[0].Tick;
            to = notes.Max(static n => n.Tick + Math.Max(1, n.Length));
        }
        else if (selection.Range is { } range && range.To > range.From && _session.Focused is { } focused)
        {
            part = focused.Instrument;
            (from, to) = range;
        }
        else
        {
            _session.Status = "Select notes or a span to put star power over.";
            return;
        }

        if (Chart.PartOf(part) is not { } phrased)
            return;
        var phrase = new Phrase(PhraseKind.StarPower, from, to - from);
        _session.Execute(phrased.Phrases.IndexOf(phrase) >= 0 ? new RemovePhrase(part, phrase) : new AddPhrase(part, phrase));
    }

    /// <summary>
    /// Moves the start of every selected phrase to <paramref name="tick"/>, keeping its end, as Properties sets it
    /// (charter-spec C3 item 4), as one step.
    /// </summary>
    public void SetPhraseStart(long tick) => ChangeSelectedPhrases(p => p with { Tick = tick, Length = p.Tick + p.Length - tick });

    /// <summary>Moves the end of every selected phrase to <paramref name="tick"/>, as Properties sets it, as one step.</summary>
    public void SetPhraseEnd(long tick) => ChangeSelectedPhrases(p => p with { Length = tick - p.Tick });

    /// <summary>
    /// Extends every selected roll and trill down to <paramref name="difficulty"/> from Expert (charter-spec C6 item 6), as
    /// Properties sets it, as one step. Other phrases are left as they are.
    /// </summary>
    public void SetLowestDifficulty(ChartDifficulty difficulty) =>
        ChangeSelectedPhrases(p => Phrase.HasLowestDifficulty(p.Kind) ? p with { LowestDifficulty = difficulty } : p);

    /// <summary>
    /// Moves the start of every selected disco flip section to <paramref name="tick"/>, keeping its end, as Properties sets
    /// it (charter-spec C3 item 4, C6 item 7), as one step.
    /// </summary>
    public void SetDiscoFlipStart(long tick) => ChangeSelectedDiscoFlips(r => (tick, r.EndTick));

    /// <summary>Moves the end of every selected disco flip section to <paramref name="tick"/>, as Properties sets it, as one step.</summary>
    public void SetDiscoFlipEnd(long tick) => ChangeSelectedDiscoFlips(r => (r.Tick, tick));

    /// <summary>Changes the selected disco flip sections, as one step, and selects them as they become.</summary>
    private void ChangeSelectedDiscoFlips(Func<DiscoFlipRange, (long Tick, long? EndTick)> change)
    {
        var selection = _session.Selection;
        var selected = selection.DiscoFlips;
        var changes = new List<(DiscoFlipSection From, long Tick, long EndTick)>();
        foreach (var section in selected)
        {
            var (tick, end) = change(section.Range);
            if (tick == section.Range.Tick && end == section.Range.EndTick)
                continue;
            if (end is not { } endTick)
            {
                _session.Status = DiscoFlipWithoutEnd;
                return;
            }

            if (tick < 0 || endTick <= tick)
            {
                _session.Status = NoDiscoFlipLength;
                return;
            }

            changes.Add((section, tick, endTick));
        }

        if (changes.Count > 0 && _session.Execute(DiscoFlipChangeOf(changes)))
            ReselectDiscoFlips(selected, changes.Select(static c => (c.From, c.Tick)));
    }

    /// <summary>
    /// Selects <paramref name="selected"/> in place of the disco flip sections selected, each of <paramref name="changed"/>
    /// as the edit has left it: the section now covering its new start.
    /// </summary>
    private void ReselectDiscoFlips(IEnumerable<DiscoFlipSection> selected, IEnumerable<(DiscoFlipSection From, long Tick)> changed)
    {
        var now = changed.ToDictionary(static c => c.From, c => SectionAt(c.From.Difficulty, c.Tick));
        var selection = _session.Selection;
        selection.ReplaceDiscoFlips(selected.Select(s => now.TryGetValue(s, out var after) ? after : s).OfType<DiscoFlipSection>());
        selection.Reconcile();
    }

    /// <summary>
    /// The command that changes each disco flip section to start and end where given, as one: those moving later first,
    /// the latest first, then the others, the earliest first, so none is refused for a place another is leaving.
    /// </summary>
    private static IChartCommand DiscoFlipChangeOf(IEnumerable<(DiscoFlipSection From, long Tick, long EndTick)> changes)
    {
        var ordered = changes
            .OrderByDescending(static c => c.Tick > c.From.Range.Tick)
            .ThenBy(static c => c.Tick > c.From.Range.Tick ? -c.From.Range.Tick : c.From.Range.Tick)
            .Select(static c => (IChartCommand)new ChangeDiscoFlip(c.From.Difficulty, c.From.Range, c.Tick, c.EndTick))
            .ToList();
        return ordered.Count == 1 ? ordered[0] : new CompositeCommand(ordered);
    }

    /// <summary>
    /// The disco flip section of <paramref name="difficulty"/> that covers <paramref name="tick"/> as the chart is now, for
    /// selecting a section as an edit has left it: moved, or joined with one it now touches.
    /// </summary>
    private DiscoFlipSection? SectionAt(ChartDifficulty difficulty, long tick)
    {
        if (Chart.PartOf(ChartInstrument.Drums) is not { } drums)
            return null;
        foreach (var range in DiscoFlips.RangesOf(drums[difficulty]))
        {
            if (range.Covers(tick))
                return new DiscoFlipSection(difficulty, range);
        }

        return null;
    }

    /// <summary>Changes the selected phrases, as one step, and selects them as they become.</summary>
    private void ChangeSelectedPhrases(Func<Phrase, Phrase> change)
    {
        var selection = _session.Selection;
        if (selection.PhrasePart is not { } part)
            return;
        var from = selection.Phrases;
        var to = from.Select(p => Classified(part, change(p))).ToList();
        var changing = Enumerable.Range(0, from.Count).Where(i => from[i] != to[i]).ToList();
        if (changing.Count == 0)
            return;
        if (changing.Any(i => to[i].Tick < 0 || to[i].Length <= 0))
        {
            _session.Status = NoPhraseLength;
            return;
        }

        if (_session.Execute(ChangeOf(part, [.. changing.Select(i => from[i])], [.. changing.Select(i => to[i])])))
            selection.ReplacePhrases(part, to);
    }

    /// <summary>
    /// <paramref name="phrase"/> as <paramref name="part"/> holds it at its tick: on drums, a fill at or after <c>[coda]</c>
    /// is a big rock ending, and one before it a fill (charter-spec C8 item 4).
    /// </summary>
    private Phrase Classified(ChartInstrument part, Phrase phrase) =>
        ChartInstruments.KindOf(part) == ChartInstrumentKind.Drums && phrase.Kind is PhraseKind.DrumFill or PhraseKind.BigRockEnding
            ? phrase with { Kind = DrumFills.KindAt(Chart, phrase.Tick) }
            : phrase;

    /// <summary>The command that changes each of <paramref name="from"/> to the phrase at its index in <paramref name="to"/>.</summary>
    private static IChartCommand ChangeOf(ChartInstrument part, IReadOnlyList<Phrase> from, IReadOnlyList<Phrase> to)
    {
        var commands = from.Select((p, i) => (IChartCommand)new ChangePhrase(part, p, to[i])).ToList();
        return commands.Count == 1 ? commands[0] : new CompositeCommand(commands);
    }

    /// <summary>
    /// Turns <paramref name="flag"/> on for every selected chord, or off when every one already has it (charter-spec §9:
    /// H, G and T). Force HOPO and force strum exclude each other.
    /// </summary>
    public void ToggleChordFlag(ChordFlags flag)
    {
        var selection = _session.Selection;
        if (selection.HasRangeAcrossLanes)
        {
            ToggleRangeChordFlag(flag);
            return;
        }

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
    /// Makes every selected chord <paramref name="type"/>, as the note menu picks it, as one step: force HOPO, force strum
    /// or tap, each turning the other two off, or natural, with none of them, for <see cref="ChordFlags.None"/>.
    /// </summary>
    public void SetChordType(ChordFlags type)
    {
        if (type is not (ChordFlags.None or ChordFlags.ForceHopo or ChordFlags.ForceStrum or ChordFlags.Tap))
            throw new ArgumentOutOfRangeException(nameof(type), type, "A chord is natural, force HOPO, force strum or tap.");
        if (SelectedChords() is not { } selected)
            return;

        // The markings turned off go first, so the chord never has two at once.
        var (key, track, ticks) = selected;
        var commands = new List<IChartCommand>();
        foreach (var flag in ChordTypeFlags.OrderBy(f => f == type))
        {
            bool on = flag == type;
            var changing = ticks.Where(t => track.FlagsAt(t).HasFlag(flag) != on).ToList();
            if (changing.Count > 0)
                commands.Add(new SetChordFlags(key.Instrument, key.Difficulty, changing, flag, on));
        }

        if (commands.Count > 0)
            _session.Execute(commands.Count == 1 ? commands[0] : new CompositeCommand(commands));
    }

    /// <summary>
    /// The marking every selected chord has, for the note menu: force HOPO, force strum, tap, or
    /// <see cref="ChordFlags.None"/> for natural. Null when they differ, or no fret notes are selected.
    /// </summary>
    public ChordFlags? SelectedChordType()
    {
        if (SelectedChords() is not { } selected)
            return null;
        const ChordFlags types = ChordFlags.ForceHopo | ChordFlags.ForceStrum | ChordFlags.Tap;
        var chords = selected.Ticks.Select(t => selected.Track.FlagsAt(t) & types).Distinct().ToList();
        return chords.Count == 1 ? chords[0] : null;
    }

    /// <summary>
    /// Makes the selected notes open notes, as one step: the selected frets of each chord become one open note as long as
    /// the longest of them, beside any frets not selected, and the chord keeps its marking. Unless <paramref name="open"/>,
    /// the selected open notes go on the first fret instead. Where the lane they go to has a note already, they join it.
    /// The notes made are selected.
    /// </summary>
    public void SetOpen(bool open)
    {
        if (SelectedChords() is not { } selected)
            return;
        var (key, track, _) = selected;
        int to = open ? ChartNote.OpenLane : 0;
        var remove = new List<EditableNote>();
        var add = new List<EditableNote>();
        foreach (var chord in _session.Selection.Notes.Where(n => (n.Lane == ChartNote.OpenLane) != open).GroupBy(static n => n.Tick))
        {
            remove.AddRange(chord);
            if (!track.TryGetNote(chord.Key, to, doubleKick: false, out _))
                add.Add(new EditableNote(chord.Key, chord.Max(static n => n.Length), to));
        }

        if (remove.Count == 0)
            return;
        var kept = _session.Selection.Notes.Where(n => !remove.Contains(n)).ToList();
        if (_session.Execute(new ReplaceNotes(key.Instrument, key.Difficulty, remove, add)))
            _session.Selection.Select(key, [.. kept, .. add]);
    }

    /// <summary>Whether every selected note is an open note; false when no fret notes are selected.</summary>
    public bool IsSelectionOpen() =>
        SelectedChords() is not null && _session.Selection.Notes.All(static n => n.Lane == ChartNote.OpenLane);

    /// <summary>The selected fret notes' track, and their chords' ticks; null when none are selected, or they're drum notes.</summary>
    private (ChartTrackKey Key, EditableTrack Track, IReadOnlyList<long> Ticks)? SelectedChords()
    {
        var selection = _session.Selection;
        if (selection.Track is not { } key || _session.TrackOf(key) is not { } track || track.Kind == ChartInstrumentKind.Drums)
            return null;
        var ticks = selection.Notes.Select(static n => n.Tick).Distinct().ToList();
        return ticks.Count == 0 ? null : (key, track, ticks);
    }

    /// <summary>
    /// Makes the selected yellow, blue and green Pro drum notes toms, or cymbals when every one is a tom already
    /// (charter-spec §9: C). Cymbal or tom is the whole part's, so the same pads at those ticks change on every
    /// difficulty, and the status says which others have a note there (C6 item 3).
    /// </summary>
    public void ToggleCymbal()
    {
        if (SelectedCymbals() is { } selected)
            SetTom(!selected.Places.All(p => DrumCymbals.IsTom(selected.Drums, p.Pad, p.Tick)));
    }

    /// <summary>
    /// Makes the selected yellow, blue and green Pro drum notes toms, or cymbals (charter-spec C6 item 3), as the
    /// Properties panel sets them. The same pads at those ticks change on every difficulty, and the status says which
    /// others have a note there.
    /// </summary>
    public void SetTom(bool tom)
    {
        if (SelectedCymbals() is not { } selected)
            return;
        var places = selected.Places.Where(p => DrumCymbals.IsTom(selected.Drums, p.Pad, p.Tick) != tom).ToList();
        if (places.Count == 0)
            return;

        var command = new SetToms(places, tom, selected.Key.Difficulty);
        if (_session.Execute(command) && command.SharedWith.Count > 0)
        {
            string what = tom ? "toms" : "cymbals";
            _session.Status = $"Made {what} on every difficulty: {Names(command.SharedWith)} changed too.";
        }
    }

    /// <summary>
    /// Makes the selected drum pad notes accents, or ghosts, or normal hits when every one already is
    /// <paramref name="dynamics"/> (charter-spec §9: A and Shift+A). Kicks are left as they are.
    /// </summary>
    public void ToggleDynamics(NoteDynamics dynamics)
    {
        if (SelectedPads() is { } pads)
            SetDynamics(pads.All(n => n.Dynamics == dynamics) ? NoteDynamics.None : dynamics);
    }

    /// <summary>
    /// Makes the selected drum pad notes <paramref name="dynamics"/>, as the Properties panel sets them (charter-spec C6
    /// item 4). Kicks are left as they are.
    /// </summary>
    public void SetDynamics(NoteDynamics dynamics)
    {
        if (_session.Selection.Track is not { } key || SelectedPads() is not { } pads)
            return;
        var changing = pads.Where(n => n.Dynamics != dynamics).ToList();
        if (changing.Count > 0)
            _session.Execute(new SetDynamics(key.Instrument, key.Difficulty, changing, dynamics));
    }

    /// <summary>
    /// Makes the selected Expert kicks 2× kicks, or plain kicks when every one is a 2× kick already (charter-spec C6
    /// item 2).
    /// </summary>
    public void ToggleDoubleKick()
    {
        if (SelectedDrums() is { } selected && selected.Notes.Where(static n => n.Lane == 0).ToList() is { Count: > 0 } kicks)
            SetDoubleKick(!kicks.All(static k => k.DoubleKick));
    }

    /// <summary>
    /// Makes the selected Expert kicks 2× kicks, or plain kicks, as the Properties panel sets them (charter-spec C6
    /// item 2). The kicks stay selected.
    /// </summary>
    public void SetDoubleKick(bool on)
    {
        if (SelectedDrums() is not { } selected)
            return;
        var (key, _, notes) = selected;
        var kicks = notes.Where(static n => n.Lane == 0).ToList();
        if (kicks.Count == 0)
            return;
        if (key.Difficulty != ChartDifficulty.Expert)
        {
            _session.Status = DoubleKickOffExpert;
            return;
        }

        var changing = kicks.Where(k => k.DoubleKick != on).ToList();
        if (changing.Count > 0 && _session.Execute(new SetDoubleKick(changing, on)))
            _session.Selection.Select(key, [.. notes.Where(static n => n.Lane != 0), .. kicks.Select(k => k with { DoubleKick = on })]);
    }

    /// <summary>
    /// Places a 2× kick at the cursor of the focused Expert drums lane, or removes the one there (charter-spec §9:
    /// Shift+0). A kick placed leaves nothing selected.
    /// </summary>
    public void ToggleDoubleKickAtCursor()
    {
        if (_session.Focused is not { } key || _session.TrackOf(key) is not { Kind: ChartInstrumentKind.Drums } track)
            return;
        if (key.Difficulty != ChartDifficulty.Expert)
        {
            _session.Status = DoubleKickOffExpert;
            return;
        }

        long tick = _session.CursorTick;
        if (track.TryGetNote(tick, 0, doubleKick: true, out var note))
        {
            _session.Execute(new RemoveNotes(key.Instrument, key.Difficulty, [note]));
            return;
        }

        var placed = new EditableNote(tick, EditableNote.PlainLength, 0, DoubleKick: true);
        if (_session.Execute(new AddNotes(key.Instrument, key.Difficulty, [placed])))
            _session.Selection.Clear();
    }

    /// <summary>The selected notes, when they're drum notes: their track, the drums part and the notes.</summary>
    private (ChartTrackKey Key, EditablePart Drums, IReadOnlyList<EditableNote> Notes)? SelectedDrums()
    {
        var selection = _session.Selection;
        if (selection.Track is not { } key || Chart.PartOf(key.Instrument) is not { Kind: ChartInstrumentKind.Drums } drums)
            return null;
        var notes = selection.Notes;
        return notes.Count == 0 ? null : (key, drums, notes);
    }

    /// <summary>
    /// The places of the selected yellow, blue and green drum notes; null, with the status saying why, when none is. Inside
    /// a disco flip section (charter-spec C6 item 7), red and yellow are left out: the games play red as a yellow cymbal and
    /// yellow as red whatever the tom markers say, so a toggle there would change only other difficulties' notes.
    /// </summary>
    private (ChartTrackKey Key, EditablePart Drums, IReadOnlyList<(int Pad, long Tick)> Places)? SelectedCymbals()
    {
        if (SelectedDrums() is not { } selected)
            return null;
        var key = selected.Key;
        var places = selected.Notes
            .Where(n => CanToggleCymbal(key, n))
            .Select(static n => (n.Lane, n.Tick))
            .Distinct()
            .ToList();
        if (places.Count == 0)
        {
            _session.Status = selected.Notes.Any(n => _session.ShownLane(key, n) != n.Lane)
                ? "Inside a disco flip, red plays as a yellow cymbal and yellow as red, so they can't be made cymbals or toms."
                : "Only yellow, blue and green notes are cymbals or toms.";
            return null;
        }

        return (selected.Key, selected.Drums, places);
    }

    /// <summary>
    /// Whether <paramref name="note"/> of <paramref name="key"/>'s track can be made a cymbal or a tom (charter-spec C6
    /// item 3): a yellow, blue or green note, but not one inside a disco flip section that the games play as red (C6 item
    /// 7). <c>C</c> and Properties' Cymbal box go by this.
    /// </summary>
    public bool CanToggleCymbal(ChartTrackKey key, EditableNote note) =>
        note.Lane is >= TomMarker.FirstPad and <= TomMarker.LastPad && _session.ShownLane(key, note) == note.Lane;

    /// <summary>The selected drum notes on a pad, not the kick; null, with the status saying why, when none is.</summary>
    private List<EditableNote>? SelectedPads()
    {
        if (SelectedDrums() is not { } selected)
            return null;
        var pads = selected.Notes.Where(static n => n.Lane > 0).ToList();
        if (pads.Count == 0)
        {
            _session.Status = "A kick can't be an accent or a ghost.";
            return null;
        }

        return pads;
    }

    /// <summary>Difficulties in words, for the status: "Hard", "Hard and Easy", "Hard, Medium and Easy".</summary>
    private static string Names(IReadOnlyList<ChartDifficulty> difficulties)
    {
        var names = difficulties.OrderDescending().Select(static d => d.ToString()).ToList();
        return names.Count == 1 ? names[0] : $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}";
    }

    /// <summary>
    /// What the erase tool takes away at <paramref name="point"/>, for it and for the red it shows there first: the note
    /// whose start or tail is under it, or in the phrase rail, the phrase or disco flip section whose bar is. An open note's
    /// tail is drawn across the lanes, so passing over it leaves the note; its gem erases it. Null for nothing.
    /// </summary>
    public EraseTarget? EraseTargetAt(ChartPoint point) => EraseTargetAt(point, afresh: false);

    // With afresh, the disco flip sections are read from the track rather than as the session keeps them.
    private EraseTarget? EraseTargetAt(ChartPoint point, bool afresh)
    {
        if (_session.TrackOf(point.Track) is not { } track)
            return null;
        if (point.Rail is null)
            return HitTest(point) is { } hit && !(hit.IsTail && hit.Note.Lane == ChartNote.OpenLane) ? new EraseTarget(Note: hit.Note) : null;

        if (point.Rail is { IsDiscoFlip: true })
        {
            var flip = afresh
                ? PhraseRail.HitTestDiscoFlip(track, point)
                : PhraseRail.HitTestDiscoFlip(point.Track.Difficulty, _session.DiscoFlipRangesOf(point.Track), point);
            return flip is { } bar ? new EraseTarget(DiscoFlip: bar.Section) : null;
        }

        return Chart.PartOf(point.Track.Instrument) is { } part && PhraseRail.HitTest(part, point) is { } phrase
            ? new EraseTarget(Phrase: phrase.Phrase)
            : null;
    }

    /// <summary>
    /// The note whose start or tail is under <paramref name="point"/>, the start first, or null for none. In a shared
    /// column (GHL's), a note in the half the pointer is in comes first, then the other half's, whose gem fills the column
    /// when it's alone. A note is in the column it's drawn in: inside a disco flip section, the pad the player hits
    /// (charter-spec C6 item 7, <see cref="EditorSession.ShownLane(ChartTrackKey, EditableNote)"/>).
    /// </summary>
    public (EditableNote Note, bool IsTail)? HitTest(ChartPoint point) =>
        _session.TrackOf(point.Track) is { } track ? HitTest(track, point, n => _session.ShownLane(point.Track, n)) : null;

    private static (EditableNote Note, bool IsTail)? HitTest(EditableTrack track, ChartPoint point, Func<EditableNote, int> shownLane)
    {
        long from = (long)Math.Floor(point.Tick - point.Tolerance);
        long to = (long)Math.Ceiling(point.Tick + point.Tolerance);
        int? partner = point.Lane is { } pointed ? point.Slots.PartnerOf(pointed) : null;
        int? across = point.Slots.Across;
        EditableNote? best = null, bestPartner = null, bestAcross = null;
        double bestDistance = double.MaxValue, bestPartnerDistance = double.MaxValue, bestAcrossDistance = double.MaxValue;
        foreach (var note in track.Notes.Between(Math.Max(0, from), to))
        {
            double distance = Math.Abs(note.Tick - point.Tick);
            int drawn = shownLane(note);
            if (drawn == point.Lane && distance < bestDistance)
                (best, bestDistance) = (note, distance);
            else if (drawn == partner && distance < bestPartnerDistance)
                (bestPartner, bestPartnerDistance) = (note, distance);
            else if (drawn == across && distance < bestAcrossDistance)
                (bestAcross, bestAcrossDistance) = (note, distance);
        }

        if ((best ?? bestPartner ?? bestAcross) is { } head)
            return (head, false);

        // A tail: the latest note in the lane (or its column) held past the pointer; failing that, the latest drawn across
        // the lanes, whose tail is too.
        if (point.Lane is not { } lane)
            return null;
        var notes = track.Notes;
        EditableNote? acrossTail = null;
        for (int i = notes.FirstAfter((long)Math.Floor(point.Tick)) - 1; i >= 0; i--)
        {
            var note = notes[i];
            if (note.Length <= EditableNote.PlainLength || note.Tick + note.Length < point.Tick)
                continue;
            int shown = shownLane(note);
            if (shown == lane || shown == partner)
                return (note, true);
            if (shown == across && across != lane)
                acrossTail ??= note;
        }

        return acrossTail is { } held ? (held, true) : null;
    }

    private Gesture? PressSelect(ChartPoint point, bool shift)
    {
        var selection = _session.Selection;
        if (HitTest(point) is { } hit)
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

    private Gesture? PressPlace(ChartPoint point, bool alt)
    {
        var track = _session.TrackOf(point.Track)!;
        if (PlacedLane(track.Kind, point.Lane, point.Slots, alt) is not { } shown)
            return null;
        long tick = SnapTick(point.Tick, point.Track);

        // The pad drawn in the column clicked: inside a disco flip section, yellow's column places red (C6 item 7).
        int placed = _session.StoredLane(point.Track, tick, shown);
        var note = PlacedNote(point.Track, track.Kind, tick, placed);
        if (TryGetPlaced(track, note, out var there))
        {
            if (!_session.Selection.Contains(point.Track, there))
                _session.Selection.Select(point.Track, [there]);
            return new MoveGesture(this, point, there);
        }

        var gesture = new PlaceGesture(this, point.Track, note, point.Tick);
        return gesture.Start() ? gesture : null;
    }

    /// <summary>
    /// The lane the place tool puts a note in with the pointer in <paramref name="lane"/>: the lane apart from the columns,
    /// an open note or the kick, with <paramref name="alt"/> held, or on fret parts the other way about while
    /// <see cref="PlaceOptions.Open"/> is picked. Null outside the lanes, unless Alt is held.
    /// </summary>
    public int? PlacedLane(ChartInstrumentKind kind, int? lane, LaneSlots slots, bool alt)
    {
        bool apart = kind != ChartInstrumentKind.Drums && _session.Placing.Open ? !alt : alt;
        return apart && (alt || lane is not null) ? ApartLane(slots) : lane;
    }

    /// <summary>
    /// The note the place tool puts in <paramref name="lane"/> at <paramref name="tick"/> of <paramref name="key"/>'s track,
    /// as <see cref="EditorSession.Placing"/> has it: on drums, a pad an accent or a ghost, and on Expert a 2× kick.
    /// </summary>
    public EditableNote PlacedNote(ChartTrackKey key, ChartInstrumentKind kind, long tick, int lane)
    {
        var placing = _session.Placing;
        bool drums = kind == ChartInstrumentKind.Drums;
        return new EditableNote(
            tick,
            EditableNote.PlainLength,
            lane,
            drums && lane > 0 ? placing.Dynamics : NoteDynamics.None,
            drums && lane == 0 && placing.DoubleKick && key.Difficulty == ChartDifficulty.Expert);
    }

    /// <summary>
    /// The note already where the place tool would put <paramref name="note"/>, which a click there selects rather than
    /// places over: the note in its lane at its tick, and on the kick's lane, a kick of either kind, as a kick and a 2×
    /// kick are one pad.
    /// </summary>
    public static bool TryGetPlaced(EditableTrack track, EditableNote note, out EditableNote there) =>
        track.TryGetNote(note.Tick, note.Lane, note.DoubleKick, out there)
        || (note.Lane == 0 && track.Kind == ChartInstrumentKind.Drums && track.TryGetNote(note.Tick, 0, !note.DoubleKick, out there));

    /// <summary>
    /// The command that places <paramref name="notes"/> on <paramref name="key"/>'s track as <see cref="EditorSession.Placing"/>
    /// has it: with the chords they're in made the type picked, and on Pro drums the yellow, blue and green pads made toms
    /// when that's picked, as <paramref name="toms"/>.
    /// </summary>
    private IChartCommand PlacementOf(ChartTrackKey key, IReadOnlyList<EditableNote> notes, out SetToms? toms)
    {
        toms = null;
        var placing = _session.Placing;
        var commands = new List<IChartCommand> { new AddNotes(key.Instrument, key.Difficulty, notes) };
        if (ChartInstruments.KindOf(key.Instrument) == ChartInstrumentKind.Drums)
        {
            var pads = placing.Tom && Chart.DrumsVariant == DrumsVariant.Pro
                ? notes.Where(n => CanToggleCymbal(key, n)).Select(static n => (n.Lane, n.Tick)).Distinct().ToList()
                : [];
            if (pads.Count > 0)
                commands.Add(toms = new SetToms(pads, tom: true, key.Difficulty));
        }
        else if (placing.ChordType != ChordFlags.None)
        {
            // Each set whatever the chord had, so the preview before this one, still in the chart, can't hide a change.
            var ticks = notes.Select(static n => n.Tick).Distinct().ToList();
            const ChordFlags types = ChordFlags.ForceHopo | ChordFlags.ForceStrum | ChordFlags.Tap;
            commands.Add(new SetChordFlags(key.Instrument, key.Difficulty, ticks, types & ~placing.ChordType, on: false));
            commands.Add(new SetChordFlags(key.Instrument, key.Difficulty, ticks, placing.ChordType, on: true));
        }

        return commands.Count == 1 ? commands[0] : new CompositeCommand(commands);
    }

    private SustainGesture SustainOf(ChartPoint point, EditableNote grabbed)
    {
        var selection = _session.Selection;
        var notes = selection.Contains(point.Track, grabbed) ? selection.Notes : [grabbed];
        return new SustainGesture(this, point.Track, notes);
    }

    private Gesture? PressTempo(ChartPoint point) => PressTempo(point.Tick, point.Tolerance);

    /// <summary>The tick of the tempo marker nearest <paramref name="pressed"/>, within <paramref name="tolerance"/> ticks of it; null for none.</summary>
    private long? TempoMarkerNear(double pressed, double tolerance)
    {
        long from = (long)Math.Floor(pressed - tolerance), to = (long)Math.Ceiling(pressed + tolerance);
        var near = Chart.TempoMap.Tempos.Between(Math.Max(0, from), to);
        if (near.IsEmpty)
            return null;
        long tick = near[0].Tick;
        double best = Math.Abs(tick - pressed);
        foreach (var marker in near)
        {
            if (Math.Abs(marker.Tick - pressed) < best)
                (tick, best) = (marker.Tick, Math.Abs(marker.Tick - pressed));
        }

        return tick;
    }

    /// <summary>
    /// Removes the tempo marker within the tolerance of <paramref name="pressed"/>, as the middle button does with the
    /// Tempo tool, as one step: the marker nearest it, if any. A refusal, such as for the song's first marker, is said in
    /// the status.
    /// </summary>
    private void RemoveTempoNear(double pressed, double tolerance)
    {
        if (TempoMarkerNear(pressed, tolerance) is not { } tick)
            return;
        var edit = TempoMapEdits.RemoveMarker(Chart, tick, _session.Delay, _session.KeepTimes);
        if (edit.IsRefused)
            _session.Status = edit.Refusal;
        else
            _session.Execute(edit.Command!);
    }

    // The Tempo tool's press at a tick, in any lane: on a tempo or time signature change within the tolerance, a drag of
    // it; elsewhere, a marker added on the grid.
    private Gesture? PressTempo(double pressed, double tolerance)
    {
        var map = Chart.TempoMap;
        if (ChangeNear(pressed, tolerance) is { } tick)
        {
            bool hasTempo = !map.Tempos.At(tick).IsEmpty;
            bool hasSignature = !map.TimeSignatures.At(tick).IsEmpty;
            if (hasTempo)
                _session.Selection.SelectTempos([tick]);
            else
                _session.Selection.SelectTempos([]);
            return new TempoDragGesture(this, tick, anchored: hasTempo && !map.Anchors.At(tick).IsEmpty, movesSignature: hasSignature);
        }

        long at = Snap.Nearest(pressed, Map);
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

    /// <summary>
    /// A press in the phrase rail, with any tool: on a proposed phrase's ghost, while Generate fills or Generate star power
    /// runs, it keeps or drops the phrase (charter-spec C6 item 5, C8 item 1), before any bar under it. On a phrase's bar it
    /// selects the phrase (Shift adds it or takes it away), and a drag moves it, or resizes it by the end grabbed; the same
    /// on a disco flip section's bar. Elsewhere in the rail, the Select, Place and Phrase tools draw a phrase of the row's
    /// kind, a click that draws nothing clearing the selection, and the others select no phrases or sections; Shift lends
    /// the select tool's press there too.
    /// </summary>
    private Gesture? PressRail(ChartPoint point, bool shift)
    {
        if (_session.Proposals is { } run && run.HitTest(point) is { } ghost)
        {
            run.Toggle(ghost);
            return null;
        }

        var part = Chart.PartOf(point.Track.Instrument)!;
        var selection = _session.Selection;
        if (PhraseRail.HitTestDiscoFlip(point.Track.Difficulty, _session.DiscoFlipRangesOf(point.Track), point) is { } flip)
            return PressDiscoFlip(point, flip, shift);
        if (PhraseRail.HitTest(part, point) is not { } hit)
        {
            if (!shift && PhraseDrawAt(point) is { } draw)
            {
                if (DrawsIn(point.Track, draw.Kind))
                    return PressDraw(point.Track, draw, fromRail: true);

                // The Phrase tool says why a row it can't draw in is refused; the others leave the rail as a click does.
                if (_session.Tool == EditorTool.Phrase)
                    return PressDraw(point.Track, draw, fromRail: true);
            }

            if (!shift)
            {
                selection.ClearPhrases();
                selection.ClearDiscoFlips();
            }

            return null;
        }

        if (shift)
            selection.Toggle(part.Instrument, hit.Phrase);
        else if (!selection.Contains(part.Instrument, hit.Phrase))
            selection.SelectPhrases(part.Instrument, [hit.Phrase]);
        if (!selection.Contains(part.Instrument, hit.Phrase))
            return null;
        return hit.Grip == PhraseGrip.Body
            ? new PhraseMoveGesture(this, point, hit.Phrase)
            : new PhraseResizeGesture(this, point, hit.Phrase, hit.Grip == PhraseGrip.Start);
    }

    /// <summary>
    /// A press on a disco flip section's bar (charter-spec C6 item 7), as on a phrase's: it selects the section (Shift adds
    /// it or takes it away), and a drag moves the selected sections, or resizes this one by the end grabbed.
    /// </summary>
    private Gesture? PressDiscoFlip(ChartPoint point, DiscoFlipHit hit, bool shift)
    {
        var selection = _session.Selection;
        if (shift)
            selection.Toggle(hit.Section);
        else if (!selection.Contains(hit.Section))
            selection.SelectDiscoFlips([hit.Section]);
        if (!selection.Contains(hit.Section))
            return null;
        return hit.Grip == PhraseGrip.Body
            ? new DiscoFlipMoveGesture(this, point, hit.Section)
            : new DiscoFlipResizeGesture(this, point, hit.Section, hit.Grip == PhraseGrip.Start);
    }

    /// <summary>
    /// What a press at <paramref name="point"/> would draw: the grid step nearest it and the kind. In the lanes the Phrase
    /// tool draws the kind picked; in an empty part of the rail the Select, Place and Phrase tools draw the row's kind. Null
    /// where there's nothing to draw: with another tool, outside the lanes and the rail, or where a press does something
    /// else, such as on a phrase's bar, a disco flip section's or a proposed phrase's ghost, as <see cref="PressRail"/>
    /// tells them apart.
    /// </summary>
    public PhraseDraw? PhraseDrawAt(ChartPoint point)
    {
        if (Chart.PartOf(point.Track.Instrument) is not { } part)
            return null;
        if (point.Rail is null)
        {
            if (_session.Tool != EditorTool.Phrase || point.Lane is null)
                return null;
            return new PhraseDraw(Snap.Nearest(point.Tick, Map), _session.PhraseKind);
        }

        if (_session.Tool is not (EditorTool.Select or EditorTool.Place or EditorTool.Phrase))
            return null;
        if (_session.Proposals?.HitTest(point) is not null
            || PhraseRail.HitTestDiscoFlip(point.Track.Difficulty, _session.DiscoFlipRangesOf(point.Track), point) is not null
            || PhraseRail.HitTest(part, point) is not null)
        {
            return null;
        }

        return new PhraseDraw(Snap.Nearest(point.Tick, Map), point.Rail.Value);
    }

    /// <summary>
    /// Where a press at <paramref name="point"/> starts drawing a phrase or disco flip section, in the lanes or in an empty
    /// part of the rail (<see cref="PhraseDrawAt"/>), or null where a press does something else.
    /// </summary>
    public long? PhraseDrawStart(ChartPoint point) => PhraseDrawAt(point)?.Tick;

    /// <summary>
    /// A press where it draws, as <see cref="PhraseDrawAt"/> gives it: a phrase of the kind drawn, or a disco flip section
    /// on the lane's difficulty, which only Pro drums have. <paramref name="fromRail"/> is whether the press was in the
    /// rail, where a click that draws nothing clears the selection there.
    /// </summary>
    private Gesture? PressDraw(ChartTrackKey track, PhraseDraw draw, bool fromRail = false)
    {
        if (draw.Kind.Phrase is { } kind)
            return new PhraseDrawGesture(this, track, draw.Tick, kind, fromRail);
        if (!DrawsIn(track, draw.Kind))
        {
            _session.Status = DiscoFlipOffPro;
            return null;
        }

        return new DiscoFlipDrawGesture(this, track, draw.Tick, fromRail);
    }

    /// <summary>Whether <paramref name="kind"/> can be drawn in <paramref name="track"/>'s lanes: a disco flip section only on Pro drums.</summary>
    public bool DrawsIn(ChartTrackKey track, RailKind kind) =>
        kind.Phrase is not null
        || (ChartInstruments.KindOf(track.Instrument) == ChartInstrumentKind.Drums && _session.IsProDrums);

    /// <summary>The lanes <paramref name="notes"/> of <paramref name="key"/>'s track are drawn in (<see cref="EditorSession.ShownLane(ChartTrackKey, EditableNote)"/>).</summary>
    private List<int> ShownLanes(ChartTrackKey key, IReadOnlyList<EditableNote> notes) => [.. notes.Select(n => _session.ShownLane(key, n))];

    /// <summary>
    /// The command that moves <paramref name="notes"/> of <paramref name="key"/>'s track <paramref name="tickDelta"/> ticks
    /// and <paramref name="laneDelta"/> columns across, as they're drawn, and where they land. A note moved only in time
    /// keeps its pad, so it may be drawn in another column once it's in or out of a disco flip section (charter-spec C6
    /// item 7). One moved across goes to the pad drawn in the column it's taken to, at the tick it lands on: inside a
    /// section, red and yellow swap both ways. Where that isn't one lane delta for every note, the move is made as their
    /// removal and their adding again, in one step.
    /// </summary>
    private (IChartCommand Command, IReadOnlyList<EditableNote> Moved) MoveOf(ChartTrackKey key, IReadOnlyList<EditableNote> notes, long tickDelta, int laneDelta)
    {
        var moved = notes
            .Select(n => n with
            {
                Tick = n.Tick + tickDelta,
                Lane = laneDelta == 0 ? n.Lane : _session.StoredLane(key, n.Tick + tickDelta, _session.ShownLane(key, n) + laneDelta),
            })
            .ToList();
        int stored = moved[0].Lane - notes[0].Lane;
        if (Enumerable.Range(0, notes.Count).All(i => moved[i].Lane - notes[i].Lane == stored))
            return (new MoveNotes(key.Instrument, key.Difficulty, notes, tickDelta, stored), moved);
        return (new CompositeCommand([new RemoveNotes(key.Instrument, key.Difficulty, notes), new AddNotes(key.Instrument, key.Difficulty, moved)]), moved);
    }

    /// <summary>How many columns notes drawn in <paramref name="lanes"/> can move by, towards <paramref name="wanted"/>: none when an open note or a kick is among them, and never out of the lanes.</summary>
    private static int LaneDelta(IReadOnlyList<int> lanes, int wanted, LaneSlots slots)
    {
        int apart = ApartLane(slots);
        if (wanted == 0 || lanes.Any(l => l == apart || slots.IndexOf(l) < 0))
            return 0;
        int movable = slots.Across is null ? slots.Slots.Count - 1 : slots.Slots.Count;
        int lowest = lanes.Min(slots.IndexOf);
        int highest = lanes.Max(slots.IndexOf);

        // The lanes are numbered in their column order, so a move across columns is a move across lane numbers. A
        // column's shared lanes are numbered in the same order (GHL's black frets), so they move with them.
        return Math.Clamp(wanted, -lowest, movable - 1 - highest);
    }

    /// <summary>
    /// How many lanes <paramref name="notes"/> move by between the halves of shared columns (GHL's white and black frets),
    /// as the pointer has gone from <paramref name="from"/>'s half to <paramref name="to"/>'s: none when it hasn't, when
    /// either isn't a shared column's (an open note's row), or when the notes aren't all in the half it left.
    /// </summary>
    private static int RowDelta(IReadOnlyList<EditableNote> notes, int? from, int? to, LaneSlots slots)
    {
        if (from is not { } a || to is not { } b || slots.PartnerOf(a) is null || slots.PartnerOf(b) is null || slots.IsShared(a) == slots.IsShared(b))
            return 0;
        bool toShared = slots.IsShared(b);
        int? delta = null;
        foreach (var note in notes)
        {
            if (slots.IsShared(note.Lane) == toShared || slots.PartnerOf(note.Lane) is not { } partner)
                return 0;
            if (delta is { } d && d != partner - note.Lane)
                return 0;
            delta = partner - note.Lane;
        }

        return delta ?? 0;
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

    /// <summary>A gesture of one tool, from a press to its release, in a highway's lane or a vocal part's.</summary>
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

        /// <summary>Moves on to <paramref name="point"/> in a vocal part's lane; a highway's gesture ignores it.</summary>
        public virtual void Drag(VocalPoint point)
        {
        }

        public virtual void Release(VocalPoint point)
        {
            Drag(point);
            Session.CommitPreview();
        }

        public virtual void Cancel()
        {
        }
    }

    /// <summary>
    /// Drags selected notes in time (to the grid) and across the columns as they're drawn (<see cref="MoveOf"/>). The
    /// pointer's columns are the ones drawn, so its column delta is worked out on the notes' drawn lanes too.
    /// </summary>
    private sealed class MoveGesture : Gesture
    {
        private readonly ChartTrackKey _track;
        private readonly ChartPoint _pressed;
        private readonly EditableNote _grabbed;
        private readonly IReadOnlyList<EditableNote> _notes;
        private readonly List<int> _shown;
        private readonly IReadOnlyCollection<NotePlace> _selected;

        // The move previewed, and the notes where it put them; null with none.
        private (long Ticks, int Lanes)? _delta;
        private IReadOnlyList<EditableNote>? _moved;

        public MoveGesture(EditorTools tools, ChartPoint pressed, EditableNote grabbed)
            : base(tools)
        {
            _track = pressed.Track;
            _pressed = pressed;
            _grabbed = grabbed;
            _notes = Session.Selection.Notes;
            _shown = Tools.ShownLanes(_track, _notes);
            _selected = [.. Session.Selection.Places];
        }

        public override void Drag(ChartPoint point)
        {
            // The note grabbed goes to the grid or an onset; the others keep their places around it.
            long tick = Tools.SnapTick(_grabbed.Tick + (point.Tick - _pressed.Tick), _track);
            long tickDelta = Math.Max(tick - _grabbed.Tick, -_notes.Min(static n => n.Tick));
            int apart = ApartLane(point.Slots);
            int wanted = point.Lane is { } lane && _pressed.Lane is { } from && lane != apart && from != apart
                ? point.Slots.IndexOf(lane) - point.Slots.IndexOf(from)
                : 0;
            int laneDelta = LaneDelta(_shown, wanted, point.Slots) + RowDelta(_notes, _pressed.Lane, point.Lane, point.Slots);
            if (_delta == (tickDelta, laneDelta))
                return;

            if (tickDelta == 0 && laneDelta == 0)
            {
                Session.Preview(null);
                (_delta, _moved) = (null, null);
                Session.Selection.SelectPlaces(_track, _selected);
                return;
            }

            var (command, moved) = Tools.MoveOf(_track, _notes, tickDelta, laneDelta);
            if (Session.Preview(command))
            {
                (_delta, _moved) = ((tickDelta, laneDelta), moved);
                Session.Selection.Select(_track, moved);
            }
        }

        public override void Release(ChartPoint point)
        {
            Drag(point);
            if (Session.CommitPreview() is not null && _moved is { } moved)
                Session.Selection.Select(_track, moved);
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
            Session.Preview(SustainsOf(track, notes, n => Tools.LengthTo(n, end)));
        }
    }

    /// <summary>
    /// Lengthens or shortens the sustains of notes as a button drags: each by the ticks the pointer has moved on the grid,
    /// so they keep their own lengths, or each to end where the pointer is, as <see cref="SustainGesture"/> does.
    /// </summary>
    /// <remarks>
    /// Or it lays a run of each note instead, a copy on every grid step: as many steps on (or back) from each note as the
    /// pointer has moved, or up to where the pointer is, so the runs end together. With <paramref name="laysRun"/> given, the
    /// left button's gesture (<see cref="PressSelected"/>), it keeps to that, by the steps moved. Without, the right
    /// button's (<see cref="PressContext"/>), the keys held decide as it goes: none for the sustains by the steps moved,
    /// Ctrl for them ending at the pointer, Alt for runs up to it.
    /// </remarks>
    private sealed class NoteLengthGesture(EditorTools tools, ChartTrackKey track, IReadOnlyList<EditableNote> notes, double pressed, bool? laysRun) : Gesture(tools)
    {
        private (long At, bool Match, bool Run)? _last;
        private AddNotes? _run;

        public override void Drag(ChartPoint point)
        {
            long at = Tools.Snap.Nearest(point.Tick, Tools.Map);
            bool alt = Tools._modifiers.HasFlag(EditorModifiers.Alt);
            bool match = laysRun is null && (alt || Tools._modifiers.HasFlag(EditorModifiers.Control));
            bool run = laysRun ?? alt;
            if (_last == (at, match, run))
                return;
            _last = (at, match, run);

            // The run previewed last is in the chart until another preview replaces it, so its copies don't count as there.
            var previewed = _run?.Notes.ToHashSet() ?? [];
            _run = null;

            long from = Tools.Snap.Nearest(pressed, Tools.Map);
            if (!match && at == from)
            {
                Session.Preview(null);
                return;
            }

            if (run)
            {
                var runs = Tools.RunOf(track, notes, match ? null : Tools.StepsBetween(from, at), at, previewed);
                _run = Session.Preview(runs) ? runs : null;
                return;
            }

            if (Session.TrackOf(track) is { Kind: ChartInstrumentKind.Drums })
            {
                Session.Preview(null);
                Session.Status = NoDrumSustain;
                return;
            }

            long delta = at - from;
            Session.Preview(SustainsOf(track, notes, n => match ? Tools.LengthTo(n, at) : Tools.LengthTo(n, SustainEnd(n) + delta)));
        }

        public override void Release(ChartPoint point)
        {
            Drag(point);
            if (Session.CommitPreview() is AddNotes added)
                Session.Selection.Select(track, [.. notes, .. added.Notes]);
        }
    }

    /// <summary>
    /// The command that lays a run of each of <paramref name="notes"/>: a copy, without its sustain, on each grid step on
    /// from it, or back for negative <paramref name="steps"/>; or with no steps given, on each grid step from it up to
    /// <paramref name="end"/>. Copies where the lane has a note already, but one of <paramref name="previewed"/>, are left
    /// out; null when none is left.
    /// </summary>
    private AddNotes? RunOf(ChartTrackKey key, IReadOnlyList<EditableNote> notes, int? steps, long end, IReadOnlySet<EditableNote> previewed)
    {
        if (_session.TrackOf(key) is not { } track)
            return null;
        var copies = new List<EditableNote>();
        var places = new HashSet<(long Tick, int Lane, bool DoubleKick)>();
        foreach (var note in notes)
        {
            int direction = Math.Sign(steps ?? end - note.Tick);
            long tick = note.Tick;
            for (int i = 0; steps is not { } count || i < Math.Abs(count); i++)
            {
                long next = Snap.Step(tick, direction, Map);
                if (next == tick || (steps is null && (direction > 0 ? next > end : next < end)))
                    break;
                tick = next;
                var copy = note with { Tick = tick, Length = EditableNote.PlainLength };
                if (places.Add((tick, copy.Lane, copy.DoubleKick))
                    && (!track.TryGetNote(tick, copy.Lane, copy.DoubleKick, out var there) || previewed.Contains(there)))
                {
                    copies.Add(copy);
                }
            }
        }

        return copies.Count == 0 ? null : new AddNotes(key.Instrument, key.Difficulty, copies);
    }

    /// <summary>How many grid steps on from <paramref name="from"/> <paramref name="to"/> is, back being negative; a part step doesn't count.</summary>
    private int StepsBetween(long from, long to)
    {
        int direction = Math.Sign(to - from), steps = 0;
        for (long tick = from; ;)
        {
            long next = Snap.Step(tick, direction, Map);
            if (direction == 0 || next == tick || (direction > 0 ? next > to : next < to))
                return steps * direction;
            tick = next;
            steps++;
        }
    }

    /// <summary>The command that gives each of <paramref name="notes"/> the length <paramref name="length"/> has for it; null when none changes.</summary>
    private static SetSustain? SustainsOf(ChartTrackKey track, IEnumerable<EditableNote> notes, Func<EditableNote, long> length)
    {
        var lengths = notes
            .Select(n => (Note: n, Length: length(n)))
            .Where(static p => p.Length != p.Note.Length)
            .ToList();
        return lengths.Count == 0 ? null : new SetSustain(track.Instrument, track.Difficulty, lengths);
    }

    /// <summary>
    /// The length that ends <paramref name="note"/> at <paramref name="end"/>: none, at or before its tick, or when it would
    /// be shorter than the sustain cutoff, as the games would read it as a plain note anyway.
    /// </summary>
    private long LengthTo(EditableNote note, long end)
    {
        long length = end - note.Tick;
        return length < SustainCutoff.Of(_session.Chart.Resolution, _session.ReadOptions.SustainCutoff) ? EditableNote.PlainLength : length;
    }

    /// <summary>Where <paramref name="note"/>'s sustain ends: its own tick when it has none.</summary>
    private static long SustainEnd(EditableNote note) => note.Length <= EditableNote.PlainLength ? note.Tick : note.Tick + note.Length;

    /// <summary>
    /// Places a note, and as the pointer drags, a run of it on every grid step up to the pointer, on or back, leaving out
    /// the places with a note already; with Ctrl, its sustain instead, which drums don't have, or the other way about
    /// while <see cref="PlaceOptions.DragSustains"/> is picked. Each chord placed in is made as <see cref="EditorSession.Placing"/>
    /// has it. It leaves nothing selected.
    /// </summary>
    private sealed class PlaceGesture(EditorTools tools, ChartTrackKey track, EditableNote note, double pressed) : Gesture(tools)
    {
        private (long End, bool Sustain)? _last;
        private IReadOnlyList<EditableNote> _placed = [];
        private SetToms? _toms;

        public bool Start() => Preview([note]);

        public override void Drag(ChartPoint point)
        {
            // Back on the grid step pressed, the note alone: the note itself may have gone to an onset off the grid.
            long end = Tools.Snap.Nearest(point.Tick, Tools.Map);
            bool fret = Session.TrackOf(track) is not { Kind: ChartInstrumentKind.Drums };
            bool sustain = Tools._modifiers.HasFlag(EditorModifiers.Control) != (fret && Session.Placing.DragSustains);
            if (_last == (end, sustain))
                return;
            _last = (end, sustain);
            if (end == Tools.Snap.Nearest(pressed, Tools.Map))
            {
                Preview([note]);
                return;
            }

            if (sustain)
            {
                if (!fret)
                {
                    Preview([note]);
                    Session.Status = NoDrumSustain;
                    return;
                }

                Preview([note with { Length = Tools.LengthTo(note, end) }]);
                return;
            }

            // The run previewed last is in the chart until this one replaces it, so its notes don't count as there.
            var run = Tools.RunOf(track, [note], steps: null, end, _placed.ToHashSet());
            Preview([note, .. run?.Notes ?? []]);
        }

        public override void Release(ChartPoint point)
        {
            Drag(point);
            if (Session.CommitPreview() is null)
                return;
            Session.Selection.Clear();
            if (_toms is { SharedWith.Count: > 0 } toms)
                Session.Status = $"Made toms on every difficulty: {Names(toms.SharedWith)} changed too.";
        }

        private bool Preview(IReadOnlyList<EditableNote> notes)
        {
            var placed = Tools.PlacementOf(track, notes, out var toms);
            if (!Session.Preview(placed))
                return false;
            (_placed, _toms) = (notes, toms);
            return true;
        }
    }

    /// <summary>Removes each note the pointer passes over, and in the phrase rail each phrase or disco flip section.</summary>
    private sealed class EraseGesture : Gesture
    {
        private readonly ChartTrackKey _track;
        private readonly List<EditableNote> _erased = [];
        private readonly List<Phrase> _phrases = [];
        private readonly List<DiscoFlipRange> _discoFlips = [];

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

            // What's erased so far is out of the chart, as the preview, so the hit is among what's left; the sections are
            // read afresh, not as the session keeps them, so one this erase previews out is gone at once.
            switch (Tools.EraseTargetAt(point, afresh: true))
            {
                case { Note: { } note }:
                    _erased.Add(note);
                    break;
                case { Phrase: { } phrase }:
                    _phrases.Add(phrase);
                    break;
                case { DiscoFlip: { } flip }:
                    _discoFlips.Add(flip.Range);
                    break;
                default:
                    return;
            }

            var commands = new List<IChartCommand>();
            if (_erased.Count > 0)
                commands.Add(new RemoveNotes(_track.Instrument, _track.Difficulty, _erased));
            commands.AddRange(_phrases.Select(p => new RemovePhrase(_track.Instrument, p)));
            commands.AddRange(_discoFlips.Select(r => new RemoveDiscoFlip(_track.Difficulty, r)));
            Session.Preview(commands.Count == 1 ? commands[0] : new CompositeCommand(commands));
        }
    }

    /// <summary>
    /// Drags the tempo or time signature change at a tick: a time signature alone or beside a tempo marker, in ticks; a
    /// tempo marker alone in ticks when it isn't anchored, and in time when it is (charter-spec C4 item 2).
    /// </summary>
    private sealed class TempoDragGesture(EditorTools tools, long tick, bool anchored, bool movesSignature) : Gesture(tools)
    {
        private double _last = double.NaN;

        public override void Drag(ChartPoint point) => DragTo(point.Tick);

        public override void Drag(VocalPoint point) => DragTo(point.Tick);

        public override void Release(ChartPoint point) => ReleaseAt(point.Tick);

        public override void Release(VocalPoint point) => ReleaseAt(point.Tick);

        private void DragTo(double pointer)
        {
            // Where the pointer is, in audio seconds for an anchored marker, or in ticks: as the chart is with the
            // preview, which the pointer was mapped through.
            double target = anchored ? Session.AudioAt(pointer) : Tools.Snap.Nearest(pointer, Tools.Map);
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
                edit = movesSignature
                    ? TempoMapEdits.MoveChange(Session.Chart, tick, to, Session.Delay, Session.KeepTimes)
                    : TempoMapEdits.MoveMarker(Session.Chart, tick, to, Session.Delay, Session.KeepTimes);
            }

            if (edit.IsRefused)
                Session.Status = edit.Refusal;
            else
                Session.Preview(edit.Command);
        }

        private void ReleaseAt(double pointer)
        {
            DragTo(pointer);
            var command = Session.CommitPreview();
            if (command is ChangeTempoMap && !anchored && (long)Tools.Snap.Nearest(pointer, Tools.Map) is var to
                && !Session.Chart.TempoMap.Tempos.At(to).IsEmpty)
            {
                Session.Selection.SelectTempos([to]);
            }
        }
    }

    /// <summary>
    /// Draws out a phrase of one kind along time, from the grid step pressed to the one the pointer is at, either way
    /// (charter-spec C8). New rolls and trills reach Expert only (C6 item 6). A click without a drag adds nothing, and
    /// selects no phrases.
    /// </summary>
    private sealed class PhraseDrawGesture : Gesture
    {
        private readonly ChartInstrument _part;
        private readonly PhraseKind _kind;
        private readonly long _from;

        // Whether the press was in the rail, where a click clears the disco flip sections selected as well.
        private readonly bool _fromRail;
        private Phrase? _tried;

        public PhraseDrawGesture(EditorTools tools, ChartTrackKey track, long from, PhraseKind kind, bool fromRail)
            : base(tools)
        {
            _part = track.Instrument;
            _kind = kind;
            _from = from;
            _fromRail = fromRail;
        }

        public override void Drag(ChartPoint point)
        {
            if (point.Track.Instrument != _part)
                return;
            long to = Tools.Snap.Nearest(point.Tick, Tools.Map);
            Phrase? phrase = to == _from
                ? null
                : Tools.Classified(_part, new Phrase(_kind, Math.Min(_from, to), Math.Abs(to - _from), Phrase.HasLowestDifficulty(_kind) ? ChartDifficulty.Expert : null));
            if (phrase == _tried)
                return;
            _tried = phrase;

            // A refusal, said in the status, leaves nothing to add, rather than the phrase drawn before it.
            if (!Session.Preview(phrase is { } added ? new AddPhrase(_part, added) : null))
                Session.Preview(null);
        }

        public override void Release(ChartPoint point)
        {
            Drag(point);
            if (Session.CommitPreview() is AddPhrase added)
                Session.Selection.SelectPhrases(_part, [added.Phrase]);
            else if (_tried is null)
            {
                Session.Selection.ClearPhrases();
                if (_fromRail)
                    Session.Selection.ClearDiscoFlips();
            }
        }
    }

    /// <summary>
    /// Drags the selected phrases in time, by the bar grabbed, to the grid, each keeping its length. Nothing moves until
    /// the pointer has, so a click only selects, and leaves a phrase off the grid where it is.
    /// </summary>
    private sealed class PhraseMoveGesture : Gesture
    {
        private readonly ChartInstrument _part;
        private readonly ChartPoint _pressed;
        private readonly Phrase _grabbed;
        private readonly IReadOnlyList<Phrase> _phrases;
        // The move last tried, so one refused isn't tried, and said, again as the pointer moves on within a grid step.
        private long _delta;
        private bool _moved;

        public PhraseMoveGesture(EditorTools tools, ChartPoint pressed, Phrase grabbed)
            : base(tools)
        {
            _part = pressed.Track.Instrument;
            _pressed = pressed;
            _grabbed = grabbed;
            _phrases = Session.Selection.Phrases;
        }

        public override void Drag(ChartPoint point)
        {
            _moved |= Math.Abs(point.Tick - _pressed.Tick) > _pressed.Tolerance;
            if (!_moved)
                return;
            long tick = Tools.Snap.Nearest(_grabbed.Tick + (point.Tick - _pressed.Tick), Tools.Map);
            long delta = Math.Max(tick - _grabbed.Tick, -_phrases.Min(static p => p.Tick));
            if (delta == _delta)
                return;

            if (delta == 0)
            {
                _delta = 0;
                Session.Preview(null);
                Session.Selection.ReplacePhrases(_part, _phrases);
                return;
            }

            // A refusal, said in the status, leaves the phrases where they were rather than where the last drag put them.
            var moved = _phrases.Select(p => Tools.Classified(_part, p with { Tick = p.Tick + delta })).ToList();
            bool previewed = Session.Preview(ChangeOf(_part, _phrases, moved));
            if (!previewed)
                Session.Preview(null);
            _delta = delta;
            Session.Selection.ReplacePhrases(_part, previewed ? moved : _phrases);
        }

        public override void Cancel() => Session.Selection.ReplacePhrases(_part, _phrases);
    }

    /// <summary>
    /// Drags one end of a phrase to the grid, the other staying where it is. It never reaches the other end: at the
    /// nearest, it's a grid step (a tick, with snap off) short of it. Nothing changes until the pointer has moved, so a
    /// click only selects, and leaves an end off the grid, such as one S put a tick past a note, where it is.
    /// </summary>
    private sealed class PhraseResizeGesture : Gesture
    {
        private readonly ChartInstrument _part;
        private readonly ChartPoint _pressed;
        private readonly Phrase _phrase;
        private readonly bool _start;
        private readonly IReadOnlyList<Phrase> _selected;

        // The phrase last tried, so one refused isn't tried, and said, again as the pointer moves on within a grid step.
        private Phrase _tried;
        private bool _moved;

        public PhraseResizeGesture(EditorTools tools, ChartPoint pressed, Phrase phrase, bool start)
            : base(tools)
        {
            _part = pressed.Track.Instrument;
            _pressed = pressed;
            _phrase = phrase;
            _start = start;
            _selected = Session.Selection.Phrases;
            _tried = phrase;
        }

        public override void Drag(ChartPoint point)
        {
            _moved |= Math.Abs(point.Tick - _pressed.Tick) > _pressed.Tolerance;
            if (!_moved)
                return;
            var snap = Tools.Snap;
            var map = Tools.Map;
            long at = snap.Nearest(point.Tick, map);
            long start = _phrase.Tick, end = _phrase.Tick + _phrase.Length;
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

            var resized = Tools.Classified(_part, _phrase with { Tick = start, Length = end - start });
            if (resized == _tried)
                return;
            _tried = resized;

            // A refusal, said in the status, leaves the phrase as it was rather than as the last drag made it.
            bool previewed = Session.Preview(resized == _phrase ? null : new ChangePhrase(_part, _phrase, resized));
            if (!previewed)
                Session.Preview(null);
            var shown = previewed ? resized : _phrase;
            Session.Selection.ReplacePhrases(_part, _selected.Select(p => p == _phrase ? shown : p));
        }

        public override void Cancel() => Session.Selection.ReplacePhrases(_part, _selected);
    }

    /// <summary>
    /// Draws out a disco flip section on the lane's difficulty along time (charter-spec C6 item 7), as a phrase is drawn:
    /// from the grid step pressed to the one the pointer is at, either way. A click without a drag adds nothing, and
    /// selects no sections.
    /// </summary>
    private sealed class DiscoFlipDrawGesture : Gesture
    {
        private readonly ChartTrackKey _track;
        private readonly long _from;

        // Whether the press was in the rail, where a click clears the phrases selected as well.
        private readonly bool _fromRail;
        private (long Tick, long EndTick)? _tried;

        public DiscoFlipDrawGesture(EditorTools tools, ChartTrackKey track, long from, bool fromRail)
            : base(tools)
        {
            _track = track;
            _from = from;
            _fromRail = fromRail;
        }

        public override void Drag(ChartPoint point)
        {
            if (point.Track != _track)
                return;
            long to = Tools.Snap.Nearest(point.Tick, Tools.Map);
            (long, long)? span = to == _from ? null : (Math.Min(_from, to), Math.Max(_from, to));
            if (span == _tried)
                return;
            _tried = span;

            // A refusal, said in the status, leaves nothing to add, rather than the section drawn before it.
            if (!Session.Preview(span is { } added ? new AddDiscoFlip(_track.Difficulty, added.Item1, added.Item2) : null))
                Session.Preview(null);
        }

        public override void Release(ChartPoint point)
        {
            Drag(point);
            if (Session.CommitPreview() is AddDiscoFlip added && Tools.SectionAt(added.Difficulty, added.Tick) is { } section)
                Session.Selection.SelectDiscoFlips([section]);
            else if (_tried is null)
            {
                if (_fromRail)
                    Session.Selection.ClearPhrases();
                Session.Selection.ClearDiscoFlips();
            }
        }
    }

    /// <summary>
    /// Drags the selected disco flip sections in time, by the bar grabbed, to the grid, each keeping its length, as phrases
    /// are dragged. Nothing moves until the pointer has, so a click only selects. A section without an end can't move.
    /// </summary>
    private sealed class DiscoFlipMoveGesture : Gesture
    {
        private readonly ChartPoint _pressed;
        private readonly DiscoFlipSection _grabbed;
        private readonly IReadOnlyList<DiscoFlipSection> _sections;

        // The move last tried, so one refused isn't tried, and said, again as the pointer moves on within a grid step.
        private long _delta;
        private bool _moved;

        public DiscoFlipMoveGesture(EditorTools tools, ChartPoint pressed, DiscoFlipSection grabbed)
            : base(tools)
        {
            _pressed = pressed;
            _grabbed = grabbed;
            _sections = Session.Selection.DiscoFlips;
        }

        public override void Drag(ChartPoint point)
        {
            _moved |= Math.Abs(point.Tick - _pressed.Tick) > _pressed.Tolerance;
            if (!_moved)
                return;
            long tick = Tools.Snap.Nearest(_grabbed.Range.Tick + (point.Tick - _pressed.Tick), Tools.Map);
            long delta = Math.Max(tick - _grabbed.Range.Tick, -_sections.Min(static s => s.Range.Tick));
            if (delta == _delta)
                return;
            _delta = delta;

            Session.Preview(null);
            Session.Selection.ReplaceDiscoFlips(_sections);
            if (delta == 0)
                return;
            if (_sections.Any(static s => s.Range.EndTick is null))
            {
                Session.Status = DiscoFlipWithoutEnd;
                return;
            }

            // A refusal, said in the status, leaves the sections where they were rather than where the last drag put them.
            var changes = _sections.Select(s => (From: s, Tick: s.Range.Tick + delta, EndTick: s.Range.EndTick!.Value + delta)).ToList();
            if (Session.Preview(DiscoFlipChangeOf(changes)))
                Tools.ReselectDiscoFlips(_sections, changes.Select(static c => (c.From, c.Tick)));
        }

        public override void Cancel() => Session.Selection.ReplaceDiscoFlips(_sections);
    }

    /// <summary>
    /// Drags one end of a disco flip section to the grid, the other staying where it is, as a phrase's end is dragged: it
    /// never reaches the other end, and nothing changes until the pointer has moved. A section without an end can't have
    /// its start dragged.
    /// </summary>
    private sealed class DiscoFlipResizeGesture : Gesture
    {
        private readonly ChartPoint _pressed;
        private readonly DiscoFlipSection _section;
        private readonly bool _start;
        private readonly IReadOnlyList<DiscoFlipSection> _selected;

        // The span last tried, so one refused isn't tried, and said, again as the pointer moves on within a grid step.
        private (long Tick, long? EndTick) _tried;
        private bool _moved;

        public DiscoFlipResizeGesture(EditorTools tools, ChartPoint pressed, DiscoFlipSection section, bool start)
            : base(tools)
        {
            _pressed = pressed;
            _section = section;
            _start = start;
            _selected = Session.Selection.DiscoFlips;
            _tried = (section.Range.Tick, section.Range.EndTick);
        }

        public override void Drag(ChartPoint point)
        {
            _moved |= Math.Abs(point.Tick - _pressed.Tick) > _pressed.Tolerance;
            if (!_moved)
                return;
            if (_section.Range.EndTick is not { } end)
            {
                Session.Status = DiscoFlipWithoutEnd;
                return;
            }

            var snap = Tools.Snap;
            var map = Tools.Map;
            long at = snap.Nearest(point.Tick, map);
            long start = _section.Range.Tick;
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

            if ((start, end) == _tried)
                return;
            _tried = (start, end);

            // A refusal, said in the status, leaves the section as it was rather than as the last drag made it.
            Session.Preview(null);
            Session.Selection.ReplaceDiscoFlips(_selected);
            if ((start, end) == (_section.Range.Tick, _section.Range.EndTick))
                return;
            if (Session.Preview(new ChangeDiscoFlip(_section.Difficulty, _section.Range, start, end)))
                Tools.ReselectDiscoFlips(_selected, [(_section, start)]);
        }

        public override void Cancel() => Session.Selection.ReplaceDiscoFlips(_selected);
    }

    /// <summary>Drags a box over notes, selecting them and the span of ticks it covers; a click in an empty place clears the selection.</summary>
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

            // By the columns the notes are drawn in, which inside a disco flip section swap red and yellow.
            var inBox = new List<NotePlace>();
            foreach (var note in track.Notes.Between(from, to))
            {
                int index = slots.IndexOf(Session.ShownLane(_pressed.Track, note));
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

            // A click in an empty place; the cursor stays where playback is.
            if (!_add)
                Session.Selection.Clear();
        }
    }
}
