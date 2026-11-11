using ChartShared.Audio.Decoding;
using ChartShared.Audio.Encoding;
using ChartShared.Audio.Mixing;
using ChartShared.Core.Optimization;
using ChartShared.Core.Scanning;
using ChartShared.Core.Tests.Optimization;
using static ChartShared.Core.Tests.Optimization.OptimizationTestSupport;

namespace ChartShared.Core.Tests.Audio;

/// <summary>
/// <see cref="OpusAudioOptimizer"/> and the stream-based <see cref="OpusTranscoder.Transcode"/> under it, on audio generated
/// in code: round trips, the sampled estimate, the skip rules through the planner, stem mixdown through <see cref="OptimizeJob"/>
/// for a folder and a <c>.sng</c>, and the temp files left after cancel and failure.
/// </summary>
[TestClass]
public sealed class OpusAudioOptimizerTests
{
    private DirectoryInfo _root = null!;

    [TestInitialize]
    public void CreateRoot() => _root = Directory.CreateTempSubdirectory("chartpacker-opusopt-");

    [TestCleanup]
    public void DeleteRoot() => _root.Delete(recursive: true);

    public TestContext TestContext { get; set; } = null!;

    private static readonly OptimizeOptions MixdownOptions = Only(OptimizeStrategy.StemMixdown, margin: 0);

    private static readonly OptimizeOptions AudioOptions = Only(OptimizeStrategy.Audio, margin: 0.1);

    // ---- The stream-based transcode ----------------------------------------------------------------------------------

    [TestMethod]
    public void TranscodeToAStreamRoundTrips()
    {
        byte[] wav = TestAudio.Wav(48000, 48000, 2, (n, c) => (float)TestAudio.Chirp((double)n / 48000, rising: c == 0));
        using var source = AudioDecoder.Open(new MemoryStream(wav), "guitar.wav");
        var output = new MemoryStream();

        OpusTranscoder.Transcode(source, output, 128, "guitar.wav", cancellationToken: TestContext.CancellationToken);

        using var decoded = AudioDecoder.Open(new MemoryStream(output.ToArray()), "guitar.opus");
        Assert.AreEqual(48000, decoded.SampleRate);
        Assert.AreEqual(2, decoded.Channels);
        Assert.AreEqual(48000, decoded.LengthFrames);
        Assert.IsGreaterThan(0.9, TestAudio.CorrelationWithChirp(TestAudio.ReadAll(decoded), 2, 48000));
        Assert.IsFalse(source.LengthFrames < 0);
    }

    [TestMethod]
    public void TranscodeWritesToAStreamThatCannotSeek()
    {
        byte[] wav = TestAudio.Wav(24000, 48000, 1, (n, _) => 0.5f * MathF.Sin(2 * MathF.PI * 440 * n / 48000));
        using var source = AudioDecoder.Open(new MemoryStream(wav), "a.wav");
        var seekable = new MemoryStream();
        OpusTranscoder.Transcode(source, seekable, 96, "a.wav", cancellationToken: TestContext.CancellationToken);

        using var again = AudioDecoder.Open(new MemoryStream(wav), "a.wav");
        var counted = new CountingStream();
        OpusTranscoder.Transcode(again, counted, 96, "a.wav", cancellationToken: TestContext.CancellationToken);

        Assert.AreEqual(seekable.Length, counted.BytesWritten, "The sink counts exactly the bytes a file would hold.");
    }

    [TestMethod]
    public void TranscodeFileMatchesTheStreamPathAndLeavesNothingOnFailure()
    {
        string wav = Write("guitar.wav", TestAudio.Wav(48000, 48000, 2, (n, c) => (float)TestAudio.Chirp((double)n / 48000, rising: c == 0)));
        string opus = Path.Combine(_root.FullName, "guitar.opus");

        OpusTranscoder.TranscodeFile(wav, opus, 128, cancellationToken: TestContext.CancellationToken);

        using var source = AudioDecoder.Open(File.OpenRead(wav), wav);
        var counted = new CountingStream();
        OpusTranscoder.Transcode(source, counted, 128, wav, cancellationToken: TestContext.CancellationToken);
        Assert.AreEqual(new FileInfo(opus).Length, counted.BytesWritten);

        string empty = Write("empty.wav", TestAudio.Wav(0, 48000, 1, (_, _) => 0));
        string target = Path.Combine(_root.FullName, "empty.opus");
        Assert.ThrowsExactly<InvalidDataException>(() => OpusTranscoder.TranscodeFile(empty, target, 128, cancellationToken: TestContext.CancellationToken));
        Assert.IsFalse(File.Exists(target));
    }

    [TestMethod]
    public void MonoGetsHalfTheBitrateAndTheEncoderIsKeptInRange()
    {
        Assert.AreEqual(128000, OpusBitrates.ForStereoBps(128000, 2));
        Assert.AreEqual(64000, OpusBitrates.ForStereoBps(128000, 1));
        Assert.AreEqual(OpusBitrates.MaximumBps, OpusBitrates.ForStereoBps(1_000_000, 2));
        Assert.AreEqual(OpusBitrates.MinimumBps, OpusBitrates.ForStereoBps(1000, 1));
        Assert.AreEqual(OpusBitrates.ForChannels(OpusQuality.Music, 1), OpusBitrates.ForStereoBps(128000, 1));
    }

    [TestMethod]
    public void AHugeBitrateIsCappedNotWrappedToTheMinimum()
    {
        // 3,000,000 kbps is 3 billion bps, past int.MaxValue.
        var wav = TestAudio.Wav(24000, 48000, 2, (n, _) => 0.5f * MathF.Sin(2 * MathF.PI * 440 * n / 48000));
        using var source = AudioDecoder.Open(new MemoryStream(wav), "a.wav");
        var huge = new CountingStream();
        OpusTranscoder.Transcode(source, huge, 3_000_000, "a.wav", cancellationToken: TestContext.CancellationToken);

        using var again = AudioDecoder.Open(new MemoryStream(wav), "a.wav");
        var minimum = new CountingStream();
        OpusTranscoder.Transcode(again, minimum, OpusBitrates.MinimumBps / 1000, "a.wav", cancellationToken: TestContext.CancellationToken);

        Assert.IsGreaterThan(minimum.BytesWritten, huge.BytesWritten);
    }

    [TestMethod]
    public void AWindowReportsWhatWasReadAndRefusesABufferSmallerThanAFrame()
    {
        using var source = new TestAudio.FakeSource(48000, 2, 1000, (n, _) => n * 0.0001f);
        var window = new PcmWindow(source, 900, 500);

        Assert.AreEqual(100, window.LengthFrames, "Clamped to what the source has.");
        Assert.ThrowsExactly<ArgumentException>(() => window.ReadFrames(new float[1]));
        var buffer = new float[400];
        Assert.AreEqual(100, window.ReadFrames(buffer));
        Assert.AreEqual(100, window.FramesRead);
        Assert.AreEqual(0, window.ReadFrames(buffer));
    }

    [TestMethod]
    public async Task AFailedOptimizeLeavesAnExistingFileAlone()
    {
        string destination = Path.Combine(_root.FullName, "song.opus");
        File.WriteAllBytes(destination, [1, 2, 3]);
        string path = Write("guitar.wav", Bytes(5000, 9));
        var file = new AssetFile(Asset("guitar.wav", 5000), path);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new OpusAudioOptimizer().OptimizeAsync([file], destination, AudioOptimizeOptions.Default, null, TestContext.CancellationToken));

        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, File.ReadAllBytes(destination), "The sources failed to open before anything was written.");
    }

    [TestMethod]
    public void BitratePresetsAreTheOpusBitrates()
    {
        Assert.AreEqual(OpusBitrates.StereoBps(OpusQuality.Music) / 1000, AudioOptimizeOptions.Default.BitrateKbps);
        Assert.AreEqual(OpusBitrates.SmallerStereoBps / 1000, AudioOptimizeOptions.Smaller.BitrateKbps);
        Assert.AreEqual(80, AudioOptimizeOptions.Smaller.BitrateKbps);
    }

    [TestMethod]
    public async Task ASmallerPresetWritesASmallerFile()
    {
        var file = StemFile("guitar.wav", Music(10, 44100, seed: 1));
        var optimizer = new OpusAudioOptimizer();

        var normal = await optimizer.EstimateAsync([file], AudioOptimizeOptions.Default, TestContext.CancellationToken);
        var smaller = await optimizer.EstimateAsync([file], AudioOptimizeOptions.Smaller, TestContext.CancellationToken);

        Assert.IsLessThan(normal.OutputBytes, smaller.OutputBytes);
    }

    // ---- Estimates ----------------------------------------------------------------------------------------------------

    /// <summary>How far a sampled estimate may be from the real size: 10%.</summary>
    private const double EstimateTolerance = 0.10;

    [TestMethod]
    public async Task ASampledEstimateIsCloseToTheRealSize()
    {
        // 60 s is far longer than the windows (8 x 3 s), so only part of it is encoded to estimate.
        var file = StemFile("guitar.wav", Music(60, 44100, seed: 2));
        var optimizer = new OpusAudioOptimizer();

        var estimate = await optimizer.EstimateAsync([file], AudioOptimizeOptions.Default, TestContext.CancellationToken);

        string real = Path.Combine(_root.FullName, "real.opus");
        await optimizer.OptimizeAsync([file], real, AudioOptimizeOptions.Default, null, TestContext.CancellationToken);
        long actual = new FileInfo(real).Length;
        Assert.AreEqual(".opus", estimate.OutputExtension);
        Assert.IsFalse(estimate.AlreadyOptimal);
        double error = Math.Abs(estimate.OutputBytes - actual) / (double)actual;
        Assert.IsLessThanOrEqualTo(EstimateTolerance, error, $"Estimated {estimate.OutputBytes}, wrote {actual} ({error:P1} out).");
    }

    [TestMethod]
    public async Task AShortFileIsEncodedWholeSoItsEstimateIsExact()
    {
        var file = StemFile("guitar.wav", Music(4, 44100, seed: 3));
        var optimizer = new OpusAudioOptimizer();

        var estimate = await optimizer.EstimateAsync([file], AudioOptimizeOptions.Default, TestContext.CancellationToken);

        string real = Path.Combine(_root.FullName, "real.opus");
        await optimizer.OptimizeAsync([file], real, AudioOptimizeOptions.Default, null, TestContext.CancellationToken);
        Assert.AreEqual(new FileInfo(real).Length, estimate.OutputBytes);
    }

    [TestMethod]
    public async Task AMixdownIsEstimatedFromTheMix()
    {
        var files = new[]
        {
            StemFile("guitar.wav", Music(60, 44100, seed: 4)),
            StemFile("bass.wav", Music(55, 48000, seed: 5), 48000),
        };
        var optimizer = new OpusAudioOptimizer();

        var estimate = await optimizer.EstimateAsync(files, AudioOptimizeOptions.Default, TestContext.CancellationToken);

        string real = Path.Combine(_root.FullName, "song.opus");
        await optimizer.OptimizeAsync(files, real, AudioOptimizeOptions.Default, null, TestContext.CancellationToken);
        long actual = new FileInfo(real).Length;
        double error = Math.Abs(estimate.OutputBytes - actual) / (double)actual;
        Assert.IsLessThanOrEqualTo(EstimateTolerance, error, $"Estimated {estimate.OutputBytes}, wrote {actual} ({error:P1} out).");
    }

    [TestMethod]
    public async Task EstimatingLeavesNothingBehind()
    {
        var file = StemFile("guitar.wav", Music(30, 44100, seed: 6));
        await new OpusAudioOptimizer().EstimateAsync([file], AudioOptimizeOptions.Default, TestContext.CancellationToken);

        Assert.AreEqual(1, Directory.GetFileSystemEntries(_root.FullName).Length, "Only the stem itself is in the folder.");
    }

    // ---- Skip rules ---------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task AStemThatIsAlreadyOpusIsSkippedWithoutBeingDecoded()
    {
        // The file doesn't exist: an Opus stem is recognised from its name, so nothing reads it.
        var file = new AssetFile(Asset("guitar.opus", 12345), Path.Combine(_root.FullName, "missing.opus"));

        var estimate = await new OpusAudioOptimizer().EstimateAsync([file], AudioOptions.Audio, TestContext.CancellationToken);

        Assert.IsTrue(estimate.AlreadyOptimal);
        Assert.AreEqual(12345, estimate.OutputBytes);
        Assert.AreEqual(OptimizeSkipReason.AlreadyOptimal, OptimizationPlanner.Judge(12345, estimate, 0.1));
    }

    [TestMethod]
    public async Task APlanSkipsOpusStemsBelowTheMarginAndLargerOutputs()
    {
        // Opus already; noise at 8 kHz 8-bit, which a 192 kbps Opus is larger than; and a stem that does shrink.
        var opus = Folder(_root.FullName, "Chart", null,
            ("song.opus", File.ReadAllBytes(Transcoded(Music(5, 44100, seed: 7), "ready.opus"))),
            ("guitar.wav", NoiseWav(8000, bits: 8, seconds: 10)),
            ("rhythm.wav", Wav(Music(20, 44100, seed: 8))));
        var chart = await AssetInspector.InspectAsync(opus, cancellationToken: TestContext.CancellationToken);
        var optimizers = new OptimizerSet(new OpusAudioOptimizer());
        var options = AudioOptions with { Audio = new AudioOptimizeOptions(Enabled: true, BitrateKbps: 192) };

        var plan = await OptimizationPlanner.PlanAsync(chart, options, optimizers, TestContext.CancellationToken);

        var song = ActionFor(plan, OptimizeStrategy.Audio, "song.opus");
        Assert.AreEqual(OptimizeSkipReason.AlreadyOptimal, song.Reason);
        var guitar = ActionFor(plan, OptimizeStrategy.Audio, "guitar.wav");
        Assert.AreEqual(OptimizeSkipReason.WouldGrow, guitar.Reason, $"Estimated {guitar.EstimatedOutputBytes} from {guitar.InputBytes}.");
        var rhythm = ActionFor(plan, OptimizeStrategy.Audio, "rhythm.wav");
        Assert.AreEqual(OptimizeActionKind.Transcode, rhythm.Kind);
        Assert.AreEqual("rhythm.opus", rhythm.OutputName);

        var strict = await OptimizationPlanner.PlanAsync(chart, options with { SavingsMargin = 0.999 }, optimizers, TestContext.CancellationToken);
        Assert.AreEqual(OptimizeSkipReason.BelowMargin, ActionFor(strict, OptimizeStrategy.Audio, "rhythm.wav").Reason);
    }

    // ---- Stem mixdown -------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task AFolderMixesDownToOneOpusStemAndLeavesPreviewAndCrowd()
    {
        var entry = Folder(_root.FullName, "Chart", null, MixdownStems());

        var item = await RunMixdownAsync(entry);

        Assert.AreEqual(OptimizeItemStatus.Succeeded, item.Status, item.ErrorMessage);
        Assert.AreSequenceEqual(["crowd.wav", "preview.wav", "song.ini", "song.opus"], Names(entry));
        await AssertMixAsync(entry);
        Assert.AreEqual(0, Leftovers(_root.FullName).Count);
    }

    [TestMethod]
    public async Task ASngMixesDownToOneOpusStemAndLeavesPreviewAndCrowd()
    {
        var entry = await SngAsync(_root.FullName, "Packed", null, MixdownStems());

        var item = await RunMixdownAsync(entry);

        Assert.AreEqual(OptimizeItemStatus.Succeeded, item.Status, item.ErrorMessage);
        Assert.AreSequenceEqual(["crowd.wav", "preview.wav", "song.opus"], Names(entry));
        await AssertMixAsync(entry);
        Assert.AreEqual(0, Leftovers(_root.FullName).Count);
    }

    [TestMethod]
    public async Task ADrumKitSplitAcrossStemsIsMixedIn()
    {
        var entry = Folder(_root.FullName, "Chart", null,
            ("guitar.wav", Wav(Tone(3, 48000, 330, 0.25f))),
            ("drums_1.wav", Wav(Tone(3, 48000, 110, 0.25f))),
            ("drums_2.wav", Wav(Tone(3, 48000, 220, 0.25f))),
            ("drums_3.wav", Wav(Tone(3, 48000, 440, 0.25f))));

        var item = await RunMixdownAsync(entry);

        Assert.AreEqual(OptimizeItemStatus.Succeeded, item.Status, item.ErrorMessage);
        Assert.AreSequenceEqual(["song.ini", "song.opus"], Names(entry));
    }

    [TestMethod]
    public void TheMixClipsAtFullScaleRatherThanNormalising()
    {
        // Two stems at 0.8 sum to 1.6; the mixer clips that to 1, and nothing scales it down.
        using var loud1 = new TestAudio.FakeSource(48000, 2, 48000, (n, _) => 0.8f * MathF.Sin(2 * MathF.PI * 440 * n / 48000));
        using var loud2 = new TestAudio.FakeSource(48000, 2, 48000, (n, _) => 0.8f * MathF.Sin(2 * MathF.PI * 440 * n / 48000));
        var mixer = new StemMixer([KeyValuePair.Create<string, IPcmSource>("a", loud1), KeyValuePair.Create<string, IPcmSource>("b", loud2)]);

        float[] mix = TestAudio.ReadAll(new MixedPcmSource(mixer));

        Assert.AreEqual(48000 * 2, mix.Length);
        Assert.AreEqual(1f, mix.Max(s => Math.Abs(s)));
        Assert.IsTrue(mix.All(s => Math.Abs(s) <= 1f), "Nothing is beyond the mixer's limits.");
    }

    [TestMethod]
    public void ADamagedStemFailsTheMixInsteadOfGoingMissingFromIt()
    {
        using var good = new TestAudio.FakeSource(48000, 2, 48000, (_, _) => 0.1f);
        using var bad = new TestAudio.FakeSource(48000, 2, 48000, (_, _) => 0.1f) { IsDamaged = true };
        var mixer = new StemMixer([KeyValuePair.Create<string, IPcmSource>("good", good), KeyValuePair.Create<string, IPcmSource>("bad", bad)]);
        var mixed = new MixedPcmSource(mixer);

        var error = Assert.ThrowsExactly<InvalidDataException>(() => TestAudio.ReadAll(mixed));

        StringAssert.Contains(error.Message, "bad");
    }

    [TestMethod]
    public async Task ACorruptStemFailsTheMixdownAndLeavesTheChartAsItWas()
    {
        var entry = Folder(_root.FullName, "Chart", null,
            ("guitar.wav", Wav(Tone(3, 48000, 330, 0.25f))),
            ("bass.wav", Wav(Tone(3, 48000, 110, 0.25f))));
        var chart = await AssetInspector.InspectAsync(entry, cancellationToken: TestContext.CancellationToken);
        var optimizers = new OptimizerSet(new OpusAudioOptimizer());
        var plan = await OptimizationPlanner.PlanAsync(chart, MixdownOptions, optimizers, TestContext.CancellationToken);
        Assert.AreEqual(OptimizeActionKind.MixDown, ActionFor(plan, OptimizeStrategy.StemMixdown).Kind);
        // It goes bad between planning and running.
        File.WriteAllBytes(Path.Combine(entry.FullPath, "bass.wav"), Bytes(5000, 9));

        var result = await OptimizeJob.RunAsync([plan], MixdownOptions, optimizers, null, null, TestContext.CancellationToken);

        var item = result.Items.Single();
        Assert.AreNotEqual(OptimizeItemStatus.Succeeded, item.Status);
        Assert.AreSequenceEqual(["bass.wav", "guitar.wav", "song.ini"], Names(entry));
        Assert.AreEqual(0, Leftovers(_root.FullName).Count);
    }

    // ---- Cancel and failure -------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task ACancelledOptimizeLeavesNoOutput()
    {
        var file = StemFile("guitar.wav", Music(20, 44100, seed: 10));
        string destination = Path.Combine(_root.FullName, "guitar.opus");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        // Cancels part-way through the encode.
        var cancelling = new CancellingProgress(0.3, cts);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new OpusAudioOptimizer().OptimizeAsync([file], destination, AudioOptimizeOptions.Default, cancelling, cts.Token));

        Assert.IsTrue(cancelling.Cancelled, "The run got far enough to be cancelled part-way.");
        Assert.IsFalse(File.Exists(destination));
    }

    [TestMethod]
    public async Task ACancelledJobLeavesNoTempFilesAndTheChartAsItWas()
    {
        var entry = Folder(_root.FullName, "Chart", null,
            ("guitar.wav", Wav(Music(20, 44100, seed: 11))),
            ("rhythm.wav", Wav(Music(20, 44100, seed: 12))));
        var chart = await AssetInspector.InspectAsync(entry, cancellationToken: TestContext.CancellationToken);
        var optimizers = new OptimizerSet(new OpusAudioOptimizer());
        var plan = await OptimizationPlanner.PlanAsync(chart, AudioOptions, optimizers, TestContext.CancellationToken);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        var cancelling = new ActionProgress<OptimizeProgress>(p =>
        {
            if (p.FileFraction is > 0.3)
                cts.Cancel();
        });

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            OptimizeJob.RunAsync([plan], AudioOptions, optimizers, null, cancelling, cts.Token));

        Assert.AreSequenceEqual(["guitar.wav", "rhythm.wav", "song.ini"], Names(entry));
        Assert.AreEqual(0, Leftovers(_root.FullName).Count);
    }

    [TestMethod]
    public async Task AFolderTranscodesInPlaceThroughTheJob()
    {
        var entry = Folder(_root.FullName, "Chart", null,
            ("guitar.wav", Wav(Music(10, 44100, seed: 13))),
            ("song.opus", File.ReadAllBytes(Transcoded(Music(5, 44100, seed: 14), "ready.opus"))));
        long before = new FileInfo(Path.Combine(entry.FullPath, "guitar.wav")).Length;
        var chart = await AssetInspector.InspectAsync(entry, cancellationToken: TestContext.CancellationToken);
        var optimizers = new OptimizerSet(new OpusAudioOptimizer());
        var plan = await OptimizationPlanner.PlanAsync(chart, AudioOptions, optimizers, TestContext.CancellationToken);

        var result = await OptimizeJob.RunAsync([plan], AudioOptions, optimizers, null, null, TestContext.CancellationToken);

        var item = result.Items.Single();
        Assert.AreEqual(OptimizeItemStatus.Succeeded, item.Status, item.ErrorMessage);
        Assert.AreSequenceEqual(["guitar.opus", "song.ini", "song.opus"], Names(entry));
        Assert.IsLessThan(before, new FileInfo(Path.Combine(entry.FullPath, "guitar.opus")).Length);
        using var decoded = AudioDecoder.Open(File.OpenRead(Path.Combine(entry.FullPath, "guitar.opus")), "guitar.opus");
        Assert.AreEqual(10 * 44100 * 48000L / 44100, decoded.LengthFrames, delta: 960);
        Assert.AreEqual(0, Leftovers(_root.FullName).Count);
    }

    // ---- Helpers ------------------------------------------------------------------------------------------------------

    private async Task<OptimizeItemResult> RunMixdownAsync(SongEntry entry)
    {
        var chart = await AssetInspector.InspectAsync(entry, cancellationToken: TestContext.CancellationToken);
        var optimizers = new OptimizerSet(new OpusAudioOptimizer());
        var plan = await OptimizationPlanner.PlanAsync(chart, MixdownOptions, optimizers, TestContext.CancellationToken);

        var mixdown = ActionFor(plan, OptimizeStrategy.StemMixdown);
        Assert.AreEqual(OptimizeActionKind.MixDown, mixdown.Kind, mixdown.Reason.ToString());
        Assert.DoesNotContain("preview.wav", mixdown.SourceNames);
        Assert.DoesNotContain("crowd.wav", mixdown.SourceNames);

        var result = await OptimizeJob.RunAsync([plan], MixdownOptions, optimizers, null, null, TestContext.CancellationToken);
        return result.Items.Single();
    }

    // The stems of a chart: three that are mixed, of different lengths and rates, and the two that aren't.
    private static (string Name, byte[] Data)[] MixdownStems() =>
    [
        ("guitar.wav", Wav(Tone(6, 44100, 330, 0.3f))),
        ("bass.wav", Wav(Tone(5, 48000, 110, 0.3f))),
        ("drums.wav", Wav(Tone(4, 48000, 220, 0.3f))),
        ("preview.wav", Wav(Tone(3, 48000, 880, 0.3f))),
        ("crowd.wav", Wav(Tone(3, 48000, 1760, 0.3f))),
    ];

    /// <summary>The mix is as long as the longest stem and is the three stems summed, within the lossy codec's error.</summary>
    private async Task AssertMixAsync(SongEntry entry)
    {
        byte[] opus = (await ReadAsync(entry, "song.opus"))!;
        using var decoded = AudioDecoder.Open(new MemoryStream(opus), "song.opus");
        Assert.AreEqual(2, decoded.Channels);
        Assert.AreEqual(48000, decoded.SampleRate);

        // The longest stem is 6 s (44.1 kHz), which is 6 s at 48 kHz.
        Assert.AreEqual(6 * 48000L, decoded.LengthFrames, delta: 960);
        float[] samples = TestAudio.ReadAll(decoded);

        // The first 4 s have all three stems; correlate with their sum.
        double xy = 0, xx = 0, yy = 0;
        for (int i = 4800; i < 4 * 48000; i++)
        {
            double t = (double)i / 48000;
            double expected = 0.3 * (Math.Sin(2 * Math.PI * 330 * t) + Math.Sin(2 * Math.PI * 110 * t) + Math.Sin(2 * Math.PI * 220 * t));
            double actual = samples[i * 2];
            xy += actual * expected;
            xx += actual * actual;
            yy += expected * expected;
        }

        Assert.IsGreaterThan(0.98, xy / Math.Sqrt(xx * yy), "The mix is the stems summed.");
        Assert.IsLessThanOrEqualTo(1.1f, samples.Max(s => Math.Abs(s)), "The mix isn't louder than the mixer's limit, give or take the codec's overshoot.");

        // After the shorter stems end, the 6 s stem plays on alone.
        double tail = 0;
        for (int i = (5 * 48000) + 4800; i < 6 * 48000; i++)
            tail = Math.Max(tail, Math.Abs(samples[i * 2]));
        Assert.IsGreaterThan(0.2, tail);
    }

    /// <summary>A stereo clip with some body to it: a few tones whose pitch and loudness wander, and a little noise.</summary>
    private static float[] Music(int seconds, int rate, int seed)
    {
        var random = new Random(seed);
        var samples = new float[seconds * rate * 2];
        double phase1 = 0, phase2 = 0;
        for (int n = 0; n < seconds * rate; n++)
        {
            double t = (double)n / rate;
            phase1 += 2 * Math.PI * (220 + (80 * Math.Sin(t * 0.7))) / rate;
            phase2 += 2 * Math.PI * (660 + (200 * Math.Sin(t * 1.3))) / rate;
            double envelope = 0.5 + (0.4 * Math.Sin(t * 2.1));
            double noise = (random.NextDouble() - 0.5) * 0.3 * (0.3 + (0.7 * Math.Abs(Math.Sin(t * 0.37))));
            samples[n * 2] = (float)((envelope * (0.3 * Math.Sin(phase1)) + (0.2 * Math.Sin(phase2))) + noise);
            samples[(n * 2) + 1] = (float)((envelope * (0.3 * Math.Sin(phase1 * 1.01)) + (0.2 * Math.Sin(phase2 * 0.99))) + noise);
        }

        return samples;
    }

    /// <summary>A stereo sine, as the 16-bit samples <see cref="Wav(float[], int)"/> writes.</summary>
    private static (float[] Samples, int Rate) Tone(int seconds, int rate, double hertz, float amplitude)
    {
        var samples = new float[seconds * rate * 2];
        for (int n = 0; n < seconds * rate; n++)
            samples[n * 2] = samples[(n * 2) + 1] = amplitude * (float)Math.Sin(2 * Math.PI * hertz * n / rate);
        return (samples, rate);
    }

    private static byte[] Wav((float[] Samples, int Rate) tone) => TestAudio.Wav(tone.Samples, tone.Rate, 2);

    private static byte[] Wav(float[] stereo44100) => TestAudio.Wav(stereo44100, 44100, 2);

    private static byte[] NoiseWav(int rate, int bits, int seconds)
    {
        var random = new Random(99);
        var samples = new float[rate * seconds];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = (float)((random.NextDouble() * 1.6) - 0.8);
        return TestAudio.Wav(samples, rate, 1, bits);
    }

    private string Write(string name, byte[] data)
    {
        string path = Path.Combine(_root.FullName, name);
        File.WriteAllBytes(path, data);
        return path;
    }

    /// <summary>A 44.1 kHz stereo WAV stem on disk, as an optimizer is given it.</summary>
    private AssetFile StemFile(string name, float[] stereo44100, int rate = 44100)
    {
        string path = Write(name, TestAudio.Wav(stereo44100, rate, 2));
        return new AssetFile(Asset(name, new FileInfo(path).Length), path);
    }

    /// <summary>Encodes <paramref name="stereo44100"/> to an Opus file in the temp folder, returning its path.</summary>
    private string Transcoded(float[] stereo44100, string name)
    {
        string wav = Write("source-" + name + ".wav", Wav(stereo44100));
        string opus = Path.Combine(_root.FullName, name);
        OpusTranscoder.TranscodeFile(wav, opus, 128, cancellationToken: TestContext.CancellationToken);
        File.Delete(wav);
        return opus;
    }

    /// <summary>Cancels once the transcode reports <paramref name="fraction"/>.</summary>
    private sealed class CancellingProgress(double fraction, CancellationTokenSource source) : IProgress<double>
    {
        public bool Cancelled { get; private set; }

        public void Report(double value)
        {
            if (value >= fraction && !Cancelled)
            {
                Cancelled = true;
                source.Cancel();
            }
        }
    }

    private sealed class ActionProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
