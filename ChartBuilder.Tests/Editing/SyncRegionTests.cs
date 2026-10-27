using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Commands;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charts;
using static ChartBuilder.Tests.Editing.EditorSessions;

namespace ChartBuilder.Tests.Editing;

/// <summary>
/// The Sync tool: a region drawn on a lane, whose edits are made again in every passage of the lane that matched it
/// exactly as it was last drawn, moved or resized, as one undoable step. A region is kept with nothing matching it,
/// hashed in yellow, and is never shorter than a 32nd note.
/// </summary>
[TestClass]
public sealed class SyncRegionTests
{
    // A measure's riff, by offset from its start: green, red, then yellow, a beat apart, in the first three beats.
    private static readonly EditableNote[] Riff = [new(0, 1, 0), new(480, 1, 1), new(960, 1, 2)];

    private const long Measure = 1920;

    /// <summary>A guitar session with <see cref="Riff"/> in each of <paramref name="measures"/>.</summary>
    private static EditorSession WithRiffs(params int[] measures)
    {
        var session = Of(Guitar());
        foreach (int measure in measures)
            Lay(session, measure * Measure, Riff);
        return session;
    }

    private static void Lay(EditorSession session, long start, params EditableNote[] notes)
    {
        foreach (var note in notes)
            Expert(session).Notes.Add(note with { Tick = start + note.Tick });
    }

    /// <summary>The notes of the guitar's Expert track from <paramref name="start"/> up to a measure on, by offset.</summary>
    private static List<EditableNote> NotesOfMeasureAt(EditorSession session, long start) =>
        [.. Expert(session).Notes.Where(n => n.Tick >= start && n.Tick < start + Measure).Select(n => n with { Tick = n.Tick - start })];

    private static void SyncFirstMeasure(EditorSession session) =>
        Assert.IsTrue(session.Sync.Define(GuitarExpert, 0, Measure), session.Status);

    [TestMethod]
    public void DrawingARegionFindsTheOtherPassagesMatchingItExactly()
    {
        var session = WithRiffs(0, 2, 4);
        Lay(session, 6 * Measure, new(0, 1, 0), new(480, 1, 1), new(960, 1, 3));
        Lay(session, 8 * Measure, [.. Riff, new(1440, 1, 4)]);
        Lay(session, 10 * Measure, new(0, 1, 0), new(480, 480, 1), new(960, 1, 2));
        Lay(session, 12 * Measure, Riff);
        Expert(session).SetFlags(12 * Measure + 480, ChordFlags.Tap);

        SyncFirstMeasure(session);

        Assert.IsTrue(session.Sync.IsActive);
        Assert.AreEqual((0L, Measure), session.Sync.Span);
        Assert.AreSequenceEqual([2 * Measure, 4 * Measure], session.Sync.Matches.ToList(), "A lane, a sustain, a note more or a marking apart doesn't match.");
    }

    [TestMethod]
    public void PassagesMatchedDontOverlapTheRegionOrEachOther()
    {
        var session = Of(Guitar());
        foreach (long tick in new long[] { 0, 120, 240, 360 })
            Expert(session).Notes.Add(new EditableNote(tick, 1, 1));

        Assert.IsTrue(session.Sync.Define(GuitarExpert, 0, 240));

        Assert.AreSequenceEqual([240L], session.Sync.Matches.ToList());
    }

    [TestMethod]
    public void OnProDrumsATomDoesntMatchACymbal()
    {
        var session = Of(Drums(), focused: DrumsExpert);
        var track = session.Chart.PartOf(ChartInstrument.Drums)![ChartDifficulty.Expert];
        foreach (long start in new[] { 0, Measure, 2 * Measure })
        {
            track.Notes.Add(new EditableNote(start, 1, 0));
            track.Notes.Add(new EditableNote(start + 480, 1, 2));
        }

        Assert.IsTrue(session.Execute(new SetToms([(2, Measure + 480)], tom: true)));

        Assert.IsTrue(session.Sync.Define(DrumsExpert, 0, Measure));
        Assert.AreSequenceEqual([2 * Measure], session.Sync.Matches.ToList());
    }

    [TestMethod]
    public void ARegionOfOneChordIsKept()
    {
        var session = WithRiffs(2);
        Lay(session, 0, new(0, 1, 0), new(0, 1, 1), new(0, 1, 2));
        Lay(session, 4 * Measure, new(0, 1, 0), new(0, 1, 1), new(0, 1, 2));

        Assert.IsTrue(session.Sync.Define(GuitarExpert, 0, Measure), "Three notes in one chord are still one chord, and a region of one chord is kept.");

        Assert.IsTrue(session.Sync.IsActive);
        Assert.AreSequenceEqual([4 * Measure], session.Sync.Matches.ToList());
        Assert.IsFalse(session.Sync.IsUnmatched);
    }

    [TestMethod]
    public void ARegionNothingElseMatchesIsKeptHashedAsUnmatched()
    {
        var session = WithRiffs(0);

        Assert.IsTrue(session.Sync.Define(GuitarExpert, 0, Measure), "A region with nothing to synchronize with is kept all the same.");

        Assert.IsTrue(session.Sync.IsActive);
        Assert.IsTrue(session.Sync.IsUnmatched);
        Assert.IsEmpty(session.Sync.Matches);
        Assert.IsNotNull(session.Status);
    }

    [TestMethod]
    public void ANotePlacedInTheRegionIsPlacedInEveryMatchAsOneStep()
    {
        var session = WithRiffs(0, 2, 4);
        SyncFirstMeasure(session);
        session.Tool = EditorTool.Place;

        Click(session, At(session, 1440, 3));

        EditableNote[] edited = [.. Riff, new(1440, 1, 3)];
        foreach (long start in new[] { 0, 2 * Measure, 4 * Measure })
            Assert.AreSequenceEqual(edited, NotesOfMeasureAt(session, start));
        Assert.AreEqual(1, session.UndoStack.UndoCount);

        session.Undo();

        foreach (long start in new[] { 0, 2 * Measure, 4 * Measure })
            Assert.AreSequenceEqual(Riff, NotesOfMeasureAt(session, start));
        session.Redo();
        Assert.AreSequenceEqual(edited, NotesOfMeasureAt(session, 4 * Measure));
    }

    [TestMethod]
    public void ANoteErasedInTheRegionIsErasedInEveryMatch()
    {
        var session = WithRiffs(0, 2);
        SyncFirstMeasure(session);
        session.Tool = EditorTool.Erase;

        Click(session, At(session, 480, 1));

        Assert.AreSequenceEqual([Riff[0], Riff[2]], NotesOfMeasureAt(session, 2 * Measure));
        Assert.AreEqual(1, session.UndoStack.UndoCount);
    }

    [TestMethod]
    public void ANoteMovedInTheRegionIsMovedInEveryMatch()
    {
        var session = WithRiffs(0, 2);
        SyncFirstMeasure(session);

        DragFromTo(session, At(session, 482, 1), At(session, 725, 3));

        Assert.AreSequenceEqual([Riff[0], Riff[2], new(720, 1, 3)], NotesOfMeasureAt(session, 2 * Measure).OrderBy(n => n.Lane).ToList());
        Assert.AreEqual(1, session.UndoStack.UndoCount);
    }

    [TestMethod]
    public void ASustainAndAChordsMarkingInTheRegionAreCopiedToEveryMatch()
    {
        var session = WithRiffs(0, 2);
        SyncFirstMeasure(session);

        Assert.IsTrue(session.Execute(new SetSustain(ChartInstrument.Guitar, ChartDifficulty.Expert, [Riff[2]], 480)));
        session.Selection.Select(GuitarExpert, [Expert(session).Notes[1]]);
        session.Tools.ToggleChordFlag(ChordFlags.Tap);

        Assert.AreSequenceEqual([Riff[0], Riff[1], new(960, 480, 2)], NotesOfMeasureAt(session, 2 * Measure));
        Assert.AreEqual(ChordFlags.Tap, Expert(session).FlagsAt(2 * Measure + 480));
        Assert.AreEqual(2, session.UndoStack.UndoCount);
    }

    [TestMethod]
    public void AnAccentAndATomInTheRegionAreCopiedToEveryMatch()
    {
        var session = Of(Drums(), focused: DrumsExpert);
        var part = session.Chart.PartOf(ChartInstrument.Drums)!;
        var track = part[ChartDifficulty.Expert];
        foreach (long start in new[] { 0, Measure })
        {
            track.Notes.Add(new EditableNote(start, 1, 0));
            track.Notes.Add(new EditableNote(start + 480, 1, 2));
        }

        Assert.IsTrue(session.Sync.Define(DrumsExpert, 0, Measure));

        Assert.IsTrue(session.Execute(new SetDynamics(ChartInstrument.Drums, ChartDifficulty.Expert, [new EditableNote(480, 1, 2)], NoteDynamics.Accent)));
        Assert.IsTrue(session.Execute(new SetToms([(2, 480)], tom: true, ChartDifficulty.Expert)));

        Assert.IsTrue(track.TryGetNote(Measure + 480, 2, false, out var copied));
        Assert.AreEqual(NoteDynamics.Accent, copied.Dynamics);
        Assert.IsTrue(DrumCymbals.IsTom(part, 2, Measure + 480));
    }

    [TestMethod]
    public void ANoteDraggedOutOfTheRegionIsTakenOutOfTheMatches()
    {
        var session = WithRiffs(0, 2);
        SyncFirstMeasure(session);

        DragFromTo(session, At(session, 962, 2), At(session, 2400, 2));

        Assert.AreSequenceEqual([Riff[0], Riff[1]], NotesOfMeasureAt(session, 0));
        Assert.AreSequenceEqual([Riff[0], Riff[1]], NotesOfMeasureAt(session, 2 * Measure));
        Assert.IsTrue(Expert(session).TryGetNote(2400, 2, false, out _));
    }

    [TestMethod]
    public void TheRegionStaysWhenItsLeftWithFewerThanTwoNotes()
    {
        var session = WithRiffs(0, 2);
        SyncFirstMeasure(session);

        Assert.IsTrue(session.Execute(new RemoveNotes(ChartInstrument.Guitar, ChartDifficulty.Expert, [Riff[1], Riff[2]])));

        Assert.IsTrue(session.Sync.IsActive);
        Assert.AreSequenceEqual([Riff[0]], NotesOfMeasureAt(session, 2 * Measure));

        session.Tool = EditorTool.Place;
        Click(session, At(session, 480, 4));
        Assert.AreSequenceEqual([Riff[0], new(480, 1, 4)], NotesOfMeasureAt(session, 2 * Measure));
    }

    [TestMethod]
    public void AMatchEditedByHandStopsSyncingAsSoonAsItsEdited()
    {
        var session = WithRiffs(0, 2, 4);
        SyncFirstMeasure(session);
        var said = new List<string>();
        session.StatusPosted += (_, status) => said.Add(status);

        Assert.IsTrue(session.Execute(new AddNotes(ChartInstrument.Guitar, ChartDifficulty.Expert, [new EditableNote(2 * Measure + 1440, 1, 4)])));

        Assert.AreSequenceEqual([4 * Measure], session.Sync.Matches.ToList());
        Assert.AreSequenceEqual([2 * Measure, 4 * Measure], session.Sync.Found.ToList());
        Assert.HasCount(1, said);
    }

    [TestMethod]
    public void AMatchEditedByHandIsntWrittenOverByAnEditInTheRegion()
    {
        var session = WithRiffs(0, 2, 4);
        SyncFirstMeasure(session);
        Assert.IsTrue(session.Execute(new AddNotes(ChartInstrument.Guitar, ChartDifficulty.Expert, [new EditableNote(2 * Measure + 1440, 1, 4)])));

        Assert.IsTrue(session.Execute(new AddNotes(ChartInstrument.Guitar, ChartDifficulty.Expert, [new EditableNote(1440, 1, 3)])));

        Assert.AreSequenceEqual([.. Riff, new(1440, 1, 4)], NotesOfMeasureAt(session, 2 * Measure));
        Assert.AreSequenceEqual([.. Riff, new(1440, 1, 3)], NotesOfMeasureAt(session, 4 * Measure));
        Assert.AreSequenceEqual([4 * Measure], session.Sync.Matches.ToList());
    }

    [TestMethod]
    public void UndoingTheEditToAMatchSyncsItAgain()
    {
        var session = WithRiffs(0, 2, 4);
        SyncFirstMeasure(session);
        Assert.IsTrue(session.Execute(new AddNotes(ChartInstrument.Guitar, ChartDifficulty.Expert, [new EditableNote(2 * Measure + 1440, 1, 4)])));

        session.Undo();

        Assert.AreSequenceEqual([2 * Measure, 4 * Measure], session.Sync.Matches.ToList());
        session.Redo();
        Assert.AreSequenceEqual([4 * Measure], session.Sync.Matches.ToList());
        session.Undo();

        Assert.IsTrue(session.Execute(new AddNotes(ChartInstrument.Guitar, ChartDifficulty.Expert, [new EditableNote(1440, 1, 3)])));
        Assert.AreSequenceEqual([.. Riff, new(1440, 1, 3)], NotesOfMeasureAt(session, 2 * Measure), "Synced again, it's copied to again.");
    }

    [TestMethod]
    public void TheRegionStaysWhenNoMatchIsSynced()
    {
        var session = WithRiffs(0, 2);
        SyncFirstMeasure(session);

        Assert.IsTrue(session.Execute(new AddNotes(ChartInstrument.Guitar, ChartDifficulty.Expert, [new EditableNote(2 * Measure + 1440, 1, 4)])));

        Assert.IsTrue(session.Sync.IsActive);
        Assert.IsEmpty(session.Sync.Matches);
        session.Undo();
        Assert.AreSequenceEqual([2 * Measure], session.Sync.Matches.ToList());
    }

    [TestMethod]
    public void AnEditOutsideTheRegionIsDoneAlone()
    {
        var session = WithRiffs(0, 2);
        SyncFirstMeasure(session);
        var add = new AddNotes(ChartInstrument.Guitar, ChartDifficulty.Expert, [new EditableNote(6 * Measure, 1, 0)]);

        Assert.IsTrue(session.Execute(add));

        Assert.AreSame(add, session.UndoStack.NextUndo);
        Assert.AreSequenceEqual(Riff, NotesOfMeasureAt(session, 2 * Measure));
    }

    [TestMethod]
    public void APassageMadeToMatchAfterTheRegionIsDrawnIsntSynced()
    {
        var session = WithRiffs(0, 2);
        SyncFirstMeasure(session);
        Assert.IsTrue(session.Execute(new AddNotes(ChartInstrument.Guitar, ChartDifficulty.Expert, Riff.Select(n => n with { Tick = (4 * Measure) + n.Tick }))));

        Assert.IsTrue(session.Execute(new AddNotes(ChartInstrument.Guitar, ChartDifficulty.Expert, [new EditableNote(1440, 1, 3)])));

        Assert.AreSequenceEqual([.. Riff, new(1440, 1, 3)], NotesOfMeasureAt(session, 2 * Measure));
        Assert.AreSequenceEqual(Riff, NotesOfMeasureAt(session, 4 * Measure));
    }

    [TestMethod]
    public void DraggingWithTheSyncToolDrawsARegionOnTheGridAndGoesBackToTheToolBefore()
    {
        var session = WithRiffs(0, 2);
        session.Tool = EditorTool.Place;
        session.Tool = EditorTool.Sync;

        DragFromTo(session, At(session, 10, 3), At(session, 1910, 3));

        Assert.IsTrue(session.Sync.IsActive);
        Assert.AreEqual((0L, Measure), session.Sync.Span);
        Assert.AreEqual(EditorTool.Place, session.Tool);
        Assert.IsNull(session.Sync.Draft);
        Assert.AreEqual(0, session.UndoStack.UndoCount);
    }

    [TestMethod]
    public void AClickWithTheSyncToolDrawsNothingAndLeavesTheSyncToolPicked()
    {
        var session = WithRiffs(0, 2);
        session.Tool = EditorTool.Sync;

        Click(session, At(session, 480, 1));

        Assert.IsFalse(session.Sync.IsActive);
        Assert.AreEqual(EditorTool.Sync, session.Tool);
    }

    [TestMethod]
    public void ADrawnRegionShorterThanA32ndNoteIsDrawnOne32ndLong()
    {
        var session = WithRiffs(0, 2);
        session.Snap = SnapGrid.Of(64);
        session.Tool = EditorTool.Sync;

        DragFromTo(session, At(session, 0, 0), At(session, 30, 0));

        Assert.AreEqual((0L, 60L), session.Sync.Span, "A 32nd note is 60 ticks at 480 per beat.");
    }

    [TestMethod]
    public void ADrawnRegionSnapsDownToA32ndNoteWhenThePointerIsNearItsShortest()
    {
        var session = WithRiffs(0, 2);
        session.Tool = EditorTool.Sync;

        DragFromTo(session, At(session, 0, 0), At(session, 80, 0));

        Assert.AreEqual((0L, 60L), session.Sync.Span, "Nearer a 32nd note than the 16th the grid offers, the end snaps down to it.");
    }

    [TestMethod]
    public void TheShortestRegionSnapsDownToA32ndNoteHoweverThePointerMoves()
    {
        var session = WithRiffs(0, 2);
        SyncFirstMeasure(session);
        session.Tool = EditorTool.Sync;

        DragFromTo(session, At(session, Measure, 0), At(session, 80, 0));
        Assert.AreEqual((0L, 60L), session.Sync.Span, "Near the minimum, the end snaps down to exactly a 32nd note.");

        DragFromTo(session, At(session, 60, 0), At(session, 30, 0));
        Assert.AreEqual((0L, 60L), session.Sync.Span, "Dragged past the minimum, it stays exactly a 32nd note.");
    }

    [TestMethod]
    public void TheStartCantCrossTheEndOrLeaveLessThanA32ndNote()
    {
        var session = WithRiffs(0, 2);
        session.Snap = SnapGrid.Of(64);
        SyncFirstMeasure(session);
        session.Tool = EditorTool.Sync;

        DragFromTo(session, At(session, 0, 0), At(session, 1950, 0));

        Assert.AreEqual((1860L, Measure), session.Sync.Span);
    }

    [TestMethod]
    public void TheEndCantCrossTheStartOrLeaveLessThanA32ndNote()
    {
        var session = WithRiffs(0, 2);
        session.Snap = SnapGrid.Of(64);
        SyncFirstMeasure(session);
        session.Tool = EditorTool.Sync;

        DragFromTo(session, At(session, Measure, 0), At(session, 30, 0));

        Assert.AreEqual((0L, 60L), session.Sync.Span);
    }

    [TestMethod]
    public void DraggingTheBodyMovesTheRegionAndFindsTheMatchesThereAgain()
    {
        var session = WithRiffs(0, 1, 2);
        SyncFirstMeasure(session);
        session.Tool = EditorTool.Sync;

        DragFromTo(session, At(session, 960, 2), At(session, 960 + Measure, 2));

        Assert.AreEqual((Measure, 2 * Measure), session.Sync.Span);
        Assert.AreSequenceEqual([0L, 2 * Measure], session.Sync.Matches.ToList());
    }

    [TestMethod]
    public void ClickingAMatchedPassageMakesItTheNewSource()
    {
        var session = WithRiffs(0, 2, 4);
        SyncFirstMeasure(session);
        session.Tool = EditorTool.Sync;

        Click(session, At(session, (2 * Measure) + 480, 1));

        Assert.AreEqual((2 * Measure, 3 * Measure), session.Sync.Span);
        Assert.AreSequenceEqual([0L, 4 * Measure], session.Sync.Matches.ToList());
        Assert.IsFalse(session.Sync.IsUnmatched);
    }

    [TestMethod]
    public void HoldingCtrlAndShiftDrawsWithoutPickingTheSyncTool()
    {
        var session = WithRiffs(0, 2);
        session.Tool = EditorTool.Place;

        DragFromTo(session, At(session, 10, 3), At(session, 1910, 3), EditorModifiers.Control | EditorModifiers.Shift);

        Assert.IsTrue(session.Sync.IsActive);
        Assert.AreEqual((0L, Measure), session.Sync.Span);
        Assert.AreEqual(EditorTool.Place, session.Tool, "Ctrl + Shift holds the Sync tool only while they're held.");
    }

    [TestMethod]
    public void HoldingCtrlAndShiftAlsoResizesMovesAndMakesANewSource()
    {
        var session = WithRiffs(0, 2, 4);
        SyncFirstMeasure(session);
        session.Tool = EditorTool.Place;
        var held = EditorModifiers.Control | EditorModifiers.Shift;

        Click(session, At(session, (2 * Measure) + 480, 1), held);
        Assert.AreEqual((2 * Measure, 3 * Measure), session.Sync.Span, "A click on a synced passage makes it the source.");

        DragFromTo(session, At(session, 3 * Measure, 2), At(session, 30, 2), held);
        Assert.AreEqual((2 * Measure, (2 * Measure) + 60), session.Sync.Span, "A drag of the region's end resizes it, never shorter than a 32nd note.");

        DragFromTo(session, At(session, (2 * Measure) + 30, 2), At(session, (2 * Measure) + 30 - Measure, 2), held);
        Assert.AreEqual((Measure, Measure + 60), session.Sync.Span, "A drag of the region's body moves it.");
        Assert.AreEqual(EditorTool.Place, session.Tool);
    }

    [TestMethod]
    public void TheSyncLineIsWhereANewRegionWouldStart()
    {
        var session = WithRiffs(0, 2);
        session.Tool = EditorTool.Sync;

        Assert.AreEqual(120L, session.Tools.SyncDrawStart(At(session, 130, 1)), "The grid step the press would snap to.");
        Assert.AreEqual(0L, session.Tools.SyncDrawStart(At(session, 10, 1)), "None before the start.");
        Assert.IsNull(session.Tools.SyncDrawStart(At(session, 130, 1), EditorModifiers.Shift), "Shift alone lends any tool the select tool's press.");
        Assert.IsNull(session.Tools.SyncDrawStart(RailAt(session, 130, RailKind.DiscoFlip)), "Only in the lanes.");
    }

    [TestMethod]
    public void TheSyncLineIsGoneWhereAPressTakesHoldOfTheRegion()
    {
        var session = WithRiffs(0, 2, 4);
        SyncFirstMeasure(session);
        session.Tool = EditorTool.Sync;

        Assert.IsNull(session.Tools.SyncDrawStart(At(session, 0, 0)), "On the region's start, a drag resizes it.");
        Assert.IsNull(session.Tools.SyncDrawStart(At(session, Measure, 0)), "On its end too.");
        Assert.IsNull(session.Tools.SyncDrawStart(At(session, 960, 1)), "On its body, a drag moves it.");
        Assert.IsNull(session.Tools.SyncDrawStart(At(session, (2 * Measure) + 480, 1)), "On a passage synced with it, a click makes it the source.");
        Assert.AreEqual(3 * Measure, session.Tools.SyncDrawStart(At(session, (3 * Measure) + 10, 1)), "Elsewhere, a drag draws a new region.");
    }

    [TestMethod]
    public void TheSyncLineFollowsCtrlShiftWithoutPickingTheTool()
    {
        var session = WithRiffs(0, 2);
        session.Tool = EditorTool.Place;

        Assert.IsNull(session.Tools.SyncDrawStart(At(session, 130, 1)), "Another tool's press draws nothing.");
        Assert.AreEqual(120L, session.Tools.SyncDrawStart(At(session, 130, 1), EditorModifiers.Control | EditorModifiers.Shift), "Ctrl + Shift picks the Sync tool up.");
    }

    [TestMethod]
    public void TheSyncToolDoesntDeleteNotes()
    {
        var session = WithRiffs(0, 2);
        SyncFirstMeasure(session);
        session.Selection.Select(GuitarExpert, [Expert(session).Notes[0]]);
        session.Tool = EditorTool.Sync;

        session.Tools.DeleteSelection();

        Assert.AreSequenceEqual(Riff, NotesOfMeasureAt(session, 0));
        Assert.AreSequenceEqual(Riff, NotesOfMeasureAt(session, 2 * Measure), "A delete in the region would go through every match.");

        session.Tool = EditorTool.Place;
        session.Tools.HoldKeys(EditorModifiers.Control | EditorModifiers.Shift);
        session.Tools.DeleteSelection();
        session.Tools.HoldKeys(EditorModifiers.None);

        Assert.AreSequenceEqual(Riff, NotesOfMeasureAt(session, 0), "Held on with Ctrl + Shift, the Sync tool doesn't delete either.");
        Assert.AreSequenceEqual(Riff, NotesOfMeasureAt(session, 2 * Measure));
    }

    [TestMethod]
    public void NumberKeysDontRemoveNotesWithTheSyncToolActive()
    {
        var session = WithRiffs(0, 2);
        SyncFirstMeasure(session);
        session.Tool = EditorTool.Sync;
        session.MoveCursorTo(480);

        session.Tools.ToggleNoteAtCursor(1);

        Assert.AreSequenceEqual(Riff, NotesOfMeasureAt(session, 0));
        Assert.AreSequenceEqual(Riff, NotesOfMeasureAt(session, 2 * Measure));
    }

    [TestMethod]
    public void TheMiddleButtonTakesTheRegionAwayRatherThanErasingNotes()
    {
        var session = WithRiffs(0, 2);
        SyncFirstMeasure(session);
        session.Tool = EditorTool.Sync;

        session.Tools.PressErase(At(session, 480, 1));

        Assert.IsFalse(session.Sync.IsActive);
        Assert.AreSequenceEqual(Riff, NotesOfMeasureAt(session, 0));
        Assert.AreSequenceEqual(Riff, NotesOfMeasureAt(session, 2 * Measure));
    }

    [TestMethod]
    public void ClearingTheRegionSyncsNothingMore()
    {
        var session = WithRiffs(0, 2);
        SyncFirstMeasure(session);

        session.Sync.Clear();
        Assert.IsTrue(session.Execute(new AddNotes(ChartInstrument.Guitar, ChartDifficulty.Expert, [new EditableNote(1440, 1, 3)])));

        Assert.IsFalse(session.Sync.IsActive);
        Assert.IsEmpty(session.Sync.Matches);
        Assert.AreSequenceEqual(Riff, NotesOfMeasureAt(session, 2 * Measure));
    }

    [TestMethod]
    public void TheRegionGoesWithItsLane()
    {
        var session = WithRiffs(0, 2);
        SyncFirstMeasure(session);

        Assert.IsTrue(session.Execute(new RemovePart(ChartInstrument.Guitar)));

        Assert.IsFalse(session.Sync.IsActive);
    }
}
