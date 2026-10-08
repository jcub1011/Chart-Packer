using ChartPacker.Core.Charting;
using ChartPacker.Core.Charting.Commands;
using ChartPacker.Core.Charting.Editing;
using ChartPacker.Core.Charts;
using static ChartBuilder.Tests.Editing.EditorSessions;

namespace ChartBuilder.Tests.Editing;

/// <summary>Find first downbeat's lead-in (charter-spec C14 item 5), and tidying a detected tempo (C1).</summary>
[TestClass]
public sealed class FirstDownbeatTests
{
    [TestMethod]
    public void MeasureOneLeadsInToTheDownbeat()
    {
        var chart = Guitar();

        var edit = TempoMapEdits.LeadIn(chart, 1.3, TimeSpan.Zero, keepTimes: false);
        edit.Command!.Apply(chart);

        Assert.AreSequenceEqual([new TempoChange(0, 325_000), new TempoChange(1920, 500_000)], chart.TempoMap.Tempos.ToList());
        Assert.AreEqual(1.3, chart.TempoMap.ToTempoMap().SecondsAt(1920), 0.001);
    }

    [TestMethod]
    public void TheDelayIsTakenOffTheDownbeat()
    {
        var chart = Guitar();

        TempoMapEdits.LeadIn(chart, 1.3, TimeSpan.FromSeconds(0.2), keepTimes: false).Command!.Apply(chart);

        Assert.AreEqual(275_000, chart.TempoMap.Tempos[0].MicrosecondsPerQuarter);
        Assert.AreEqual(1.1, chart.TempoMap.ToTempoMap().SecondsAt(1920), 0.001);
    }

    [TestMethod]
    public void MeasureOnesOtherMarkersGo()
    {
        var chart = Guitar();
        TempoMapEdits.AddMarker(chart, 960).Command!.Apply(chart);
        new SetTempo(new TempoChange(960, 500_000), 400_000).Apply(chart);

        TempoMapEdits.LeadIn(chart, 1.6, TimeSpan.Zero, keepTimes: false).Command!.Apply(chart);

        Assert.AreSequenceEqual([0L, 1920L], chart.TempoMap.Tempos.Select(t => t.Tick).ToList());
        Assert.AreEqual(400_000, chart.TempoMap.Tempos[1].MicrosecondsPerQuarter, "Measure 2 keeps the tempo that was in force there.");
    }

    [TestMethod]
    public void ADownbeatAtOrBeforeTheChartsStartIsLeftToDelay()
    {
        var chart = Guitar();

        Assert.IsTrue(TempoMapEdits.LeadIn(chart, 0.15, TimeSpan.FromSeconds(0.2), keepTimes: false).IsRefused);
        Assert.IsTrue(TempoMapEdits.LeadIn(chart, 0.2, TimeSpan.FromSeconds(0.2), keepTimes: false).IsRefused);
    }

    [TestMethod]
    public void ALeadInFasterThan1000BpmIsRefused()
    {
        // Four beats in 0.2 s would be 1200 BPM.
        var edit = TempoMapEdits.LeadIn(Guitar(), 0.2, TimeSpan.Zero, keepTimes: false);

        Assert.IsTrue(edit.IsRefused);
        StringAssert.Contains(edit.Refusal, "Delay");
    }

    [TestMethod]
    public void AnAnchorInsideMeasureOneIsRefused()
    {
        var chart = Guitar();
        TempoMapEdits.AddMarker(chart, 960).Command!.Apply(chart);
        new SetAnchor(960, 500_000).Apply(chart);

        Assert.IsTrue(TempoMapEdits.LeadIn(chart, 1.3, TimeSpan.Zero, keepTimes: false).IsRefused);
    }

    [TestMethod]
    public void AnAnchorOnMeasureTwoIsBlockedWithTheOfferToUnanchorIt()
    {
        var chart = Guitar();
        TempoMapEdits.AddMarker(chart, 1920).Command!.Apply(chart);
        new SetAnchor(1920, 1_000_000).Apply(chart);

        var refused = TempoMapEdits.LeadIn(chart, 1.3, TimeSpan.Zero, keepTimes: false);
        var unanchored = TempoMapEdits.LeadIn(chart, 1.3, TimeSpan.Zero, keepTimes: false, BlockedAnchor.Unanchor);

        Assert.AreEqual(1920, refused.Blocking?.Tick);
        Assert.IsFalse(unanchored.IsRefused);
    }

    [TestMethod]
    public void ALaterAnchorStaysWhereItIs()
    {
        var chart = Guitar();
        TempoMapEdits.AddMarker(chart, 9600).Command!.Apply(chart);
        new SetAnchor(9600, 10_000_000).Apply(chart);

        TempoMapEdits.LeadIn(chart, 1.3, TimeSpan.Zero, keepTimes: false).Command!.Apply(chart);

        Assert.AreEqual(10.0, chart.TempoMap.ToTempoMap().SecondsAt(9600), 0.001);
    }

    [TestMethod]
    public void ALeadInIsOneUndoableStep()
    {
        var session = Of(Guitar());
        var before = session.Chart.Clone();

        Assert.IsTrue(session.Execute(TempoMapEdits.LeadIn(session.Chart, 1.3, TimeSpan.Zero, keepTimes: false).Command!));
        session.Undo();

        Assert.IsEmpty(ChartModelComparer.Differences(before, session.Chart));
    }

    [TestMethod]
    [DataRow(127.97, 128.0)]
    [DataRow(128.04, 128.0)]
    [DataRow(128.06, 128.06)]
    [DataRow(93.73456, 93.735)]
    public void ADetectedTempoIsTidied(double bpm, double tidied)
    {
        Assert.AreEqual(tidied, BpmTidy.Tidy(bpm), 1e-9);
    }
}
