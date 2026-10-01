using ChartPacker.Audio.Waveforms;

namespace ChartPacker.Core.Tests.Audio;

[TestClass]
public sealed class WaveformResolutionTests
{
    [TestMethod]
    [DataRow(WaveformFidelity.Low, 50, 2000)]
    [DataRow(WaveformFidelity.Medium, 100, 2000)]
    [DataRow(WaveformFidelity.High, 200, 2000)]
    [DataRow(WaveformFidelity.Ultra, 400, 4000)]
    public void EachFidelityHasItsBucketsAndSamplesForMixing(WaveformFidelity fidelity, int bucketsPerSecond, int signalRate)
    {
        var resolution = WaveformResolution.Of(fidelity);

        Assert.AreEqual(bucketsPerSecond, resolution.BucketsPerSecond);
        Assert.AreEqual(signalRate, resolution.SignalRate);
        Assert.AreEqual(signalRate / bucketsPerSecond, resolution.TicksPerBucket);
    }

    [TestMethod]
    public void AFidelityNotKnownReadsAtHigh() => Assert.AreEqual(WaveformResolution.High, WaveformResolution.Of((WaveformFidelity)99));

    [TestMethod]
    [DataRow(0)]
    [DataRow(-50)]
    [DataRow(30)]
    public void AResolutionMustSampleAWholeNumberOfTimesABucket(int bucketsPerSecond) =>
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new WaveformResolution(bucketsPerSecond));

    [TestMethod]
    [DataRow(200)]
    [DataRow(400)]
    public void BucketsFollowTimeAtAFinerResolution(int bucketsPerSecond)
    {
        var resolution = new WaveformResolution(bucketsPerSecond);

        Assert.AreEqual(bucketsPerSecond, WaveformPeaks.Compute("x", new TestAudio.FakeSource(44100, 1, 44100, (_, _) => 0.1f), resolution).Count);
        var peaks = WaveformPeaks.Compute("x", new TestAudio.FakeSource(48000, 1, 48000, (_, _) => 0.1f), resolution);
        Assert.AreEqual(bucketsPerSecond, peaks.Count);
        Assert.AreEqual(resolution, peaks.Resolution);
        Assert.AreEqual(1.0, WaveformPeaks.Of(peaks).Seconds, 1e-9);
    }

    [TestMethod]
    public void AFinerResolutionShowsWhatACoarserOneBlursTogether()
    {
        // A click 2 ms long, 10 ms in: 20 ms buckets hold it whole; 2.5 ms ones place it.
        static TestAudio.FakeSource Click() => new(48000, 1, 4800, (n, _) => n is >= 480 and < 576 ? 0.8f : 0f);

        var low = WaveformPeaks.Compute("x", Click(), WaveformResolution.Low);
        var ultra = WaveformPeaks.Compute("x", Click(), WaveformResolution.Ultra);

        CollectionAssert.AreEqual(new[] { 0.8f, 0f, 0f, 0f, 0f }, low.Max.ToArray());
        Assert.AreEqual(4, ultra.Max.ToList().FindIndex(v => v > 0), "The click starts in the fifth 2.5 ms bucket.");
        Assert.AreEqual(1, ultra.Max.Count(v => v > 0));
    }

    [TestMethod]
    [DataRow(44100, 3 * 44100, 7)]
    [DataRow(48000, (2 * 48000) + 1, 100)]
    public void ChunksAtAFinerResolutionGiveThePeaksOfOneRead(int rate, int frames, int chunkBuckets)
    {
        var resolution = WaveformResolution.Ultra;
        var expected = WaveformPeaks.Compute("x", Wavy(rate, frames), resolution);

        var reader = new StemPeaksReader("x", Wavy(rate, frames), chunkBuckets: chunkBuckets, resolution: resolution);
        for (int i = reader.ChunkCount - 1; i >= 0; i--)
            reader.ReadChunk(i, Wavy(rate, frames));
        var actual = reader.Finish();

        Assert.AreEqual(resolution, actual.Resolution);
        CollectionAssert.AreEqual(expected.Max.ToArray(), actual.Max.ToArray());
        CollectionAssert.AreEqual(expected.Rms.ToArray(), actual.Rms.ToArray());
        var expectedMix = WaveformPeaks.Mix([new AudibleStem(expected, false, false, 0.5f), new AudibleStem(expected, false, false, 0.5f)]);
        var actualMix = WaveformPeaks.Mix([new AudibleStem(actual, false, false, 0.5f), new AudibleStem(actual, false, false, 0.5f)]);
        CollectionAssert.AreEqual(expectedMix.Max.ToArray(), actualMix.Max.ToArray());
    }

    [TestMethod]
    public void AChunkIsFifteenSecondsAtAnyResolution()
    {
        var source = new TestAudio.FakeSource(1000, 1, 60 * 1000, (_, _) => 0.5f);

        Assert.AreEqual(4, new StemPeaksReader("x", source).ChunkCount);
        Assert.AreEqual(4, new StemPeaksReader("x", source, resolution: WaveformResolution.Ultra).ChunkCount);
    }

    [TestMethod]
    public void TwoStemsMixedAtAFinerResolutionSumAsTheyDoAtACoarserOne()
    {
        var resolution = WaveformResolution.High;
        var mix = WaveformPeaks.Mix([Stem(0.25f, resolution), Stem(0.5f, resolution)]);

        Assert.AreEqual(resolution, mix.Resolution);
        Assert.IsTrue(mix.Max.All(v => Math.Abs(v - 0.75f) < 1e-3f));
        Assert.IsTrue(mix.Min.All(v => Math.Abs(v + 0.75f) < 1e-3f));
    }

    [TestMethod]
    public void StemsReadAtDifferentResolutionsArentMixed() =>
        Assert.ThrowsExactly<ArgumentException>(() => WaveformPeaks.Mix([Stem(0.25f, WaveformResolution.Low), Stem(0.5f, WaveformResolution.High)]));

    [TestMethod]
    public void LevelsAreThoseOfTheBucketsAcrossTheStretch()
    {
        var waveform = WaveformPeaks.Of(WaveformPeaks.Compute("x", Wavy(48000, 48000), WaveformResolution.High));

        foreach (var (first, last) in new[] { (0, 1), (3, 17), (190, 200), (0, 200), (150, 400), (-5, 3) })
        {
            int from = Math.Max(0, first), to = Math.Min(waveform.ReadCount, last);
            var bucketMin = waveform.Min.Skip(from).Take(to - from).ToList();
            var bucketMax = waveform.Max.Skip(from).Take(to - from).ToList();
            double squares = waveform.Rms.Skip(from).Take(to - from).Sum(r => (double)r * r);

            var (low, high, rms) = waveform.LevelsOf(first, last);

            Assert.AreEqual((double)Math.Min(0f, bucketMin.Min()), low, $"Low of {first}..{last}.");
            Assert.AreEqual((double)Math.Max(0f, bucketMax.Max()), high, $"High of {first}..{last}.");
            Assert.AreEqual(Math.Sqrt(squares / (to - from)), rms, 1e-9, $"Loudness of {first}..{last}.");
        }
    }

    [TestMethod]
    public void LevelsAreOnlyOfWhatHasBeenRead()
    {
        var snapshots = new List<StemPeaks>();
        WaveformPeaks.Compute("x", new TestAudio.FakeSource(1000, 1, 10000, (_, _) => 0.5f), snapshots.Add, TimeSpan.Zero, resolution: WaveformResolution.Medium);
        var partial = WaveformPeaks.Of(snapshots[0]);

        Assert.IsTrue(partial.ReadCount < partial.Count);
        Assert.AreEqual((0.0, 0.0, 0.0), partial.LevelsOf(partial.ReadCount, partial.Count));
        Assert.AreEqual(0.5, partial.LevelsOf(0, partial.Count).High);
    }

    [TestMethod]
    public void PeaksReadAtAnotherResolutionArentFoundInTheCache()
    {
        var cache = new StemPeaksCache(1024 * 1024);
        var key = new StemPeaksKey("C:\\songs\\guitar.wav", "guitar.wav", 100, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var peaks = WaveformPeaks.Compute("guitar", new TestAudio.FakeSource(1000, 1, 1000, (_, _) => 0.5f), WaveformResolution.High);

        cache.Add(key, peaks);

        Assert.IsTrue(cache.TryGet(key, WaveformResolution.High, out var found));
        Assert.AreSame(peaks, found);
        Assert.IsFalse(cache.TryGet(key, WaveformResolution.Low, out _));
    }

    // Stereo, with something different in every bucket.
    private static TestAudio.FakeSource Wavy(int rate, int frames) =>
        new(rate, 2, frames, (n, c) => (float)(0.8 * Math.Sin((n * (c == 0 ? 0.013 : 0.029)) + (n / 5000.0))));

    // A second holding a level of `level` and -level throughout, each sample of it sampled twice for mixing.
    private static AudibleStem Stem(float level, WaveformResolution resolution) =>
        new(WaveformPeaks.Compute("stem", new TestAudio.FakeSource(1000, 1, 1000, (n, _) => n % 2 == 0 ? level : -level), resolution), false, false);
}
