using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charts;
using static ChartBuilder.Tests.Editing.EditorSessions;

namespace ChartBuilder.Tests.Editing;

/// <summary>
/// The Loop tool (charter-spec C10 item 2): a region of the song the Loop button plays over and over. It's drawn, moved
/// and resized in the lanes with the tool picked or L held, and cleared by its X or the middle button. The region is
/// playback's, in seconds of audio, which <see cref="FakeLoop"/> stands in for here.
/// </summary>
[TestClass]
public sealed class LoopToolTests
{
    /// <summary>A measure's ticks, and its seconds at 480 ticks per beat and 120 BPM.</summary>
    private const long Measure = 1920;
    private const double MeasureSeconds = 2;

    /// <summary>How close the region's times must be to what the grid says, as they're worked out through the tempo map.</summary>
    private const double Tolerance = 1e-9;

    /// <summary>The loop region as a session's tools see it, in seconds of audio, with no audio to play it.</summary>
    private sealed class FakeLoop : ILoopSpan
    {
        public (double Start, double End)? Span { get; set; }

        public int Defines { get; private set; }

        public int Clears { get; private set; }

        public void DragTo(double start, double end) => Span = (start, end);

        public void Define(double start, double end)
        {
            Span = (start, end);
            Defines++;
        }

        public void Clear()
        {
            Span = null;
            Clears++;
        }
    }

    /// <summary>A guitar session with no loop region drawn yet.</summary>
    private static (EditorSession Session, FakeLoop Loop) WithoutLoop()
    {
        var session = Of(Guitar());
        var loop = new FakeLoop();
        session.Loop = loop;
        return (session, loop);
    }

    /// <summary>A guitar session with a loop region drawn, in seconds of audio.</summary>
    private static (EditorSession Session, FakeLoop Loop) WithLoop(double start, double end)
    {
        var (session, loop) = WithoutLoop();
        loop.Span = (start, end);
        return (session, loop);
    }

    private static void AssertSpan(FakeLoop loop, double start, double end)
    {
        Assert.IsNotNull(loop.Span, $"Expected the loop region from {start} to {end}, but there's none.");
        var (actualStart, actualEnd) = loop.Span.Value;
        Assert.IsTrue(
            Math.Abs(actualStart - start) < Tolerance && Math.Abs(actualEnd - end) < Tolerance,
            $"Expected the loop region from {start} to {end}, but it's from {actualStart} to {actualEnd}.");
    }

    [TestMethod]
    public void DrawingWithTheLoopToolSetsTheRegionOnTheGridAndGoesBackToTheToolBefore()
    {
        var (session, loop) = WithoutLoop();
        session.Tool = EditorTool.Place;
        session.Tool = EditorTool.Loop;

        DragFromTo(session, At(session, 10, 3), At(session, Measure - 10, 3));

        AssertSpan(loop, 0, MeasureSeconds);
        Assert.AreEqual(1, loop.Defines);
        Assert.AreEqual(EditorTool.Place, session.Tool, "A region drawn goes back to the tool picked before.");
        Assert.AreEqual(0, session.UndoStack.UndoCount, "The loop region isn't part of the chart, so it's not a step to undo.");
    }

    [TestMethod]
    public void DrawingShorterThanA32ndNoteGivesA32ndNote()
    {
        var (session, loop) = WithoutLoop();
        session.Snap = SnapGrid.Of(64);
        session.Tool = EditorTool.Loop;

        DragFromTo(session, At(session, 0, 0), At(session, 30, 0));

        AssertSpan(loop, 0, 60 * 0.5 / 480);
    }

    [TestMethod]
    public void AClickWithTheLoopToolDrawsNothing()
    {
        var (session, loop) = WithoutLoop();
        session.Tool = EditorTool.Loop;

        Click(session, At(session, 480, 1));

        Assert.IsNull(loop.Span);
        Assert.AreEqual(0, loop.Defines);
        Assert.AreEqual(EditorTool.Loop, session.Tool, "A click draws nothing, so there's no region to go back from.");
    }

    [TestMethod]
    public void TheEdgeIsDraggedToResizeTheRegion()
    {
        var (session, loop) = WithLoop(0.5, 1.5);
        session.Tool = EditorTool.Loop;

        DragFromTo(session, At(session, 480, 1), At(session, 360, 1));

        AssertSpan(loop, 0.375, 1.5);
        Assert.AreEqual(1, loop.Defines);
        Assert.AreEqual(EditorTool.Loop, session.Tool, "Resizing a region keeps the tool picked.");
    }

    [TestMethod]
    public void TheBodyIsDraggedToMoveTheRegionKeepingItsLength()
    {
        var (session, loop) = WithLoop(0.5, 1.5);
        session.Tool = EditorTool.Loop;

        DragFromTo(session, At(session, 960, 1), At(session, 1200, 1));

        AssertSpan(loop, 0.75, 1.75);
        Assert.AreEqual(1, loop.Defines);
    }

    [TestMethod]
    public void AClickOnTheBodyOnlyPicksTheRegionUp()
    {
        var (session, loop) = WithLoop(0.5, 1.5);
        session.Tool = EditorTool.Loop;

        Click(session, At(session, 960, 1));

        AssertSpan(loop, 0.5, 1.5);
        Assert.AreEqual(0, loop.Defines);
    }

    [TestMethod]
    public void TheEndsCantCrossOrLeaveLessThanA32ndNote()
    {
        var (session, loop) = WithLoop(0, MeasureSeconds);
        session.Tool = EditorTool.Loop;

        DragFromTo(session, At(session, Measure, 1), At(session, -400, 1));
        AssertSpan(loop, 0, 60 * 0.5 / 480);

        DragFromTo(session, At(session, 0, 1), At(session, -400, 1));
        AssertSpan(loop, 0, 60 * 0.5 / 480);
    }

    [TestMethod]
    public void AHeldLoopToolDrawsTheRegionWhateverToolIsPicked()
    {
        var (session, loop) = WithoutLoop();
        session.Tool = EditorTool.Place;
        session.LoopToolHeld = true;

        DragFromTo(session, At(session, 10, 1), At(session, Measure - 10, 1));

        AssertSpan(loop, 0, MeasureSeconds);
        Assert.IsEmpty(Expert(session).Notes, "The place tool's press is held off while L holds the Loop tool on.");
        Assert.AreEqual(EditorTool.Place, session.Tool, "L holds the Loop tool on only while it's held.");
    }

    [TestMethod]
    public void AHeldLoopToolDoesNothingInThePhraseRail()
    {
        var (session, loop) = WithLoop(0.5, 1.5);
        session.Tool = EditorTool.Phrase;
        session.LoopToolHeld = true;

        DragFromTo(session, RailAt(session, 0, PhraseKind.StarPower), RailAt(session, Measure, PhraseKind.StarPower));

        AssertSpan(loop, 0.5, 1.5);
        Assert.AreEqual(0, session.UndoStack.UndoCount, "The rail isn't the loop region's business.");
    }

    [TestMethod]
    public void TheMiddleButtonClearsTheRegionAndErasesNothing()
    {
        var (session, loop) = WithLoop(0.5, 1.5);
        session.Tool = EditorTool.Loop;
        EditableNote there = new(960, 1, 1);
        Expert(session).Notes.Add(there);

        session.Tools.PressErase(At(session, 960, 1));

        Assert.IsNull(loop.Span);
        Assert.AreEqual(1, loop.Clears);
        Assert.AreSequenceEqual([there], Expert(session).Notes.ToList());
    }

    [TestMethod]
    public void DrawingTheRegionTouchesItSoATapOfLCantToggleRepeating()
    {
        var (session, _) = WithoutLoop();
        session.Tool = EditorTool.Loop;

        DragFromTo(session, At(session, 10, 1), At(session, Measure - 10, 1));

        Assert.IsTrue(session.LoopTouched);
    }

    [TestMethod]
    public void AClickOrATakenBackDragTouchesNothing()
    {
        var (session, _) = WithoutLoop();
        session.Tool = EditorTool.Loop;

        Click(session, At(session, 480, 1));
        Assert.IsFalse(session.LoopTouched, "A click draws nothing, so it's no use of the region.");

        session.Tools.Press(At(session, 0, 1));
        session.Tools.Drag(At(session, Measure, 1));
        session.Tools.Cancel();
        Assert.IsFalse(session.LoopTouched, "A drag taken back leaves the region as it was.");
    }

    [TestMethod]
    public void TheMiddleButtonNotesThatTheRegionWasCleared()
    {
        var (session, _) = WithLoop(0.5, 1.5);
        session.Tool = EditorTool.Loop;

        session.Tools.PressErase(At(session, 960, 1));

        Assert.IsTrue(session.LoopTouched);
    }

    [TestMethod]
    public void HoldingAndLettingGoOfLStartAPressAfresh()
    {
        var (session, _) = WithLoop(0.5, 1.5);
        session.Tool = EditorTool.Loop;
        DragFromTo(session, At(session, 240, 1), At(session, 480, 1));
        Assert.IsTrue(session.LoopTouched);

        session.LoopToolHeld = true;
        Assert.IsFalse(session.LoopTouched, "A press of L starts with the region untouched.");

        session.Tools.PressErase(At(session, 960, 1));
        Assert.IsTrue(session.LoopTouched);

        session.LoopToolHeld = false;
        Assert.IsFalse(session.LoopTouched);
    }

    [TestMethod]
    public void AnotherToolsPressLeavesTheRegionAlone()
    {
        var (session, loop) = WithLoop(0.5, 1.5);
        session.Tool = EditorTool.Place;

        DragFromTo(session, At(session, 10, 1), At(session, 300, 1));

        AssertSpan(loop, 0.5, 1.5);
        Assert.AreEqual(0, loop.Defines);
        Assert.AreEqual(0, loop.Clears);
        Assert.IsNotEmpty(Expert(session).Notes);
    }

    [TestMethod]
    public void EscDuringADragPutsTheRegionBack()
    {
        var (session, loop) = WithLoop(0.5, 1.5);
        session.Tool = EditorTool.Loop;

        session.Tools.Press(At(session, 480, 1));
        session.Tools.Drag(At(session, 240, 1));
        AssertSpan(loop, 0.25, 1.5);
        Assert.AreEqual(0, loop.Defines, "The drag only previews where the region lands.");
        session.Tools.Cancel();

        AssertSpan(loop, 0.5, 1.5);
        Assert.AreEqual(0, loop.Defines);
    }

    [TestMethod]
    public void EscDuringADrawLeavesNoRegionBehind()
    {
        var (session, loop) = WithoutLoop();
        session.Tool = EditorTool.Loop;

        session.Tools.Press(At(session, 0, 1));
        session.Tools.Drag(At(session, Measure, 1));
        Assert.IsNotNull(loop.Span);
        session.Tools.Cancel();

        Assert.IsNull(loop.Span);
        Assert.AreEqual(0, loop.Defines);
    }

    [TestMethod]
    public void AVocalLanesPressDrawsTheRegionToo()
    {
        var session = Of(WithVocals([new VocalNoteEvent(0, 480, 60)]));
        var loop = new FakeLoop();
        session.Loop = loop;
        session.Tool = EditorTool.Loop;

        DragFromTo(session, VocalAt(10, 60), VocalAt(Measure - 10, 60));

        AssertSpan(loop, 0, MeasureSeconds);
        Assert.AreEqual(EditorTool.Select, session.Tool, "A region drawn goes back to the tool picked before.");
    }

    [TestMethod]
    public void AVocalLanesPressOutsideItsRowsOrInThePhraseStripDrawsNothing()
    {
        var session = Of(WithVocals([new VocalNoteEvent(0, 480, 60)]));
        var loop = new FakeLoop();
        session.Loop = loop;
        session.Tool = EditorTool.Loop;

        DragFromTo(session, VocalAt(10, null), VocalAt(Measure - 10, null));
        DragFromTo(session, PhraseStripAt(10), PhraseStripAt(Measure - 10));

        Assert.IsNull(loop.Span, "Only the lane's rows are the loop region's business, as in a highway's lane.");
        Assert.AreEqual(0, loop.Defines);
        Assert.AreEqual(EditorTool.Loop, session.Tool, "Nothing was drawn, so there's no region to go back from.");
    }

    [TestMethod]
    public void TheLoopToolDeletesNothing()
    {
        var (session, loop) = WithLoop(0.5, 1.5);
        session.Tool = EditorTool.Loop;
        EditableNote there = new(960, 1, 1);
        Expert(session).Notes.Add(there);
        session.Selection.Select(GuitarExpert, [there]);

        session.Tools.DeleteSelection();

        Assert.AreSequenceEqual([there], Expert(session).Notes.ToList());
        Assert.IsNotNull(session.Status);
    }
}
