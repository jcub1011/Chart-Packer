using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Commands;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charts;
using static ChartBuilder.Tests.Charting.CommandChecks;
using static ChartBuilder.Tests.Editing.EditorSessions;

namespace ChartBuilder.Tests.Editing;

/// <summary>Cut, copy and paste (charter-spec C15 item 3).</summary>
[TestClass]
public sealed class ClipboardTests
{
    private static readonly ChartTrackKey GuitarHard = new(ChartInstrument.Guitar, ChartDifficulty.Hard);

    [TestMethod]
    public void APastedPassageKeepsItsTimingFromTheCursor()
    {
        var session = Session();
        Select(session, 480, 960);
        var clip = ClipboardEdits.Copy(session)!;

        var paste = ClipboardEdits.Paste(session, clip, 3840, PasteMode.Overwrite);
        Assert.IsTrue(session.Execute(paste.Command!));

        Assert.AreSequenceEqual([(3840L, 1), (4320L, 2)], Expert(session).Notes.Where(n => n.Tick >= 3840).Select(n => (n.Tick, n.Lane)).ToList());
        Assert.AreSequenceEqual([3840L, 4320L], paste.Placed.Single().Notes.Select(n => n.Tick).ToList());
    }

    [TestMethod]
    public void TicksAreScaledToTheResolutionPastedInto()
    {
        var session = Session();
        Select(session, 480, 960);
        var clip = ClipboardEdits.Copy(session)! with { Resolution = 192 };

        var paste = ClipboardEdits.Paste(session, clip, 0, PasteMode.Merge);

        // 0 and 480 ticks at 192 to a beat are 0 and 1200 at 480.
        Assert.AreSequenceEqual([0L, 1200L], paste.Placed.Single().Notes.Select(n => n.Tick).ToList());
    }

    [TestMethod]
    public void OverwritingTakesOutWhatsInTheSpanAndMergingKeepsIt()
    {
        var overwrite = Session();
        var merge = Session();
        foreach (var session in new[] { overwrite, merge })
            Select(session, 0, 0);
        var clip = ClipboardEdits.Copy(overwrite)! with { Length = 960 };

        overwrite.Execute(ClipboardEdits.Paste(overwrite, clip, 480, PasteMode.Overwrite).Command!);
        merge.Execute(ClipboardEdits.Paste(merge, clip, 480, PasteMode.Merge).Command!);

        Assert.AreSequenceEqual([(0L, 0), (480L, 0), (1920L, 3)], Expert(overwrite).Notes.Select(n => (n.Tick, n.Lane)).ToList());
        Assert.AreSequenceEqual([(0L, 0), (480L, 0), (960L, 2), (1920L, 3)], Expert(merge).Notes.Select(n => (n.Tick, n.Lane)).ToList());
    }

    [TestMethod]
    public void AChordsForcingTravelsWithIt()
    {
        var session = Session();
        new SetChordFlags(ChartInstrument.Guitar, ChartDifficulty.Expert, [480], ChordFlags.Tap, on: true).Apply(session.Chart);
        Select(session, 480, 480);
        var clip = ClipboardEdits.Copy(session)!;

        session.Execute(ClipboardEdits.Paste(session, clip, 2880, PasteMode.Overwrite).Command!);

        Assert.AreEqual(ChordFlags.Tap, Expert(session).FlagsAt(2880));
    }

    [TestMethod]
    public void PastingIntoAnotherDifficultyOfThePartWorks()
    {
        var session = Session();
        Select(session, 0, 960);
        var clip = ClipboardEdits.Copy(session)!;
        session.Focused = GuitarHard;

        session.Execute(ClipboardEdits.Paste(session, clip, 0, PasteMode.Overwrite).Command!);

        Assert.AreSequenceEqual(Expert(session).Notes.Take(3).ToList(), [.. session.Chart.PartOf(ChartInstrument.Guitar)![ChartDifficulty.Hard].Notes]);
    }

    [TestMethod]
    public void FiveFretIntoGhlNeedsALaneMapping()
    {
        var chart = Guitar();
        new AddPart(ChartInstrument.GhlGuitar).Apply(chart);
        new AddNotes(ChartInstrument.Guitar, ChartDifficulty.Expert, [new(0, 1, 0), new(480, 1, 4), new(960, 1, ChartNote.OpenLane)]).Apply(chart);
        var session = Of(chart);
        Select(session, 0, 960);
        var clip = ClipboardEdits.Copy(session)!;
        session.Focused = GhlExpert;

        Assert.IsNull(ClipboardEdits.CanPaste(session, clip, out var needed));
        Assert.IsNotNull(needed);
        Assert.IsNotNull(ClipboardEdits.Paste(session, clip, 0, PasteMode.Overwrite).Refusal);

        var mapping = LaneMapping.Default(ChartInstrumentKind.FiveFret, ChartInstrumentKind.SixFret);
        session.Execute(ClipboardEdits.Paste(session, clip, 0, PasteMode.Overwrite, mapping).Command!);

        Assert.AreSequenceEqual([(0L, 0), (480L, 4), (960L, ChartNote.OpenLane)], chart.PartOf(ChartInstrument.GhlGuitar)![ChartDifficulty.Expert].Notes.Select(n => (n.Tick, n.Lane)).ToList());
    }

    [TestMethod]
    public void DrumsPasteOnlyIntoDrumsChartedTheSameWay()
    {
        var chart = Drums(DrumsVariant.Pro);
        new AddNotes(ChartInstrument.Drums, ChartDifficulty.Expert, [new(0, 1, 1)]).Apply(chart);
        var session = Of(chart, focused: DrumsExpert);
        session.Selection.Select(DrumsExpert, chart.PartOf(ChartInstrument.Drums)![ChartDifficulty.Expert].Notes);
        var clip = ClipboardEdits.Copy(session)!;

        Assert.IsNull(ClipboardEdits.CanPaste(session, clip, out _));
        Assert.IsNotNull(ClipboardEdits.CanPaste(session, clip with { Drums = DrumsVariant.FiveLane }, out _));
    }

    [TestMethod]
    public void DrumsAndFretsDontPasteIntoEachOther()
    {
        var chart = Drums(DrumsVariant.Pro);
        new AddPart(ChartInstrument.Guitar).Apply(chart);
        new AddNotes(ChartInstrument.Drums, ChartDifficulty.Expert, [new(0, 1, 1)]).Apply(chart);
        var session = Of(chart, focused: DrumsExpert);
        session.Selection.Select(DrumsExpert, chart.PartOf(ChartInstrument.Drums)![ChartDifficulty.Expert].Notes);
        var clip = ClipboardEdits.Copy(session)!;
        session.Focused = GuitarExpert;

        Assert.IsNotNull(ClipboardEdits.CanPaste(session, clip, out _));
    }

    [TestMethod]
    public void TwoTimesKicksAreLeftOutBelowExpert()
    {
        var chart = Drums(DrumsVariant.Pro);
        new AddNotes(ChartInstrument.Drums, ChartDifficulty.Expert, [new(0, 1, 0), new(0, 1, 0, DoubleKick: true), new(240, 1, 1)]).Apply(chart);
        var session = Of(chart, focused: DrumsExpert);
        session.Selection.Select(DrumsExpert, chart.PartOf(ChartInstrument.Drums)![ChartDifficulty.Expert].Notes);
        var clip = ClipboardEdits.Copy(session)!;
        session.Focused = new ChartTrackKey(ChartInstrument.Drums, ChartDifficulty.Hard);

        var paste = ClipboardEdits.Paste(session, clip, 0, PasteMode.Overwrite);
        session.Execute(paste.Command!);

        Assert.AreEqual(1, paste.DroppedDoubleKicks);
        Assert.AreSequenceEqual([(0L, 0), (240L, 1)], chart.PartOf(ChartInstrument.Drums)![ChartDifficulty.Hard].Notes.Select(n => (n.Tick, n.Lane)).ToList());
    }

    [TestMethod]
    public void APassageCopiedOutOfADiscoFlipKeepsThePadsThePlayerSees()
    {
        // Inside the flip, stored yellow plays as red. Pasted where there's no flip, it's stored red.
        var chart = Drums(DrumsVariant.Pro);
        new AddNotes(ChartInstrument.Drums, ChartDifficulty.Expert, [new(0, 1, 2)]).Apply(chart);
        new AddDiscoFlip(ChartDifficulty.Expert, 0, 480).Apply(chart);
        var session = Of(chart, focused: DrumsExpert);
        session.Selection.Select(DrumsExpert, chart.PartOf(ChartInstrument.Drums)![ChartDifficulty.Expert].Notes);
        var clip = ClipboardEdits.Copy(session)!;

        session.Execute(ClipboardEdits.Paste(session, clip, 1920, PasteMode.Overwrite).Command!);

        Assert.IsTrue(chart.PartOf(ChartInstrument.Drums)![ChartDifficulty.Expert].TryGetNote(1920, 1, false, out _));
    }

    [TestMethod]
    public void ProDrumsKeepWhetherAPadWasATom()
    {
        var chart = Drums(DrumsVariant.Pro);
        new AddNotes(ChartInstrument.Drums, ChartDifficulty.Expert, [new(0, 1, 3)]).Apply(chart);
        new SetToms([(3, 0L)], tom: true).Apply(chart);
        var session = Of(chart, focused: DrumsExpert);
        session.Selection.Select(DrumsExpert, chart.PartOf(ChartInstrument.Drums)![ChartDifficulty.Expert].Notes);
        var clip = ClipboardEdits.Copy(session)!;

        session.Execute(ClipboardEdits.Paste(session, clip, 1920, PasteMode.Overwrite).Command!);

        Assert.IsTrue(DrumCymbals.IsTom(chart.PartOf(ChartInstrument.Drums)!, 3, 1920));
    }

    [TestMethod]
    public void ARangeAcrossLanesPastesIntoTheSameTracksAndUndoesInOneStep()
    {
        var session = Session();
        new AddNotes(ChartInstrument.Guitar, ChartDifficulty.Hard, [new(480, 1, 2)]).Apply(session.Chart);
        var start = session.Chart.Clone();
        session.Selection.SelectRange(0, 959, [GuitarExpert, GuitarHard]);
        var clip = ClipboardEdits.Copy(session)!;

        var paste = ClipboardEdits.Paste(session, clip, 3840, PasteMode.Overwrite);
        session.Execute(paste.Command!);

        Assert.HasCount(2, paste.Placed);
        Assert.IsTrue(session.Chart.PartOf(ChartInstrument.Guitar)![ChartDifficulty.Hard].TryGetNote(4320, 2, false, out _));
        Assert.IsTrue(Expert(session).TryGetNote(4320, 1, false, out _));
        session.Undo();
        AssertSame(start, session.Chart, "after one undo");
    }

    [TestMethod]
    public void NothingSelectedCopiesNothing()
    {
        var session = Session();

        Assert.IsNull(ClipboardEdits.Copy(session));
        Assert.IsNotNull(session.Status);
    }

    // Expert: green at 0, red at 480, yellow at 960, blue at 1920.
    private static EditorSession Session()
    {
        var chart = Guitar();
        new AddNotes(ChartInstrument.Guitar, ChartDifficulty.Expert, [new(0, 1, 0), new(480, 1, 1), new(960, 1, 2), new(1920, 1, 3)]).Apply(chart);
        return Of(chart);
    }

    private static void Select(EditorSession session, long from, long to) =>
        session.Selection.Select(GuitarExpert, Expert(session).Notes.Where(n => n.Tick >= from && n.Tick <= to));
}
