using ChartPacker.Audio.Spectrum;

namespace ChartPacker.Core.Tests.Audio;

[TestClass]
public sealed class SpectrumTests
{
    private const int Rate = 48000;

    [TestMethod]
    [DataRow(1000.0)]
    [DataRow(100.0)]
    [DataRow(8000.0)]
    public void ASineLightsTheBandItIsIn(double hz)
    {
        float[] levels = Analyze(i => Math.Sin(2 * Math.PI * hz * i / Rate));

        int loudest = Array.IndexOf(levels, levels.Max());
        Assert.AreEqual(BandOf(hz), loudest, string.Join(", ", levels.Select(l => l.ToString("0.00"))));

        // Bands well away from it hardly move.
        for (int band = 0; band < levels.Length; band++)
        {
            if (Math.Abs(band - loudest) > 3)
                Assert.IsLessThan(0.2f, levels[band], $"Band {band} is at {levels[band]}.");
        }
    }

    [TestMethod]
    public void AFullScaleSineAtOneKilohertzIsAtTheTop()
    {
        float[] levels = Analyze(i => Math.Sin(2 * Math.PI * 1000 * i / Rate));

        Assert.AreEqual(1f, levels[BandOf(1000)], 0.05f);
    }

    [TestMethod]
    public void QuieterIsLower()
    {
        // 30 dB down is halfway down the 60 dB shown.
        float full = Analyze(i => Math.Sin(2 * Math.PI * 1000 * i / Rate))[BandOf(1000)];
        float quieter = Analyze(i => 0.0316 * Math.Sin(2 * Math.PI * 1000 * i / Rate))[BandOf(1000)];

        Assert.AreEqual(full - 0.5f, quieter, 0.01f);
    }

    [TestMethod]
    public void SilenceIsNothing()
    {
        Assert.IsTrue(Analyze(_ => 0).All(l => l == 0));
    }

    [TestMethod]
    public void TheWrongNumberOfSamplesOrLevelsIsRefused()
    {
        var analyzer = new SpectrumAnalyzer();

        Assert.ThrowsExactly<ArgumentException>(() => analyzer.Analyze(new float[100], Rate, new float[SpectrumAnalyzer.BandCount]));
        Assert.ThrowsExactly<ArgumentException>(() => analyzer.Analyze(new float[SpectrumAnalyzer.FftSize], Rate, new float[3]));
    }

    [TestMethod]
    public void TheSampleRateCanChange()
    {
        var analyzer = new SpectrumAnalyzer();
        var levels = new float[SpectrumAnalyzer.BandCount];
        var samples = new float[SpectrumAnalyzer.FftSize];

        for (int i = 0; i < samples.Length; i++)
            samples[i] = (float)Math.Sin(2 * Math.PI * 1000 * i / 44100);
        analyzer.Analyze(samples, 44100, levels);

        Assert.AreEqual(BandOf(1000), Array.IndexOf(levels, levels.Max()));
    }

    [TestMethod]
    public void BarsRiseAtOnceAndFallAtTheirRate()
    {
        var bars = new SpectrumBars(2);

        bars.Update([1f, 0.5f], TimeSpan.FromMilliseconds(16));
        Assert.AreEqual(1f, bars.Levels[0]);
        Assert.AreEqual(0.5f, bars.Levels[1]);

        bars.Update([], TimeSpan.FromSeconds(0.2));
        Assert.AreEqual(1f - (SpectrumBars.FallPerSecond * 0.2f), bars.Levels[0], 1e-5f);
        Assert.AreEqual(0.5f - (SpectrumBars.FallPerSecond * 0.2f), bars.Levels[1], 1e-5f);

        // A louder level than where the bar has fallen to takes it straight back up.
        bars.Update([0.9f, 0f], TimeSpan.FromMilliseconds(16));
        Assert.AreEqual(0.9f, bars.Levels[0]);
    }

    [TestMethod]
    public void CapsHoldAtThePeakThenFall()
    {
        var bars = new SpectrumBars(1);
        bars.Update([1f], TimeSpan.Zero);

        bars.Update([], TimeSpan.FromSeconds(0.3));
        Assert.AreEqual(1f, bars.Peaks[0]);
        Assert.IsLessThan(1f, bars.Levels[0]);

        bars.Update([], TimeSpan.FromSeconds(0.2));
        Assert.IsLessThan(1f, bars.Peaks[0]);
        Assert.IsGreaterThanOrEqualTo(bars.Levels[0], bars.Peaks[0]);
    }

    [TestMethod]
    public void BarsSettleWhenThereIsNothingToShow()
    {
        var bars = new SpectrumBars(3);
        Assert.IsTrue(bars.IsSettled);

        bars.Update([0.2f, 1f, 0.7f], TimeSpan.Zero);
        Assert.IsFalse(bars.IsSettled);

        for (int i = 0; i < 100; i++)
            bars.Update([], TimeSpan.FromMilliseconds(50));

        Assert.IsTrue(bars.IsSettled);
        Assert.IsTrue(bars.Levels.All(l => l == 0));
    }

    [TestMethod]
    public void ResettingDropsEverythingAtOnce()
    {
        var bars = new SpectrumBars(2);
        bars.Update([1f, 0.6f], TimeSpan.Zero);
        bars.Update([], TimeSpan.FromSeconds(0.3));

        bars.Reset();

        Assert.IsTrue(bars.IsSettled);
        Assert.IsTrue(bars.Levels.All(l => l == 0));

        // A new peak holds for the whole hold again, as if the old one had never been.
        bars.Update([0.5f, 0f], TimeSpan.Zero);
        bars.Update([], TimeSpan.FromSeconds(0.3));
        Assert.AreEqual(0.5f, bars.Peaks[0]);
    }

    [TestMethod]
    public void TheTapGivesTheFramesEndingWhereAsked()
    {
        var tap = new OutputTap(8);
        tap.Write(Stereo(0, 6), 0);
        tap.Write(Stereo(6, 6), 6);
        var mono = new float[4];

        // 12 frames written; the last 8, frames 4 to 11, are held.
        Assert.IsTrue(tap.TryRead(12, mono));
        Assert.AreSequenceEqual([8f, 9f, 10f, 11f], mono);
        Assert.IsTrue(tap.TryRead(8, mono));
        Assert.AreSequenceEqual([4f, 5f, 6f, 7f], mono);

        Assert.IsFalse(tap.TryRead(7, mono), "Frame 3 isn't held any more.");
        Assert.IsFalse(tap.TryRead(13, mono), "Frame 12 hasn't been written.");
        Assert.IsFalse(tap.TryRead(12, new float[9]), "More than the tap holds.");
    }

    [TestMethod]
    public void TheTapAveragesTheChannels()
    {
        var tap = new OutputTap(8);
        tap.Write([0.5f, -0.25f, 1f, 0f], 0);
        var mono = new float[2];

        Assert.IsTrue(tap.TryRead(2, mono));
        Assert.AreSequenceEqual([0.125f, 0.5f], mono);
    }

    [TestMethod]
    public void AClearedTapHoldsNothingUntilWrittenAgain()
    {
        var tap = new OutputTap(8);
        tap.Write(Stereo(0, 6), 0);
        var mono = new float[2];

        tap.Clear();
        Assert.IsFalse(tap.TryRead(6, mono));

        // After a seek, frames carry on from wherever the mixer is.
        tap.Write(Stereo(100, 3), 100);
        Assert.IsFalse(tap.TryRead(6, mono));
        Assert.IsTrue(tap.TryRead(103, mono));
        Assert.AreSequenceEqual([101f, 102f], mono);
    }

    /// <summary>The band a frequency falls in, from the bands' spacing: even in pitch from 40 Hz to 16 kHz.</summary>
    private static int BandOf(double hz) =>
        (int)Math.Floor(SpectrumAnalyzer.BandCount * Math.Log(hz / 40) / Math.Log(16000.0 / 40));

    private static float[] Analyze(Func<int, double> sample)
    {
        var samples = new float[SpectrumAnalyzer.FftSize];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = (float)sample(i);

        var levels = new float[SpectrumAnalyzer.BandCount];
        new SpectrumAnalyzer().Analyze(samples, Rate, levels);
        return levels;
    }

    // `frames` stereo frames from `first`, both channels holding the frame's number.
    private static float[] Stereo(int first, int frames) =>
        [.. Enumerable.Range(first, frames).SelectMany(n => new[] { (float)n, n })];
}
