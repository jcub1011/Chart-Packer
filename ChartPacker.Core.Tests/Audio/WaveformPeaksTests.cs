using ChartPacker.Audio.Decoding;
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

    [TestMethod]
    public void EachBucketHoldsTheRootMeanSquareOfItsSamples()
    {
        // A square wave is as loud as its peaks; a sine is 1/√2 of them.
        Assert.AreEqual(0.5f, Stem(0.5f, muted: false).Peaks.Rms[0], 1e-6f);

        var sine = Sine(48000, 100, 0.5, 0).Peaks;
        Assert.IsTrue(sine.Rms.All(v => Math.Abs(v - (0.5 / Math.Sqrt(2))) < 1e-3), $"The loudness ran from {sine.Rms.Min()} to {sine.Rms.Max()}.");
    }

    [TestMethod]
    public void TheLoudnessOfSummedStemsIsThatOfTheSum()
    {
        var inPhase = WaveformPeaks.Mix([Stem(0.25f, muted: false), Stem(0.5f, muted: false)]);
        var outOfPhase = WaveformPeaks.Mix([Sine(48000, 100, 0.5, 0), Sine(48000, 100, 0.5, Math.PI)]);

        Assert.AreEqual(0.75f, inPhase.Rms[0], 1e-6f);
        Assert.IsTrue(outOfPhase.Rms.All(v => v < 1e-3f), $"The mix's loudness reached {outOfPhase.Rms.Max()}.");
    }

    [TestMethod]
    public void AStemsVolumeScalesItsLoudness()
    {
        var alone = WaveformPeaks.Mix([Stem(0.5f, muted: false) with { Volume = 0.5f }, Stem(0.25f, muted: true)]);
        var mixed = WaveformPeaks.Mix([Stem(0.5f, muted: false) with { Volume = 0.5f }, Stem(0.25f, muted: false)]);

        Assert.AreEqual(0.25f, alone.Rms[0], 1e-6f);
        Assert.AreEqual(0.5f, mixed.Rms[0], 1e-6f);
    }

    [TestMethod]
    public void SnapshotsSayHowMuchWasReadAndTheResultIsAllRead()
    {
        var snapshots = new List<StemPeaks>();
        var source = new TestAudio.FakeSource(Rate, 1, 10000, (n, _) => n % 20 == 3 ? 0.5f : 0f);

        var peaks = WaveformPeaks.Compute("x", source, snapshots.Add, TimeSpan.Zero);

        int read = 0;
        foreach (var snapshot in snapshots)
        {
            Assert.IsTrue(snapshot.ReadCount > read && snapshot.ReadCount < snapshot.Count, $"{snapshot.ReadCount} of {snapshot.Count} read.");
            Assert.IsTrue(snapshot.Max.Take(snapshot.ReadCount).All(v => v == 0.5f), "What it says was read was.");
            Assert.IsTrue(snapshot.Rms.Skip(snapshot.ReadCount).All(v => v == 0f), "What hasn't been read is silent.");
            read = snapshot.ReadCount;
        }

        Assert.AreEqual(peaks.Count, peaks.ReadCount);
        var expected = WaveformPeaks.Compute("x", new TestAudio.FakeSource(Rate, 1, 10000, (n, _) => n % 20 == 3 ? 0.5f : 0f));
        CollectionAssert.AreEqual(expected.Rms.ToArray(), peaks.Rms.ToArray());
    }

    [TestMethod]
    public void TheMixIsKnownAsFarAsTheHeardStemReadLeastFar()
    {
        var reading = new AudibleStem(FirstSnapshot(), false, false);
        var done = Stem(0.25f, muted: false, frames: 20);

        // A stem read to its end is known all the way, though it's shorter.
        Assert.AreEqual(reading.Peaks.ReadCount, WaveformPeaks.Mix([reading, done]).ReadCount);

        // What isn't heard doesn't hold it back...
        var muted = WaveformPeaks.Mix([reading with { IsMuted = true }, done]);
        Assert.AreEqual(muted.Count, muted.ReadCount);

        // ...unless nothing is.
        Assert.AreEqual(reading.Peaks.ReadCount, WaveformPeaks.Mix([reading with { IsMuted = true }, done with { IsMuted = true }]).ReadCount);
        Assert.AreEqual(0, Waveform.Empty.ReadCount);
    }

    [TestMethod]
    public void ThePeakIsOfWhatHasBeenReadOnly()
    {
        var snapshots = new List<StemPeaks>();
        var peaks = WaveformPeaks.Compute("x", new TestAudio.FakeSource(Rate, 1, 10000, (n, _) => n >= 9000 ? 0.75f : -0.25f), snapshots.Add, TimeSpan.Zero);
        var snapshot = snapshots[0];

        Assert.IsLessThan(snapshot.Count, snapshot.ReadCount);
        Assert.AreEqual(0.25f, snapshot.Peak);
        Assert.AreEqual(0.25f, WaveformPeaks.Of(snapshot).Peak);
        Assert.AreEqual(0.75f, peaks.Peak);
        Assert.AreEqual(0.75f, WaveformPeaks.Of(peaks).Peak);
        Assert.AreEqual(0f, Waveform.Empty.Peak);
    }

    [TestMethod]
    public void TheMixsPeakIsOfWhatIsHeard()
    {
        Assert.AreEqual(0.75f, WaveformPeaks.Mix([Stem(0.25f, muted: false), Stem(0.5f, muted: false)]).Peak, 1e-3f);
        Assert.AreEqual(0.25f, WaveformPeaks.Mix([Stem(0.25f, muted: false), Stem(0.5f, muted: true)]).Peak, 1e-3f);
        Assert.AreEqual(0.25f, WaveformPeaks.Mix([Stem(0.5f, muted: false) with { Volume = 0.5f }]).Peak, 1e-3f);
    }

    [TestMethod]
    public void OfGivesTheStemAloneAsFarAsItWasRead()
    {
        var snapshot = FirstSnapshot();
        var peaks = Stem(0.5f, muted: false, frames: 60).Peaks;

        var partial = WaveformPeaks.Of(snapshot);
        var whole = WaveformPeaks.Of(peaks);

        Assert.AreEqual(snapshot.Count, partial.Count);
        Assert.AreEqual(snapshot.ReadCount, partial.ReadCount);
        CollectionAssert.AreEqual(snapshot.Max.ToArray(), partial.Max.ToArray());
        Assert.AreEqual(3, whole.ReadCount);
        CollectionAssert.AreEqual(peaks.Min.ToArray(), whole.Min.ToArray());
        CollectionAssert.AreEqual(peaks.Rms.ToArray(), whole.Rms.ToArray());
        Assert.AreEqual(0.06, whole.ReadSeconds, 1e-9);
    }

    [TestMethod]
    [DataRow(Rate, 10000, 7)]
    [DataRow(Rate, 10000, 100)]
    [DataRow(Rate, 10001, 100)]
    [DataRow(Rate, 9999, 100)]
    [DataRow(Rate, 100, 100)]
    [DataRow(44100, 3 * 44100, 20)]
    [DataRow(48000, (2 * 48000) + 1, 13)]
    [DataRow(22050, 22050, 3)]
    public void ChunksReadInAnyOrderGiveTheSamePeaksAsOneRead(int rate, int frames, int chunkBuckets)
    {
        var expected = WaveformPeaks.Compute("x", Wavy(rate, frames));

        var reader = new StemPeaksReader("x", Wavy(rate, frames), chunkBuckets: chunkBuckets);
        for (int i = reader.ChunkCount - 1; i >= 0; i--)
            reader.ReadChunk(i, Wavy(rate, frames));

        AssertSamePeaks(expected, reader.Finish());
    }

    [TestMethod]
    public void ChunksReadSideBySideGiveTheSamePeaksAsOneRead()
    {
        var expected = WaveformPeaks.Compute("x", Wavy(44100, 20 * 44100));

        var reader = new StemPeaksReader("x", Wavy(44100, 20 * 44100), chunkBuckets: 50);
        Parallel.For(0, reader.ChunkCount, new ParallelOptions { MaxDegreeOfParallelism = 4 }, i => reader.ReadChunk(i, Wavy(44100, 20 * 44100)));

        Assert.AreEqual(20, reader.ChunkCount);
        AssertSamePeaks(expected, reader.Finish());
    }

    [TestMethod]
    public void AStemOfUnknownLengthIsOneChunk()
    {
        static TestAudio.FakeSource Source() => new(Rate, 1, 10000, (_, _) => 0.5f) { LengthFrames = -1 };
        var reader = new StemPeaksReader("x", Source(), chunkBuckets: 7);

        Assert.AreEqual(1, reader.ChunkCount);
        reader.ReadChunk(0, Source());
        AssertSamePeaks(WaveformPeaks.Compute("x", new TestAudio.FakeSource(Rate, 1, 10000, (_, _) => 0.5f)), reader.Finish());
    }

    [TestMethod]
    public void SnapshotsOfChunksShowOnlyWhatIsReadFromTheStart()
    {
        // Five chunks of 100 buckets, 2000 frames: each is one read, followed by a snapshot, before its last bucket is done.
        var snapshots = new List<StemPeaks>();
        static TestAudio.FakeSource Source() => new(Rate, 1, 10000, (n, _) => n % 20 == 3 ? 0.5f : 0f);
        var reader = new StemPeaksReader("x", Source(), snapshots.Add, TimeSpan.Zero, chunkBuckets: 100);

        foreach (int i in new[] { 0, 2, 1, 4, 3 })
            reader.ReadChunk(i, Source());

        // Chunk 2 adds nothing while chunk 1 isn't read, nor chunk 4 while 3 isn't.
        CollectionAssert.AreEqual(new[] { 99, 100, 199, 300, 399 }, snapshots.Select(s => s.ReadCount).ToArray());
        foreach (var snapshot in snapshots)
        {
            Assert.AreEqual(500, snapshot.Count);
            Assert.IsTrue(snapshot.Max.Take(snapshot.ReadCount).All(v => v == 0.5f), "What it says was read was.");
            Assert.IsTrue(snapshot.Max.Skip(snapshot.ReadCount).All(v => v == 0f), "What hasn't been read from the start is silent.");
        }

        AssertSamePeaks(WaveformPeaks.Compute("x", Source()), reader.Finish());
    }

    [TestMethod]
    public void ADamagedChunkIsSilentAndTheChunksAfterItAreKept()
    {
        static TestAudio.FakeSource Source() => new(Rate, 1, 6000, (_, _) => 0.5f);
        var reader = new StemPeaksReader("x", Source(), chunkBuckets: 100);

        reader.ReadChunk(0, Source());
        reader.ReadChunk(1, new TestAudio.FakeSource(Rate, 1, 6000, (_, _) => 0.5f) { IsDamaged = true });
        reader.ReadChunk(2, Source());
        var peaks = reader.Finish();

        Assert.AreEqual(300, peaks.Count);
        Assert.IsTrue(peaks.Max.Take(100).All(v => v == 0.5f));
        Assert.IsTrue(peaks.Max.Skip(100).Take(100).All(v => v == 0f));
        Assert.IsTrue(peaks.Max.Skip(200).All(v => v == 0.5f));
    }

    [TestMethod]
    public void TheLastChunkReadsOnPastTheLengthTheSourceSaid()
    {
        // It says 6000 frames, three chunks of 100 buckets, but has 10000.
        static TestAudio.FakeSource Source() => new(Rate, 1, 10000, (n, _) => n % 20 == 3 ? 0.5f : 0f) { LengthFrames = 6000 };
        var reader = new StemPeaksReader("x", Source(), chunkBuckets: 100);

        Assert.AreEqual(3, reader.ChunkCount);
        for (int i = 0; i < reader.ChunkCount; i++)
            reader.ReadChunk(i, Source());

        var expected = WaveformPeaks.Compute("x", new TestAudio.FakeSource(Rate, 1, 10000, (n, _) => n % 20 == 3 ? 0.5f : 0f));
        var peaks = reader.Finish();
        AssertSamePeaks(expected, peaks);
        Assert.AreEqual(expected.SizeBytes, peaks.SizeBytes);
    }

    [TestMethod]
    public void ChunksEndWhereTheAudioDoesWhenTheSourceIsShorterThanItSaid()
    {
        static TestAudio.FakeSource Source() => new(Rate, 1, 1000, (_, _) => 0.5f) { LengthFrames = 5000 };
        var reader = new StemPeaksReader("x", Source(), chunkBuckets: 40);
        for (int i = 0; i < reader.ChunkCount; i++)
            reader.ReadChunk(i, Source());

        var expected = WaveformPeaks.Compute("x", new TestAudio.FakeSource(Rate, 1, 1000, (_, _) => 0.5f));
        var peaks = reader.Finish();
        AssertSamePeaks(expected, peaks);
        Assert.AreEqual(expected.SizeBytes, peaks.SizeBytes);
    }

    [TestMethod]
    public void CancellationStopsAChunk()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var reader = new StemPeaksReader("x", new TestAudio.FakeSource(Rate, 1, 10000, (_, _) => 0.5f), chunkBuckets: 100);

        Assert.ThrowsExactly<OperationCanceledException>(() => reader.ReadChunk(1, new TestAudio.FakeSource(Rate, 1, 10000, (_, _) => 0.5f), cts.Token));
    }

    [TestMethod]
    public void AChunkIsReadOnlyOnce()
    {
        var reader = new StemPeaksReader("x", new TestAudio.FakeSource(Rate, 1, 10000, (_, _) => 0.5f), chunkBuckets: 100);
        reader.ReadChunk(1, new TestAudio.FakeSource(Rate, 1, 10000, (_, _) => 0.5f));

        Assert.ThrowsExactly<InvalidOperationException>(() => reader.ReadChunk(1, new TestAudio.FakeSource(Rate, 1, 10000, (_, _) => 0.5f)));
    }

    [TestMethod]
    [DataRow("chirp-stereo-44100.ogg", AudioBackend.Managed)]
    [DataRow("chirp-stereo-48000.opus", AudioBackend.Managed)]
    [DataRow("chirp-mono-48000.opus", AudioBackend.Managed)]
    [DataRow("chirp-stereo-44100.mp3", AudioBackend.Managed)]
    [DataRow("chirp-stereo-44100.ogg", AudioBackend.Ffmpeg)]
    [DataRow("chirp-stereo-48000.opus", AudioBackend.Ffmpeg)]
    [DataRow("chirp-mono-48000.opus", AudioBackend.Ffmpeg)]
    [DataRow("chirp-stereo-44100.mp3", AudioBackend.Ffmpeg)]
    public void ChunksOfRealAudioGiveThePeaksOfOneRead(string name, AudioBackend backend)
    {
        StemPeaks expected;
        using (var source = TestAudio.OpenFixture(name, backend))
            expected = WaveformPeaks.Compute(name, source);

        // A second in chunks of 7 buckets: every chunk but the first seeks.
        StemPeaksReader reader;
        using (var source = TestAudio.OpenFixture(name, backend))
            reader = new StemPeaksReader(name, source, chunkBuckets: 7);
        Assert.AreEqual(8, reader.ChunkCount);
        Parallel.For(0, reader.ChunkCount, i =>
        {
            using var source = TestAudio.OpenFixture(name, backend);
            reader.ReadChunk(i, source);
        });

        // A seek lands within rounding of a straight read.
        AssertSamePeaks(expected, reader.Finish(), 2e-3f);
    }

    private static void AssertSamePeaks(StemPeaks expected, StemPeaks actual, float delta = 0f)
    {
        Assert.AreEqual(expected.Count, actual.Count);
        Assert.AreEqual(actual.Count, actual.ReadCount);
        AssertClose(expected.Min, actual.Min, delta, "Min");
        AssertClose(expected.Max, actual.Max, delta, "Max");
        AssertClose(expected.Rms, actual.Rms, delta, "Rms");

        // The samples for mixing, which two of a stem mixed are made from.
        var expectedMix = WaveformPeaks.Mix([new AudibleStem(expected, false, false, 0.5f), new AudibleStem(expected, false, false, 0.5f)]);
        var actualMix = WaveformPeaks.Mix([new AudibleStem(actual, false, false, 0.5f), new AudibleStem(actual, false, false, 0.5f)]);
        AssertClose(expectedMix.Max, actualMix.Max, delta, "Mixed max");
        AssertClose(expectedMix.Rms, actualMix.Rms, delta, "Mixed loudness");
    }

    private static void AssertClose(IReadOnlyList<float> expected, IReadOnlyList<float> actual, float delta, string what)
    {
        Assert.AreEqual(expected.Count, actual.Count, what);
        for (int i = 0; i < expected.Count; i++)
            Assert.AreEqual(expected[i], actual[i], delta, $"{what} of bucket {i}.");
    }

    // Stereo, with something different in every bucket.
    private static TestAudio.FakeSource Wavy(int rate, int frames) =>
        new(rate, 2, frames, (n, c) => (float)(0.8 * Math.Sin((n * (c == 0 ? 0.013 : 0.029)) + (n / 5000.0))));

    // The first progress snapshot of a stem ten seconds long: part read, 500 buckets long.
    private static StemPeaks FirstSnapshot()
    {
        var snapshots = new List<StemPeaks>();
        WaveformPeaks.Compute("reading", new TestAudio.FakeSource(Rate, 1, 10000, (n, _) => n % 2 == 0 ? 0.5f : -0.5f), snapshots.Add, TimeSpan.Zero);
        return snapshots[0];
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
