using ChartPacker.Audio.Analysis;
using ChartPacker.Core.Charting.Analysis;

namespace ChartPacker.Core.Tests.Audio.Analysis;

[TestClass]
public sealed class TempoEstimatorTests
{
    private const int Rate = 44100;

    public TestContext TestContext { get; set; } = null!;

    /// <summary>AC C14.1, from 60 to 200 BPM.</summary>
    [TestMethod]
    [DataRow(60.0)]
    [DataRow(75.0)]
    [DataRow(90.0)]
    [DataRow(93.7)]
    [DataRow(100.0)]
    [DataRow(120.0)]
    [DataRow(128.0)]
    [DataRow(137.5)]
    [DataRow(150.0)]
    [DataRow(175.0)]
    [DataRow(199.5)]
    [DataRow(200.0)]
    public void AClickTracksTempoAndBeatsAreFound(double bpm)
    {
        var clicks = AnalysisSignals.Beats(bpm, (int)(30 * bpm / 60), 1.0);
        var proposal = Estimate(clicks, 33);

        Assert.IsNotNull(proposal.Bpm);
        Assert.AreEqual(bpm, proposal.Bpm.Value, 0.1);
        AssertBeatsOnClicks(proposal, clicks);
        Assert.IsGreaterThanOrEqualTo(clicks.Count - 2, proposal.Beats.Count, "A beat on (nearly) every click.");
    }

    /// <summary>AC C14.1, from 200 to 240 BPM: the tempo or half of it.</summary>
    [TestMethod]
    [DataRow(205.0)]
    [DataRow(220.0)]
    [DataRow(240.0)]
    public void AFastClickTracksTempoIsFoundOrHalved(double bpm)
    {
        var clicks = AnalysisSignals.Beats(bpm, (int)(30 * bpm / 60), 1.0);
        var proposal = Estimate(clicks, 33);

        Assert.IsNotNull(proposal.Bpm);
        double found = proposal.Bpm.Value;
        Assert.IsTrue(Math.Abs(found - bpm) <= 0.1 || Math.Abs(found - (bpm / 2)) <= 0.1, $"Found {found:0.000} BPM.");
        AssertBeatsOnClicks(proposal, clicks);
    }

    /// <summary>AC C14.2's analysis side: beats follow tempo steps.</summary>
    [TestMethod]
    public void BeatsFollowStepsInTempo()
    {
        var clicks = AnalysisSignals.SteppedBeats(1.0, (100, 16), (130, 16), (90, 16), (160, 17));
        var proposal = Estimate(clicks, clicks[^1] + 2);

        AssertBeatsOnClicks(proposal, clicks);
        var confident = proposal.Regions.Where(r => !r.IsLowConfidence).ToList();
        Assert.HasCount(4, confident, string.Join("; ", proposal.Regions));
        double[] tempos = [100, 130, 90, 160];
        for (int i = 0; i < 4; i++)
            Assert.AreEqual(tempos[i], confident[i].Bpm, 0.1, $"Region {i}.");
    }

    [TestMethod]
    public void ASelectionGivesItsOwnTempo()
    {
        var clicks = AnalysisSignals.SteppedBeats(1.0, (100, 20), (130, 30), (90, 20));
        var onsets = Analyze(clicks, clicks[^1] + 2);
        double from = clicks[22], to = clicks[47];

        var proposal = TempoEstimator.Estimate(onsets, from, to, TestContext.CancellationToken);

        Assert.AreEqual(130, proposal.Bpm!.Value, 0.1);
        Assert.IsTrue(proposal.Beats.All(b => b >= from - 0.002 && b <= to + 0.002));
    }

    [TestMethod]
    public void AStretchWithNoSteadyBeatIsMarkedUnsure()
    {
        var random = new Random(4);
        var first = AnalysisSignals.Beats(120, 40, 1.0);
        double middleFrom = first[^1] + 0.5;
        var middle = Enumerable.Range(0, 30).Select(_ => middleFrom + (random.NextDouble() * 10)).Order().ToList();
        var last = AnalysisSignals.Beats(120, 40, middleFrom + 10.5);
        var clicks = first.Concat(middle).Concat(last).ToList();

        var proposal = Estimate(clicks, last[^1] + 2);

        var unsure = proposal.Regions.Where(r => r.IsLowConfidence).ToList();
        Assert.IsNotEmpty(unsure, string.Join("; ", proposal.Regions));
        Assert.Contains(r => proposal.Beats[r.LastBeat] > middleFrom + 1 && proposal.Beats[r.FirstBeat] < middleFrom + 9, unsure,
            "The stretch without a steady beat is marked.");
        Assert.AreEqual(120, proposal.Bpm!.Value, 0.1);
    }

    [TestMethod]
    public void SilenceHasNoBeats()
    {
        var onsets = OnsetDetector.Analyze(AnalysisSignals.Input(new float[Rate * 10], Rate), false, TestContext.CancellationToken);

        Assert.IsTrue(TempoEstimator.Estimate(onsets, TestContext.CancellationToken).IsEmpty);
    }

    [TestMethod]
    public void ASlowSongMayBeAtHalfTempo()
    {
        // Every other click quieter: 75 BPM accents over 150 BPM clicks, which may be heard either way.
        var clicks = AnalysisSignals.Beats(150, 75, 1.0);
        var samples = AnalysisSignals.ClickTrack(clicks, 33, Rate, accentEvery: 2);
        var onsets = OnsetDetector.Analyze(AnalysisSignals.Input(samples, Rate), false, TestContext.CancellationToken);

        var proposal = TempoEstimator.Estimate(onsets, TestContext.CancellationToken);

        Assert.IsTrue(Math.Abs(proposal.Bpm!.Value - 150) <= 0.1 || proposal.MayBeHalfTempo, $"{proposal.Bpm} BPM, may be half: {proposal.MayBeHalfTempo}.");
    }

    private BeatProposal Estimate(IReadOnlyList<double> clicks, double seconds) =>
        TempoEstimator.Estimate(Analyze(clicks, seconds), TestContext.CancellationToken);

    private OnsetAnalysis Analyze(IReadOnlyList<double> clicks, double seconds) =>
        OnsetDetector.Analyze(AnalysisSignals.Input(AnalysisSignals.ClickTrack(clicks, seconds, Rate), Rate), false, TestContext.CancellationToken);

    private static void AssertBeatsOnClicks(BeatProposal proposal, IReadOnlyList<double> clicks)
    {
        Assert.IsFalse(proposal.IsEmpty);
        foreach (double beat in proposal.Beats)
            Assert.IsLessThanOrEqualTo(0.010, AnalysisSignals.DistanceToNearest(clicks, beat), $"The beat at {beat:0.000} s.");
    }
}
