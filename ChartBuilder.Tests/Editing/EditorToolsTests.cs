using ChartPacker.Core.Charting;
using ChartPacker.Core.Charting.Editing;
using ChartPacker.Core.Charts;
using static ChartBuilder.Tests.Editing.EditorSessions;

namespace ChartBuilder.Tests.Editing;

/// <summary>
/// AC C3.2: every tool, run against <see cref="EditorTools"/> rather than either canvas, with the same points in both
/// views giving the same chart.
/// </summary>
[TestClass]
public sealed class EditorToolsTests
{
    private const int Open = ChartNote.OpenLane;

    public static IEnumerable<object[]> Views => [[LaneView.Highway], [LaneView.Timeline]];

    [TestMethod]
    [DynamicData(nameof(Views))]
    public void PlacePutsANoteOnTheGridAsOneStep(LaneView view)
    {
        var session = Of(Guitar());
        session.Tool = EditorTool.Place;

        Click(session, At(session, 130, 2, view));

        Assert.AreSequenceEqual([new EditableNote(120, EditableNote.PlainLength, 2)], Expert(session).Notes.ToList());
        Assert.AreEqual(1, session.UndoStack.UndoCount);
        Assert.AreSequenceEqual([new NotePlace(120, 2)], session.Selection.Places.ToList());
    }

    [TestMethod]
    [DynamicData(nameof(Views))]
    public void DraggingWhilePlacingDrawsOutTheSustain(LaneView view)
    {
        var session = Of(Guitar());
        session.Tool = EditorTool.Place;

        DragFromTo(session, At(session, 120, 1, view), At(session, 500, 1, view));

        Assert.AreSequenceEqual([new EditableNote(120, 360, 1)], Expert(session).Notes.ToList());
        Assert.AreEqual(1, session.UndoStack.UndoCount);
        session.Undo();
        Assert.IsEmpty(Expert(session).Notes);
    }

    [TestMethod]
    [DynamicData(nameof(Views))]
    public void AnOpenNoteIsNotPlacedInAChordWithFretsNorAFretOnAnOpen(LaneView view)
    {
        // C5 item 2.
        var session = Of(Guitar());
        session.Tool = EditorTool.Place;
        Click(session, At(session, 0, 1, view));
        Click(session, At(session, 480, 1, view), EditorModifiers.Shift);

        Click(session, At(session, 0, 0, view), EditorModifiers.Shift);
        Assert.IsNotNull(session.Status);
        Click(session, At(session, 480, 3, view));
        Assert.IsNotNull(session.Status);

        Assert.AreSequenceEqual([new EditableNote(0, 1, 1), new EditableNote(480, 1, Open)], Expert(session).Notes.ToList());
        Assert.AreEqual(2, session.UndoStack.UndoCount);
    }

    [TestMethod]
    [DynamicData(nameof(Views))]
    public void SelectingAndDraggingMovesNotesOnTheGridAndAcrossLanes(LaneView view)
    {
        var session = Of(Guitar());
        Expert(session).Notes.Add(new EditableNote(480, 1, 1));
        Expert(session).Notes.Add(new EditableNote(960, 1, 2));
        Expert(session).SetFlags(480, ChordFlags.Tap);

        DragFromTo(session, At(session, 482, 1, view), At(session, 735, 2, view));

        Assert.AreSequenceEqual([new EditableNote(720, 1, 2), new EditableNote(960, 1, 2)], Expert(session).Notes.ToList());
        Assert.AreEqual(ChordFlags.Tap, Expert(session).FlagsAt(720), "A chord's marking moves with it.");
        Assert.AreEqual(1, session.UndoStack.UndoCount);
        Assert.AreSequenceEqual([new NotePlace(720, 2)], session.Selection.Places.ToList());
    }

    [TestMethod]
    [DynamicData(nameof(Views))]
    public void AnOpenNoteMovesOnlyInTime(LaneView view)
    {
        var session = Of(Guitar());
        Expert(session).Notes.Add(new EditableNote(480, 1, Open));
        int? openLane = view == LaneView.Highway ? 2 : Open;

        DragFromTo(session, At(session, 480, openLane, view), At(session, 960, 4, view));

        Assert.AreSequenceEqual([new EditableNote(960, 1, Open)], Expert(session).Notes.ToList());
    }

    [TestMethod]
    [DynamicData(nameof(Views))]
    public void ABoxSelectsTheNotesInItAndTheSpanItCovers(LaneView view)
    {
        var session = Of(Guitar());
        var track = Expert(session);
        track.Notes.Add(new EditableNote(480, 1, 0));
        track.Notes.Add(new EditableNote(600, 1, 1));
        track.Notes.Add(new EditableNote(720, 1, 3));
        track.Notes.Add(new EditableNote(1920, 1, 1));

        DragFromTo(session, At(session, 400, 0, view), At(session, 800, 2, view));

        Assert.AreSequenceEqual([new EditableNote(480, 1, 0), new EditableNote(600, 1, 1)], session.Selection.Notes.ToList());
        Assert.AreEqual((400L, 800L), session.Selection.Range);
        Assert.AreEqual(0, session.UndoStack.UndoCount);
    }

    [TestMethod]
    [DynamicData(nameof(Views))]
    public void ClickingAnEmptyPlaceMovesTheCursorToTheGridAndSelectsNothing(LaneView view)
    {
        var session = Of(Guitar());
        Expert(session).Notes.Add(new EditableNote(480, 1, 0));
        session.Selection.Select(GuitarExpert, Expert(session).Notes);

        Click(session, At(session, 1000, 3, view));

        Assert.IsTrue(session.Selection.IsEmpty);
        Assert.AreEqual(960, session.CursorTick);
    }

    [TestMethod]
    [DynamicData(nameof(Views))]
    public void ErasingRemovesEveryNotePassedOverAsOneStep(LaneView view)
    {
        var session = Of(Guitar());
        var track = Expert(session);
        track.Notes.Add(new EditableNote(480, 1, 2));
        track.Notes.Add(new EditableNote(720, 1, 2));
        track.Notes.Add(new EditableNote(960, 1, 2));
        session.Tool = EditorTool.Erase;

        session.Tools.Press(At(session, 480, 2, view));
        session.Tools.Drag(At(session, 600, 2, view));
        session.Tools.Drag(At(session, 722, 2, view));
        session.Tools.Release(At(session, 722, 2, view));

        Assert.AreSequenceEqual([new EditableNote(960, 1, 2)], track.Notes.ToList());
        Assert.AreEqual(1, session.UndoStack.UndoCount);
    }

    [TestMethod]
    [DynamicData(nameof(Views))]
    public void TheSustainToolEndsTheSelectedNotesOnTheGrid(LaneView view)
    {
        // A disjoint chord and an extended sustain are both allowed (C5 item 3).
        var session = Of(Guitar());
        var track = Expert(session);
        track.Notes.Add(new EditableNote(0, 1, 0));
        track.Notes.Add(new EditableNote(240, 1, 1));
        session.Selection.Select(GuitarExpert, track.Notes);
        session.Tool = EditorTool.Sustain;

        DragFromTo(session, At(session, 0, 0, view), At(session, 950, 0, view));

        Assert.AreSequenceEqual([new EditableNote(0, 960, 0), new EditableNote(240, 720, 1)], track.Notes.ToList());
        Assert.AreEqual(1, session.UndoStack.UndoCount);
    }

    [TestMethod]
    [DynamicData(nameof(Views))]
    public void DraggingATailSetsOnlyThatNotesSustain(LaneView view)
    {
        var session = Of(Guitar());
        var track = Expert(session);
        track.Notes.Add(new EditableNote(0, 480, 0));
        track.Notes.Add(new EditableNote(0, 480, 1));

        DragFromTo(session, At(session, 300, 1, view), At(session, 1440, 1, view));

        Assert.AreSequenceEqual([new EditableNote(0, 480, 0), new EditableNote(0, 1440, 1)], track.Notes.ToList());
    }

    [TestMethod]
    public void CancellingAGestureLeavesTheChartAsItWas()
    {
        var session = Of(Guitar());
        Expert(session).Notes.Add(new EditableNote(480, 1, 1));

        session.Tools.Press(At(session, 480, 1));
        session.Tools.Drag(At(session, 960, 3));
        Assert.IsTrue(session.HasPreview);
        session.Tools.Cancel();

        Assert.AreSequenceEqual([new EditableNote(480, 1, 1)], Expert(session).Notes.ToList());
        Assert.AreSequenceEqual([new NotePlace(480, 1)], session.Selection.Places.ToList());
        Assert.AreEqual(0, session.UndoStack.UndoCount);
        Assert.IsFalse(session.HasPreview);
    }

    [TestMethod]
    public void UndoInTheMiddleOfAGestureTakesTheGestureBackFirst()
    {
        var session = Of(Guitar());
        session.Tool = EditorTool.Place;
        Click(session, At(session, 0, 0));
        session.Tools.Press(At(session, 480, 1));

        session.Undo();

        Assert.IsEmpty(Expert(session).Notes);
        Assert.IsFalse(session.Tools.IsActive);
    }

    [TestMethod]
    public void NumberKeysPlaceAndRemoveAtTheCursor()
    {
        var session = Of(Guitar());
        session.MoveCursorTo(970);

        session.Tools.ToggleNoteAtCursor(2);
        Assert.AreSequenceEqual([new EditableNote(960, 1, 2)], Expert(session).Notes.ToList());
        session.Tools.ToggleNoteAtCursor(Open);
        Assert.IsNotNull(session.Status, "An open can't join the chord.");
        session.Tools.ToggleNoteAtCursor(2);
        Assert.IsEmpty(Expert(session).Notes);
        Assert.AreEqual(2, session.UndoStack.UndoCount);
    }

    [TestMethod]
    public void TheCursorStepsByTheGridAndByMeasures()
    {
        var session = Of(Guitar());

        session.Tools.StepCursor(3);
        Assert.AreEqual(360, session.CursorTick);
        session.Snap = SnapGrid.Of(4);
        session.Tools.StepCursor(1);
        Assert.AreEqual(480, session.CursorTick);
        session.Tools.StepCursorMeasures(1);
        Assert.AreEqual(1920, session.CursorTick);
        session.Tools.StepCursor(-2);
        Assert.AreEqual(960, session.CursorTick);
    }

    [TestMethod]
    public void TheCursorStepsAlongTheGridThroughTempoAndTimeSignatureChanges()
    {
        // The wheel and the arrow keys step the strike line this way, so it stays on the grid past the first tempo and
        // signature: 7/8 from the start, 4/4 from tick 1680, which isn't on the quarter-note grid, and 200 BPM from 960.
        var chart = Guitar();
        var map = chart.TempoMap;
        map.TimeSignatures.Remove(map.TimeSignatures[0]);
        map.TimeSignatures.Add(new TimeSignatureMarker(0, 7, 8));
        map.TimeSignatures.Add(new TimeSignatureMarker(1680, 4, 4));
        map.Tempos.Add(new TempoChange(960, 300_000));
        var session = Of(chart);
        session.Snap = SnapGrid.Of(4);

        var stepped = new List<(long Tick, double Seconds)>();
        for (int i = 0; i < 6; i++)
        {
            session.Tools.StepCursor(1);
            stepped.Add((session.CursorTick, session.Viewport.Cursor));
        }

        Assert.AreSequenceEqual([480L, 960, 1440, 1680, 2160, 2640], stepped.Select(s => s.Tick).ToList());
        double[] seconds = [0.5, 1.0, 1.3, 1.45, 1.75, 2.05];
        for (int i = 0; i < seconds.Length; i++)
            Assert.AreEqual(seconds[i], stepped[i].Seconds, 1e-9, $"Step {i + 1} is at its tick's time under the tempo there.");

        session.Tools.StepCursor(-3);
        Assert.AreEqual(1440, session.CursorTick);
    }

    [TestMethod]
    [DynamicData(nameof(Views))]
    public void AltArrowsMoveTheSelectionAStepOrALane(LaneView view)
    {
        var session = Of(Guitar());
        Expert(session).Notes.Add(new EditableNote(480, 1, 3));
        Expert(session).Notes.Add(new EditableNote(480, 1, 4));
        session.Selection.Select(GuitarExpert, Expert(session).Notes);
        var slots = session.SlotsOf(ChartInstrument.Guitar, view);

        session.Tools.MoveSelection(1, 0, slots);
        session.Tools.MoveSelection(0, 1, slots);
        session.Tools.MoveSelection(0, -2, slots);

        Assert.AreSequenceEqual([new EditableNote(600, 1, 1), new EditableNote(600, 1, 2)], Expert(session).Notes.ToList());
        Assert.AreEqual(2, session.UndoStack.UndoCount, "A move up past orange moves nothing.");
    }

    [TestMethod]
    public void ForcingKeysMarkTheSelectedChordsAndClearWhenAllHaveIt()
    {
        var session = Of(Guitar());
        var track = Expert(session);
        track.Notes.Add(new EditableNote(0, 1, 0));
        track.Notes.Add(new EditableNote(480, 1, 1));
        track.Notes.Add(new EditableNote(480, 1, 2));
        track.SetFlags(480, ChordFlags.ForceStrum);
        session.Selection.Select(GuitarExpert, track.Notes);

        session.Tools.ToggleChordFlag(ChordFlags.ForceHopo);
        Assert.AreEqual(ChordFlags.ForceHopo, track.FlagsAt(0));
        Assert.AreEqual(ChordFlags.ForceHopo, track.FlagsAt(480), "Force HOPO takes force strum's place.");

        session.Tools.ToggleChordFlag(ChordFlags.Tap);
        session.Tools.ToggleChordFlag(ChordFlags.ForceHopo);
        Assert.AreEqual(ChordFlags.Tap, track.FlagsAt(0));
        Assert.AreEqual(ChordFlags.Tap, track.FlagsAt(480));
        Assert.AreEqual(3, session.UndoStack.UndoCount);
    }

    [TestMethod]
    public void DeleteRemovesTheSelectedNotesAndMarkersAsOneStep()
    {
        var session = Of(Guitar());
        Expert(session).Notes.Add(new EditableNote(0, 1, 0));
        Expert(session).Notes.Add(new EditableNote(480, 1, 1));
        session.Chart.TempoMap.Tempos.Add(new TempoChange(960, 400_000));
        session.Selection.Select(GuitarExpert, [Expert(session).Notes[1]]);
        session.Selection.SelectTempos([960], add: true);

        session.Tools.DeleteSelection();

        Assert.AreSequenceEqual([new EditableNote(0, 1, 0)], Expert(session).Notes.ToList());
        Assert.HasCount(1, session.Chart.TempoMap.Tempos);
        Assert.AreEqual(1, session.UndoStack.UndoCount);
        session.Undo();
        Assert.HasCount(2, Expert(session).Notes);
        Assert.HasCount(2, session.Chart.TempoMap.Tempos);
    }

    [TestMethod]
    [DynamicData(nameof(Views))]
    public void TheTempoToolAddsAMarkerAndDragsItInTicks(LaneView view)
    {
        var session = Of(Guitar());
        session.Tool = EditorTool.Tempo;

        Click(session, At(session, 965, null, view));
        Assert.AreSequenceEqual([new TempoChange(0, 500_000), new TempoChange(960, 500_000)], session.Chart.TempoMap.Tempos.ToList());

        DragFromTo(session, At(session, 962, null, view), At(session, 1440, null, view));
        Assert.AreSequenceEqual([new TempoChange(0, 500_000), new TempoChange(1440, 500_000)], session.Chart.TempoMap.Tempos.ToList());
        Assert.AreEqual(2, session.UndoStack.UndoCount);
    }

    [TestMethod]
    public void GameTracksAreWorkedOutAgainAfterAnEditTouchesThem()
    {
        var session = Of(Guitar());
        Expert(session).Notes.Add(new EditableNote(0, 1, 0));
        var before = session.GameTrack(GuitarExpert);
        Assert.AreSame(before, session.GameTrack(GuitarExpert));

        session.Tool = EditorTool.Place;
        Click(session, At(session, 120, 1));

        var after = session.GameTrack(GuitarExpert);
        Assert.AreNotSame(before, after);
        Assert.AreEqual(NoteFlags.Hopo, after.Notes[1].Flags, "A note a sixteenth after another is a natural HOPO.");
    }
}
