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
public sealed partial class EditorTools
{
    private const string DoubleKickOffExpert = "A 2x kick is on Expert only.";
    private const string NoPhraseLength = "A phrase has to end after it starts.";
    private const string NoDiscoFlipLength = "A disco flip section has to end after it starts.";
    private const string DiscoFlipOffPro = "Only Pro drums have disco flip.";
    private const string DiscoFlipWithoutEnd = "This disco flip section never ends, as the file has it, so it can't be moved or have its start changed. Give it an end first.";

    private readonly EditorSession _session;
    private Gesture? _gesture;

    internal EditorTools(EditorSession session) => _session = session;

    /// <summary>Whether a gesture is going on.</summary>
    public bool IsActive => _gesture is not null;

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

    private TempoMap Map => _session.TempoMap;

    /// <summary>
    /// Starts a gesture at <paramref name="point"/> with the current tool; in the phrase rail, with any tool, a gesture on
    /// the phrase there (charter-spec C8).
    /// </summary>
    public void Press(ChartPoint point, EditorModifiers modifiers = EditorModifiers.None)
    {
        Cancel();
        _session.Focused = point.Track;
        if (_session.TrackOf(point.Track) is null)
            return;
        bool shift = modifiers.HasFlag(EditorModifiers.Shift);
        if (point.Rail is not null)
        {
            _gesture = PressRail(point, shift);
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
            EditorTool.Place => PressPlace(point, shift),
            EditorTool.Erase => new EraseGesture(this, point),
            EditorTool.Sustain => PressSustain(point),
            EditorTool.Phrase => point.Lane is null ? null : PressDraw(point),
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
    /// Places a note in <paramref name="shown"/>'s column at the cursor of the focused lane, or removes the one there
    /// (charter-spec §9: 1–5, and 0 for an open note or the kick). Inside a disco flip section, it's the pad drawn in that
    /// column (C6 item 7), so 2 places red.
    /// </summary>
    public void ToggleNoteAtCursor(int shown)
    {
        if (_session.Focused is not { } key || _session.TrackOf(key) is not { } track)
            return;
        long tick = _session.CursorTick;
        int lane = _session.StoredLane(key, tick, shown);
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
        if (MoveRefusal(key, notes, moved) is { } why)
        {
            _session.Status = why;
            return;
        }

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

    /// <summary>Deletes the selected notes, phrases, disco flip sections and tempo markers (Delete), as one step.</summary>
    public void DeleteSelection()
    {
        var selection = _session.Selection;
        var steps = new List<Func<(IChartCommand?, string?)>>();
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
    /// Shift+0).
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
            _session.Selection.Select(key, [placed]);
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

        // A tail: the latest note in the lane (or its column) held past the pointer.
        if (point.Lane is not { } lane)
            return null;
        var notes = track.Notes;
        for (int i = notes.FirstAfter((long)Math.Floor(point.Tick)) - 1; i >= 0; i--)
        {
            var note = notes[i];
            if (note.Length <= EditableNote.PlainLength || note.Tick + note.Length < point.Tick)
                continue;
            int shown = shownLane(note);
            if (shown == lane || shown == partner)
                return (note, true);
        }

        return null;
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

    private Gesture? PressPlace(ChartPoint point, bool shift)
    {
        var track = _session.TrackOf(point.Track)!;
        int? lane = shift ? ApartLane(point.Slots) : point.Lane;
        if (lane is not { } shown)
            return null;
        long tick = SnapTick(point.Tick, point.Track);

        // The pad drawn in the column clicked: inside a disco flip section, yellow's column places red (C6 item 7).
        int placed = _session.StoredLane(point.Track, tick, shown);
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
        return HitTest(point) is { } hit ? SustainOf(point, hit.Note) : null;
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

    /// <summary>
    /// A press in the phrase rail, with any tool: on a proposed fill's ghost, while Generate fills runs, it keeps or drops
    /// the fill (charter-spec C6 item 5), before any bar under it. On a phrase's bar it selects the phrase (Shift adds it
    /// or takes it away), and a drag moves it, or resizes it by the end grabbed; the same on a disco flip section's bar.
    /// Elsewhere in the rail, the Phrase tool draws, and the others select no phrases or sections.
    /// </summary>
    private Gesture? PressRail(ChartPoint point, bool shift)
    {
        if (_session.Fills.HitTest(point) is { } ghost)
        {
            _session.Fills.Toggle(ghost);
            return null;
        }

        var part = Chart.PartOf(point.Track.Instrument)!;
        var selection = _session.Selection;
        if (PhraseRail.HitTestDiscoFlip(point.Track.Difficulty, _session.DiscoFlipRangesOf(point.Track), point) is { } flip)
            return PressDiscoFlip(point, flip, shift);
        if (PhraseRail.HitTest(part, point) is not { } hit)
        {
            if (_session.Tool == EditorTool.Phrase)
                return PressDraw(point);
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
    /// A press with the Phrase tool where it draws: a phrase of the kind picked, or a disco flip section on the lane's
    /// difficulty, which only Pro drums have.
    /// </summary>
    private Gesture? PressDraw(ChartPoint point)
    {
        if (_session.PhraseKind.Phrase is { } kind)
            return new PhraseDrawGesture(this, point, kind);
        if (ChartInstruments.KindOf(point.Track.Instrument) != ChartInstrumentKind.Drums || !_session.IsProDrums)
        {
            _session.Status = DiscoFlipOffPro;
            return null;
        }

        return new DiscoFlipDrawGesture(this, point);
    }

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

    private string? MoveRefusal(ChartTrackKey key, IReadOnlyList<EditableNote> notes, IReadOnlyList<EditableNote> moved) =>
        PlacementRefusal(_session.TrackOf(key)!, moved, notes as IReadOnlyCollection<EditableNote>);

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
            if (Tools.MoveRefusal(_track, _notes, moved) is { } why)
            {
                Session.Status = why;
                return;
            }

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
            if (Tools.HitTest(point) is not { } hit)
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
        private Phrase? _tried;

        public PhraseDrawGesture(EditorTools tools, ChartPoint pressed, PhraseKind kind)
            : base(tools)
        {
            _part = pressed.Track.Instrument;
            _kind = kind;
            _from = Tools.Snap.Nearest(pressed.Tick, Tools.Map);
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
                Session.Selection.ClearPhrases();
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
        private (long Tick, long EndTick)? _tried;

        public DiscoFlipDrawGesture(EditorTools tools, ChartPoint pressed)
            : base(tools)
        {
            _track = pressed.Track;
            _from = Tools.Snap.Nearest(pressed.Tick, Tools.Map);
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
                Session.Selection.ClearDiscoFlips();
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
