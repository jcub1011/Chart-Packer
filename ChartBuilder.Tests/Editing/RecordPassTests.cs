using ChartPacker.Core.Charting;
using ChartPacker.Core.Charting.Commands;
using ChartPacker.Core.Charting.Editing;
using ChartPacker.Core.Charts;
using static ChartBuilder.Tests.Charting.CommandChecks;
using static ChartBuilder.Tests.Editing.EditorSessions;

namespace ChartBuilder.Tests.Editing;

/// <summary>
/// Record mode (charter-spec C15 item 5): presses at audio times, quantised, held for sustains, added as one step. At 120
/// BPM and 480 ticks a beat, a beat is half a second.
/// </summary>
[TestClass]
public sealed class RecordPassTests
{
    [TestMethod]
    public void APressGoesOnTheNearestGridStepAndThePassIsOneStep()
    {
        var session = Of(Guitar());
        var start = session.Chart.Clone();
        var pass = session.StartRecording(GuitarExpert, RecordMode.Merge, 0);

        pass.Press(0, 0.51);
        pass.Release(0, 0.53);
        pass.Press(2, 1.24);
        pass.Release(2, 1.26);

        Assert.AreEqual(0, session.UndoStack.UndoCount);
        Assert.HasCount(2, session.GhostTrack(GuitarExpert)!.Notes);
        Assert.IsTrue(pass.Finish(2));
        Assert.AreSequenceEqual([(480L, 0, 1L), (1200L, 2, 1L)], Expert(session).Notes.Select(n => (n.Tick, n.Lane, n.Length)).ToList());
        Assert.IsNull(session.Recording);
        session.Undo();
        AssertSame(start, session.Chart, "after one undo");
    }

    [TestMethod]
    public void HoldingAKeyHoldsASustainToTheGrid()
    {
        var session = Of(Guitar());
        var pass = session.StartRecording(GuitarExpert, RecordMode.Merge, 0);

        pass.Press(1, 0.5);
        pass.Release(1, 1.49);

        pass.Finish(2);
        Assert.AreEqual(960, Expert(session).Notes.Single().Length);
    }

    [TestMethod]
    public void AKeyHeldDownAndRepeatingIsPressedOnce()
    {
        var session = Of(Guitar());
        var pass = session.StartRecording(GuitarExpert, RecordMode.Merge, 0);

        Assert.IsTrue(pass.Press(1, 0.5));
        Assert.IsFalse(pass.Press(1, 0.6));
        Assert.IsFalse(pass.Press(1, 0.75));
        pass.Release(1, 0.8);

        Assert.AreEqual(1, pass.Count);
    }

    [TestMethod]
    public void KeysPressedTogetherMakeAChordAndAFretTakesThePlaceOfAnOpen()
    {
        var session = Of(Guitar());
        var pass = session.StartRecording(GuitarExpert, RecordMode.Merge, 0);

        pass.Press(ChartNote.OpenLane, 0.5);
        pass.Press(0, 0.505);
        pass.Press(3, 0.51);
        pass.ReleaseAll(0.52);

        pass.Finish(1);
        Assert.AreSequenceEqual([0, 3], Expert(session).Notes.Select(n => n.Lane).ToList());
    }

    [TestMethod]
    public void OverwritingClearsTheStretchPlayedAndMergingKeepsWhatsThere()
    {
        var overwrite = Of(Guitar());
        var merge = Of(Guitar());
        foreach (var session in new[] { overwrite, merge })
            new AddNotes(ChartInstrument.Guitar, ChartDifficulty.Expert, [new(240, 1, 4), new(960, 1, 4), new(4800, 1, 4)]).Apply(session.Chart);

        var a = overwrite.StartRecording(GuitarExpert, RecordMode.Overwrite, 0);
        a.Press(0, 0.5);
        a.Finish(2);
        var b = merge.StartRecording(GuitarExpert, RecordMode.Merge, 0);
        b.Press(0, 0.5);
        b.Finish(2);

        Assert.AreSequenceEqual([480L, 4800L], Expert(overwrite).Notes.Select(n => n.Tick).ToList());
        Assert.AreSequenceEqual([240L, 480L, 960L, 4800L], Expert(merge).Notes.Select(n => n.Tick).ToList());
    }

    [TestMethod]
    public void OverwritingAPunchRangeClearsItAndLeavesOutPressesOutsideIt()
    {
        var session = Of(Guitar());
        new AddNotes(ChartInstrument.Guitar, ChartDifficulty.Expert, [new(240, 1, 4), new(960, 1, 4), new(2400, 1, 4)]).Apply(session.Chart);
        var pass = session.StartRecording(GuitarExpert, RecordMode.Overwrite, 0, punch: (480, 1440));

        pass.Press(0, 0.25);
        pass.Press(1, 0.75);
        pass.Finish(3);

        Assert.AreSequenceEqual([(240L, 4), (720L, 1), (2400L, 4)], Expert(session).Notes.Select(n => (n.Tick, n.Lane)).ToList());
    }

    [TestMethod]
    public void SnapToOnsetIsUsed()
    {
        var session = Of(Guitar());
        session.Onsets = new FixedOnsets([0.53]);
        session.SnapToOnset = true;
        var pass = session.StartRecording(GuitarExpert, RecordMode.Merge, 0);

        pass.Press(0, 0.51);
        pass.Finish(1);

        // 0.53 s is tick 508.8, an onset within half a 1/16 step of the press.
        Assert.AreEqual(509, Expert(session).Notes.Single().Tick);
    }

    [TestMethod]
    public void DrumsLeaveOutHoldsAndTakeTheTwoTimesKickOnExpert()
    {
        var session = Of(Drums(), focused: DrumsExpert);
        var pass = session.StartRecording(DrumsExpert, RecordMode.Merge, 0);

        pass.Press(1, 0.5);
        pass.Press(0, 1.0, doubleKick: true);
        pass.Release(1, 1.5);

        pass.Finish(2);
        var notes = session.TrackOf(DrumsExpert)!.Notes;
        Assert.AreSequenceEqual([(480L, 1, 1L, false), (960L, 0, 1L, true)], notes.Select(n => (n.Tick, n.Lane, n.Length, n.DoubleKick)).ToList());
    }

    [TestMethod]
    public void MergingDrumsKeepsTheOtherPadsOnATick()
    {
        var session = Of(Drums(), focused: DrumsExpert);
        new AddNotes(ChartInstrument.Drums, ChartDifficulty.Expert, [new(480, 1, 1)]).Apply(session.Chart);
        var pass = session.StartRecording(DrumsExpert, RecordMode.Merge, 0);

        pass.Press(0, 0.5);
        pass.Finish(1);

        Assert.AreSequenceEqual([0, 1], session.TrackOf(DrumsExpert)!.Notes.Select(n => n.Lane).ToList());
    }

    [TestMethod]
    public void CancellingAddsNothing()
    {
        var session = Of(Guitar());
        var pass = session.StartRecording(GuitarExpert, RecordMode.Merge, 0);
        pass.Press(0, 0.5);

        pass.Cancel();

        Assert.IsNull(session.Recording);
        Assert.IsFalse(session.UndoStack.CanUndo);
        Assert.IsEmpty(Expert(session).Notes);
    }

    [TestMethod]
    public void APassAtHalfSpeedLandsWhereItWouldAtFullSpeed()
    {
        // The press time is song time already, whatever the speed: SmoothPosition − latency × speed (§4.4.1).
        var full = Of(Guitar());
        var half = Of(Guitar());
        var a = full.StartRecording(GuitarExpert, RecordMode.Merge, 0);
        var b = half.StartRecording(GuitarExpert, RecordMode.Merge, 0);

        a.Press(0, 0.505);
        b.Press(0, 0.505);
        a.Finish(1);
        b.Finish(1);

        Assert.AreEqual(Expert(full).Notes.Single(), Expert(half).Notes.Single());
    }

    private sealed class FixedOnsets(IReadOnlyList<double> seconds) : IOnsetSource
    {
        public IReadOnlyList<double> OnsetsFor(ChartTrackKey key) => seconds;
    }
}
