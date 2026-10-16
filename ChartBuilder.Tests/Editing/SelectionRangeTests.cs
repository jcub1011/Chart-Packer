using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Commands;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charts;
using static ChartBuilder.Tests.Charting.CommandChecks;
using static ChartBuilder.Tests.Editing.EditorSessions;

namespace ChartBuilder.Tests.Editing;

/// <summary>Selecting across lanes (charter-spec C15 item 2): a time range, a whole lane and a section.</summary>
[TestClass]
public sealed class SelectionRangeTests
{
    private static readonly ChartTrackKey GuitarHard = new(ChartInstrument.Guitar, ChartDifficulty.Hard);

    [TestMethod]
    public void ARangeAcrossLanesHoldsEachTracksNotesInsideIt()
    {
        var session = TwoDifficulties();

        session.Selection.SelectRange(480, 960, [GuitarExpert, GuitarHard]);

        var tracks = session.Selection.NotesByTrack();
        Assert.AreSequenceEqual([GuitarExpert, GuitarHard], tracks.Select(t => t.Track).ToList());
        Assert.AreSequenceEqual([480L, 960L], tracks[0].Notes.Select(n => n.Tick).ToList());
        Assert.AreSequenceEqual([480L], tracks[1].Notes.Select(n => n.Tick).ToList());
        Assert.IsTrue(session.Selection.HasRangeAcrossLanes);
        Assert.IsNull(session.Selection.Track);
    }

    [TestMethod]
    public void SelectingNotesDropsTheRange()
    {
        var session = TwoDifficulties();
        session.Selection.SelectRange(480, 960, [GuitarExpert, GuitarHard]);

        session.Selection.Select(GuitarExpert, [Expert(session).Notes[0]]);

        Assert.IsFalse(session.Selection.HasRangeAcrossLanes);
        Assert.IsEmpty(session.Selection.RangeTracks);
    }

    [TestMethod]
    public void ReconcilingDropsAPartThatsGone()
    {
        var chart = Guitar();
        new AddPart(ChartInstrument.Bass, null).Apply(chart);
        var session = Of(chart);
        var bass = new ChartTrackKey(ChartInstrument.Bass, ChartDifficulty.Expert);
        session.Selection.SelectRange(0, 960, [GuitarExpert, bass]);

        session.Execute(new RemovePart(ChartInstrument.Bass));

        Assert.AreSequenceEqual([GuitarExpert], session.Selection.RangeTracks.ToList());
    }

    [TestMethod]
    public void DeletingARangeRemovesFromEveryTrackInOneStep()
    {
        var session = TwoDifficulties();
        var start = session.Chart.Clone();
        session.Selection.SelectRange(480, 960, [GuitarExpert, GuitarHard]);

        session.Tools.DeleteSelection();

        Assert.AreSequenceEqual([0L, 1440L], Expert(session).Notes.Select(n => n.Tick).ToList());
        Assert.AreSequenceEqual([0L], session.Chart.PartOf(ChartInstrument.Guitar)![ChartDifficulty.Hard].Notes.Select(n => n.Tick).ToList());
        session.Undo();
        AssertSame(start, session.Chart, "after one undo");
    }

    [TestMethod]
    public void MovingARangeMovesEveryTracksNotesAndTheRange()
    {
        var session = TwoDifficulties();
        session.Snap = SnapGrid.Of(4);
        session.Selection.SelectRange(480, 960, [GuitarExpert, GuitarHard]);

        session.Tools.MoveSelection(1, 0, session.SlotsOf(ChartInstrument.Guitar, LaneView.Highway));

        Assert.AreSequenceEqual([0L, 960L, 1440L, 1440L], Expert(session).Notes.Select(n => n.Tick).ToList());
        Assert.AreSequenceEqual([0L, 960L], session.Chart.PartOf(ChartInstrument.Guitar)![ChartDifficulty.Hard].Notes.Select(n => n.Tick).ToList());
        Assert.AreEqual((960L, 1440L), session.Selection.Range);
    }

    [TestMethod]
    public void AltDragSelectsARangeAcrossTheLanesShown()
    {
        var session = TwoDifficulties();
        session.VisibleTracks = () => [GuitarExpert, GuitarHard];

        DragFromTo(session, At(session, 470, 0), At(session, 970, 0), EditorModifiers.Alt);

        Assert.AreEqual((480L, 960L), session.Selection.Range);
        Assert.AreSequenceEqual([GuitarExpert, GuitarHard], session.Selection.RangeTracks.ToList());
    }

    [TestMethod]
    public void SelectingTheSectionAtTheCursorCoversItToTheNextOne()
    {
        var session = TwoDifficulties();
        session.VisibleTracks = () => [GuitarExpert];
        new AddTextEvent(null, TextEvent.Of(0, "[section intro]")).Apply(session.Chart);
        new AddTextEvent(null, TextEvent.Of(960, "[section verse]")).Apply(session.Chart);
        session.MoveCursorTo(480);

        session.Tools.SelectSectionAtCursor();

        Assert.AreEqual((0L, 959L), session.Selection.Range);
        session.MoveCursorTo(1440);
        session.Tools.SelectSectionAtCursor();
        Assert.AreEqual((960L, 1441L), session.Selection.Range);
    }

    [TestMethod]
    public void SelectingALaneSelectsEveryNote()
    {
        var session = TwoDifficulties();

        session.Tools.SelectLane(GuitarHard);

        Assert.AreEqual(GuitarHard, session.Selection.Track);
        Assert.HasCount(2, session.Selection.Notes);
    }

    // Expert: 0, 480, 960 and 1440; Hard: 0 and 480.
    private static EditorSession TwoDifficulties()
    {
        var chart = Guitar();
        new AddNotes(ChartInstrument.Guitar, ChartDifficulty.Expert, [new(0, 1, 0), new(480, 1, 1), new(960, 1, 2), new(1440, 1, 3)]).Apply(chart);
        new AddNotes(ChartInstrument.Guitar, ChartDifficulty.Hard, [new(0, 1, 0), new(480, 1, 1)]).Apply(chart);
        return Of(chart);
    }
}
