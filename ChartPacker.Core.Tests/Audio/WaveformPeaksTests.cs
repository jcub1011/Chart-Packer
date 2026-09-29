using ChartPacker.Audio.Waveforms;

namespace ChartPacker.Core.Tests.Audio;

[TestClass]
public sealed class WaveformPeaksTests
{
    // 1000 Hz makes a bucket exactly 20 frames.
    private const int Rate = 1000;

    [TestMethod]
    public void EachBucketHoldsTheLowestAndHighestSample()
    {
        var source = new TestAudio.FakeSource(Rate, 1, 60, (n, _) => n switch
        {
            5 => 0.5f,
            25 => -0.25f,
            27 => 0.75f,
            _ => 0.1f,
        });

        var peaks = WaveformPeaks.Compute("guitar", source);

        Assert.AreEqual("guitar", peaks.Name);
        CollectionAssert.AreEqual(new[] { 0f, -0.25f, 0f }, peaks.Min.ToArray());
        CollectionAssert.AreEqual(new[] { 0.5f, 0.75f, 0.1f }, peaks.Max.ToArray());
    }

    [TestMethod]
    [DataRow(100, 5)]
    [DataRow(101, 6)]
    [DataRow(1, 1)]
    [DataRow(0, 0)]
    public void ABucketIsMadeForEveryTwentyMilliseconds(int frames, int expected) =>
        Assert.AreEqual(expected, WaveformPeaks.Compute("x", new TestAudio.FakeSource(Rate, 1, frames, (_, _) => 0.1f)).Count);

    [TestMethod]
    public void BucketsFollowTimeWhateverTheSampleRate()
    {
        // One second is 50 buckets at any rate, including one that doesn't divide evenly.
        Assert.AreEqual(50, WaveformPeaks.Compute("x", new TestAudio.FakeSource(44100, 1, 44100, (_, _) => 0.1f)).Count);
        Assert.AreEqual(50, WaveformPeaks.Compute("x", new TestAudio.FakeSource(48000, 1, 48000, (_, _) => 0.1f)).Count);
    }

    [TestMethod]
    public void ChannelsAreAveraged()
    {
        var source = new TestAudio.FakeSource(Rate, 2, 20, (_, c) => c == 0 ? 0.8f : -0.2f);

        var peaks = WaveformPeaks.Compute("x", source);

        Assert.AreEqual(0.3f, peaks.Max[0], 1e-6f);
    }

    [TestMethod]
    public void ADamagedStemGivesNoPeaksRatherThanThrowing()
    {
        var source = new TestAudio.FakeSource(Rate, 1, 100, (_, _) => 0.5f) { IsDamaged = true };

        Assert.AreEqual(0, WaveformPeaks.Compute("x", source).Count);
    }

    [TestMethod]
    public void CancellationStopsTheRead()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var source = new TestAudio.FakeSource(Rate, 1, 100, (_, _) => 0.5f);

        Assert.ThrowsExactly<OperationCanceledException>(() => WaveformPeaks.Compute("x", source, cts.Token));
    }

    [TestMethod]
    public void UnmutedStemsAreSummed()
    {
        var mix = WaveformPeaks.Mix([Stem(0.25f, muted: false), Stem(0.5f, muted: false)]);

        Assert.AreEqual(0.75f, mix.Max[0], 1e-6f);
        Assert.AreEqual(-0.75f, mix.Min[0], 1e-6f);
    }

    [TestMethod]
    public void MutedStemsAreLeftOut()
    {
        var mix = WaveformPeaks.Mix([Stem(0.25f, muted: false), Stem(0.5f, muted: true)]);

        Assert.AreEqual(0.25f, mix.Max[0], 1e-6f);
    }

    [TestMethod]
    public void SoloedStemsAreTheOnlyOnesHeardEvenWhenMuted()
    {
        var mix = WaveformPeaks.Mix([Stem(0.25f, muted: false), Stem(0.5f, muted: true, solo: true)]);

        Assert.AreEqual(0.5f, mix.Max[0], 1e-6f);
    }

    [TestMethod]
    public void AStemsVolumeScalesItsPeaks()
    {
        var mix = WaveformPeaks.Mix([Stem(0.5f, muted: false) with { Volume = 0.5f }, Stem(0.25f, muted: false)]);

        Assert.AreEqual(0.5f, mix.Max[0], 1e-6f);
        Assert.AreEqual(-0.5f, mix.Min[0], 1e-6f);
    }

    [TestMethod]
    public void TheSumIsClipped()
    {
        var mix = WaveformPeaks.Mix([Stem(0.75f, muted: false), Stem(0.75f, muted: false)]);

        Assert.AreEqual(1f, mix.Max[0]);
        Assert.AreEqual(-1f, mix.Min[0]);
    }

    [TestMethod]
    public void MutingEverythingKeepsTheLengthWithNothingHeard()
    {
        var mix = WaveformPeaks.Mix([Stem(0.5f, muted: true, frames: 60)]);

        Assert.AreEqual(3, mix.Count);
        Assert.IsTrue(mix.Max.All(v => v == 0f));
        Assert.IsTrue(mix.Min.All(v => v == 0f));
    }

    [TestMethod]
    public void ShorterStemsEndBeforeTheLongerOne()
    {
        var mix = WaveformPeaks.Mix([Stem(0.25f, muted: false, frames: 20), Stem(0.5f, muted: false, frames: 60)]);

        CollectionAssert.AreEqual(new[] { 0.75f, 0.5f, 0.5f }, mix.Max.ToArray());
        Assert.AreEqual(0.06, mix.Seconds, 1e-9);
    }

    [TestMethod]
    public void NoStemsGiveAnEmptyWaveform() => Assert.AreEqual(0, WaveformPeaks.Mix([]).Count);

    // A stem holding a level of `level` and -level throughout.
    private static AudibleStem Stem(float level, bool muted, bool solo = false, int frames = 20) =>
        new(
            WaveformPeaks.Compute("stem", new TestAudio.FakeSource(Rate, 1, frames, (n, _) => n % 2 == 0 ? level : -level)),
            muted,
            solo);
}
