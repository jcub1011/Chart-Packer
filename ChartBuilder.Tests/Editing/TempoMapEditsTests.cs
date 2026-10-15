using ChartBuilder.Tests.Charting;
using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charts;
using static ChartBuilder.Tests.Editing.EditorSessions;

namespace ChartBuilder.Tests.Editing;

[TestClass]
public sealed class TempoMapEditsTests
{
    // At 120 BPM and 480 ticks per beat, a second is 960 ticks.
    private const long TenSeconds = 9_600;
    private const long ThirtySeconds = 28_800;

    [TestMethod]
    public void ChangingTheFirstSegmentKeepsTheAnchorWhereItIs()
    {
        // AC C4.1: markers at 0 s and 10 s, and an anchor at 30.000 s; the segment from 10 s takes up the change.
        var chart = Anchored();

        var edit = TempoMapEdits.SetBpm(chart, 0, EditableTempoMap.MicrosecondsPerQuarterOf(140), TimeSpan.Zero, keepTimes: false);

        Assert.IsFalse(edit.IsRefused, edit.Refusal);
        CommandChecks.AppliesRevertsAndRedoes(chart, edit.Command!);
        var map = chart.TempoMap.ToTempoMap();
        Assert.AreEqual(30.0, map.SecondsAt(ThirtySeconds), 0.001);
        Assert.AreEqual(EditableTempoMap.MicrosecondsPerQuarterOf(140), chart.TempoMap.Tempos[0].MicrosecondsPerQuarter);
        Assert.AreNotEqual(500_000, chart.TempoMap.Tempos[1].MicrosecondsPerQuarter, "The segment from 10 s took up the change.");
        Assert.AreEqual(new TempoAnchor(ThirtySeconds, 30_000_000), chart.TempoMap.Anchors.Single());
    }

    [TestMethod]
    public void ChangingTheSegmentThatRunsIntoTheAnchorIsRefusedWithTheOffers()
    {
        // AC C4.1.
        var chart = Anchored();
        var before = chart.Clone();

        var edit = TempoMapEdits.SetBpm(chart, TenSeconds, EditableTempoMap.MicrosecondsPerQuarterOf(140), TimeSpan.Zero, keepTimes: false);

        Assert.IsTrue(edit.IsRefused);
        Assert.IsFalse(string.IsNullOrWhiteSpace(edit.Refusal));
        Assert.AreEqual(new TempoAnchor(ThirtySeconds, 30_000_000), edit.Blocking);
        Assert.AreEqual(ThirtySeconds - 480, edit.OneBeatBefore);
        CommandChecks.AssertSame(before, chart, "after refusing");
    }

    [TestMethod]
    public void TheRefusalsOffersUnanchorOrAddAMarkerOneBeatBefore()
    {
        var chart = Anchored();
        long tempo = EditableTempoMap.MicrosecondsPerQuarterOf(140);

        var unanchored = TempoMapEdits.SetBpm(chart, TenSeconds, tempo, TimeSpan.Zero, keepTimes: false, BlockedAnchor.Unanchor);
        var withMarker = TempoMapEdits.SetBpm(chart, TenSeconds, tempo, TimeSpan.Zero, keepTimes: false, BlockedAnchor.AddMarkerBefore);

        var first = chart.Clone();
        unanchored.Command!.Apply(first);
        Assert.IsEmpty(first.TempoMap.Anchors);
        Assert.AreEqual(tempo, first.TempoMap.Tempos[1].MicrosecondsPerQuarter);

        CommandChecks.AppliesRevertsAndRedoes(chart, withMarker.Command!);
        Assert.AreEqual(ThirtySeconds - 480, chart.TempoMap.Tempos[2].Tick);
        Assert.AreEqual(30.0, chart.TempoMap.ToTempoMap().SecondsAt(ThirtySeconds), 0.001);
        Assert.AreEqual(tempo, chart.TempoMap.Tempos[1].MicrosecondsPerQuarter);
    }

    [TestMethod]
    public void TheDelayRelatesAnAnchorsAudioTimeToTheChart()
    {
        var chart = Anchored(delayMicroseconds: 250_000);

        var edit = TempoMapEdits.SetBpm(chart, 0, EditableTempoMap.MicrosecondsPerQuarterOf(100), TimeSpan.FromMilliseconds(250), keepTimes: false);
        edit.Command!.Apply(chart);

        Assert.AreEqual(30.0, chart.TempoMap.ToTempoMap().SecondsAt(ThirtySeconds), 0.001, "The anchor is at 30.25 s of audio, so 30 s of chart.");
    }

    [TestMethod]
    public void DraggingAnAnchoredMarkerChangesTheTempoOnBothSides()
    {
        var chart = Anchored();
        chart.TempoMap.Tempos.Add(new TempoChange(ThirtySeconds + 9_600, 500_000));
        chart.TempoMap.Anchors.Add(new TempoAnchor(ThirtySeconds + 9_600, 40_000_000));

        var edit = TempoMapEdits.DragAnchored(chart, ThirtySeconds, 31, TimeSpan.Zero, keepTimes: false);

        Assert.IsFalse(edit.IsRefused, edit.Refusal);
        CommandChecks.AppliesRevertsAndRedoes(chart, edit.Command!);
        var map = chart.TempoMap.ToTempoMap();
        Assert.AreEqual(31.0, map.SecondsAt(ThirtySeconds), 0.001);
        Assert.AreEqual(40.0, map.SecondsAt(ThirtySeconds + 9_600), 0.001, "The next anchor stays put.");
        Assert.AreEqual(31_000_000, chart.TempoMap.Anchors.First(a => a.Tick == ThirtySeconds).AudioMicroseconds);
        Assert.AreEqual(0.0, map.SecondsAt(0));
        Assert.AreEqual(10.0, map.SecondsAt(TenSeconds), 0.001, "Only the segment before the marker changed on that side.");
    }

    [TestMethod]
    public void AMarkerCannotBeMovedPastAnAnchor()
    {
        var chart = Anchored();

        var edit = TempoMapEdits.MoveMarker(chart, TenSeconds, ThirtySeconds + 960, TimeSpan.Zero, keepTimes: false);

        Assert.IsTrue(edit.IsRefused);
    }

    [TestMethod]
    public void RemovingAMarkerIsTakenUpBeforeTheAnchor()
    {
        var chart = Anchored();
        chart.TempoMap.Tempos.Add(new TempoChange(4_800, 400_000));
        var tempos = chart.TempoMap.Tempos.ToList();
        Assert.AreEqual(TenSeconds, tempos.Last(t => t.Tick < ThirtySeconds).Tick);

        var edit = TempoMapEdits.RemoveMarker(chart, 4_800, TimeSpan.Zero, keepTimes: false);
        CommandChecks.AppliesRevertsAndRedoes(chart, edit.Command!);

        Assert.IsTrue(chart.TempoMap.Tempos.At(4_800).IsEmpty);
        Assert.AreEqual(30.0, chart.TempoMap.ToTempoMap().SecondsAt(ThirtySeconds), 0.001);
    }

    [TestMethod]
    public void BpmFromASpanFitsTheBeatsToIt()
    {
        var chart = Guitar();

        // Two seconds at 120 BPM, said to be three beats: 90 BPM.
        var edit = TempoMapEdits.BpmFromRange(chart, 960, 960 + 1920, 3, TimeSpan.Zero, keepTimes: false);
        CommandChecks.AppliesRevertsAndRedoes(chart, edit.Command!);

        Assert.AreEqual(new TempoChange(960, EditableTempoMap.MicrosecondsPerQuarterOf(90)), chart.TempoMap.Tempos.Single(t => t.Tick == 960));
    }

    [TestMethod]
    public void AnchoringPinsTheMarkerWhereItIsInTheAudio()
    {
        var chart = Guitar();
        chart.TempoMap.Tempos.Add(new TempoChange(1920, 400_000));

        var edit = TempoMapEdits.SetAnchored(chart, 1920, anchored: true, TimeSpan.FromSeconds(0.5));
        CommandChecks.AppliesRevertsAndRedoes(chart, edit.Command!);

        Assert.AreEqual(new TempoAnchor(1920, 2_500_000), chart.TempoMap.Anchors.Single());
        Assert.IsTrue(TempoMapEdits.SetAnchored(chart, 1920, anchored: true, TimeSpan.Zero).IsRefused);
    }

    [TestMethod]
    public void KeepingTimesThroughTheTempoToolLeavesEveryNoteWhereItWasInTime()
    {
        // AC C4.2, through the tool.
        var chart = Guitar();
        var track = chart.PartOf(ChartInstrument.Guitar)![ChartDifficulty.Expert];
        for (int i = 0; i < 40; i++)
            track.Notes.Add(new EditableNote(240 + (i * 177), 1 + (i * 13), i % 5));
        chart.TempoMap.Tempos.Add(new TempoChange(1920, 400_000));
        var session = Of(chart);
        session.KeepTimes = true;
        session.Tool = EditorTool.Tempo;
        var before = chart.TempoMap.ToTempoMap();
        var times = track.Notes.Select(n => (Start: before.SecondsAt(n.Tick), End: before.SecondsAt(n.Tick + n.Length))).ToList();

        DragFromTo(session, At(session, 1922, null), At(session, 2400, null));

        var after = chart.TempoMap.ToTempoMap();
        Assert.AreEqual(2400, chart.TempoMap.Tempos[1].Tick);
        double halfTick = 0.5 * 500_000 / 1_000_000.0 / 480;
        for (int i = 0; i < track.Notes.Count; i++)
        {
            Assert.AreEqual(times[i].Start, after.SecondsAt(track.Notes[i].Tick), halfTick + 1e-9);
            Assert.AreEqual(times[i].End, after.SecondsAt(track.Notes[i].Tick + track.Notes[i].Length), halfTick + 1e-9);
        }
    }

    [TestMethod]
    public void TapTempoTakesTheMedianInterval()
    {
        var taps = new TapTempo();
        Assert.IsNull(taps.Bpm);

        foreach (double seconds in new[] { 10.0, 10.5, 11.0, 11.62, 12.0, 12.5 })
            taps.Tap(seconds);

        Assert.AreEqual(120, taps.Bpm!.Value, 1e-6);
        Assert.IsTrue(taps.Tap(20), "A long pause starts a new run.");
        Assert.AreEqual(1, taps.Count);
    }

    /// <summary>Markers at 0 s and 10 s and an anchored one at 30 s, all at 120 BPM.</summary>
    private static EditableChart Anchored(long delayMicroseconds = 0)
    {
        var chart = Guitar();
        chart.TempoMap.Tempos.Add(new TempoChange(TenSeconds, 500_000));
        chart.TempoMap.Tempos.Add(new TempoChange(ThirtySeconds, 500_000));
        chart.TempoMap.Anchors.Add(new TempoAnchor(ThirtySeconds, 30_000_000 + delayMicroseconds));
        return chart;
    }
}
