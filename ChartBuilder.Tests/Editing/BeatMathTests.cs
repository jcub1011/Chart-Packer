using ChartShared.Core.Charting.Analysis;

namespace ChartBuilder.Tests.Editing;

[TestClass]
public sealed class BeatMathTests
{
    [TestMethod]
    public void ALineThroughEvenBeatsIsExact()
    {
        double[] beats = [.. Enumerable.Range(0, 20).Select(i => 1.25 + (i * 0.4))];

        var (intercept, period, residual) = BeatMath.FitLine(beats);

        Assert.AreEqual(1.25, intercept, 1e-12);
        Assert.AreEqual(0.4, period, 1e-12);
        Assert.AreEqual(0, residual, 1e-12);
        Assert.AreEqual(150, BeatMath.BpmOf(beats), 1e-9);
    }

    [TestMethod]
    public void JitterAveragesOutOfTheTempo()
    {
        var random = new Random(2);
        double[] beats = [.. Enumerable.Range(0, 32).Select(i => (i * 0.5) + ((random.NextDouble() - 0.5) * 0.004))];

        Assert.AreEqual(120, BeatMath.BpmOf(beats), 0.05);
    }

    [TestMethod]
    public void DoublingPutsABeatBetweenEachTwo()
    {
        var proposal = new BeatProposal([1.0, 1.5, 2.0], [1f, 0.5f, 1f], [new TempoRegion(0, 2, 120, 0.9f)], 120, true);

        var doubled = BeatMath.Doubled(proposal);

        Assert.AreSequenceEqual(new[] { 1.0, 1.25, 1.5, 1.75, 2.0 }, [.. doubled.Beats]);
        Assert.AreSequenceEqual(new[] { 1f, 0.5f, 0.5f, 0.5f, 1f }, [.. doubled.BeatConfidence]);
        Assert.AreEqual(new TempoRegion(0, 4, 240, 0.9f), doubled.Regions.Single());
        Assert.AreEqual(240, doubled.Bpm);
        Assert.IsFalse(doubled.MayBeHalfTempo);
    }

    [TestMethod]
    public void HalvingKeepsEveryOtherBeatFromThePhase()
    {
        var proposal = new BeatProposal([1.0, 1.25, 1.5, 1.75, 2.0], [1f, 1f, 1f, 1f, 1f], [new TempoRegion(0, 4, 240, 1f)], 240, false);

        var even = BeatMath.Halved(proposal, 0);
        var odd = BeatMath.Halved(proposal, 1);

        Assert.AreSequenceEqual(new[] { 1.0, 1.5, 2.0 }, [.. even.Beats]);
        Assert.AreEqual(new TempoRegion(0, 2, 120, 1f), even.Regions.Single());
        Assert.AreSequenceEqual(new[] { 1.25, 1.75 }, [.. odd.Beats]);
        Assert.AreEqual(new TempoRegion(0, 1, 120, 1f), odd.Regions.Single());
    }

    /// <summary>AC C14.3's reach: half a 1/16 step at 120 BPM is 62.5 ms.</summary>
    [TestMethod]
    public void TheNearestTimeIsFoundWithinReach()
    {
        double[] onsets = [0.5, 1.0, 1.5];

        Assert.AreEqual(1, BeatMath.NearestIndex(onsets, 1.015, 0.0625));
        Assert.AreEqual(1, BeatMath.NearestIndex(onsets, 0.985, 0.0625));
        Assert.AreEqual(-1, BeatMath.NearestIndex(onsets, 1.25, 0.0625));
        Assert.AreEqual(0, BeatMath.NearestIndex(onsets, 0.75, 0.25), "Of two as near, the earlier.");
        Assert.AreEqual(2, BeatMath.NearestIndex(onsets, 9, 10));
        Assert.AreEqual(-1, BeatMath.NearestIndex([], 1, 10));
    }
}
