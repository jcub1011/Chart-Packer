using ChartPacker.Core.Charting;
using ChartPacker.Core.Charting.Commands;
using ChartPacker.Core.Charting.Editing;
using ChartPacker.Core.Charting.Layout;
using ChartPacker.Core.Charts;
using static ChartBuilder.Tests.Editing.EditorSessions;

namespace ChartBuilder.Tests.Editing;

/// <summary>
/// The middle and right buttons, whatever the tool, and what goes with them: the middle button erases notes and phrases;
/// the right button lengthens or shortens the selected notes and opens the note menu, whose types are set here; leaving
/// the select tool selects nothing; and Ctrl with the place tool puts an open note down.
/// </summary>
[TestClass]
public sealed class MouseGestureTests
{
    private const int Open = ChartNote.OpenLane;

    // A grid step at the default snap, 1/16, at 480 ticks a beat.
    private const int Step = 120;

    public static IEnumerable<object[]> Views => [[LaneView.Highway], [LaneView.Timeline]];

    // The middle button.

    [TestMethod]
    [DynamicData(nameof(Views))]
    public void TheMiddleButtonErasesEveryNotePassedOverWithAnyToolAsOneStep(LaneView view)
    {
        var session = Of(Guitar());
        var track = Expert(session);
        track.Notes.Add(new EditableNote(480, 1, 2));
        track.Notes.Add(new EditableNote(720, 1, 2));
        track.Notes.Add(new EditableNote(960, 1, 2));
        session.Tool = EditorTool.Place;

        session.Tools.PressErase(At(session, 480, 2, view));
        session.Tools.Drag(At(session, 722, 2, view));
        session.Tools.Release(At(session, 722, 2, view));

        Assert.AreSequenceEqual([new EditableNote(960, 1, 2)], track.Notes.ToList());
        Assert.AreEqual(1, session.UndoStack.UndoCount);
    }

    [TestMethod]
    [DynamicData(nameof(Views))]
    public void TheMiddleButtonErasesEveryPhrasePassedOverInTheRailAsOneStep(LaneView view)
    {
        var chart = Guitar();
        var solo = new Phrase(PhraseKind.Solo, 0, 960);
        new AddPhrase(ChartInstrument.Guitar, new Phrase(PhraseKind.StarPower, 0, 480)).Apply(chart);
        new AddPhrase(ChartInstrument.Guitar, new Phrase(PhraseKind.StarPower, 960, 480)).Apply(chart);
        new AddPhrase(ChartInstrument.Guitar, solo).Apply(chart);
        var session = Of(chart);

        session.Tools.PressErase(RailAt(session, 240, PhraseKind.StarPower, view));
        session.Tools.Drag(RailAt(session, 1200, PhraseKind.StarPower, view));
        session.Tools.Release(RailAt(session, 1200, PhraseKind.StarPower, view));

        Assert.AreSequenceEqual([solo], Phrases(session));
        Assert.AreEqual(1, session.UndoStack.UndoCount);
        session.Undo();
        Assert.HasCount(3, Phrases(session));
    }

    [TestMethod]
    public void TheMiddleButtonErasesADiscoFlipSection()
    {
        var chart = Drums();
        new AddDiscoFlip(ChartDifficulty.Expert, 480, 960).Apply(chart);
        var session = Of(chart, focused: DrumsExpert);

        var point = RailAt(session, 600, RailKind.DiscoFlip, key: DrumsExpert);
        session.Tools.PressErase(point);
        session.Tools.Release(point);

        Assert.IsEmpty(session.DiscoFlipRangesOf(DrumsExpert));
        session.Undo();
        Assert.HasCount(1, session.DiscoFlipRangesOf(DrumsExpert));
    }

    [TestMethod]
    public void TheMiddleButtonErasesVocalNotesAndPhrasesAsOneStep()
    {
        var session = Of(WithVocals([new(0, Step, 60), new(480, Step, 62)], phrases: [new(0, 960)]));

        session.Tools.PressErase(VocalAt(0, 60));
        session.Tools.Drag(PhraseStripAt(480));
        session.Tools.Release(PhraseStripAt(480));

        Assert.AreSequenceEqual([new VocalNoteEvent(480, Step, 62)], Vocals(session).Notes.ToList());
        Assert.IsEmpty(Vocals(session).Phrases);
        Assert.AreEqual(1, session.UndoStack.UndoCount);
    }

    // The right button's drag.

    [TestMethod]
    [DynamicData(nameof(Views))]
    public void ARightDragLengthensEachSelectedNoteByTheStepsDragged(LaneView view)
    {
        var session = Of(Guitar());
        var track = Expert(session);
        EditableNote plain = new(0, EditableNote.PlainLength, 1), held = new(480, 240, 2);
        track.Notes.Add(plain);
        track.Notes.Add(held);
        session.Selection.Select(GuitarExpert, [plain, held]);

        session.Tools.PressContext(At(session, 480, 2, view));
        session.Tools.Drag(At(session, 480 + Step, 2, view));
        session.Tools.Release(At(session, 480 + Step, 2, view));

        // A note without a sustain grows one from its tick.
        Assert.AreSequenceEqual([new EditableNote(0, Step, 1), new EditableNote(480, 240 + Step, 2)], track.Notes.ToList());
        Assert.AreEqual(1, session.UndoStack.UndoCount);
        session.Undo();
        Assert.AreSequenceEqual([plain, held], track.Notes.ToList());
    }

    [TestMethod]
    public void AnOpenNotesTailIsGrabbedInAnyColumnOfAHighwayAfterAFretsOwn()
    {
        // Drawn across the lanes, an open note's tail is under every column; a fret's own tail in the column comes first.
        var session = Of(Guitar());
        var track = Expert(session);
        EditableNote open = new(0, 480, Open), fret = new(240, 480, 3);
        track.Notes.Add(open);
        track.Notes.Add(fret);

        Assert.AreEqual((open, true), session.Tools.HitTest(At(session, 300, 1)));
        Assert.AreEqual((fret, true), session.Tools.HitTest(At(session, 300, 3)));

        DragFromTo(session, At(session, 300, 1), At(session, 720, 1));
        Assert.AreSequenceEqual([open with { Length = 720 }, fret], track.Notes.ToList());
    }

    [TestMethod]
    public void ARightDragShortensEachSelectedNoteButNoShorterThanNoSustain()
    {
        var session = Of(Guitar());
        var track = Expert(session);
        EditableNote longer = new(0, 480, 1), shorter = new(480, Step, 2);
        track.Notes.Add(longer);
        track.Notes.Add(shorter);
        session.Selection.Select(GuitarExpert, [longer, shorter]);

        session.Tools.PressContext(At(session, 480, 2));
        session.Tools.Release(At(session, 240, 2));

        Assert.AreSequenceEqual([new EditableNote(0, 240, 1), new EditableNote(480, EditableNote.PlainLength, 2)], track.Notes.ToList());
    }

    [TestMethod]
    public void CtrlEndsEverySelectedNoteWhereThePointerIsAndLettingGoOfItStepsAgain()
    {
        var session = Of(Guitar());
        var track = Expert(session);
        EditableNote plain = new(0, EditableNote.PlainLength, 1), held = new(480, 240, 2);
        track.Notes.Add(plain);
        track.Notes.Add(held);
        session.Selection.Select(GuitarExpert, [plain, held]);

        session.Tools.PressContext(At(session, 480, 2));
        session.Tools.Drag(At(session, 1200, 2), EditorModifiers.Control);
        Assert.AreSequenceEqual([new EditableNote(0, 1200, 1), new EditableNote(480, 720, 2)], track.Notes.ToList());

        session.Tools.Drag(At(session, 1200, 2));
        session.Tools.Release(At(session, 1200, 2));
        Assert.AreSequenceEqual([new EditableNote(0, 720, 1), new EditableNote(480, 960, 2)], track.Notes.ToList());
        Assert.AreEqual(1, session.UndoStack.UndoCount);
    }

    [TestMethod]
    public void ARightPressOnANoteNotSelectedSelectsItAloneAndAClickChangesNothing()
    {
        var session = Of(Guitar());
        var track = Expert(session);
        EditableNote first = new(0, EditableNote.PlainLength, 1), second = new(480, EditableNote.PlainLength, 2);
        track.Notes.Add(first);
        track.Notes.Add(second);
        session.Selection.Select(GuitarExpert, [first]);

        session.Tools.PressContext(At(session, 480, 2));
        session.Tools.Cancel();

        Assert.AreSequenceEqual([new NotePlace(480, 2)], session.Selection.Places.ToList());
        Assert.AreSequenceEqual([first, second], track.Notes.ToList());
        Assert.AreEqual(0, session.UndoStack.UndoCount);
    }

    [TestMethod]
    public void ARightPressWithNothingSelectedOnNoNoteStartsNothing()
    {
        var session = Of(Guitar());

        session.Tools.PressContext(At(session, 480, 2));

        Assert.IsFalse(session.Tools.IsActive);
    }

    [TestMethod]
    public void ARightDragOnDrumsChangesNoLengthAndSaysWhy()
    {
        var session = Of(Drums(), focused: DrumsExpert);
        var track = session.Chart.PartOf(ChartInstrument.Drums)![ChartDifficulty.Expert];
        EditableNote pad = new(0, EditableNote.PlainLength, 1);
        track.Notes.Add(pad);
        session.Selection.Select(DrumsExpert, [pad]);

        session.Tools.PressContext(At(session, 0, 1, key: DrumsExpert));
        session.Tools.Release(At(session, 480, 1, key: DrumsExpert));

        Assert.AreSequenceEqual([pad], track.Notes.ToList());
        Assert.IsNotNull(session.Status);
        Assert.AreEqual(0, session.UndoStack.UndoCount);
    }

    [TestMethod]
    public void ARightDragLengthensEachSelectedVocalNoteByTheStepsDragged()
    {
        var session = Of(WithVocals([new(0, Step, 60), new(480, 240, 62)]));
        session.Selection.SelectVocalNotes(VocalTrack.Vocals, [new VocalNotePlace(0, 60), new VocalNotePlace(480, 62)]);

        session.Tools.PressContext(VocalAt(480, 62));
        session.Tools.Drag(VocalAt(480 + Step, 62));
        session.Tools.Release(VocalAt(480 + Step, 62));

        Assert.AreSequenceEqual([new VocalNoteEvent(0, 2 * Step, 60), new VocalNoteEvent(480, 240 + Step, 62)], Vocals(session).Notes.ToList());
        Assert.AreEqual(1, session.UndoStack.UndoCount);
    }

    // The note menu's types.

    [TestMethod]
    public void AChordTypeIsSetOnEverySelectedChordAndTurnsTheOthersOffAsOneStep()
    {
        var session = Of(Guitar());
        var track = Expert(session);
        EditableNote[] notes = [new(0, 1, 1), new(480, 1, 2), new(960, 1, 3)];
        foreach (var note in notes)
            track.Notes.Add(note);
        track.SetFlags(480, ChordFlags.ForceStrum);
        track.SetFlags(960, ChordFlags.Tap);
        session.Selection.Select(GuitarExpert, notes);
        Assert.IsNull(session.Tools.SelectedChordType(), "The chords differ.");

        session.Tools.SetChordType(ChordFlags.ForceHopo);

        Assert.IsTrue(notes.All(n => track.FlagsAt(n.Tick) == ChordFlags.ForceHopo));
        Assert.AreEqual(ChordFlags.ForceHopo, session.Tools.SelectedChordType());
        Assert.AreEqual(1, session.UndoStack.UndoCount);

        session.Tools.SetChordType(ChordFlags.None);
        Assert.IsTrue(notes.All(n => track.FlagsAt(n.Tick) == ChordFlags.None));
        Assert.AreEqual(ChordFlags.None, session.Tools.SelectedChordType(), "Natural.");
        session.Undo();
        Assert.IsTrue(notes.All(n => track.FlagsAt(n.Tick) == ChordFlags.ForceHopo));
    }

    [TestMethod]
    public void OpenMakesTheSelectedFretsOfEachChordOneOpenNoteBesideTheOthers()
    {
        var session = Of(Guitar());
        var track = Expert(session);
        EditableNote[] chord = [new(0, 240, 1), new(0, 480, 2), new(0, 1, 3)];
        EditableNote single = new(960, 1, 0);
        foreach (var note in chord.Append(single))
            track.Notes.Add(note);
        track.SetFlags(0, ChordFlags.ForceHopo);

        // An open note in a chord with frets is allowed: the frets not selected stay.
        session.Selection.Select(GuitarExpert, [chord[0], single]);
        session.Tools.SetOpen(true);

        Assert.AreSequenceEqual(
            [new EditableNote(0, 240, Open), chord[1], chord[2], new EditableNote(960, 1, Open)],
            track.Notes.ToList());
        Assert.AreEqual(ChordFlags.ForceHopo, track.FlagsAt(0));
        Assert.IsTrue(session.Tools.IsSelectionOpen());
        Assert.AreEqual(1, session.UndoStack.UndoCount);

        session.Tools.SetOpen(false);
        Assert.AreSequenceEqual(
            [new EditableNote(0, 240, 0), chord[1], chord[2], new EditableNote(960, 1, 0)],
            track.Notes.ToList());

        session.Undo();
        session.Undo();
        Assert.AreSequenceEqual([.. chord, single], track.Notes.ToList());
    }

    // The right button's drag with Shift: runs of notes.

    [TestMethod]
    [DynamicData(nameof(Views))]
    public void AShiftRightDragLaysARunOfEachSelectedNoteAsManyStepsAsDraggedAsOneStep(LaneView view)
    {
        var session = Of(Guitar());
        var track = Expert(session);
        EditableNote first = new(0, EditableNote.PlainLength, 1), held = new(240, 240, 2);
        track.Notes.Add(first);
        track.Notes.Add(held);
        session.Selection.Select(GuitarExpert, [first, held]);

        session.Tools.PressContext(At(session, 240, 2, view));
        session.Tools.Drag(At(session, 480, 2, view), EditorModifiers.Shift);
        session.Tools.Release(At(session, 480, 2, view));

        // Each note's run is two steps long, from the note; the copies have no sustain.
        Assert.AreSequenceEqual(
            [first, new EditableNote(120, 1, 1), new EditableNote(240, 1, 1), held, new EditableNote(360, 1, 2), new EditableNote(480, 1, 2)],
            track.Notes.ToList());
        Assert.HasCount(6, session.Selection.Notes);
        Assert.AreEqual(1, session.UndoStack.UndoCount);
    }

    [TestMethod]
    public void ARunDraggedStepByStepHasANoteOnEveryStep()
    {
        // Each drag's run is worked out against the chart without the run previewed before it.
        var session = Of(Guitar());
        var track = Expert(session);
        EditableNote note = new(0, EditableNote.PlainLength, 1);
        track.Notes.Add(note);
        session.Selection.Select(GuitarExpert, [note]);

        session.Tools.PressContext(At(session, 0, 1));
        for (int step = 1; step <= 4; step++)
            session.Tools.Drag(At(session, step * Step, 1), EditorModifiers.Shift);
        session.Tools.Release(At(session, 4 * Step, 1));

        Assert.AreSequenceEqual([.. Enumerable.Range(0, 5).Select(i => new EditableNote(i * Step, 1, 1))], track.Notes.ToList());
        Assert.AreEqual(1, session.UndoStack.UndoCount);
    }

    [TestMethod]
    public void CtrlShiftRightDragEndsEveryRunWhereThePointerIs()
    {
        var session = Of(Guitar());
        var track = Expert(session);
        EditableNote first = new(0, EditableNote.PlainLength, 1), second = new(240, EditableNote.PlainLength, 2);
        track.Notes.Add(first);
        track.Notes.Add(second);
        session.Selection.Select(GuitarExpert, [first, second]);

        session.Tools.PressContext(At(session, 240, 2));
        session.Tools.Drag(At(session, 480, 2), EditorModifiers.Shift | EditorModifiers.Control);
        session.Tools.Release(At(session, 480, 2));

        // The first note's run reaches further, to end with the second's.
        Assert.AreSequenceEqual(
            [
                first, new EditableNote(120, 1, 1), new EditableNote(240, 1, 1), second, new EditableNote(360, 1, 1),
                new EditableNote(360, 1, 2), new EditableNote(480, 1, 1), new EditableNote(480, 1, 2),
            ],
            track.Notes.ToList());
    }

    [TestMethod]
    public void ARunGoesBackDraggedBackAndLeavesOutNotesAlreadyThere()
    {
        var session = Of(Guitar());
        var track = Expert(session);
        EditableNote note = new(480, EditableNote.PlainLength, 1), there = new(360, EditableNote.PlainLength, 1);
        track.Notes.Add(note);
        track.Notes.Add(there);
        session.Selection.Select(GuitarExpert, [note]);

        session.Tools.PressContext(At(session, 480, 1));
        session.Tools.Drag(At(session, 240, 1), EditorModifiers.Shift);
        session.Tools.Release(At(session, 240, 1));

        Assert.AreSequenceEqual([new EditableNote(240, 1, 1), there, note], track.Notes.ToList());
    }

    [TestMethod]
    public void ARunIsLaidOnDrumsToo()
    {
        var session = Of(Drums(), focused: DrumsExpert);
        var track = session.Chart.PartOf(ChartInstrument.Drums)![ChartDifficulty.Expert];
        EditableNote pad = new(0, EditableNote.PlainLength, 1, NoteDynamics.Accent);
        track.Notes.Add(pad);
        session.Selection.Select(DrumsExpert, [pad]);

        session.Tools.PressContext(At(session, 0, 1, key: DrumsExpert));
        session.Tools.Drag(At(session, Step, 1, key: DrumsExpert), EditorModifiers.Shift);
        session.Tools.Release(At(session, Step, 1, key: DrumsExpert));

        Assert.AreSequenceEqual([pad, pad with { Tick = Step }], track.Notes.ToList());
        Assert.IsNull(session.Status);
    }

    // The tools.

    [TestMethod]
    public void LeavingTheSelectToolSelectsNothing()
    {
        var session = Of(Guitar());
        EditableNote note = new(0, 1, 1);
        Expert(session).Notes.Add(note);
        session.Selection.Select(GuitarExpert, [note]);

        session.Tool = EditorTool.Place;

        Assert.IsTrue(session.Selection.IsEmpty);
    }

    [TestMethod]
    public void ChangingBetweenOtherToolsKeepsTheSelection()
    {
        var session = Of(Guitar());
        session.Tool = EditorTool.Place;
        Click(session, At(session, 0, 1));
        Assert.IsFalse(session.Selection.IsEmpty);

        session.Tool = EditorTool.Sustain;

        Assert.IsFalse(session.Selection.IsEmpty);
    }

    [TestMethod]
    [DynamicData(nameof(Views))]
    public void CtrlWithThePlaceToolPutsDownAnOpenNote(LaneView view)
    {
        var session = Of(Guitar());
        session.Tool = EditorTool.Place;

        Click(session, At(session, 130, 2, view), EditorModifiers.Control);

        Assert.AreSequenceEqual([new EditableNote(120, EditableNote.PlainLength, Open)], Expert(session).Notes.ToList());
    }

    [TestMethod]
    public void CtrlWithThePlaceToolPutsDownAKickOnDrums()
    {
        var session = Of(Drums(), focused: DrumsExpert);
        session.Tool = EditorTool.Place;

        Click(session, At(session, 130, 2, key: DrumsExpert), EditorModifiers.Control);

        var track = session.Chart.PartOf(ChartInstrument.Drums)![ChartDifficulty.Expert];
        Assert.AreSequenceEqual([new EditableNote(120, EditableNote.PlainLength, 0)], track.Notes.ToList());
    }

    private static List<Phrase> Phrases(EditorSession session) => [.. session.Chart.PartOf(ChartInstrument.Guitar)!.Phrases];
}
