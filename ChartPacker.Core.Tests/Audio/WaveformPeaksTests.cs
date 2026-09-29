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

    [TestMethod]
    public void StemsOutOfPhaseCancel()
    {
        var mix = WaveformPeaks.Mix([Sine(48000, 100, 0.5, 0), Sine(48000, 100, 0.5, Math.PI)]);

        Assert.AreEqual(50, mix.Count);
        Assert.IsTrue(mix.Max.All(v => v < 1e-3f) && mix.Min.All(v => v > -1e-3f), $"The mix reached {mix.Max.Max()}.");
    }

    [TestMethod]
    public void PeaksThatDontCoincideAreNotAdded()
    {
        // A quarter-cycle apart, two sines of 0.5 sum to one of 0.5·√2, not 1. Sampled 20 times a cycle, the
        // highest sample can be up to 1.2% short of the peak.
        var mix = WaveformPeaks.Mix([Sine(48000, 100, 0.5, 0), Sine(48000, 100, 0.5, Math.PI / 2)]);

        Assert.AreEqual(0.5 * Math.Sqrt(2), mix.Max.Skip(1).Max(), 0.015);
        Assert.AreEqual(-0.5 * Math.Sqrt(2), mix.Min.Skip(1).Min(), 0.015);
    }

    [TestMethod]
    public void StemsAtDifferentRatesAreMixedByTime()
    {
        // The same sine at two rates, one inverted: sampled at the same instants, they cancel.
        var mix = WaveformPeaks.Mix([Sine(44100, 50, 0.5, 0), Sine(48000, 50, 0.5, Math.PI)]);

        Assert.AreEqual(50, mix.Count);
        Assert.IsTrue(mix.Max.All(v => v < 0.01f) && mix.Min.All(v => v > -0.01f), $"The mix reached {mix.Max.Max()}.");
    }

    [TestMethod]
    public void EachBucketOfTheMixCoversItsOwnTime()
    {
        // Positive for the first half second and negative for the second, at rates that divide it differently.
        static float Step(long n, int rate) => n < rate / 2 ? 0.25f : -0.25f;
        var a = new AudibleStem(WaveformPeaks.Compute("a", new TestAudio.FakeSource(44100, 1, 44100, (n, _) => Step(n, 44100))), false, false);
        var b = new AudibleStem(WaveformPeaks.Compute("b", new TestAudio.FakeSource(48000, 1, 48000, (n, _) => Step(n, 48000))), false, false);

        var mix = WaveformPeaks.Mix([a, b]);

        for (int i = 0; i < 50; i++)
        {
            Assert.AreEqual(i < 25 ? 0.5f : 0f, mix.Max[i], 1e-6f, $"Bucket {i}'s high.");
            Assert.AreEqual(i < 25 ? 0f : -0.5f, mix.Min[i], 1e-6f, $"Bucket {i}'s low.");
        }
    }

    [TestMethod]
    public void AStemHeardAloneGivesItsOwnPeaks()
    {
        // A single sample the 2 kHz grid misses still shows.
        var spike = WaveformPeaks.Compute("spike", new TestAudio.FakeSource(48000, 1, 960, (n, _) => n == 7 ? 0.9f : 0f));

        var mix = WaveformPeaks.Mix([new AudibleStem(spike, false, false, 0.5f), Stem(0.25f, muted: true)]);

        Assert.AreEqual(0.45f, mix.Max[0], 1e-6f);
    }

    [TestMethod]
    public void ProgressIsReportedAtTheStemsFullLengthAndAgreesWithTheResult()
    {
        var snapshots = new List<StemPeaks>();
        var source = new TestAudio.FakeSource(Rate, 1, 10000, (n, _) => n % 20 == 3 ? 0.5f : 0f);

        var peaks = WaveformPeaks.Compute("x", source, snapshots.Add, TimeSpan.Zero);

        Assert.AreEqual(500, peaks.Count);
        Assert.IsTrue(snapshots.Count >= 2, $"{snapshots.Count} snapshots.");
        int filled = 0;
        foreach (var snapshot in snapshots)
        {
            Assert.AreEqual(peaks.Count, snapshot.Count);
            int count = snapshot.Max.TakeWhile(v => v == 0.5f).Count();
            Assert.IsTrue(count > filled, "Each snapshot has more read than the last.");
            Assert.IsTrue(snapshot.Max.Skip(count).All(v => v == 0f), "What hasn't been read is silent.");
            filled = count;
        }

        CollectionAssert.AreEqual(WaveformPeaks.Compute("x", new TestAudio.FakeSource(Rate, 1, 10000, (n, _) => n % 20 == 3 ? 0.5f : 0f)).Max.ToArray(), peaks.Max.ToArray());
    }

    [TestMethod]
    public void ASourceLongerThanItSaysGivesAllItsPeaksAndEarlierSnapshotsKeepWhatTheyShowed()
    {
        // It says 6000 frames, 300 buckets, but has 10000: it outgrows what was set aside after the first read of 4096.
        var snapshots = new List<StemPeaks>();
        var source = new TestAudio.FakeSource(Rate, 1, 10000, (n, _) => n % 20 == 3 ? 0.5f : 0f) { LengthFrames = 6000 };

        var peaks = WaveformPeaks.Compute("x", source, snapshots.Add, TimeSpan.Zero);

        var expected = WaveformPeaks.Compute("x", new TestAudio.FakeSource(Rate, 1, 10000, (n, _) => n % 20 == 3 ? 0.5f : 0f));
        CollectionAssert.AreEqual(expected.Max.ToArray(), peaks.Max.ToArray());
        Assert.AreEqual(expected.SizeBytes, peaks.SizeBytes);

        var first = snapshots[0];
        Assert.AreEqual(300, first.Count);
        int read = first.Max.TakeWhile(v => v == 0.5f).Count();
        Assert.IsTrue(read is > 0 and < 300, $"{read} buckets read.");
        Assert.IsTrue(first.Max.Skip(read).All(v => v == 0f), "What was read after the snapshot doesn't show in it.");

        // Its samples stop where it does too: two of it mixed reach 1 only where it had been read, which takes
        // in the bucket it was part way through.
        var mix = WaveformPeaks.Mix([new AudibleStem(first, false, false), new AudibleStem(first, false, false)]);
        Assert.IsTrue(mix.Max.Take(read).All(v => v == 1f), "The read buckets sum.");
        Assert.IsTrue(mix.Max.Skip(read + 1).All(v => v == 0f), "The rest is silent.");
    }

    [TestMethod]
    public void ASourceShorterThanItSaysGivesOnlyWhatItHas()
    {
        var source = new TestAudio.FakeSource(Rate, 1, 1000, (_, _) => 0.5f) { LengthFrames = 5000 };

        var peaks = WaveformPeaks.Compute("x", source);

        var expected = WaveformPeaks.Compute("x", new TestAudio.FakeSource(Rate, 1, 1000, (_, _) => 0.5f));
        Assert.AreEqual(50, peaks.Count);
        CollectionAssert.AreEqual(expected.Max.ToArray(), peaks.Max.ToArray());
        Assert.AreEqual(expected.SizeBytes, peaks.SizeBytes);
    }

    [TestMethod]
    public void NoProgressIsReportedWhenTheLengthIsUnknown()
    {
        var snapshots = new List<StemPeaks>();
        var source = new TestAudio.FakeSource(Rate, 1, 10000, (_, _) => 0.5f) { LengthFrames = -1 };

        var peaks = WaveformPeaks.Compute("x", source, snapshots.Add, TimeSpan.Zero);

        Assert.AreEqual(500, peaks.Count);
        Assert.AreEqual(0, snapshots.Count);
    }

    // One second of a sine of `amplitude` at `hz`, starting at `phase`.
    private static AudibleStem Sine(int rate, double hz, double amplitude, double phase) =>
        new(
            WaveformPeaks.Compute("sine", new TestAudio.FakeSource(rate, 1, rate, (n, _) => (float)(amplitude * Math.Sin((2 * Math.PI * hz * n / rate) + phase)))),
            false,
            false);

    // A stem holding a level of `level` and -level throughout.
    private static AudibleStem Stem(float level, bool muted, bool solo = false, int frames = 20) =>
        new(
            WaveformPeaks.Compute("stem", new TestAudio.FakeSource(Rate, 1, frames, (n, _) => n % 2 == 0 ? level : -level)),
            muted,
            solo);
}
