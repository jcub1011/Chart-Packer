using ChartShared.Audio.Video;
using ChartShared.Audio.Waveforms;
using ChartShared.Core.Optimization;
using ChartShared.Core.Tests.Optimization;
using ChartShared.Ffmpeg;
using ChartShared.Video;

namespace ChartShared.Core.Tests.Video;

/// <summary>
/// Re-encodes the fixtures (two seconds of a 64×36 test pattern at 10 fps, 20 frames) and synthetic videos of other
/// sizes to VP8 in WebM. Needs FFmpeg with the libvpx encoder beside the tests (build/ffmpeg); without it these are
/// inconclusive.
/// </summary>
[TestClass]
public sealed class VideoReencoderTests
{
    private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "Video", "Fixtures");

    private DirectoryInfo _root = null!;

    [TestInitialize]
    public void Initialize()
    {
        if (!FfmpegLibrary.IsAvailable)
            Assert.Inconclusive(FfmpegLibrary.UnavailableReason ?? "");
        if (!VideoReencoder.IsAvailable)
            Assert.Inconclusive(VideoReencoder.UnavailableReason ?? "");
        _root = Directory.CreateTempSubdirectory("chartpacker-reencode-");
    }

    [TestCleanup]
    public void Cleanup() => _root?.Delete(recursive: true);

    private string Temp(string name) => Path.Combine(_root.FullName, name);

    private static int CountFrames(string path)
    {
        using var decoder = VideoDecoder.Open(File.OpenRead(path), 4096);
        int frames = 0;
        while (decoder.Next(out _))
            frames++;
        return frames;
    }

    [TestMethod]
    [DataRow("tiny.mp4")]
    [DataRow("tiny.webm")]
    public void AFixtureIsReencodedToVp8WithTheSameFramesAndSize(string name)
    {
        string output = Temp("out.webm");

        VideoReencoder.Reencode(Path.Combine(Fixtures, name), output, new VideoReencodeSettings());

        var info = VideoProbe.Probe(output);
        Assert.AreEqual("vp8", info.Codec);
        Assert.AreEqual(64, info.Width);
        Assert.AreEqual(36, info.Height);
        Assert.IsLessThan(0.1, Math.Abs(10 - info.FrameRate), $"Frame rate {info.FrameRate}");
        Assert.IsLessThan(0.2, Math.Abs(info.Duration.TotalSeconds - 2), $"Duration {info.Duration}");
        Assert.AreEqual(20, CountFrames(output));
        AssertOnlyTheOutputIsLeft(output);
    }

    [TestMethod]
    [DataRow("tiny-audio.mp4")]
    [DataRow("tiny-audio.mkv")]
    public void TheOutputHasNoAudioTrack(string name)
    {
        string source = Path.Combine(Fixtures, name);
        Assert.IsTrue(VideoAudio.Read(File.OpenRead(source), WaveformResolution.Low).HasAudio, "The fixture has audio to begin with.");
        string output = Temp("out.webm");

        VideoReencoder.Reencode(source, output, new VideoReencodeSettings());

        Assert.IsFalse(VideoAudio.Read(File.OpenRead(output), WaveformResolution.Low).HasAudio);
        Assert.AreEqual("vp8", VideoProbe.Probe(output).Codec);
        Assert.IsGreaterThan(0, CountFrames(output));
    }

    [TestMethod]
    [DataRow(2400, 1350, VideoTarget.Fhd, 1920, 1080)]
    [DataRow(1920, 1080, VideoTarget.Fhd, 1920, 1080)]
    [DataRow(1280, 720, VideoTarget.Fhd, 1280, 720)]
    [DataRow(1280, 720, VideoTarget.Uhd, 1280, 720)]
    [DataRow(1177, 663, VideoTarget.Fhd, 1176, 662)]
    [DataRow(1080, 1920, VideoTarget.Fhd, 608, 1080)]
    [DataRow(640, 360, VideoTarget.Uhd, 640, 360)]
    public void ASyntheticVideoIsFittedInsideTheTargetButNeverScaledUp(int width, int height, VideoTarget target, int expectedWidth, int expectedHeight)
    {
        string source = Temp("source.webm");
        SyntheticVideo.Write(source, width, height, frames: 6, fps: 10);
        string output = Temp("out.webm");

        VideoReencoder.Reencode(source, output, new VideoReencodeSettings(target));

        var info = VideoProbe.Probe(output);
        Assert.AreEqual((expectedWidth, expectedHeight), (info.Width, info.Height));
        Assert.AreEqual(6, CountFrames(output));
        // The shape is kept, to within the pixel that making a side even can cost.
        Assert.IsLessThan(0.01, Math.Abs(((double)width / height) - ((double)info.Width / info.Height)), $"Aspect {info.Width}x{info.Height} from {width}x{height}");
    }

    [TestMethod]
    public void AVideoWithGapsKeepsItsTiming()
    {
        string source = Temp("gaps.webm");
        SyntheticVideo.Write(source, 128, 72, frames: 40, fps: 10, skipEvery: 10);
        string output = Temp("out.webm");

        VideoReencoder.Reencode(source, output, new VideoReencodeSettings());

        double sourceSeconds = VideoProbe.Probe(source).Duration.TotalSeconds;
        double outputSeconds = VideoProbe.Probe(output).Duration.TotalSeconds;
        Assert.IsLessThan(0.15, Math.Abs(sourceSeconds - outputSeconds), $"{sourceSeconds}s became {outputSeconds}s");
        Assert.AreEqual(40, CountFrames(output));
    }

    [TestMethod]
    public void ALowerCqGivesALargerFile()
    {
        string source = Temp("source.webm");
        SyntheticVideo.Write(source, 320, 180, frames: 30, fps: 30);
        string fine = Temp("fine.webm");
        string coarse = Temp("coarse.webm");

        VideoReencoder.Reencode(source, fine, new VideoReencodeSettings(Cq: 10, MaxBitrateKbps: 100_000));
        VideoReencoder.Reencode(source, coarse, new VideoReencodeSettings(Cq: 50, MaxBitrateKbps: 100_000));

        Assert.IsGreaterThan(new FileInfo(coarse).Length, new FileInfo(fine).Length);
    }

    [TestMethod]
    public void TheBitrateCapLimitsTheFile()
    {
        string source = Temp("source.webm");
        SyntheticVideo.Write(source, 320, 180, frames: 90, fps: 30);
        string capped = Temp("capped.webm");
        string free = Temp("free.webm");

        VideoReencoder.Reencode(source, free, new VideoReencodeSettings(Cq: 4, MaxBitrateKbps: 100_000));
        VideoReencoder.Reencode(source, capped, new VideoReencodeSettings(Cq: 4, MaxBitrateKbps: 300));

        Assert.IsLessThan(new FileInfo(free).Length, new FileInfo(capped).Length);
        // 3 seconds at 300 kbps is about 112 KB; rate control overshoots a little.
        Assert.IsLessThan(200_000, new FileInfo(capped).Length);
    }

    [TestMethod]
    [DataRow(3)]
    [DataRow(64)]
    public void AnOutOfRangeCqIsRefused(int cq)
    {
        string output = Temp("out.webm");

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => VideoReencoder.Reencode(Path.Combine(Fixtures, "tiny.webm"), output, new VideoReencodeSettings(Cq: cq)));
        Assert.IsFalse(File.Exists(output));
    }

    [TestMethod]
    public void AnExistingOutputIsReplacedOnlyOnceTheEncodeIsComplete()
    {
        string output = Temp("out.webm");
        File.WriteAllText(output, "the old one");

        VideoReencoder.Reencode(Path.Combine(Fixtures, "tiny.webm"), output, new VideoReencodeSettings());

        Assert.AreEqual("vp8", VideoProbe.Probe(output).Codec);
        AssertOnlyTheOutputIsLeft(output);
    }

    [TestMethod]
    public void TheSourceIsNeverDeletedOrReplaced()
    {
        string path = Temp("video.webm");
        string source = Temp("video.mp4");
        File.Copy(Path.Combine(Fixtures, "tiny.mp4"), source);
        byte[] before = File.ReadAllBytes(source);

        VideoReencoder.Reencode(source, path, new VideoReencodeSettings());

        Assert.AreEqual(20, CountFrames(path));
        Assert.AreSequenceEqual(before, File.ReadAllBytes(source));
    }

    [TestMethod]
    public void ProgressReachesOneAndNeverGoesBack()
    {
        var progress = new SyncProgress<double>();

        VideoReencoder.Reencode(Path.Combine(Fixtures, "tiny.mp4"), Temp("out.webm"), new VideoReencodeSettings(), progress);

        Assert.IsNotEmpty(progress.Reports);
        Assert.AreEqual(1.0, progress.Reports[^1]);
        Assert.AreSequenceEqual(progress.Reports.Order().ToList(), progress.Reports);
    }

    [TestMethod]
    public void ACancelBeforeItStartsLeavesNothingAndTheOriginalAlone()
    {
        string output = Temp("out.webm");
        File.WriteAllText(output, "the old one");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.ThrowsExactly<OperationCanceledException>(
            () => VideoReencoder.Reencode(Path.Combine(Fixtures, "tiny.mp4"), output, new VideoReencodeSettings(), cancellationToken: cancelled.Token));

        Assert.AreEqual("the old one", File.ReadAllText(output));
        AssertOnlyTheOutputIsLeft(output);
    }

    [TestMethod]
    public void ACancelPartwayLeavesNothingAndTheOriginalAlone()
    {
        string source = Temp("source.webm");
        SyntheticVideo.Write(source, 320, 180, frames: 60, fps: 30);
        string output = Temp("out.webm");
        File.WriteAllText(output, "the old one");
        using var cancel = new CancellationTokenSource();
        var progress = new CancellingProgress(cancel, 0.3);

        Assert.ThrowsExactly<OperationCanceledException>(
            () => VideoReencoder.Reencode(source, output, new VideoReencodeSettings(), progress, cancel.Token));

        Assert.IsTrue(progress.Triggered, "The encode was cancelled partway, not before or after it.");
        Assert.AreEqual("the old one", File.ReadAllText(output));
        Assert.IsTrue(File.Exists(source));
        Assert.AreEqual(2, Directory.GetFileSystemEntries(_root.FullName).Length, "Only the source and the old output are left.");
    }

    [TestMethod]
    public void AFileThatIsNotAVideoFailsCleanly()
    {
        string source = Temp("garbage.mp4");
        File.WriteAllBytes(source, [.. Enumerable.Range(0, 4096).Select(i => (byte)((i * 31) ^ 0x5A))]);
        string output = Temp("out.webm");
        File.WriteAllText(output, "the old one");

        Assert.ThrowsExactly<InvalidDataException>(() => VideoReencoder.Reencode(source, output, new VideoReencodeSettings()));

        Assert.AreEqual("the old one", File.ReadAllText(output));
        Assert.AreEqual(2, Directory.GetFileSystemEntries(_root.FullName).Length);
    }

    [TestMethod]
    public void ATruncatedVideoFailsOrEncodesWhatItHasWithoutLeavingTemps()
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(Fixtures, "tiny.mp4"));
        string source = Temp("cut.mp4");
        File.WriteAllBytes(source, bytes.AsSpan(0, bytes.Length / 3).ToArray());
        string output = Temp("out.webm");

        try
        {
            VideoReencoder.Reencode(source, output, new VideoReencodeSettings());
            Assert.IsTrue(File.Exists(output));
        }
        catch (InvalidDataException)
        {
            Assert.IsFalse(File.Exists(output));
        }

        Assert.IsFalse(Directory.GetFiles(_root.FullName).Any(f => Path.GetFileName(f).StartsWith('.')), "No temp file is left.");
    }

    [TestMethod]
    public void TheEstimateOfAShortVideoIsItsOwnEncode()
    {
        string source = Path.Combine(Fixtures, "tiny.mp4");
        string output = Temp("out.webm");

        VideoReencoder.Reencode(source, output, new VideoReencodeSettings());
        long estimate = VideoReencoder.EstimateSize(source, new VideoReencodeSettings());

        long actual = new FileInfo(output).Length;
        Assert.IsLessThan(0.1, Math.Abs(estimate - actual) / (double)actual, $"Estimated {estimate} bytes, wrote {actual}");
    }

    // Stated tolerance: a sampled estimate of a long video (two windows of 512 frames, so nearly a third of this
    // 1500-frame video is left out) is within 25% of what encoding all of it weighs.
    [TestMethod]
    public void ASampledEstimateIsWithinTwentyFivePercentOfTheRealSize()
    {
        string source = Temp("long.webm");
        SyntheticVideo.Write(source, 320, 180, frames: 1500, fps: 30, cq: 20);
        string output = Temp("out.webm");
        var settings = new VideoReencodeSettings(Cq: 20, MaxBitrateKbps: 100_000);

        VideoReencoder.Reencode(source, output, settings);
        long estimate = VideoReencoder.EstimateSize(source, settings);

        long actual = new FileInfo(output).Length;
        Assert.IsLessThan(0.25, Math.Abs(estimate - actual) / (double)actual, $"Estimated {estimate} bytes, wrote {actual}");
    }

    // Each window opens with a keyframe, so short windows hold far more of them for their length than the encode does.
    // Counted at the encode's own rate, short windows agree with windows a whole keyframe interval long, on a video whose
    // frames all weigh much the same. (Real videos are why the default windows are long: see DefaultWindowCount.)
    [TestMethod]
    public void ShortWindowsEstimateWhatLongOnesDo()
    {
        string source = Temp("long.webm");
        SyntheticVideo.Write(source, 320, 180, frames: 1500, fps: 30, cq: 20);
        var settings = new VideoReencodeSettings(Cq: 20, MaxBitrateKbps: 100_000);

        long longWindows = VideoReencoder.EstimateSize(source, settings, windowCount: 5, windowFrames: VideoReencodeSettings.GopFrames);
        long shortWindows = VideoReencoder.EstimateSize(source, settings, windowCount: 20, windowFrames: 12);

        Assert.IsLessThan(0.15, Math.Abs(shortWindows - longWindows) / (double)longWindows, $"Short windows {shortWindows} bytes, long windows {longWindows}");
    }

    [TestMethod]
    public void APreviewIsTheSameFrameBeforeAndAfter()
    {
        string source = Temp("source.webm");
        SyntheticVideo.Write(source, 320, 180, frames: 120, fps: 30);

        var preview = VideoReencoder.PreviewFrame(source, new VideoReencodeSettings(), position: 0.5, maxWidth: 160);

        Assert.AreEqual((160, 90), (preview.Width, preview.Height));
        Assert.AreEqual((160, 90), (preview.AfterWidth, preview.AfterHeight));
        Assert.HasCount(160 * 90 * 4, preview.Before);
        Assert.HasCount(160 * 90 * 4, preview.After);
        Assert.IsTrue(preview.Time > TimeSpan.FromSeconds(1.5) && preview.Time < TimeSpan.FromSeconds(2.5), preview.Time.ToString());

        // The same picture, give or take what the encoder lost: nothing like a frame from elsewhere, or a blank one.
        double difference = 0;
        for (int i = 0; i < preview.Before.Length; i++)
            difference += Math.Abs(preview.Before[i] - preview.After[i]);
        Assert.IsLessThan(24, difference / preview.Before.Length);
        Assert.IsGreaterThan(1, preview.Before.Distinct().Count());
        Assert.HasCount(1, Directory.GetFiles(_root.FullName), "only the source: the encoded frames went to a temp file that's gone");
    }

    [TestMethod]
    [DataRow(0.0)]
    [DataRow(1.0)]
    public void APreviewAtEitherEndOfTheVideoIsAFrame(double position)
    {
        string source = Temp("source.webm");
        SyntheticVideo.Write(source, 160, 90, frames: 40, fps: 20);

        var preview = VideoReencoder.PreviewFrame(source, new VideoReencodeSettings(), position, maxWidth: 1280);

        Assert.AreEqual((160, 90), (preview.Width, preview.Height));
        Assert.IsTrue(preview.Time >= TimeSpan.Zero && preview.Time <= TimeSpan.FromSeconds(2));
    }

    [TestMethod]
    public void APreviewCanBeCancelled()
    {
        string source = Temp("source.webm");
        SyntheticVideo.Write(source, 160, 90, frames: 60, fps: 30);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(() => VideoReencoder.PreviewFrame(source, new VideoReencodeSettings(), 0.5, 160, cancelled.Token));
    }

    [TestMethod]
    public void AnEstimateCanBeCancelled()
    {
        string source = Temp("long.webm");
        SyntheticVideo.Write(source, 160, 90, frames: 600, fps: 30);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.ThrowsExactly<OperationCanceledException>(
            () => VideoReencoder.EstimateSize(source, new VideoReencodeSettings(), cancellationToken: cancelled.Token));
    }

    // The encoder frame is FFmpeg's own, so swscale's writes past the end of a row (for widths that aren't a multiple of
    // its block size) land in FFmpeg's padding; every width here is odd or awkward, and each must encode and decode whole.
    [TestMethod]
    [DataRow(VideoEncodeSpeed.Fast)]
    [DataRow(VideoEncodeSpeed.Balanced)]
    [DataRow(VideoEncodeSpeed.Slow)]
    [DataRow(VideoEncodeSpeed.Best)]
    [DataRow(VideoEncodeSpeed.Fastest)]
    public void EverySpeedEncodesAndEstimatesAValidVideo(VideoEncodeSpeed speed)
    {
        string source = Temp("source.webm");
        SyntheticVideo.Write(source, 320, 180, frames: 40, fps: 20);
        string output = Temp("out.webm");
        var settings = new VideoReencodeSettings(Speed: speed, Threads: 2);

        VideoReencoder.Reencode(source, output, settings);
        long estimate = VideoReencoder.EstimateSize(source, settings);

        Assert.AreEqual(40, CountFrames(output));
        long actual = new FileInfo(output).Length;
        Assert.IsLessThan(0.1, Math.Abs(estimate - actual) / (double)actual, $"{speed}: estimated {estimate}, wrote {actual}");
    }

    [TestMethod]
    public void AnEncodeThatPassesItsLimitStopsAndLeavesNothing()
    {
        string source = Temp("source.webm");
        SyntheticVideo.Write(source, 320, 180, frames: 40, fps: 20);
        string output = Temp("out.webm");

        Assert.ThrowsExactly<OutputTooLargeException>(() => VideoReencoder.Reencode(source, output, new VideoReencodeSettings(Threads: 2), maxBytes: 200));

        Assert.AreSequenceEqual(new[] { "source.webm" }, Directory.GetFileSystemEntries(_root.FullName).Select(Path.GetFileName).ToArray()!);
    }

    [TestMethod]
    public void AnEncodeWithinItsLimitIsWrittenWhole()
    {
        string source = Temp("source.webm");
        SyntheticVideo.Write(source, 320, 180, frames: 40, fps: 20);
        string output = Temp("out.webm");

        VideoReencoder.Reencode(source, output, new VideoReencodeSettings(Threads: 2), maxBytes: 100_000_000);

        Assert.AreEqual(40, CountFrames(output));
    }

    [TestMethod]
    public void TheFastestSpeedIsTheOneEstimatesAreMadeAt()
    {
        // So an estimate for it needs no scaling, and every other speed's is scaled down, as a slower encode is smaller.
        Assert.AreEqual(VideoReencodeSettings.EstimateCpuUsed, VideoReencodeSettings.CpuUsedOf(VideoEncodeSpeed.Fastest));
        Assert.AreEqual(1.0, VideoReencodeSettings.EstimateFactor(VideoEncodeSpeed.Fastest));
        foreach (var speed in Enum.GetValues<VideoEncodeSpeed>())
        {
            Assert.IsGreaterThan(0.5, VideoReencodeSettings.EstimateFactor(speed));
            Assert.IsLessThanOrEqualTo(1.0, VideoReencodeSettings.EstimateFactor(speed));
        }

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => VideoReencodeSettings.EstimateFactor((VideoEncodeSpeed)99));
    }

    [TestMethod]
    public void ASlowerSpeedIsNoLessEfficientThanTheFastest()
    {
        // The two ends of the range, on the same picture and CQ: slower spends no more bits than faster (given a little room,
        // as their pictures differ too, so this is a sanity check of the setting reaching the encoder, not a benchmark).
        string source = Temp("source.webm");
        SyntheticVideo.Write(source, 480, 270, frames: 60, fps: 30);
        string fast = Temp("fast.webm");
        string best = Temp("best.webm");

        VideoReencoder.Reencode(source, fast, new VideoReencodeSettings(Cq: 20, MaxBitrateKbps: 100_000, Speed: VideoEncodeSpeed.Fast));
        VideoReencoder.Reencode(source, best, new VideoReencodeSettings(Cq: 20, MaxBitrateKbps: 100_000, Speed: VideoEncodeSpeed.Best));

        Assert.AreNotEqual(new FileInfo(fast).Length, new FileInfo(best).Length, "The setting changes what's written.");
    }

    [TestMethod]
    public void AwkwardWidthsConvertWithoutCorruption()
    {
        foreach (int width in new[] { 66, 98, 130, 162, 258, 514, 1030 })
        {
            string source = Temp($"w{width}.webm");
            SyntheticVideo.Write(source, width, 50, frames: 4, fps: 10);
            string output = Temp($"o{width}.webm");

            VideoReencoder.Reencode(source, output, new VideoReencodeSettings());

            Assert.AreEqual(4, CountFrames(output), $"Width {width}");
            Assert.AreEqual(width, VideoProbe.Probe(output).Width);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
    }

    [TestMethod]
    public void TheProbeReadsCodecSizeRateAndLength()
    {
        var info = VideoProbe.Probe(Path.Combine(Fixtures, "tiny.mp4"));

        Assert.AreEqual("mpeg4", info.Codec);
        Assert.AreEqual((64, 36), (info.Width, info.Height));
        Assert.IsLessThan(0.1, Math.Abs(10 - info.FrameRate));
        Assert.IsLessThan(0.2, Math.Abs(info.Duration.TotalSeconds - 2));
        Assert.AreEqual("vp8", VideoProbe.Probe(Path.Combine(Fixtures, "tiny.webm")).Codec);
    }

    [TestMethod]
    public async Task TheProbeServiceGivesACodecKeyAndNullForANonVideo()
    {
        var probe = new FfmpegVideoProbe();

        var video = await probe.ProbeAsync("tiny.webm", File.OpenRead(Path.Combine(Fixtures, "tiny.webm")), CancellationToken.None);
        var text = await probe.ProbeAsync("readme.txt", new MemoryStream("not a video"u8.ToArray()), CancellationToken.None);

        Assert.AreEqual("video/vp8", video?.CodecKey);
        Assert.AreEqual(64, video?.Width);
        Assert.IsNull(text);
    }

    private void AssertOnlyTheOutputIsLeft(string output) =>
        Assert.AreSequenceEqual(new[] { Path.GetFileName(output) }, Directory.GetFileSystemEntries(_root.FullName).Select(Path.GetFileName).ToArray()!);
}

/// <summary>Cancels once the encode has reported more than a fraction of the video, which it does between frames.</summary>
internal sealed class CancellingProgress(CancellationTokenSource source, double after) : IProgress<double>
{
    public bool Triggered { get; private set; }

    public void Report(double value)
    {
        if (value > after && !Triggered)
        {
            Triggered = true;
            source.Cancel();
        }
    }
}

/// <summary>Output sizes; they need no FFmpeg.</summary>
[TestClass]
public sealed class VideoReencoderSizeTests
{
    [TestMethod]
    [DataRow(3840, 2160, VideoTarget.Fhd, 1920, 1080)]
    [DataRow(1920, 1080, VideoTarget.Fhd, 1920, 1080)]
    [DataRow(1280, 720, VideoTarget.Fhd, 1280, 720)]
    [DataRow(64, 36, VideoTarget.Fhd, 64, 36)]
    [DataRow(1080, 1920, VideoTarget.Fhd, 608, 1080)]
    [DataRow(1001, 563, VideoTarget.Fhd, 1000, 562)]
    [DataRow(3840, 2160, VideoTarget.Uhd, 3840, 2160)]
    [DataRow(4096, 2160, VideoTarget.Uhd, 3840, 2024)]
    [DataRow(7680, 4320, VideoTarget.Uhd, 3840, 2160)]
    [DataRow(1920, 1080, VideoTarget.Sd, 854, 480)]
    [DataRow(640, 360, VideoTarget.Sd, 640, 360)]
    [DataRow(1920, 1080, VideoTarget.Hd, 1280, 720)]
    [DataRow(640, 360, VideoTarget.Hd, 640, 360)]
    [DataRow(1080, 1920, VideoTarget.Hd, 404, 720)]
    [DataRow(3840, 2160, VideoTarget.Qhd, 2560, 1440)]
    [DataRow(1920, 1080, VideoTarget.Qhd, 1920, 1080)]
    [DataRow(1, 1, VideoTarget.Fhd, 2, 2)]
    [DataRow(5000, 100, VideoTarget.Fhd, 1920, 38)]
    public void AVideoIsFittedInsideTheTargetWithEvenSides(int width, int height, VideoTarget target, int expectedWidth, int expectedHeight) =>
        Assert.AreEqual((expectedWidth, expectedHeight), VideoReencoder.OutputSize(width, height, target));

    [TestMethod]
    public void EachSpeedIsALibvpxCpuUsedFromFastToSlow()
    {
        Assert.AreEqual(VideoEncodeSpeed.Balanced, new VideoReencodeSettings().Speed);
        Assert.AreEqual(VideoEncodeSpeed.Balanced, VideoOptimizeOptions.Default.Speed);
        Assert.AreSequenceEqual(new[] { 4, 2, 1, 0 }, new[] { VideoEncodeSpeed.Fast, VideoEncodeSpeed.Balanced, VideoEncodeSpeed.Slow, VideoEncodeSpeed.Best }.Select(VideoReencodeSettings.CpuUsedOf).ToArray());
        Assert.AreEqual(VideoReencodeSettings.CpuUsedOf(VideoEncodeSpeed.Best), new VideoReencodeSettings(Speed: VideoEncodeSpeed.Best).CpuUsed);
        Assert.AreEqual(VideoEncodeSpeed.Best, VideoReencodeSettings.From(new VideoOptimizeOptions(Speed: VideoEncodeSpeed.Best)).Speed);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => VideoReencodeSettings.CpuUsedOf((VideoEncodeSpeed)99));
    }

    [TestMethod]
    public void TheDefaultsAreCq32AndTheTargetsBitrateCap()
    {
        var fhd = new VideoReencodeSettings();
        var uhd = new VideoReencodeSettings(VideoTarget.Uhd);

        Assert.AreEqual(32, fhd.Cq);
        Assert.AreEqual(VideoReencodeSettings.FhdMaxBitrateKbps, fhd.EffectiveMaxBitrateKbps);
        Assert.AreEqual(VideoReencodeSettings.UhdMaxBitrateKbps, uhd.EffectiveMaxBitrateKbps);
        Assert.AreEqual(VideoReencodeSettings.SdMaxBitrateKbps, new VideoReencodeSettings(VideoTarget.Sd).EffectiveMaxBitrateKbps);
        Assert.AreEqual(VideoReencodeSettings.HdMaxBitrateKbps, new VideoReencodeSettings(VideoTarget.Hd).EffectiveMaxBitrateKbps);
        Assert.AreEqual(VideoReencodeSettings.QhdMaxBitrateKbps, new VideoReencodeSettings(VideoTarget.Qhd).EffectiveMaxBitrateKbps);
        Assert.AreEqual(500, new VideoReencodeSettings(MaxBitrateKbps: 500).EffectiveMaxBitrateKbps);
    }
}
