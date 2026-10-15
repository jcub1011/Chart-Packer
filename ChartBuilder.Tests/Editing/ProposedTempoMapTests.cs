using ChartBuilder.Tests.Charting;
using ChartPacker.Core.Charting;
using ChartPacker.Core.Charting.Analysis;
using ChartPacker.Core.Charting.Commands;
using ChartPacker.Core.Charting.Editing;
using ChartPacker.Core.Charts;
using static ChartBuilder.Tests.Editing.EditorSessions;

namespace ChartBuilder.Tests.Editing;

/// <summary>Proposed tempo maps, accepted (charter-spec C14 item 4), at 480 ticks per beat in 4/4.</summary>
[TestClass]
public sealed class ProposedTempoMapTests
{
    [TestMethod]
    public void ASteadyTempoIsOneSegmentAfterTheLeadIn()
    {
        var chart = Guitar();
        var beats = Beats(120, 32, 1.0);

        var edit = ProposedTempoMap.Build(chart, beats, [], 0, ProposalOptions.Default with { AnchorEveryMeasures = 0 }, TimeSpan.Zero, keepTimes: false);
        edit.Command!.Apply(chart);

        // Measure 1 leads in to the downbeat on measure 2's line: 1920 ticks in a second.
        Assert.AreSequenceEqual([new TempoChange(0, 250_000), new TempoChange(1920, 500_000)], chart.TempoMap.Tempos.ToList());
        Assert.IsEmpty(chart.TempoMap.Anchors);
        AssertBeatsLand(chart, beats, TimeSpan.Zero, 0.001);
    }

    /// <summary>AC C14.2: an accepted map's beats are each within 10 ms of the clicks, through steps in tempo.</summary>
    [TestMethod]
    [DataRow(0.0, 0.0)]
    [DataRow(0.0, 5.0)]
    [DataRow(0.4, 0.0)]
    [DataRow(0.4, 5.0)]
    public void AcceptedBeatsLandOnTheProposalThroughTempoSteps(double delaySeconds, double tolerance)
    {
        var session = Of(Guitar(), TimeSpan.FromSeconds(delaySeconds));
        var beats = Stepped(1.5, (120, 16), (150, 20), (96, 12));
        session.TempoProposal.Start(Proposal(beats), 0);

        Assert.IsTrue(session.TempoProposal.Accept(new ProposalOptions(4, tolerance)), session.Status);

        AssertBeatsLand(session.Chart, beats, TimeSpan.FromSeconds(delaySeconds), 0.010);
        Assert.IsFalse(session.TempoProposal.IsActive, "Accepting ends the run.");
        if (tolerance > 0)
            Assert.IsLessThan(10, session.Chart.TempoMap.Tempos.Count, "Steady stretches are a segment each, not a marker a beat.");
    }

    [TestMethod]
    public void AnchorsGoEveryNMeasuresAtTheirTimes()
    {
        var chart = Guitar();
        var beats = Beats(120, 40, 1.0);

        ProposedTempoMap.Build(chart, beats, [], 0, new ProposalOptions(AnchorEveryMeasures: 4), TimeSpan.FromSeconds(0.25), keepTimes: false).Command!.Apply(chart);

        // Measures 5 and 9 start at ticks 7680 and 15360: beats 12 and 28.
        Assert.AreSequenceEqual([7680L, 15360L], chart.TempoMap.Anchors.Select(a => a.Tick).ToList());
        Assert.AreEqual((long)((beats[12] * 1_000_000) + 0.5), chart.TempoMap.Anchors[0].AudioMicroseconds, 1);
    }

    [TestMethod]
    public void AnUnsureStretchIsOneSegmentAnchoredAtBothEnds()
    {
        var chart = Guitar();
        var beats = Beats(120, 24, 1.0);
        beats[9] += 0.08;
        beats[10] -= 0.05;

        ProposedTempoMap.Build(chart, beats, [(8, 12)], 0, new ProposalOptions(0, 5), TimeSpan.Zero, keepTimes: false).Command!.Apply(chart);

        var anchored = chart.TempoMap.Anchors.Select(a => a.Tick).ToList();
        Assert.AreSequenceEqual([1920L + (8 * 480), 1920L + (12 * 480)], anchored);
        var map = chart.TempoMap.ToTempoMap();
        Assert.AreEqual(beats[8], map.SecondsAt(1920 + (8 * 480)), 0.001);
        Assert.AreEqual(beats[12], map.SecondsAt(1920 + (12 * 480)), 0.001);
    }

    [TestMethod]
    public void APickupBeatGoesInMeasureOne()
    {
        var chart = Guitar();
        var beats = Beats(120, 17, 0.75);

        ProposedTempoMap.Build(chart, beats, [], 1, new ProposalOptions(0, 5), TimeSpan.Zero, keepTimes: false).Command!.Apply(chart);

        var map = chart.TempoMap.ToTempoMap();
        Assert.AreEqual(beats[0], map.SecondsAt(1440), 0.001, "The pickup is the beat before measure 2.");
        Assert.AreEqual(beats[1], map.SecondsAt(1920), 0.001);
    }

    [TestMethod]
    public void ASelectionsProposalKeepsTheAnchorAfterIt()
    {
        var chart = Guitar();
        new SetAnchor(0, 0).Apply(chart);
        var anchorAfter = TempoMapEdits.AddMarker(chart, 9600).Command!;
        anchorAfter.Apply(chart);
        new SetAnchor(9600, 10_000_000).Apply(chart);

        // A little slower than 120 over measures 2 and 3: the first beat is nearest the beat line at 1920.
        var beats = Beats(118, 9, 2.01);
        var edit = ProposedTempoMap.Build(chart, beats, [], 0, new ProposalOptions(0, 5), TimeSpan.Zero, keepTimes: false, span: (1920, 5760));
        Assert.IsFalse(edit.IsRefused, edit.Refusal);
        edit.Command!.Apply(chart);

        var map = chart.TempoMap.ToTempoMap();
        Assert.AreEqual(10.0, map.SecondsAt(9600), 0.001, "The anchor after the proposal stays where it is.");
        for (int i = 0; i < beats.Length; i++)
            Assert.AreEqual(beats[i], map.SecondsAt(1920 + (i * 480)), 0.010, $"Beat {i}.");
    }

    [TestMethod]
    public void KeepingTimesKeepsTheNotesWhereTheyAreInTheAudio()
    {
        var chart = Guitar();
        new AddNotes(ChartInstrument.Guitar, ChartDifficulty.Expert, [new EditableNote(2400, 0, 1)]).Apply(chart);
        double before = chart.TempoMap.ToTempoMap().SecondsAt(2400);

        ProposedTempoMap.Build(chart, Beats(110, 24, 0.6), [], 0, ProposalOptions.Default, TimeSpan.Zero, keepTimes: true).Command!.Apply(chart);

        long tick = chart.PartOf(ChartInstrument.Guitar)![ChartDifficulty.Expert].Notes[0].Tick;
        Assert.AreEqual(before, chart.TempoMap.ToTempoMap().SecondsAt(tick), 0.002);
    }

    [TestMethod]
    public void AcceptingIsOneStepThatUndoesExactly()
    {
        var chart = Guitar();
        var edit = ProposedTempoMap.Build(chart, Stepped(1.2, (100, 12), (140, 12)), [(10, 14)], 0, ProposalOptions.Default, TimeSpan.FromSeconds(0.1), keepTimes: false);

        CommandChecks.AppliesRevertsAndRedoes(chart, edit.Command!);
    }

    private static void AssertBeatsLand(EditableChart chart, IReadOnlyList<double> beats, TimeSpan delay, double within)
    {
        var map = chart.TempoMap.ToTempoMap();
        for (int i = 0; i < beats.Count; i++)
            Assert.AreEqual(beats[i], map.SecondsAt(1920 + (i * 480)) + delay.TotalSeconds, within, $"Beat {i}.");
    }

    internal static double[] Beats(double bpm, int count, double start) => [.. Enumerable.Range(0, count).Select(i => start + (i * 60 / bpm))];

    internal static double[] Stepped(double start, params (double Bpm, int Beats)[] steps)
    {
        var beats = new List<double>();
        double at = start;
        foreach (var (bpm, count) in steps)
        {
            for (int i = 0; i < count; i++)
            {
                beats.Add(at);
                at += 60 / bpm;
            }
        }

        return [.. beats];
    }

    internal static BeatProposal Proposal(double[] beats, params TempoRegion[] regions) =>
        new(beats, [.. beats.Select(_ => 1f)], regions, BeatMath.BpmOf(beats), false);
}
