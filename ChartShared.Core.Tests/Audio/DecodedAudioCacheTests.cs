using ChartShared.Audio.Decoding;
using ChartShared.Audio.Waveforms;

namespace ChartShared.Core.Tests.Audio;

/// <summary>The stems decoded once, as they're kept within the budget, filled chunk by chunk, and read.</summary>
[TestClass]
public sealed class DecodedAudioCacheTests
{
    private const int Rate = 8000;

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void TheLeastRecentlyUsedIsReleasedToStayWithinTheBudget()
    {
        using var cache = new DecodedAudioCache(2 * 4000);
        Keep(cache, Key("a"), 1000);
        Keep(cache, Key("b"), 1000);
        Assert.IsTrue(cache.TryGet(Key("a"), out _), "A is used, so B is the least recent.");

        Keep(cache, Key("c"), 1000);

        Assert.IsTrue(cache.TryGet(Key("a"), out _));
        Assert.IsFalse(cache.TryGet(Key("b"), out _));
        Assert.IsTrue(cache.TryGet(Key("c"), out _));
        Assert.AreEqual(8000, cache.SizeBytes);
    }

    [TestMethod]
    public void AStemBeingDecodedCountsAgainstTheBudgetAndIsntReleased()
    {
        using var cache = new DecodedAudioCache(2 * 4000);
        var a = Begin(cache, Key("a"), 1000);
        Keep(cache, Key("b"), 1000);

        Keep(cache, Key("c"), 1000);

        Assert.IsTrue(cache.TryGet(Key("a"), out var found), "It's being decoded.");
        Assert.AreSame(a, found);
        Assert.IsFalse(cache.TryGet(Key("b"), out _), "B was the oldest decoded.");
        Assert.IsNull(Begin(cache, Key("d"), 2000, expectNull: true), "Even with C released, A is in the way.");
        Assert.IsTrue(cache.TryGet(Key("c"), out _));
    }

    [TestMethod]
    public void AStemThatWontFitReleasesNothingTryingTo()
    {
        using var cache = new DecodedAudioCache(3 * 4000);
        Begin(cache, Key("a"), 1000);
        Begin(cache, Key("b"), 1000);
        Keep(cache, Key("c"), 1000);

        // Releasing C would leave 8 KB being decoded, and 8 KB more doesn't fit beside it.
        Assert.IsNull(Begin(cache, Key("d"), 2000, expectNull: true));

        Assert.IsTrue(cache.TryGet(Key("c"), out _), "C wasn't released for nothing.");
        Assert.AreEqual(12000, cache.SizeBytes);
    }

    [TestMethod]
    public void AStemOverTheWholeBudgetOrOneAlreadyThereOrWithoutFramesIsRefused()
    {
        using var cache = new DecodedAudioCache(4000);
        Begin(cache, Key("a"), 500);

        Assert.IsNull(Begin(cache, Key("b"), 1001, expectNull: true));
        Assert.IsNull(Begin(cache, Key("a"), 500, expectNull: true), "It's being decoded.");
        Assert.IsNull(Begin(cache, Key("c"), 0, expectNull: true));
        Assert.AreEqual(2000, cache.SizeBytes);
    }

    [TestMethod]
    public void AStemWhoseDecodeFailsIsReleasedAndItsRoomGivenBack()
    {
        using var cache = new DecodedAudioCache(4000);
        var a = Begin(cache, Key("a"), 1000)!;
        a.Write(0, 0, new float[500]);

        Assert.IsTrue(a.Finish(whole: false));
        cache.Finished(a);

        Assert.IsFalse(a.IsComplete);
        Assert.IsFalse(cache.TryGet(Key("a"), out _));
        Assert.AreEqual(0, cache.SizeBytes);
        Assert.IsNotNull(Begin(cache, Key("a"), 1000), "Its room was given back.");
    }

    [TestMethod]
    public void AStemLongerThanItSaidCountsAtItsLengthAndTheOthersMakeRoomForIt()
    {
        using var cache = new DecodedAudioCache(8200);
        Keep(cache, Key("b"), 1000);
        var a = Begin(cache, Key("a"), 1000, chunkFrames: 1000)!;

        // The only chunk is the last, which reads on to the end.
        a.Write(0, 0, Enumerable.Repeat(0.5f, 1100).ToArray());
        a.EndChunk(0, 1100);
        a.Finish(whole: true);
        cache.Finished(a);

        Assert.AreEqual(1100, a.LengthFrames);
        Assert.AreEqual(4400, a.SizeBytes);
        Assert.IsTrue(cache.TryGet(Key("a"), out _), "The one just decoded is kept.");
        Assert.IsFalse(cache.TryGet(Key("b"), out _), "B made room for it.");
        Assert.AreEqual(4400, cache.SizeBytes);
    }

    [TestMethod]
    public void AStemDecodedWithNoRoomLeftIsntKeptButIsWholeForWhatHasIt()
    {
        using var cache = new DecodedAudioCache(4000);
        var a = Begin(cache, Key("a"), 1000)!;
        cache.BudgetBytes = 0;
        Assert.IsTrue(cache.TryGet(Key("a"), out _), "It isn't released while it's decoded.");

        Fill(a, 1000, 0.25f);
        cache.Finished(a);

        Assert.IsFalse(cache.TryGet(Key("a"), out _));
        Assert.AreEqual(0, cache.SizeBytes);
        Assert.IsTrue(a.IsComplete);
        Assert.AreEqual(0.25f, TestAudio.Read(new CachedMonoSource(a), 1000)[999]);
    }

    [TestMethod]
    public void LoweringTheBudgetReleasesTheLeastRecentlyUsedAtOnce()
    {
        using var cache = new DecodedAudioCache(2 * 4000);
        Keep(cache, Key("a"), 1000);
        Keep(cache, Key("b"), 1000);

        cache.BudgetBytes = 4000;

        Assert.IsFalse(cache.TryGet(Key("a"), out _));
        Assert.IsTrue(cache.TryGet(Key("b"), out _));
        Assert.AreEqual(4000, cache.SizeBytes);
    }

    [TestMethod]
    public void WhatSharesTheBudgetComesFirst()
    {
        long shared = 0;
        using var cache = new DecodedAudioCache(2 * 4000) { SharedBytes = () => shared };
        Keep(cache, Key("a"), 1000);
        Keep(cache, Key("b"), 1000);

        shared = 4000;
        cache.Trim();

        Assert.IsFalse(cache.TryGet(Key("a"), out _), "The spectrograms took half.");
        Assert.IsTrue(cache.TryGet(Key("b"), out _));

        shared = 8000;
        Assert.IsNull(Begin(cache, Key("c"), 1, expectNull: true), "Nothing's left.");
    }

    [TestMethod]
    public void RetainingReleasesTheStemsOfOtherFiles()
    {
        using var cache = new DecodedAudioCache();
        Keep(cache, Key("a"), 1000);
        Keep(cache, Key("b"), 1000);
        var c = Begin(cache, Key("c"), 1000)!;

        cache.Retain([Key("b")]);

        Assert.IsFalse(cache.TryGet(Key("a"), out _));
        Assert.IsTrue(cache.TryGet(Key("b"), out _));
        Assert.IsTrue(cache.TryGet(Key("c"), out var found), "It's being decoded, so it goes when its decode stops.");
        Assert.AreSame(c, found);
    }

    [TestMethod]
    public void ClearingReleasesWhatsDecodedAndCountsWhatsBeingDecodedUntilItStops()
    {
        using var cache = new DecodedAudioCache();
        Keep(cache, Key("a"), 1000);
        var b = Begin(cache, Key("b"), 1000)!;

        cache.Clear();

        Assert.IsFalse(cache.TryGet(Key("a"), out _));
        Assert.AreEqual(4000, cache.SizeBytes, "B's memory is taken until its decode stops.");

        b.Finish(whole: false);
        cache.Finished(b);

        Assert.AreEqual(0, cache.SizeBytes);
    }

    [TestMethod]
    public void ADisposedCacheKeepsAndFindsNothing()
    {
        var cache = new DecodedAudioCache();
        Keep(cache, Key("a"), 1000);

        cache.Dispose();

        Assert.IsFalse(cache.TryGet(Key("a"), out _));
        Assert.IsNull(Begin(cache, Key("b"), 1000, expectNull: true));
        Assert.AreEqual(0, cache.SizeBytes);
    }

    [TestMethod]
    public void ChunksWrittenOnSeveralThreadsFillTheWholeStem()
    {
        // Longer than a block, so chunks meet inside blocks and a block spans chunks.
        const int frames = 200_000;
        var stem = new DecodedStem(Key("a"), Rate, frames, 30_000, CancellationToken.None);

        Parallel.For(0, stem.ChunkCount, chunk =>
        {
            var (from, to) = stem.ChunkRange(chunk);
            stem.Write(chunk, from, Enumerable.Range((int)from, (int)(to - from)).Select(n => (float)n).ToArray());
            stem.EndChunk(chunk, to);
        });
        Assert.IsTrue(stem.Finish(whole: true));

        Assert.AreEqual(frames, stem.LengthFrames);
        float[] all = TestAudio.ReadAll(new CachedMonoSource(stem), 7777);
        Assert.HasCount(frames, all);
        foreach (int n in new[] { 0, 29_999, 30_000, 65_535, 65_536, 131_072, 199_999 })
            Assert.AreEqual(n, all[n], $"Frame {n}.");
    }

    [TestMethod]
    public void FramesNotDecodedAreSilent()
    {
        var stem = new DecodedStem(Key("a"), Rate, 3000, 1000, CancellationToken.None);
        stem.Write(0, 0, Enumerable.Repeat(1f, 1000).ToArray());
        stem.EndChunk(0, 1000);
        stem.EndChunk(1, 1000);
        stem.Write(2, 2000, Enumerable.Repeat(1f, 1000).ToArray());
        stem.EndChunk(2, 3000);
        stem.Finish(whole: true);

        float[] all = TestAudio.ReadAll(new CachedMonoSource(stem));

        Assert.HasCount(3000, all);
        Assert.AreEqual(1f, all[999]);
        Assert.AreEqual(0f, all[1500], "Damaged audio ended the middle chunk.");
        Assert.AreEqual(1f, all[2000]);
    }

    [TestMethod]
    public void TheLastChunkReadsOnPastTheLengthTheDecoderSaid()
    {
        // The decoder said 3000; there are 3500.
        var source = new TestAudio.FakeSource(Rate, 2, 3500, (n, c) => c == 0 ? n : 0) { LengthFrames = 3000 };
        var stem = Decode(source, chunkFrames: 1000);

        Assert.IsTrue(stem.IsComplete);
        Assert.AreEqual(3500, stem.LengthFrames);
        float[] all = TestAudio.ReadAll(new CachedMonoSource(stem));
        Assert.HasCount(3500, all);
        Assert.AreEqual(3499 / 2f, all[3499]);
    }

    [TestMethod]
    [DataRow(2500)]
    [DataRow(1500)]
    public void AStemThatEndsEarlyEndsWhereTheLastChunkThatReadAnythingDid(int frames)
    {
        var source = new TestAudio.FakeSource(Rate, 1, frames, (_, _) => 0.5f) { LengthFrames = 3000 };
        var stem = Decode(source, chunkFrames: 1000);

        Assert.AreEqual(frames, stem.LengthFrames);
        Assert.HasCount(frames, TestAudio.ReadAll(new CachedMonoSource(stem)));
    }

    [TestMethod]
    public async Task AStretchIsReadableOnceTheChunksOverItAreDecoded()
    {
        var stem = new DecodedStem(Key("a"), Rate, 3000, 1000, CancellationToken.None);
        var first = stem.WhenReadable(0, 1000);
        var across = stem.WhenReadable(500, 1500);
        var last = stem.WhenReadable(2000, long.MaxValue);

        stem.EndChunk(0, 1000);
        await first.WaitAsync(TimeSpan.FromSeconds(5), TestContext.CancellationToken);
        Assert.IsFalse(across.IsCompleted, "Chunk 1 isn't decoded yet.");

        stem.EndChunk(1, 1000);
        stem.EndChunk(2, 3000);
        await Task.Delay(50, TestContext.CancellationToken);
        Assert.IsFalse(across.IsCompleted, "Chunk 1 ended early, so where the stem ends isn't known until it's done.");
        Assert.IsFalse(last.IsCompleted, "Past the length it said, only the whole stem tells.");

        stem.Finish(whole: true);
        await Task.WhenAll(across, last).WaitAsync(TimeSpan.FromSeconds(5), TestContext.CancellationToken);
    }

    [TestMethod]
    public async Task WhatWaitsOnAStemWhoseDecodeFailsIsLetGo()
    {
        var stem = new DecodedStem(Key("a"), Rate, 3000, 1000, CancellationToken.None);
        var waiting = stem.WhenReadable(1000, 2000);

        stem.Finish(whole: false);

        await waiting.WaitAsync(TimeSpan.FromSeconds(5), TestContext.CancellationToken);
        Assert.IsFalse(await stem.Completion);
        Assert.AreEqual(3000, stem.LengthFrames, "Not finished, it's as long as it said.");
    }

    [TestMethod]
    public void ACachedSourceReadsSeeksAndEndsAsAMonoSource()
    {
        float[] samples = [.. Enumerable.Range(0, 100).Select(i => (float)i)];
        using var source = new CachedMonoSource(DecodedStem.Of(Key("a"), samples, Rate));

        Assert.AreEqual(1, source.Channels);
        Assert.AreEqual(Rate, source.SampleRate);
        Assert.AreEqual(100, source.LengthFrames);
        Assert.AreSequenceEqual(samples[..30], TestAudio.Read(source, 30));

        source.SeekToFrame(90);
        var tail = new float[20];
        Assert.AreEqual(10, source.ReadFrames(tail));
        Assert.AreSequenceEqual(samples[90..], tail[..10]);
        Assert.AreEqual(0, source.ReadFrames(tail), "Nothing's left.");

        source.SeekToFrame(1000);
        Assert.AreEqual(0, source.ReadFrames(tail), "Past the end reads nothing.");
        source.SeekToFrame(-5);
        Assert.AreEqual(0f, TestAudio.Read(source, 1)[0], "Before the start is the start.");
    }

    [TestMethod]
    public void SourcesOverOneStemReadOnTheirOwn()
    {
        var stem = DecodedStem.Of(Key("a"), [.. Enumerable.Range(0, 100).Select(i => (float)i)], Rate);
        using var one = new CachedMonoSource(stem);
        using var other = new CachedMonoSource(stem);
        other.SeekToFrame(50);

        Assert.AreEqual(0f, TestAudio.Read(one, 1)[0]);
        Assert.AreEqual(50f, TestAudio.Read(other, 1)[0]);
        Assert.AreEqual(1f, TestAudio.Read(one, 1)[0]);
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
    public void DecodedInChunksItIsTheStemReadStraightThrough(string name, AudioBackend backend)
    {
        // Each chunk decodes from a little before its start, so the seams don't show in what the analysis reads: here,
        // one every 50 ms. Without the overlap, stereo Opus differs by over 1e-3 just after a seam.
        float[] straight;
        int channels, rate;
        long length;
        using (var source = TestAudio.OpenFixture(name, backend))
        {
            channels = source.Channels;
            rate = source.SampleRate;
            length = source.LengthFrames;
            straight = TestAudio.ReadAll(source);
        }

        var stem = new DecodedStem(Key("a"), rate, length, rate / 20, CancellationToken.None);
        for (int i = 0; i < stem.ChunkCount; i++)
        {
            using var source = TestAudio.OpenFixture(name, backend);
            StemDecoder.DecodeChunk(stem, i, source);
        }

        stem.Finish(whole: true);

        Assert.IsGreaterThan(10, stem.ChunkCount);
        float[] decoded = TestAudio.ReadAll(new CachedMonoSource(stem));
        Assert.HasCount(straight.Length / channels, decoded);
        for (int n = 0; n < decoded.Length; n++)
        {
            float sum = 0;
            for (int c = 0; c < channels; c++)
                sum += straight[(n * channels) + c];
            Assert.AreEqual(sum / channels, decoded[n], 1e-5f, $"Frame {n}.");
        }
    }

    internal static StemPeaksKey Key(string name) => new($"C:\\songs\\{name}.ogg", $"{name}.ogg", 100, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

    /// <summary>Decodes all of <paramref name="source"/> as the decoder does, chunk by chunk, every chunk from the one source.</summary>
    private static DecodedStem Decode(TestAudio.FakeSource source, long chunkFrames)
    {
        var stem = new DecodedStem(Key("a"), source.SampleRate, source.LengthFrames, chunkFrames, CancellationToken.None);
        for (int i = 0; i < stem.ChunkCount; i++)
            StemDecoder.DecodeChunk(stem, i, source);
        stem.Finish(whole: true);
        return stem;
    }

    private static DecodedStem? Begin(DecodedAudioCache cache, StemPeaksKey key, long frames, long chunkFrames = 1000, bool expectNull = false)
    {
        var stem = cache.TryBegin(key, frames, Rate, chunkFrames, CancellationToken.None);
        if (!expectNull)
            Assert.IsNotNull(stem);
        return stem;
    }

    private static void Fill(DecodedStem stem, int frames, float value)
    {
        for (int i = 0; i < stem.ChunkCount; i++)
        {
            var (from, to) = stem.ChunkRange(i);
            stem.Write(i, from, Enumerable.Repeat(value, (int)(Math.Min(to, frames) - from)).ToArray());
            stem.EndChunk(i, Math.Min(to, frames));
        }

        stem.Finish(whole: true);
    }

    private static void Keep(DecodedAudioCache cache, StemPeaksKey key, int frames)
    {
        var stem = Begin(cache, key, frames)!;
        Fill(stem, frames, 0f);
        cache.Finished(stem);
        Assert.IsTrue(cache.TryGet(key, out _));
    }
}
