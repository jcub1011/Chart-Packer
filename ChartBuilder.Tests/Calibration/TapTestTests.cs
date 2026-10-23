using ChartShared.Core.Calibration;

namespace ChartBuilder.Tests.Calibration;

[TestClass]
public sealed class TapTestTests
{
    private static readonly double[] Notes = CalibrationChart.DeviceNoteTimes();

    /// <summary>
    /// AC C11.2: simulated taps with a known offset, normal jitter with σ = 20 ms and two outliers of ±150 ms. Over 1,000
    /// seeded trials the median estimate is within 2 ms of the offset, and 95% of estimates are within 20 ms.
    /// </summary>
    [TestMethod]
    public void EstimatesTheOffsetOfSimulatedTaps()
    {
        var random = new Random(1101);
        var misses = new List<double>();
        for (int trial = 0; trial < 1000; trial++)
        {
            double offset = (random.NextDouble() - 0.5) * 0.4; // within ±200 ms
            var estimate = TapTest.Estimate(Notes, SimulatedTaps(random, offset));

            Assert.IsNotNull(estimate);
            misses.Add(estimate.OffsetMilliseconds - (offset * 1000));
        }

        misses.Sort();
        double median = misses[misses.Count / 2];
        double within = misses.Count(m => Math.Abs(m) <= 20) / (double)misses.Count;
        Assert.IsLessThanOrEqualTo(2, Math.Abs(median), $"The median estimate was {median:F2} ms off.");
        Assert.IsGreaterThanOrEqualTo(0.95, within, $"{within:P1} of estimates were within 20 ms.");
    }

    [TestMethod]
    public void TapsOnTheNotesGiveNoOffsetAndNoSpread()
    {
        var estimate = TapTest.Estimate(Notes, Notes);

        Assert.IsNotNull(estimate);
        Assert.AreEqual(0, estimate.OffsetMilliseconds, 1e-9);
        Assert.AreEqual(0, estimate.SpreadMilliseconds, 1e-9);
        Assert.AreEqual(Notes.Length, estimate.Kept);
    }

    [TestMethod]
    public void ADeviceThatReportsLateGivesAPositiveOffset()
    {
        var estimate = TapTest.Estimate(Notes, [.. Notes.Select(n => n + 0.045)]);

        Assert.AreEqual(45, estimate!.OffsetMilliseconds, 1e-6);
    }

    [TestMethod]
    public void TapsInTheLeadInAndFarFromAnyNoteAreIgnored()
    {
        // Tapping along with the count-in, and once between two notes, as well as 30 ms late on every note.
        double beat = CalibrationChart.BeatSeconds;
        double[] leadIn = [.. Enumerable.Range(0, CalibrationChart.DeviceLeadBeats - 1).Select(b => b * beat)];
        double between = Notes[3] + (beat / 2);

        var estimate = TapTest.Estimate(Notes, [.. leadIn, between, .. Notes.Select(n => n + 0.03)]);

        Assert.AreEqual(30, estimate!.OffsetMilliseconds, 1e-6);
        Assert.AreEqual(Notes.Length, estimate.Kept);
    }

    [TestMethod]
    public void OnlyTheClosestTapToANoteIsUsed()
    {
        // A double hit on every note: one 20 ms late, the other 200 ms late.
        var estimate = TapTest.Estimate(Notes, [.. Notes.SelectMany(n => new[] { n + 0.02, n + 0.2 })]);

        Assert.AreEqual(20, estimate!.OffsetMilliseconds, 1e-6);
        Assert.AreEqual(Notes.Length, estimate.Kept);
    }

    [TestMethod]
    public void TooFewTapsGiveNoEstimate()
    {
        Assert.IsNull(TapTest.Estimate(Notes, [.. Notes.Take(TapTest.MinimumKept - 1)]));
        Assert.IsNull(TapTest.Estimate(Notes, []));
        Assert.IsNull(TapTest.Estimate([], Notes));
        Assert.IsNotNull(TapTest.Estimate(Notes, [.. Notes.Take(TapTest.MinimumKept)]));
    }

    // A tap for each note, `offset` late plus jitter, two of them 150 ms off either way.
    private static double[] SimulatedTaps(Random random, double offset)
    {
        var outliers = new HashSet<int>();
        while (outliers.Count < 2)
            outliers.Add(random.Next(Notes.Length));

        int sign = 1;
        var taps = new double[Notes.Length];
        for (int n = 0; n < Notes.Length; n++)
        {
            if (outliers.Contains(n))
            {
                taps[n] = Notes[n] + offset + (sign * 0.15);
                sign = -sign;
            }
            else
            {
                taps[n] = Notes[n] + offset + (Gaussian(random) * 0.02);
            }
        }

        return taps;
    }

    // A standard normal value (Box–Muller).
    private static double Gaussian(Random random) =>
        Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble());
}
