using ChartShared.Core.Optimization;
using ChartShared.Core.Tests.Optimization;
using ChartShared.Ffmpeg;
using ChartShared.Video;
using static ChartShared.Core.Tests.Optimization.OptimizationTestSupport;

namespace ChartShared.Core.Tests.Video;

/// <summary>
/// <see cref="VideoOptimizer"/> on the fixtures and synthetic videos, with the real encoder: the skip rules (already VP8
/// WebM within the target, below the margin, a larger output), and cancel and failure leaving nothing behind. Needs FFmpeg
/// with libvpx beside the tests (build/ffmpeg); without it these are inconclusive.
/// </summary>
[TestClass]
public sealed class VideoOptimizerTests
{
    private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "Video", "Fixtures");

    private DirectoryInfo _root = null!;

    public TestContext TestContext { get; set; }

    [TestInitialize]
    public void Initialize()
    {
        if (!FfmpegLibrary.IsAvailable)
            Assert.Inconclusive(FfmpegLibrary.UnavailableReason ?? "");
        if (!VideoReencoder.IsAvailable)
            Assert.Inconclusive(VideoReencoder.UnavailableReason ?? "");
        _root = Directory.CreateTempSubdirectory("chartpacker-videoopt-");
    }

    [TestCleanup]
    public void Cleanup() => _root?.Delete(recursive: true);

    private string Temp(string name) => Path.Combine(_root.FullName, name);

    private static AssetFile FileOf(string path, string? storedName = null)
    {
        string name = storedName ?? Path.GetFileName(path);
        return new AssetFile(new ChartAsset(name, Path.GetExtension(name).ToLowerInvariant(), new FileInfo(path).Length, AssetKind.Video), path);
    }

    private static readonly VideoOptimizeOptions Enabled = new(Enabled: true);

    [TestMethod]
    public void ItIsAvailableWhenTheEncoderIs() => Assert.IsTrue(new VideoOptimizer().IsAvailable);

    [TestMethod]
    public async Task AVideoThatIsAlreadyVp8WebmWithinTheTargetIsAlreadyOptimal()
    {
        string path = Temp("lean.webm");
        SyntheticVideo.Write(path, 320, 180, frames: 30, noise: 0);

        var estimate = await new VideoOptimizer().EstimateAsync(FileOf(path), Enabled, TestContext.CancellationToken);

        Assert.IsTrue(estimate.AlreadyOptimal);
        Assert.AreEqual(".webm", estimate.OutputExtension);
        Assert.AreEqual(new FileInfo(path).Length, estimate.OutputBytes);
    }

    [TestMethod]
    public async Task AVp8WebmLargerThanTheTargetIsNotAlreadyOptimalUntilTheTargetFitsIt()
    {
        string path = Temp("big.webm");
        SyntheticVideo.Write(path, 2400, 1350, frames: 4, fps: 10, noise: 0);
        var optimizer = new VideoOptimizer();

        var fhd = await optimizer.EstimateAsync(FileOf(path), Enabled with { Target = VideoTarget.Fhd }, TestContext.CancellationToken);
        var uhd = await optimizer.EstimateAsync(FileOf(path), Enabled with { Target = VideoTarget.Uhd }, TestContext.CancellationToken);

        Assert.IsFalse(fhd.AlreadyOptimal);
        Assert.IsTrue(uhd.AlreadyOptimal);
    }

    [TestMethod]
    public async Task AVp8WebmWithFarMoreBitsThanTheEncoderWouldWriteIsEstimatedNotLeftAlone()
    {
        // Noise makes a file of well over LavishBitsPerPixelFrame, which the encoder at CQ 32 would shrink a great deal.
        string path = Path.Combine(Fixtures, "tiny.webm");

        var estimate = await new VideoOptimizer().EstimateAsync(FileOf(path), Enabled, TestContext.CancellationToken);

        Assert.IsFalse(estimate.AlreadyOptimal);
        Assert.AreEqual(".webm", estimate.OutputExtension);
    }

    [TestMethod]
    public async Task AVp8StreamInAnotherContainerIsNotAlreadyOptimal()
    {
        // The same VP8, stored under a name the games read differently: it's rewritten as .webm.
        string path = Temp("video.mkv");
        File.Copy(Path.Combine(Fixtures, "tiny.webm"), path);

        var estimate = await new VideoOptimizer().EstimateAsync(FileOf(path), Enabled, TestContext.CancellationToken);

        Assert.IsFalse(estimate.AlreadyOptimal);
        Assert.AreEqual(".webm", estimate.OutputExtension);
    }

    [TestMethod]
    public async Task AnEstimateOfAnMp4IsAWebmOfAboutTheSizeItWouldBe()
    {
        string source = Path.Combine(Fixtures, "tiny.mp4");
        var optimizer = new VideoOptimizer();
        string output = Temp("video.webm");

        var estimate = await optimizer.EstimateAsync(FileOf(source), Enabled, TestContext.CancellationToken);
        await optimizer.OptimizeAsync(FileOf(source), output, Enabled, null, TestContext.CancellationToken);

        Assert.IsFalse(estimate.AlreadyOptimal);
        Assert.AreEqual(".webm", estimate.OutputExtension);
        long actual = new FileInfo(output).Length;
        // The estimate is encoded at the quickest speed and scaled, so it's near the size written, not the size itself.
        Assert.IsLessThan(0.35, Math.Abs(estimate.OutputBytes - actual) / (double)actual, $"Estimated {estimate.OutputBytes}, wrote {actual}");
        Assert.AreEqual("vp8", VideoProbe.Probe(output).Codec);
    }

    [TestMethod]
    public async Task AnEstimateTakesTheSameSampleWhateverTheSpeedAndScalesIt()
    {
        var source = FileOf(Path.Combine(Fixtures, "tiny.mp4"));
        var optimizer = new VideoOptimizer();

        var fastest = await optimizer.EstimateAsync(source, Enabled with { Speed = VideoEncodeSpeed.Fastest }, TestContext.CancellationToken);
        var best = await optimizer.EstimateAsync(source, Enabled with { Speed = VideoEncodeSpeed.Best }, TestContext.CancellationToken);

        Assert.AreEqual(Math.Round(fastest.OutputBytes * VideoReencodeSettings.EstimateFactor(VideoEncodeSpeed.Best)), best.OutputBytes, 1.0);
    }

    [TestMethod]
    public async Task AnOptimizeWhoseOutputPassesItsSourceStopsAndLeavesNothing()
    {
        // The source is said to be far smaller than anything the encoder could write.
        string path = Path.Combine(Fixtures, "tiny.mp4");
        var source = new AssetFile(new ChartAsset("video.mp4", ".mp4", 200, AssetKind.Video), path);

        await Assert.ThrowsExactlyAsync<OutputTooLargeException>(
            () => new VideoOptimizer().OptimizeAsync(source, Temp("video.webm"), Enabled, null, TestContext.CancellationToken));

        Assert.IsEmpty(Directory.GetFileSystemEntries(_root.FullName));
    }

    [TestMethod]
    public async Task AConversionForCompatibilityIsWrittenHoweverLargeItIs()
    {
        string path = Path.Combine(Fixtures, "tiny.mp4");
        var source = new AssetFile(new ChartAsset("video.mp4", ".mp4", 200, AssetKind.Video, Probe: new ProbeResult("video/vp9")), path);
        string output = Temp("video.webm");

        await new VideoOptimizer().OptimizeAsync(source, output, Enabled with { ConvertUnsupported = true }, null, TestContext.CancellationToken);

        Assert.IsGreaterThan(200, new FileInfo(output).Length);
    }

    [TestMethod]
    public async Task OptimizingReportsProgressOnTheCallingThreadAndEndsAtOne()
    {
        var progress = new SyncProgress<double>();

        await new VideoOptimizer().OptimizeAsync(FileOf(Path.Combine(Fixtures, "tiny.mp4")), Temp("video.webm"), Enabled, progress, TestContext.CancellationToken);

        Assert.AreEqual(1.0, progress.Reports[^1]);
        Assert.AreSequenceEqual(progress.Reports.Order().ToList(), progress.Reports);
    }

    [TestMethod]
    public async Task ACancelledOptimizeLeavesNothingAndTheSourceAlone()
    {
        string source = Temp("source.mp4");
        File.Copy(Path.Combine(Fixtures, "tiny.mp4"), source);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => new VideoOptimizer().OptimizeAsync(FileOf(source), Temp("video.webm"), Enabled, null, cancelled.Token));

        Assert.AreSequenceEqual(new[] { "source.mp4" }, Directory.GetFileSystemEntries(_root.FullName).Select(Path.GetFileName).ToArray()!);
    }

    [TestMethod]
    public async Task AFailedOptimizeLeavesNothingBehind()
    {
        string source = Temp("broken.mp4");
        File.WriteAllBytes(source, Bytes(4096, 9));

        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => new VideoOptimizer().OptimizeAsync(FileOf(source), Temp("video.webm"), Enabled, null, TestContext.CancellationToken));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => new VideoOptimizer().EstimateAsync(FileOf(source), Enabled, TestContext.CancellationToken));

        Assert.AreSequenceEqual(new[] { "broken.mp4" }, Directory.GetFileSystemEntries(_root.FullName).Select(Path.GetFileName).ToArray()!);
    }

    [TestMethod]
    public async Task SeveralVideosCanBeOptimizedAndEstimatedAtOnce()
    {
        var optimizer = new VideoOptimizer();
        var sources = new List<string>();
        for (int i = 0; i < 4; i++)
        {
            string source = Temp($"source{i}.webm");
            SyntheticVideo.Write(source, 2000 + (i * 2), 1126, frames: 6, fps: 10);
            sources.Add(source);
        }

        // Each is over the FHD limit, so each is really encoded; all four at once.
        var outputs = sources.Select((_, i) => Temp($"out{i}.webm")).ToList();
        await Task.WhenAll(sources.Select((s, i) => Task.WhenAll(
            optimizer.OptimizeAsync(FileOf(s), outputs[i], Enabled, null, TestContext.CancellationToken),
            optimizer.EstimateAsync(FileOf(s), Enabled, TestContext.CancellationToken))));

        foreach (string output in outputs)
        {
            var info = VideoProbe.Probe(output);
            Assert.AreEqual("vp8", info.Codec);
            Assert.IsLessThanOrEqualTo(1920, info.Width);
            Assert.IsLessThanOrEqualTo(1080, info.Height);
        }

        Assert.AreEqual(0, Directory.GetFiles(_root.FullName).Count(f => Path.GetFileName(f).StartsWith('.')), "No temp file is left.");
    }

    // The planner's own rules, on a real encoder: an output larger than the input is skipped, however well the CQ is chosen.
    [TestMethod]
    public async Task AVideoThatWouldComeOutLargerIsSkippedByThePlanner()
    {
        // A clean, coarsely encoded source, a few pixels too big for FHD: re-encoded at CQ 4 it can only grow, whatever the bitrate cap.
        var entry = Folder(_root.FullName, "Coarse");
        string video = Path.Combine(entry.FullPath, "video.webm");
        SyntheticVideo.Write(video, 1928, 1084, frames: 3, fps: 10, cq: 63, noise: 0);
        var options = Only(OptimizeStrategy.Video, margin: 0.1) with { Video = new VideoOptimizeOptions(Enabled: true, Cq: 4) };
        var chart = await AssetInspector.InspectAsync(entry, new AssetProbes(Video: new FfmpegVideoProbe()), TestContext.CancellationToken);
        var video_ = new VideoOptimizer();

        var plan = await OptimizationPlanner.PlanAsync(chart, options, new OptimizerSet(Video: video_), TestContext.CancellationToken);

        var action = ActionFor(plan, OptimizeStrategy.Video);
        Assert.AreEqual("video/vp8", action.CodecKey);
        Assert.AreEqual(OptimizeSkipReason.WouldGrow, action.Reason);
        Assert.IsGreaterThan(action.InputBytes, action.EstimatedOutputBytes);
    }

    [TestMethod]
    public async Task AVideoAlreadyVp8WithinTheTargetIsSkippedByThePlannerAsAlreadyOptimal()
    {
        string lean = Temp("lean.webm");
        SyntheticVideo.Write(lean, 320, 180, frames: 30, noise: 0);
        var entry = Folder(_root.FullName, "Fine", null, ("video.webm", File.ReadAllBytes(lean)));
        var chart = await AssetInspector.InspectAsync(entry, new AssetProbes(Video: new FfmpegVideoProbe()), TestContext.CancellationToken);

        var plan = await OptimizationPlanner.PlanAsync(chart, Only(OptimizeStrategy.Video, margin: 0), new OptimizerSet(Video: new VideoOptimizer()), TestContext.CancellationToken);

        Assert.AreEqual(OptimizeSkipReason.AlreadyOptimal, ActionFor(plan, OptimizeStrategy.Video).Reason);
    }
}
