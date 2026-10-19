using ChartShared.Audio.Decoding;
using ChartShared.Audio.Encoding;
using ChartShared.Core.Jobs;
using ChartShared.Core.Packing;
using ChartShared.Core.Scanning;
using ChartShared.Core.Sng;

namespace ChartShared.Core.Tests.Audio;

/// <summary>Opus transcoding: WAV/MP3/Ogg stems encode to Ogg Opus that decodes to the same audio.</summary>
[TestClass]
public sealed class OpusTranscoderTests
{
    private DirectoryInfo _root = null!;

    [TestInitialize]
    public void CreateRoot() => _root = Directory.CreateTempSubdirectory("chartpacker-transcode-");

    [TestCleanup]
    public void DeleteRoot() => _root.Delete(recursive: true);

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void MonoWavRoundTrips()
    {
        string wav = WriteWav("song.wav", 48000, 1, (n, _) => 0.5f * MathF.Sin(2 * MathF.PI * 440 * n / 48000), frames: 48000);
        string opus = Path.Combine(_root.FullName, "song.opus");

        OpusTranscoder.TranscodeFile(wav, opus, 96, cancellationToken: TestContext.CancellationToken);

        using var decoded = AudioDecoder.Open(File.OpenRead(opus), "song.opus");
        Assert.AreEqual(48000, decoded.SampleRate);
        Assert.AreEqual(1, decoded.Channels);
        Assert.AreEqual(48000, decoded.LengthFrames);
        float[] samples = TestAudio.ReadAll(decoded);
        Assert.AreEqual(48000, samples.Length);

        double xy = 0, xx = 0, yy = 0;
        for (int i = 4800; i < 43200; i++)
        {
            double expected = 0.5 * Math.Sin(2 * Math.PI * 440 * i / 48000);
            xy += samples[i] * expected;
            xx += samples[i] * samples[i];
            yy += expected * expected;
        }

        Assert.IsGreaterThan(0.99, xy / Math.Sqrt(xx * yy));
    }

    [TestMethod]
    public void Stereo44kResamplesTo48k()
    {
        string wav = WriteWav("guitar.wav", 44100, 2, (n, c) => (float)TestAudio.Chirp((double)n / 44100, rising: c == 0), frames: 44100);
        string opus = Path.Combine(_root.FullName, "guitar.opus");

        OpusTranscoder.TranscodeFile(wav, opus, 96, cancellationToken: TestContext.CancellationToken);

        using var decoded = AudioDecoder.Open(File.OpenRead(opus), "guitar.opus");
        Assert.AreEqual(48000, decoded.SampleRate);
        Assert.AreEqual(2, decoded.Channels);
        Assert.IsTrue(Math.Abs(decoded.LengthFrames - 48000) <= 48, $"Length was {decoded.LengthFrames}.");
        Assert.IsGreaterThan(0.9, TestAudio.CorrelationWithChirp(TestAudio.ReadAll(decoded), 2, 48000));
    }

    [TestMethod]
    [DataRow("chirp-stereo-44100.mp3")]
    [DataRow("chirp-stereo-44100.ogg")]
    public void FixturesTranscodeTo48k(string fixture)
    {
        string opus = Path.Combine(_root.FullName, "song.opus");

        OpusTranscoder.TranscodeFile(TestAudio.Fixture(fixture), opus, 96, cancellationToken: TestContext.CancellationToken);

        using var decoded = AudioDecoder.Open(File.OpenRead(opus), "song.opus");
        Assert.AreEqual(48000, decoded.SampleRate);
        Assert.AreEqual(2, decoded.Channels);
        Assert.IsTrue(Math.Abs(decoded.LengthFrames - 48000) <= 144, $"Length was {decoded.LengthFrames}.");
        Assert.IsGreaterThan(0.85, TestAudio.CorrelationWithChirp(TestAudio.ReadAll(decoded), 2, 48000));
    }

    [TestMethod]
    public void EmptyWavThrows()
    {
        string wav = WriteWav("song.wav", 48000, 1, (_, _) => 0, frames: 0);
        string opus = Path.Combine(_root.FullName, "song.opus");

        Assert.ThrowsExactly<InvalidDataException>(() =>
            OpusTranscoder.TranscodeFile(wav, opus, 96, cancellationToken: TestContext.CancellationToken));
    }

    [TestMethod]
    public void ProgressReachesOne()
    {
        string wav = WriteWav("song.wav", 48000, 1, (n, _) => 0.5f * MathF.Sin(2 * MathF.PI * 440 * n / 48000), frames: 48000);
        string opus = Path.Combine(_root.FullName, "song.opus");
        var seen = new List<double>();

        OpusTranscoder.TranscodeFile(wav, opus, 96, new SyncProgress(p => seen.Add(p.Fraction)), TestContext.CancellationToken);

        Assert.IsNotEmpty(seen);
        Assert.AreEqual(1, seen[^1], delta: 1e-9);
        for (int i = 1; i < seen.Count; i++)
            Assert.IsGreaterThanOrEqualTo(seen[i - 1], seen[i], "Progress never goes back.");
    }

    [TestMethod]
    public async Task PackTranscoderRenamesStemsAndPassesOpusThrough()
    {
        WriteWav("guitar.wav", 48000, 1, (n, _) => 0.5f * MathF.Sin(2 * MathF.PI * 440 * n / 48000), frames: 24000);
        OpusTranscoder.TranscodeFile(
            Path.Combine(_root.FullName, "guitar.wav"), Path.Combine(_root.FullName, "song.opus"),
            96, cancellationToken: TestContext.CancellationToken);
        File.WriteAllText(Path.Combine(_root.FullName, "song.ini"), "[song]\nname = Test\n");
        var plan = await PackPlanner.PlanAsync(_root.FullName, cancellationToken: TestContext.CancellationToken);
        Assert.IsFalse(plan.HasErrors);
        string temp = Directory.CreateDirectory(Path.Combine(_root.FullName, "tmp")).FullName;

        var included = await new OpusPackTranscoder().TranscodeAsync(
            plan, temp, 96, progress: null, TestContext.CancellationToken);

        Assert.AreSequenceEqual(
            ["guitar.opus", "song.opus"],
            [.. included.Select(f => f.StoredName)],
            Microsoft.VisualStudio.TestTools.UnitTesting.SequenceOrder.InAnyOrder);
        var guitar = included.Single(f => f.StoredName == "guitar.opus");
        StringAssert.StartsWith(guitar.SourcePath, temp);
        using var decoded = AudioDecoder.Open(File.OpenRead(guitar.SourcePath), "guitar.opus");
        Assert.IsTrue(Math.Abs(decoded.LengthFrames - 24000) <= 48, $"Length was {decoded.LengthFrames}.");
    }

    [TestMethod]
    public async Task ClashingOpusNameThrows()
    {
        WriteWav("guitar.wav", 48000, 1, (n, _) => 0.1f, frames: 4800);
        File.Copy(Path.Combine(_root.FullName, "guitar.wav"), Path.Combine(_root.FullName, "guitar.opus"));
        File.WriteAllText(Path.Combine(_root.FullName, "song.ini"), "[song]\nname = Test\n");
        var plan = await PackPlanner.PlanAsync(_root.FullName, cancellationToken: TestContext.CancellationToken);
        string temp = Directory.CreateDirectory(Path.Combine(_root.FullName, "tmp")).FullName;

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new OpusPackTranscoder().TranscodeAsync(plan, temp, 96, progress: null, TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task PackJobWithTranscodeWritesOpusSng()
    {
        string song = Path.Combine(_root.FullName, "Song");
        Directory.CreateDirectory(song);
        File.WriteAllText(Path.Combine(song, "song.ini"), "[song]\nname = Test\n");
        File.WriteAllBytes(
            Path.Combine(song, "guitar.wav"),
            TestAudio.Wav(24000, 48000, 1, (n, _) => 0.5f * MathF.Sin(2 * MathF.PI * 440 * n / 48000)));
        var entries = SongLibraryScanner.Enumerate(_root.FullName, 2, TestContext.CancellationToken).ToList();
        var results = new List<JobItemResult>();
        var phases = new List<ExportProgress>();

        await SongJob.RunAsync(
            JobKind.Pack, entries, _root.FullName, _root.FullName, OverwritePolicy.Overwrite,
            new SyncProgress<JobItemResult>(results.Add), OutputLayout.Mirror,
            new PackOptions(TranscodeToOpus: true, OpusBitrateKbps: 96), new OpusPackTranscoder(),
            new SyncProgress<ExportProgress>(phases.Add), cancellationToken: TestContext.CancellationToken);

        var packed = results.Single();
        Assert.AreEqual(JobItemStatus.Succeeded, packed.Status, packed.ErrorMessage ?? string.Join("; ", packed.Findings.Select(f => f.Message)));
        Assert.IsTrue(File.Exists(packed.OutputPath));
        var read = SngReader.Read(packed.OutputPath!, TestContext.CancellationToken);
        Assert.IsFalse(read.HasErrors);
        Assert.AreSequenceEqual(["guitar.opus"], [.. read.Files.Select(f => f.Name)]);
        Assert.IsTrue(phases.Any(p => p.Phase == ExportPhase.Transcoding));
        Assert.IsTrue(phases.Any(p => p.Phase == ExportPhase.Packing));
        Assert.IsEmpty(Directory.GetFileSystemEntries(_root.FullName, "*.chartpacker-tmp", SearchOption.AllDirectories));
    }

    [TestMethod]
    public void LinearResamplerPassesThroughExactly()
    {
        using var resampler = new LinearResampler(48000, 48000, 2);
        var input = new float[2048 * 2];
        for (int i = 0; i < input.Length; i++)
            input[i] = (float)Math.Sin(i * 0.01);
        var output = new float[input.Length];

        int written = resampler.Convert(input, output, finished: true);

        Assert.AreEqual(input.Length / 2, written);
        CollectionAssert.AreEqual(input, output);
    }

    [TestMethod]
    public void LinearResamplerUpsamplesSine()
    {
        using var resampler = new LinearResampler(8000, 48000, 1);
        int frames = 8000;
        var input = new float[frames];
        for (int n = 0; n < frames; n++)
            input[n] = 0.5f * MathF.Sin(2 * MathF.PI * 440 * n / 8000);
        var output = new float[frames * 6];

        int written = resampler.Convert(input, output, finished: true);

        Assert.IsTrue(Math.Abs(written - (frames * 6)) <= 6, $"Wrote {written}.");
        double xy = 0, xx = 0, yy = 0;
        for (int i = 4800; i < output.Length - 4800; i++)
        {
            double expected = 0.5 * Math.Sin(2 * Math.PI * 440 * i / 48000);
            xy += output[i] * expected;
            xx += output[i] * output[i];
            yy += expected * expected;
        }

        Assert.IsGreaterThan(0.99, xy / Math.Sqrt(xx * yy));
    }

    private string WriteWav(string name, int rate, int channels, Func<int, int, float> value, int frames)
    {
        string path = Path.Combine(_root.FullName, name);
        File.WriteAllBytes(path, TestAudio.Wav(frames, rate, channels, value));
        return path;
    }

    /// <summary>Reports on the calling thread, so results are complete when the call returns.</summary>
    private sealed class SyncProgress(Action<TranscodeFileProgress> report) : IProgress<TranscodeFileProgress>
    {
        public void Report(TranscodeFileProgress value) => report(value);
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
