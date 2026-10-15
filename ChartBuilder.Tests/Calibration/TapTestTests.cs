using ChartShared.Core.Calibration;

namespace ChartBuilder.Tests.Calibration;

[TestClass]
public sealed class TapTestTests
{
    private const double Period = 1.0; // 60 BPM
    private const int TapsPerPhase = 16;

    /// <summary>
    /// AC C11.2: simulated taps with a known audio offset, normal jitter with σ = 20 ms and two outliers of
    /// ±150 ms per phase. Over 1,000 seeded trials the median estimate is within 2 ms of the offset, and 95%
    /// of estimates are within 20 ms.
    /// </summary>
    [TestMethod]
    public void EstimatesTheOffsetOfSimulatedTaps()
    {
        var random = new Random(1101);
        var misses = new List<double>();
        for (int trial = 0; trial < 1000; trial++)
        {
            double offset = (random.NextDouble() - 0.5) * 0.5; // within ±250 ms
            double reaction = (random.NextDouble() - 0.5) * 0.1; // the same in both phases
            var visual = SimulatedPhase(random, firstBeat: 2, error: reaction);
            var audio = SimulatedPhase(random, firstBeat: 30, error: reaction + offset);

            var estimate = TapTest.Estimate(visual, audio);

            Assert.IsNotNull(estimate);
            misses.Add(estimate.LatencyMilliseconds - (offset * 1000));
        }

        misses.Sort();
        double median = misses[misses.Count / 2];
        double within = misses.Count(m => Math.Abs(m) <= 20) / (double)misses.Count;
        Assert.IsLessThanOrEqualTo(2, Math.Abs(median), $"The median estimate was {median:F2} ms off.");
        Assert.IsGreaterThanOrEqualTo(0.95, within, $"{within:P1} of estimates were within 20 ms.");
    }

    [TestMethod]
    public void TapsOnTheBeatGiveNoLatencyAndNoSpread()
    {
        var onBeat = new TapPhase(5, Period, [.. Enumerable.Range(0, TapsPerPhase).Select(n => 5.0 + n)]);

        var estimate = TapTest.Estimate(onBeat, onBeat with { FirstBeat = 40, Taps = [.. onBeat.Taps.Select(t => t + 35)] });

        Assert.IsNotNull(estimate);
        Assert.AreEqual(0, estimate.LatencyMilliseconds, 1e-9);
        Assert.AreEqual(0, estimate.SpreadMilliseconds, 1e-9);
        Assert.AreEqual(TapsPerPhase - TapTest.WarmUpTaps, estimate.VisualKept);
        Assert.AreEqual(TapsPerPhase - TapTest.WarmUpTaps, estimate.AudioKept);
    }

    [TestMethod]
    public void TheWarmUpTapsAreIgnored()
    {
        // The first four taps are wildly off, as while the user finds the beat; the rest are 80 ms late.
        var visual = new TapPhase(0, Period, [.. Enumerable.Range(0, 12).Select(n => n + (n < 4 ? 0.4 : 0.0))]);
        var audio = new TapPhase(0, Period, [.. Enumerable.Range(0, 12).Select(n => n + (n < 4 ? -0.3 : 0.08))]);

        var estimate = TapTest.Estimate(visual, audio);

        Assert.IsNotNull(estimate);
        Assert.AreEqual(80, estimate.LatencyMilliseconds, 1e-6);
    }

    [TestMethod]
    public void ATapIsMeasuredAgainstItsNearestBeat()
    {
        // Audio 400 ms late reads as 400 ms, and 600 ms late as 400 ms early: the beats are 1 s apart.
        var visual = new TapPhase(0, Period, [.. Enumerable.Range(0, 10).Select(n => (double)n)]);

        Assert.AreEqual(400, TapTest.Estimate(visual, visual with { Taps = [.. visual.Taps.Select(t => t + 0.4)] })!.LatencyMilliseconds, 1e-6);
        Assert.AreEqual(-400, TapTest.Estimate(visual, visual with { Taps = [.. visual.Taps.Select(t => t + 0.6)] })!.LatencyMilliseconds, 1e-6);
    }

    [TestMethod]
    public void TooFewTapsGiveNoEstimate()
    {
        var enough = new TapPhase(0, Period, [.. Enumerable.Range(0, 10).Select(n => (double)n)]);
        var tooFew = enough with { Taps = [.. enough.Taps.Take(TapTest.WarmUpTaps + TapTest.MinimumKept - 1)] };

        Assert.IsNull(TapTest.Estimate(tooFew, enough));
        Assert.IsNull(TapTest.Estimate(enough, tooFew));
        Assert.IsNull(TapTest.Estimate(enough, enough with { Taps = [] }));
    }

    // A phase of 16 taps, each `error` from its beat plus jitter, two of the counted ones 150 ms off either way.
    private static TapPhase SimulatedPhase(Random random, double firstBeat, double error)
    {
        var outliers = new HashSet<int>();
        while (outliers.Count < 2)
            outliers.Add(random.Next(TapTest.WarmUpTaps, TapsPerPhase));

        int sign = 1;
        var taps = new double[TapsPerPhase];
        for (int n = 0; n < TapsPerPhase; n++)
        {
            double beat = firstBeat + (n * Period);
            if (outliers.Contains(n))
            {
                taps[n] = beat + error + (sign * 0.15);
                sign = -sign;
            }
            else
            {
                taps[n] = beat + error + (Gaussian(random) * 0.02);
            }
        }

        return new TapPhase(firstBeat, Period, taps);
    }

    // A standard normal value (Box–Muller).
    private static double Gaussian(Random random) =>
        Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble());
}
