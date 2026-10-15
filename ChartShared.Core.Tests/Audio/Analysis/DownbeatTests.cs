using ChartShared.Audio.Analysis;
using ChartShared.Audio.Synthesis;
using ChartShared.Core.Charting.Analysis;

namespace ChartShared.Core.Tests.Audio.Analysis;

[TestClass]
public sealed class DownbeatTests
{
    private const int Rate = 44100;

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void TheFirstKickAfterALeadInIsTheDownbeat()
    {
        var beats = AnalysisSignals.Beats(120, 48, 2.0);
        var (onsets, proposal) = Analyze(beats, firstDownbeat: 0);

        var downbeat = TempoEstimator.FindFirstDownbeat(onsets, proposal.Beats, 4);

        Assert.IsNotNull(downbeat);
        Assert.AreEqual(2.0, downbeat.Value.Seconds, 0.010);
    }

    [TestMethod]
    public void APickupBeatComesBeforeTheDownbeat()
    {
        var beats = AnalysisSignals.Beats(120, 49, 2.0);
        var (onsets, proposal) = Analyze(beats, firstDownbeat: 1);

        var downbeat = TempoEstimator.FindFirstDownbeat(onsets, proposal.Beats, 4);

        Assert.IsNotNull(downbeat);
        Assert.AreEqual(beats[1], downbeat.Value.Seconds, 0.010);
    }

    [TestMethod]
    public void WithoutAKickTheAccentedClickIsTheDownbeat()
    {
        var beats = AnalysisSignals.Beats(120, 49, 2.0);
        // Accents on beats 2, 6, 10…: the first click is a pickup.
        var samples = AnalysisSignals.Silence(28, Rate, noise: 0.001f);
        var accented = Synth.Click(true, Rate).Samples.ToArray();
        var plain = Synth.Click(false, Rate).Samples.ToArray();
        for (int i = 0; i < beats.Count; i++)
            AnalysisSignals.Add(samples, i % 4 == 1 ? accented : plain, beats[i], Rate);
        var onsets = OnsetDetector.Analyze(AnalysisSignals.Input(samples, Rate), true, TestContext.CancellationToken);
        var proposal = TempoEstimator.Estimate(onsets, TestContext.CancellationToken);

        var downbeat = TempoEstimator.FindFirstDownbeat(onsets, proposal.Beats, 4);

        Assert.IsNotNull(downbeat);
        Assert.AreEqual(beats[1], downbeat.Value.Seconds, 0.010);
    }

    /// <summary>A click on every beat, and a kick on every <c>firstDownbeat + 4k</c>th.</summary>
    private (OnsetAnalysis Onsets, BeatProposal Proposal) Analyze(List<double> beats, int firstDownbeat)
    {
        var samples = AnalysisSignals.ClickTrack(beats, beats[^1] + 2, Rate, accentEvery: 0);
        var kick = AnalysisSignals.BandHit(OnsetBand.Kick, Rate, 0.5f);
        for (int i = firstDownbeat; i < beats.Count; i += 4)
            AnalysisSignals.Add(samples, kick, beats[i], Rate);

        var onsets = OnsetDetector.Analyze(AnalysisSignals.Input(samples, Rate), true, TestContext.CancellationToken);
        return (onsets, TempoEstimator.Estimate(onsets, TestContext.CancellationToken));
    }
}
