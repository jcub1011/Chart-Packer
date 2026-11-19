using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Commands;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charting.Layout;
using ChartShared.Core.Charts;
using static ChartBuilder.Tests.Editing.EditorSessions;

namespace ChartBuilder.Tests.Editing;

/// <summary>
/// The Alignment tool on the vocal parts (charter-spec C3 item 7): every vocal part shifted and stretched in the audio's time, the
/// edge a drag moves snapping to the grid while snap is on, each change one undoable step worked out from the parts as
/// they were. At 480 ticks a beat and 120 BPM a second is 960 ticks, and a 1/16 step 120.
/// </summary>
[TestClass]
public sealed class VocalAlignmentTests
{
    // Vocals: notes at 1 s and 2 s, half a second each, with their lyrics, a phrase over both and star power over the first.
    // Harmony 1: notes at 1 s and 1.5 s.
    private static EditableChart Song()
    {
        var chart = WithVocals(
            [new(960, 480, 60), new(1920, 480, 62)],
            [new(960, "one"), new(1920, "two")],
            [new(960, 1440)],
            starPower: [new(960, 960)]);
        return WithVocals([new(960, 240, 64), new(1440, 240, 65)], [new(960, "a"), new(1440, "b")], track: VocalTrack.Harmony1, chart: chart);
    }

    private static EditorSession Aligning(EditableChart chart)
    {
        var session = Of(chart);
        session.FocusedVocal = VocalTrack.Vocals;
        session.Tool = EditorTool.Align;
        return session;
    }

    private static EditorSession AligningWithSnapOff(EditableChart chart)
    {
        var session = Aligning(chart);
        session.Snap = SnapGrid.Of(16, isOn: false);
        return session;
    }

    private static long[] Ticks(EditorSession session, VocalTrack track = VocalTrack.Vocals) => [.. Vocals(session, track).Notes.Select(static n => n.Tick)];

    [TestMethod]
    public void AShiftMovesEveryPartsNotesLyricsPhrasesAndStarPowerInOneStep()
    {
        var session = Aligning(Song());

        Assert.IsTrue(session.Alignment.Apply(0.5, 1));

        var vocals = Vocals(session);
        Assert.AreSequenceEqual([1440L, 2400L], Ticks(session));
        Assert.AreSequenceEqual([1440L, 2400L], [.. vocals.Lyrics.Select(static l => l.Tick)]);
        Assert.AreEqual(new VocalPhraseEvent(1440, 1440), vocals.Phrases.Single());
        Assert.AreEqual(new VocalStarPowerEvent(1440, 960), vocals.StarPower.Single());
        Assert.AreSequenceEqual([1440L, 1920L], Ticks(session, VocalTrack.Harmony1));
        Assert.AreEqual(1, session.UndoStack.UndoCount);
        Assert.AreEqual(0.5, session.Alignment.Offset);
    }

    [TestMethod]
    public void ASpeedPlaysThePartsFasterFromTheFirstNote()
    {
        var session = Aligning(Song());

        session.Alignment.Apply(0, 2);

        // The second note, a second after the first, is half a second after it; each note is half as long.
        var vocals = Vocals(session);
        Assert.AreSequenceEqual([960L, 1440L], Ticks(session));
        Assert.IsTrue(vocals.Notes.All(static n => n.Length == 240));
        Assert.AreEqual(new VocalPhraseEvent(960, 720), vocals.Phrases.Single());
        Assert.AreEqual((1.0, 1.75), session.Alignment.Span);
    }

    [TestMethod]
    public void TheShiftIsInTheAudiosTimeNotItsTicks()
    {
        // 240 BPM from 1 s: a tick is half as long after it as before it.
        var chart = Song();
        TempoMapEdits.AddMarker(chart, 960).Command!.Apply(chart);
        new SetTempo(new TempoChange(960, 500_000), 250_000).Apply(chart);
        var session = Aligning(chart);

        session.Alignment.Apply(-0.25, 1);

        // 1 s and 1.5 s go to 0.75 s, before the change, and 1.25 s, after it: a shift of ticks would put the second at 1680.
        Assert.AreSequenceEqual([720L, 1440L], Ticks(session));
    }

    [TestMethod]
    public void EachChangeIsWorkedOutFromThePartsAsTheyWere()
    {
        var session = Aligning(Song());

        session.Alignment.Apply(0.013, 1.0371);
        session.Alignment.Apply(0, 1);

        Assert.AreSequenceEqual([960L, 1920L], Ticks(session));
        Assert.AreSequenceEqual([1440L, 2400L], [.. Vocals(session).Notes.Select(static n => n.EndTick)]);
        Assert.AreEqual(2, session.UndoStack.UndoCount);
    }

    [TestMethod]
    public void UndoAndRedoBringBackEachStepsValues()
    {
        var session = Aligning(Song());
        session.Alignment.Apply(0.5, 1);
        session.Alignment.Apply(1, 1.25);

        session.Undo();
        Assert.AreEqual((0.5, 1.0), (session.Alignment.Offset, session.Alignment.Speed));
        session.Undo();
        Assert.AreEqual((0.0, 1.0), (session.Alignment.Offset, session.Alignment.Speed));
        Assert.AreSequenceEqual([960L, 1920L], Ticks(session));
        session.Redo();
        Assert.AreEqual((0.5, 1.0), (session.Alignment.Offset, session.Alignment.Speed));
    }

    [TestMethod]
    public void AnEditMadeAnotherWayIsTakenAsTheStartAgain()
    {
        var session = Aligning(Song());
        session.Alignment.Apply(0.5, 1);

        session.Execute(new ChangeVocals(VocalTrack.Vocals, VocalEdits.Add(Vocals(session), new VocalNoteEvent(3840, 240, 64), "three")));

        Assert.AreEqual((0.0, 1.0), (session.Alignment.Offset, session.Alignment.Speed));
        Assert.AreEqual((1.5, 4.25), session.Alignment.Span);
    }

    [TestMethod]
    public void WithoutAllPartsOnlyThePartInFocusIsAligned()
    {
        var session = Aligning(Song());
        session.Alignment.AllVocalParts = false;

        session.Alignment.Apply(0.5, 1);

        Assert.AreSequenceEqual([1440L, 2400L], Ticks(session));
        Assert.AreSequenceEqual([960L, 1440L], Ticks(session, VocalTrack.Harmony1));
    }

    [TestMethod]
    public void NoNoteIsMovedBeforeTheSongStartsAndTheSpeedIsKeptInBounds()
    {
        var session = Aligning(Song());

        session.Alignment.Apply(-5, 10);

        Assert.AreEqual(-1.0, session.Alignment.Offset);
        Assert.AreEqual(Alignment.MaxSpeed, session.Alignment.Speed);
        Assert.AreEqual(0L, Ticks(session)[0]);
    }

    [TestMethod]
    public void WithSnapOffDraggingTheNotesShiftsThemFreely()
    {
        var session = AligningWithSnapOff(Song());

        DragFromTo(session, VocalAt(1500, 60), VocalAt(1977, 60));

        Assert.AreSequenceEqual([1437L, 2397L], Ticks(session));
        Assert.AreSequenceEqual([1437L, 1917L], Ticks(session, VocalTrack.Harmony1));
        Assert.AreEqual(1, session.UndoStack.UndoCount);
    }

    [TestMethod]
    public void WithSnapOnDraggingTheNotesPutsTheFirstOnTheGrid()
    {
        var session = Aligning(Song());

        // The first note dragged to 1437 goes to the step at 1440.
        DragFromTo(session, VocalAt(1500, 60), VocalAt(1977, 60));

        Assert.AreSequenceEqual([1440L, 2400L], Ticks(session));
        Assert.AreSequenceEqual([1440L, 1920L], Ticks(session, VocalTrack.Harmony1));
        Assert.AreEqual(0.5, session.Alignment.Offset, 1e-9);
        Assert.AreEqual(1, session.UndoStack.UndoCount);
    }

    [TestMethod]
    public void WithSnapOnTheEndsHandleGoesToTheGrid()
    {
        var session = Aligning(Song());

        // The last note's end dragged to 3320 goes to the step at 3360: 3.5 s, as a speed of 0.6.
        DragFromTo(session, VocalAt(2410, null), VocalAt(3330, null));

        Assert.AreEqual(0.6, session.Alignment.Speed, 1e-9);
        Assert.AreSequenceEqual([960L, 2560L], Ticks(session));
        Assert.AreEqual(3360L, Vocals(session).Notes[^1].EndTick);
    }

    [TestMethod]
    public void WithSnapOnTheStartsHandleGoesToTheGrid()
    {
        var session = Aligning(Song());

        // The first note dragged to 1420 goes to the step at 1440: 1.5 s, as a speed of 1.5.
        DragFromTo(session, VocalAt(950, null), VocalAt(1410, null));

        Assert.AreEqual(1.5, session.Alignment.Speed, 1e-9);
        Assert.AreSequenceEqual([1440L, 2080L], Ticks(session));
        Assert.AreEqual(2400L, Vocals(session).Notes[^1].EndTick);
    }

    [TestMethod]
    public void WithShiftADragMovesTheNotesAQuarterAsFar()
    {
        var session = AligningWithSnapOff(Song());

        DragFromTo(session, VocalAt(1500, 60), VocalAt(1980, 60), EditorModifiers.Shift);

        Assert.AreEqual(0.125, session.Alignment.Offset, 1e-9);
        Assert.AreSequenceEqual([1080L, 2040L], Ticks(session));
    }

    [TestMethod]
    public void TheEndsHandleStretchesThePartsWithTheFirstNoteKept()
    {
        var session = AligningWithSnapOff(Song());
        Assert.AreEqual(AlignGrip.End, session.Tools.AlignGripAt(VocalAt(2410, null)));

        // The last note's end, at 2.5 s, dragged to 3.5 s: the first note stays at 1 s, the rest take 2.5 s, not 1.5 s.
        DragFromTo(session, VocalAt(2410, null), VocalAt(3370, null));

        Assert.AreEqual(0.6, session.Alignment.Speed, 1e-9);
        Assert.AreEqual(0.0, session.Alignment.Offset, 1e-9);
        Assert.AreSequenceEqual([960L, 2560L], Ticks(session));
        Assert.AreEqual(3360L, Vocals(session).Notes[^1].EndTick);
    }

    [TestMethod]
    public void TheStartsHandleStretchesThePartsWithTheLastNotesEndKept()
    {
        var session = AligningWithSnapOff(Song());
        Assert.AreEqual(AlignGrip.Start, session.Tools.AlignGripAt(VocalAt(950, null)));

        // The first note, at 1 s, dragged to 1.5 s: the last note's end stays at 2.5 s.
        DragFromTo(session, VocalAt(950, null), VocalAt(1430, null));

        Assert.AreEqual(1.5, session.Alignment.Speed, 1e-9);
        Assert.AreSequenceEqual([1440L, 2080L], Ticks(session));
        Assert.AreEqual(2400L, Vocals(session).Notes[^1].EndTick);
    }

    [TestMethod]
    public void AClickChangesNothing()
    {
        var session = Aligning(Song());

        Click(session, VocalAt(1500, 60));

        Assert.AreEqual(0, session.UndoStack.UndoCount);
        Assert.AreSequenceEqual([960L, 1920L], Ticks(session));
    }

    [TestMethod]
    public void ThePreviewIsTakenBackWhenTheDragIsCancelled()
    {
        var session = Aligning(Song());

        session.Tools.Press(VocalAt(1500, 60));
        session.Tools.Drag(VocalAt(1980, 60));
        Assert.AreSequenceEqual([1440L, 2400L], Ticks(session));
        session.Tools.Cancel();

        Assert.AreSequenceEqual([960L, 1920L], Ticks(session));
        Assert.AreEqual(0.0, session.Alignment.Offset);
        Assert.AreEqual(0, session.UndoStack.UndoCount);
    }

    [TestMethod]
    public void TheMiddleAndRightButtonsDoNothingWithTheTool()
    {
        var session = Aligning(Song());

        session.Tools.PressErase(VocalAt(1000, 60));
        session.Tools.Release(VocalAt(1000, 60));
        session.Tools.PressContext(VocalAt(1000, 60));
        session.Tools.Release(VocalAt(1100, 60));

        Assert.AreEqual(0, session.UndoStack.UndoCount);
        Assert.HasCount(2, Vocals(session).Notes);
    }

    [TestMethod]
    public void TheToolIsPickedOnlyWhileALaneHasFocus()
    {
        var session = new EditorSession(new ChartUndoStack(Song()), default, []);

        session.Tool = EditorTool.Align;
        Assert.AreEqual(EditorTool.Select, session.Tool);
        Assert.IsNotNull(session.Status);

        session.FocusedVocal = VocalTrack.Harmony1;
        session.Tool = EditorTool.Align;
        Assert.AreEqual(EditorTool.Align, session.Tool);

        // With no lane in focus at all, it's put down.
        session.FocusedVocal = null;
        Assert.AreEqual(EditorTool.Select, session.Tool);
    }

    [TestMethod]
    public void AHighwaysLaneTakingFocusKeepsTheToolAndAlignsItsInstrument()
    {
        var chart = Song();
        new AddNotes(ChartInstrument.Guitar, ChartDifficulty.Expert, [new EditableNote(480, 0, 0)]).Apply(chart);
        var session = Aligning(chart);
        Assert.IsTrue(session.Alignment.Aligns(VocalTrack.Vocals));

        session.FocusedVocal = null;

        Assert.AreEqual(EditorTool.Align, session.Tool);
        Assert.IsFalse(session.Alignment.Aligns(VocalTrack.Vocals));
        Assert.IsTrue(session.Alignment.Aligns(GuitarExpert));
    }
}
