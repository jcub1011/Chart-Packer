using ChartShared.Audio.Analysis;
using ChartShared.Audio.Decoding;
using ChartShared.Audio.Playback;
using ChartShared.Audio.Waveforms;
using ChartShared.Core.Scanning;

namespace ChartShared.Core.Tests.Audio;

/// <summary>
/// The stems decoded once, up front, and the waveforms and analysis read from that decode rather than each decoding the
/// files again.
/// </summary>
[TestClass]
public sealed class StemDecoderTests
{
    private const int Rate = 8000;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private DirectoryInfo? _folder;

    public TestContext TestContext { get; set; } = null!;

    [TestCleanup]
    public void Cleanup()
    {
        // A decoder hands its stems over before it has closed the files it read them from.
        for (int attempt = 0; _folder is not null; attempt++)
        {
            try
            {
                _folder.Delete(recursive: true);
                return;
            }
            catch (IOException) when (attempt < 50)
            {
                Thread.Sleep(50);
            }
        }
    }

    [TestMethod]
    public async Task EachStemIsDecodedOnceAndHandedToWhateverAsks()
    {
        var tracks = Tracks(("song.wav", 0.5f), ("bass.wav", 0.25f));
        using var cache = new DecodedAudioCache();
        using var decoder = new StemDecoder(null, tracks, cache);

        decoder.Start();
        var song = await Get(decoder, tracks[0]);
        var again = await Get(decoder, tracks[0]);
        var bass = await Get(decoder, tracks[1]);

        Assert.AreSame(song, again, "The second ask gets the same decode.");
        Assert.IsTrue(await song!.Completion.WaitAsync(Timeout, TestContext.CancellationToken));
        Assert.IsTrue(await bass!.Completion.WaitAsync(Timeout, TestContext.CancellationToken));
        Assert.AreEqual(Rate * 3, song.LengthFrames);
        Assert.AreEqual(0.5f, Sample(song, 1234), 1e-3f, "The channels averaged.");
        Assert.AreEqual(0.25f, Sample(bass, Rate * 2), 1e-3f);
        Assert.AreEqual((song.SizeBytes + bass.SizeBytes), cache.SizeBytes);
    }

    [TestMethod]
    public async Task AStemAskedForBeforeTheDecodeStartsIsDecodedThen()
    {
        var tracks = Tracks(("song.wav", 0.5f));
        using var cache = new DecodedAudioCache();
        using var decoder = new StemDecoder(null, tracks, cache);

        var song = await Get(decoder, tracks[0]);

        Assert.IsNotNull(song);
        Assert.IsTrue(await song.Completion.WaitAsync(Timeout, TestContext.CancellationToken));
        Assert.IsTrue(cache.TryGet(song.Key, out _));
    }

    [TestMethod]
    public async Task AStemOfMoreThanTwoChannelsOrThatDoesntFitIsntDecoded()
    {
        string path = Path.Combine(Folder().FullName, "surround.wav");
        File.WriteAllBytes(path, TestAudio.Wav(Rate, Rate, 3, (_, _) => 0.25f));
        SongTrack[] tracks = [new("surround.wav", "surround", path), .. Tracks(("song.wav", 0.5f))];
        using var cache = new DecodedAudioCache(1000);
        using var decoder = new StemDecoder(null, tracks, cache);

        decoder.Start();

        Assert.IsNull(await Get(decoder, tracks[0]), "A mix folds more channels into two sides, which isn't their average.");
        Assert.IsNull(await Get(decoder, tracks[1]), "It needs more than the budget.");
        Assert.AreEqual(0, cache.SizeBytes);
    }

    [TestMethod]
    public async Task AStemWhoseDecodeWasStoppedElsewhereIsDecodedAgain()
    {
        var tracks = Tracks(("song.wav", 0.5f));
        using var cache = new DecodedAudioCache();
        var key = StemPeaksCache.KeyOf(null, tracks[0])!.Value;

        // As a decoder of the tracks before a change leaves it: stopped, but not yet let go of.
        using var stopped = new CancellationTokenSource();
        stopped.Cancel();
        var abandoned = cache.TryBegin(key, Rate * 3, Rate, Rate, stopped.Token)!;
        Assert.IsTrue(abandoned.IsAbandoned);

        using var decoder = new StemDecoder(null, tracks, cache);
        var asked = decoder.GetAsync(tracks[0], TestContext.CancellationToken);
        await Task.Delay(100, TestContext.CancellationToken);
        Assert.IsFalse(asked.IsCompleted, "It waits for the stopped decode to let go.");

        abandoned.Finish(whole: false);
        cache.Finished(abandoned);
        var song = await asked.WaitAsync(Timeout, TestContext.CancellationToken);

        Assert.IsNotNull(song);
        Assert.AreNotSame(abandoned, song);
        Assert.IsTrue(await song.Completion.WaitAsync(Timeout, TestContext.CancellationToken));
        Assert.AreEqual(0.5f, Sample(song, 100), 1e-3f);
    }

    [TestMethod]
    public async Task AStoppedDecoderKeepsNothingAndHandsOutNothing()
    {
        var tracks = Tracks(("song.wav", 0.5f));
        using var cache = new DecodedAudioCache();
        var decoder = new StemDecoder(null, tracks, cache);

        decoder.Dispose();
        decoder.Start();

        Assert.IsNull(await Get(decoder, tracks[0]));
        await Task.Delay(100, TestContext.CancellationToken);
        Assert.AreEqual(0, cache.SizeBytes);
    }

    [TestMethod]
    public async Task StartingReleasesTheStemsOfFilesThatArentTracksAnyMore()
    {
        var tracks = Tracks(("song.wav", 0.5f), ("bass.wav", 0.25f));
        using var cache = new DecodedAudioCache();
        using (var before = new StemDecoder(null, tracks, cache))
        {
            before.Start();
            await (await Get(before, tracks[0]))!.Completion;
            await (await Get(before, tracks[1]))!.Completion;
        }

        using var after = new StemDecoder(null, [tracks[0]], cache);
        after.Start();
        var song = await Get(after, tracks[0]);

        Assert.IsTrue(song!.IsComplete, "The song's stem was kept, and not decoded again.");
        Assert.IsFalse(cache.TryGet(StemPeaksCache.KeyOf(null, tracks[1])!.Value, out _), "The bass isn't a track now.");
    }

    [TestMethod]
    public async Task TheWaveformReadFromTheDecodeIsTheWaveformReadFromTheFile()
    {
        var tracks = Tracks(("song.wav", 0.5f));
        var fromFile = ReadFromFiles(tracks, WaveformResolution.High)["song.wav"];
        using var cache = new DecodedAudioCache();
        using var decoder = new StemDecoder(null, tracks, cache);
        decoder.Start();

        var fromDecode = await ChunkedPeaksRead.ReadDecodedAsync("song.wav", (await Get(decoder, tracks[0]))!, WaveformResolution.High, cancellationToken: TestContext.CancellationToken);

        Assert.IsNotNull(fromDecode);
        Assert.AreEqual(fromFile.Count, fromDecode.Count);
        Assert.AreSequenceEqual([.. fromFile.Max], [.. fromDecode.Max]);
        Assert.AreSequenceEqual([.. fromFile.Min], [.. fromDecode.Min]);
        Assert.AreSequenceEqual([.. fromFile.Rms], [.. fromDecode.Rms]);
        Assert.AreSequenceEqual(fromFile.Signal.ToArray(), fromDecode.Signal.ToArray());
    }

    [TestMethod]
    public async Task TheWaveformFillsInAsTheStemIsDecoded()
    {
        const int frames = Rate * 5;
        float Value(long n) => (float)Math.Sin(n * 0.01) * 0.8f;
        var expected = WaveformPeaks.Compute("stem", new TestAudio.FakeSource(Rate, 1, frames, (n, _) => Value(n)), WaveformResolution.High);
        var stem = new DecodedStem(DecodedAudioCacheTests.Key("a"), Rate, frames, Rate, CancellationToken.None);
        var progress = new List<int>();

        var reading = ChunkedPeaksRead.ReadDecodedAsync("stem", stem, WaveformResolution.High, p =>
        {
            lock (progress)
                progress.Add(p.ReadCount);
        }, TimeSpan.Zero, TestContext.CancellationToken);

        for (int i = 0; i < stem.ChunkCount; i++)
        {
            await Task.Delay(20, TestContext.CancellationToken);
            Assert.IsFalse(reading.IsCompleted, $"Chunk {i} isn't decoded yet.");
            var (from, to) = stem.ChunkRange(i);
            stem.Write(i, from, Enumerable.Range((int)from, (int)(to - from)).Select(n => Value(n)).ToArray());
            stem.EndChunk(i, to);
        }

        stem.Finish(whole: true);
        var peaks = await reading.WaitAsync(Timeout, TestContext.CancellationToken);

        Assert.IsNotNull(peaks);
        Assert.AreSequenceEqual([.. expected.Max], [.. peaks.Max]);
        Assert.AreSequenceEqual([.. expected.Rms], [.. peaks.Rms]);
        lock (progress)
            Assert.IsTrue(progress.Any(count => count > 0 && count < expected.Count), "Some was shown before all of it was decoded.");
    }

    [TestMethod]
    public async Task NoWaveformIsReadFromAStemWhoseDecodeFailed()
    {
        var stem = new DecodedStem(DecodedAudioCacheTests.Key("a"), Rate, Rate * 5, Rate, CancellationToken.None);
        var reading = ChunkedPeaksRead.ReadDecodedAsync("stem", stem, WaveformResolution.Low, cancellationToken: TestContext.CancellationToken);

        stem.EndChunk(0, Rate);
        stem.Finish(whole: false);

        Assert.IsNull(await reading.WaitAsync(Timeout, TestContext.CancellationToken), "It's read from the file instead.");
    }

    [TestMethod]
    public void AnalysisReadsTheDecodedStemAtItsOwnRateAndLength()
    {
        var tracks = Tracks(("song.wav", 0.5f));
        using var audio = SongAudio.Open(null, tracks, TestContext.CancellationToken);
        var key = StemPeaksCache.KeyOf(null, tracks[0])!.Value;

        // Shorter than the file, and of another level, so it's plain which was read.
        var stem = DecodedStem.Of(key, Enumerable.Repeat(0.125f, Rate).ToArray(), Rate);
        var input = AnalysisInput.Mix(audio, audio.Tracks, new Dictionary<SongTrack, DecodedStem> { [tracks[0]] = stem });

        Assert.AreEqual(Rate, input.LengthFrames, "Its length is the stem's, not the file's.");
        Assert.AreEqual(0.125f, Window(input, 100)[50], 1e-6f);
    }

    [TestMethod]
    public async Task AnalysisReadsAStemTheCacheHasReleasedFromItsFile()
    {
        var tracks = Tracks(("song.wav", 0.5f));
        using var cache = new DecodedAudioCache();
        using var decoder = new StemDecoder(null, tracks, cache);
        decoder.Start();
        var stem = (await Get(decoder, tracks[0]))!;
        await stem.Completion;
        using var audio = SongAudio.Open(null, tracks, TestContext.CancellationToken);
        var input = AnalysisInput.Mix(audio, audio.Tracks, new Dictionary<SongTrack, DecodedStem> { [tracks[0]] = stem }, cache);
        var cached = Window(input, 2000);

        cache.Retain([]);
        var fromFile = Window(input, 2000);

        Assert.AreEqual(0.5f, cached[1000], 1e-3f);
        for (int i = 0; i < cached.Length; i++)
            Assert.AreEqual(cached[i], fromFile[i], 1e-5f);
    }

    [TestMethod]
    public async Task AMixOfDecodedStemsIsTheMixOfThemDecodedFromTheirFiles()
    {
        var tracks = Tracks(("song.wav", 0.5f), ("bass.wav", 0.25f));
        using var cache = new DecodedAudioCache();
        using var decoder = new StemDecoder(null, tracks, cache);
        decoder.Start();
        var stems = new Dictionary<SongTrack, DecodedStem>();
        foreach (var track in tracks)
        {
            var stem = (await Get(decoder, track))!;
            Assert.IsTrue(await stem.Completion);
            stems[track] = stem;
        }

        using var audio = SongAudio.Open(null, tracks, TestContext.CancellationToken);
        var fromFiles = Window(AnalysisInput.Mix(audio, audio.Tracks), 2000);
        var fromDecode = Window(AnalysisInput.Mix(audio, audio.Tracks, stems, cache), 2000);

        Assert.AreEqual(0.75f, fromFiles[1000], 1e-3f);
        for (int i = 0; i < fromFiles.Length; i++)
            Assert.AreEqual(fromFiles[i], fromDecode[i], 1e-5f);
    }

    private static float[] Window(AnalysisInput input, int frames)
    {
        using var reader = input.Open();
        var window = new float[frames];
        WindowRead.Read(reader, 500, window);
        return window;
    }

    private static float Sample(DecodedStem stem, long frame)
    {
        using var source = new CachedMonoSource(stem);
        source.SeekToFrame(frame);
        return TestAudio.Read(source, 1)[0];
    }

    private Task<DecodedStem?> Get(StemDecoder decoder, SongTrack track) =>
        decoder.GetAsync(track, TestContext.CancellationToken).WaitAsync(Timeout, TestContext.CancellationToken);

    private Dictionary<string, StemPeaks> ReadFromFiles(SongTrack[] tracks, WaveformResolution resolution)
    {
        var peaks = new Dictionary<string, StemPeaks>();
        using var audio = SongAudio.Open(null, tracks, TestContext.CancellationToken);
        ChunkedPeaksRead.Read(audio, resolution, (opened, found) =>
        {
            lock (peaks)
                peaks[opened.Track.StoredName] = found;
        });
        return peaks;
    }

    private DirectoryInfo Folder() => _folder ??= Directory.CreateTempSubdirectory("chartpacker-decoded-");

    // Stereo three-second stems, the left channel at each level and the right at none, so their average is half of it.
    private SongTrack[] Tracks(params (string Name, float Level)[] stems) => [.. stems.Select(stem =>
    {
        string path = Path.Combine(Folder().FullName, stem.Name);
        File.WriteAllBytes(path, TestAudio.Wav(Rate * 3, Rate, 2, (_, c) => c == 0 ? stem.Level * 2 : 0f));
        return new SongTrack(stem.Name, Path.GetFileNameWithoutExtension(stem.Name), path);
    })];
}
