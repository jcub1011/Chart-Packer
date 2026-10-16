using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Commands;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charts;
using static ChartBuilder.Tests.Editing.EditorSessions;

namespace ChartBuilder.Tests.Editing;

/// <summary>Snap to onset and Nudge to onset (charter-spec C14 items 2 and 3), at 480 ticks per beat and 120 BPM.</summary>
[TestClass]
public sealed class OnsetSnapTests
{
    // At 120 BPM a beat is half a second: 480 ticks, a tick a little over a millisecond.
    private const double TicksPerSecond = 960;

    /// <summary>AC C14.3: a note placed 15 ms from an onset at a 1/16 grid at 120 BPM lands on the onset.</summary>
    [TestMethod]
    public void ANotePlacedNearAnOnsetLandsOnIt()
    {
        var session = OnsetSession([1.020]);
        session.Tool = EditorTool.Place;

        Click(session, At(session, (1.020 + 0.015) * TicksPerSecond, 1));

        Assert.AreEqual(979, Expert(session).Notes.Single().Tick, "1.020 s is tick 979.2: the onset, not the grid's 960.");
    }

    [TestMethod]
    public void TheGridWinsWhenNoOnsetIsWithinHalfAStep()
    {
        // Half a 1/16 step at 120 BPM is 62.5 ms.
        var session = OnsetSession([1.100]);
        session.Tool = EditorTool.Place;

        Click(session, At(session, 1.005 * TicksPerSecond, 1));

        Assert.AreEqual(960, Expert(session).Notes.Single().Tick);
    }

    [TestMethod]
    public void WithSnapToOnsetOffNotesGoToTheGrid()
    {
        var session = OnsetSession([1.020]);
        session.SnapToOnset = false;
        session.Tool = EditorTool.Place;

        Click(session, At(session, 1.035 * TicksPerSecond, 1));

        Assert.AreEqual(960, Expert(session).Notes.Single().Tick);
    }

    [TestMethod]
    public void OnsetsAreInAudioTimeSoTheDelayIsTakenOff()
    {
        // Audio is chart plus 0.25 s: an onset at 1.270 s of audio is 1.020 s into the chart.
        var session = OnsetSession([1.270], TimeSpan.FromSeconds(0.25));
        session.Tool = EditorTool.Place;

        Click(session, At(session, 1.035 * TicksPerSecond, 1));

        Assert.AreEqual(979, Expert(session).Notes.Single().Tick);
    }

    [TestMethod]
    public void AMovedChordsGrabbedNoteLandsOnTheOnsetAndTheOthersKeepTheirPlaces()
    {
        var session = OnsetSession([2.010]);
        session.UndoStack.Execute(new AddNotes(ChartInstrument.Guitar, ChartDifficulty.Expert, [Note(960, 1), Note(1200, 2)]));
        session.Tool = EditorTool.Select;
        session.Selection.Select(GuitarExpert, Expert(session).Notes);

        DragFromTo(session, At(session, 960, 1), At(session, 1920, 1));

        Assert.AreSequenceEqual(new long[] { 1930, 2170 }, [.. Expert(session).Notes.Select(n => n.Tick)], "2.010 s is tick 1929.6.");
    }

    [TestMethod]
    public void TheReachIsHalfTheGridStepInTime()
    {
        var map = new TempoMap(480, [new TempoChange(0, 500_000)], [new TimeSignature(0, 4, 4)]);

        Assert.AreEqual(0.0625, OnsetSnap.WindowSeconds(960, SnapGrid.Of(16), map), 1e-9);
        Assert.AreEqual(0.25, OnsetSnap.WindowSeconds(960, SnapGrid.Of(4), map), 1e-9);
        Assert.AreEqual(0.0625, OnsetSnap.WindowSeconds(960, SnapGrid.Of(16, isOn: false), map), 1e-9, "With snap off, the reach stays the snap's.");
    }

    [TestMethod]
    public void NudgingMovesEachChordToItsOwnOnset()
    {
        var session = OnsetSession([0.505, 0.990, 2.000]);
        session.UndoStack.Execute(new AddNotes(ChartInstrument.Guitar, ChartDifficulty.Expert, [Note(480, 1), Note(480, 2), Note(960, 1), Note(1440, 3)]));
        session.UndoStack.Execute(new SetChordFlags(ChartInstrument.Guitar, ChartDifficulty.Expert, [480], ChordFlags.ForceHopo, on: true));
        session.Selection.Select(GuitarExpert, Expert(session).Notes);
        int steps = session.UndoStack.UndoCount;

        var plan = session.Tools.NudgeSelectionToOnsets()!;

        Assert.AreSequenceEqual(new long[] { 485, 485, 950, 1440 }, [.. Expert(session).Notes.Select(n => n.Tick)]);
        Assert.HasCount(2, plan.Moved);
        Assert.AreEqual(5.2, plan.Moved[0].Milliseconds, 0.1, "0.505 s is tick 484.8: 5 ticks later.");
        Assert.AreEqual(-10.4, plan.Moved[1].Milliseconds, 0.1);
        Assert.AreSequenceEqual(new long[] { 1440 }, [.. plan.NoOnset], "1.5 s has no onset within 62.5 ms, so it stays.");
        Assert.AreEqual(ChordFlags.ForceHopo, Expert(session).FlagsAt(485), "The chord's forcing moves with it.");
        Assert.AreEqual(ChordFlags.None, Expert(session).FlagsAt(480));
        Assert.HasCount(4, session.Selection.Notes, "The notes stay selected where they land.");
        Assert.AreEqual(steps + 1, session.UndoStack.UndoCount, "One undoable step.");

        session.Undo();
        Assert.AreSequenceEqual(new long[] { 480, 480, 960, 1440 }, [.. Expert(session).Notes.Select(n => n.Tick)]);
        Assert.AreEqual(ChordFlags.ForceHopo, Expert(session).FlagsAt(480));
    }

    [TestMethod]
    public void OfTwoChordsAfterOneOnsetTheNearerHasIt()
    {
        var session = OnsetSession([0.250]);
        session.Snap = SnapGrid.Of(4);
        session.UndoStack.Execute(new AddNotes(ChartInstrument.Guitar, ChartDifficulty.Expert, [Note(200, 1), Note(300, 1)]));
        session.Selection.Select(GuitarExpert, Expert(session).Notes);

        var plan = session.Tools.NudgeSelectionToOnsets()!;

        Assert.AreSequenceEqual(new long[] { 240, 300 }, [.. Expert(session).Notes.Select(n => n.Tick)]);
        Assert.AreSequenceEqual(new long[] { 300 }, [.. plan.Blocked]);
    }

    [TestMethod]
    public void AChordCantLandOnANoteThatIsntMoving()
    {
        var session = OnsetSession([0.505]);
        session.UndoStack.Execute(new AddNotes(ChartInstrument.Guitar, ChartDifficulty.Expert, [Note(480, 1), Note(485, 1)]));
        session.Selection.Select(GuitarExpert, [Expert(session).Notes[0]]);

        var plan = session.Tools.NudgeSelectionToOnsets()!;

        Assert.IsNull(plan.Command);
        Assert.AreSequenceEqual(new long[] { 480 }, [.. plan.Blocked]);
        Assert.AreSequenceEqual(new long[] { 480, 485 }, [.. Expert(session).Notes.Select(n => n.Tick)]);
    }

    [TestMethod]
    public void NudgingKeepsSustains()
    {
        var session = OnsetSession([0.990]);
        session.UndoStack.Execute(new AddNotes(ChartInstrument.Guitar, ChartDifficulty.Expert, [new EditableNote(960, 240, 1)]));
        session.Selection.Select(GuitarExpert, Expert(session).Notes);

        session.Tools.NudgeSelectionToOnsets();

        Assert.AreEqual(new EditableNote(950, 240, 1), Expert(session).Notes.Single());
    }

    private static EditorSession OnsetSession(double[] onsets, TimeSpan delay = default)
    {
        var session = Of(Guitar(), delay);
        session.Snap = SnapGrid.Of(16);
        session.Onsets = new FixedOnsets(onsets);
        session.SnapToOnset = true;
        return session;
    }

    private static EditableNote Note(long tick, int lane) => new(tick, EditableNote.PlainLength, lane);

    private sealed class FixedOnsets(double[] onsets) : IOnsetSource
    {
        public IReadOnlyList<double> OnsetsFor(ChartTrackKey key) => onsets;
    }
}
