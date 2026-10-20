using ChartShared.Audio.Playback;
using ChartShared.Core.Calibration;
using ChartShared.UI.ViewModels;

namespace ChartBuilder.Tests.Calibration;

[TestClass]
public sealed class AudioTapTestTests
{
    private const int Rate = 48000;

    /// <summary>
    /// AC C11.8: simulated taps on the 16 clicks, heard a known latency late, from a device with a known offset of its own,
    /// with normal jitter of σ = 20 ms and two outliers of ±150 ms. With the device's offset taken off each tap, as the tool
    /// does, over 1,000 seeded trials the median estimate is within 2 ms of the latency, and 95% are within 20 ms.
    /// </summary>
    [TestMethod]
    public void EstimatesTheLatencyOfSimulatedTapsLessTheDevicesOffset()
    {
        var random = new Random(1105);
        var clicks = AudioTapTest.ClickTimes(TimeSpan.FromSeconds(10));
        var misses = new List<double>();
        for (int trial = 0; trial < 1000; trial++)
        {
            double latency = (random.NextDouble() * 0.5) - 0.1; // from −100 to +400 ms
            double offset = (random.NextDouble() - 0.5) * 0.1; // within ±50 ms
            var taps = SimulatedTaps(random, clicks, latency + offset).Select(t => t - offset).ToArray();
            var estimate = AudioTapTest.Estimate(clicks, taps);

            Assert.IsNotNull(estimate);
            misses.Add(estimate.OffsetMilliseconds - (latency * 1000));
        }

        misses.Sort();
        double median = misses[misses.Count / 2];
        double within = misses.Count(m => Math.Abs(m) <= 20) / (double)misses.Count;
        Assert.IsLessThanOrEqualTo(2, Math.Abs(median), $"The median estimate was {median:F2} ms off.");
        Assert.IsGreaterThanOrEqualTo(0.95, within, $"{within:P1} of estimates were within 20 ms.");
    }

    [TestMethod]
    public void ALatencyOfMostOfHalfASecondIsTakenForTheClickBefore()
    {
        // Bluetooth: each tap is nearer the next click than its own, but counts against its own.
        var clicks = AudioTapTest.ClickTimes(TimeSpan.Zero);
        var estimate = AudioTapTest.Estimate(clicks, [.. clicks.Select(c => c + 0.45)]);

        Assert.AreEqual(450, estimate!.OffsetMilliseconds, 1e-6);
        Assert.AreEqual(AudioTapTest.ClickCount, estimate.Kept);
    }

    [TestMethod]
    public void TapsOnTheCountInAreIgnored()
    {
        var clicks = AudioTapTest.ClickTimes(TimeSpan.FromSeconds(10));
        double beat = CalibrationChart.BeatSeconds;
        double[] countIn = [.. Enumerable.Range(1, AudioTapTest.CountInBeats).Select(b => clicks[0] - (b * beat) + 0.1)];

        var estimate = AudioTapTest.Estimate(clicks, [.. countIn, .. clicks.Select(c => c + 0.1)]);

        Assert.AreEqual(100, estimate!.OffsetMilliseconds, 1e-6);
        Assert.AreEqual(AudioTapTest.ClickCount, estimate.Kept);
    }

    [TestMethod]
    public void TheClicksComeAfterAQuietBarAndABarCountedIn()
    {
        double beat = CalibrationChart.BeatSeconds;
        var from = TimeSpan.FromSeconds(10.2);
        var clicks = AudioTapTest.ClickTimes(from);

        Assert.AreEqual(10.2 + (AudioTapTest.ReadyBeats * beat), AudioTapTest.CountInStart(from), 1e-9);
        Assert.HasCount(AudioTapTest.ClickCount, clicks);
        for (int i = 0; i < clicks.Length; i++)
            Assert.AreEqual(10.2 + ((AudioTapTest.ReadyBeats + AudioTapTest.CountInBeats + i) * beat), clicks[i], 1e-9);
        Assert.AreEqual(clicks[^1] + beat, AudioTapTest.EndSeconds(clicks), 1e-9);
    }

    [TestMethod]
    public void TheTestsOwnClicksAreOnlyTheClicksToTap()
    {
        // As the tool plays them in place of its metronome: nothing while getting ready or counted in, then the clicks to
        // tap, each bar's first accented, and nothing after.
        var from = TimeSpan.FromSeconds(10.2);
        var clicks = AudioTapTest.ClickTimes(from);
        var schedule = CalibrationViewModel.TapClicks(from, Rate);

        Assert.AreEqual(AudioTapTest.ClickCount, schedule.Count);
        for (int i = 0; i < clicks.Length; i++)
        {
            Assert.AreEqual((long)Math.Round(clicks[i] * Rate), schedule.FrameAt(i), 1);
            Assert.AreEqual(i % 4 == 0, schedule.IsMeasureAt(i));
        }

        var sounds = new List<ScheduledSound>();
        schedule.SoundsBetween(0, schedule.FrameAt(0), sounds);
        Assert.IsEmpty(sounds);
    }

    // A tap for each click, `late` after it plus jitter, two of them 150 ms off either way.
    private static double[] SimulatedTaps(Random random, double[] clicks, double late)
    {
        var outliers = new HashSet<int>();
        while (outliers.Count < 2)
            outliers.Add(random.Next(clicks.Length));

        int sign = 1;
        var taps = new double[clicks.Length];
        for (int n = 0; n < clicks.Length; n++)
        {
            if (outliers.Contains(n))
            {
                taps[n] = clicks[n] + late + (sign * 0.15);
                sign = -sign;
            }
            else
            {
                taps[n] = clicks[n] + late + (Gaussian(random) * 0.02);
            }
        }

        return taps;
    }

    // A standard normal value (Box–Muller).
    private static double Gaussian(Random random) =>
        Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble());
}
