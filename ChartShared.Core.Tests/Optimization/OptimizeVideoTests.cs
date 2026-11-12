using ChartShared.Core.Optimization;
using static ChartShared.Core.Tests.Optimization.OptimizationTestSupport;

namespace ChartShared.Core.Tests.Optimization;

/// <summary>
/// What the planner and the job do with videos, with fake optimizers: the savings margin and larger-file rules, and the
/// opt-in conversion of unsupported codecs (VP9, AV1, HEVC) that those rules don't apply to.
/// </summary>
[TestClass]
public sealed class OptimizeVideoTests
{
    private DirectoryInfo _root = null!;

    [TestInitialize]
    public void CreateRoot() => _root = Directory.CreateTempSubdirectory("chartpacker-optvideo-");

    [TestCleanup]
    public void DeleteRoot() => _root.Delete(recursive: true);

    public TestContext TestContext { get; set; }

    private static OptimizeOptions VideoOptions(double margin = 0.10, bool convertUnsupported = false) =>
        Only(OptimizeStrategy.Video, margin) with { Video = new VideoOptimizeOptions(Enabled: true, ConvertUnsupported: convertUnsupported) };

    /// <summary>A chart with a 5000-byte <c>video.mp4</c> whose probe says it's <paramref name="codec"/>.</summary>
    private async Task<ChartAssets> InspectedAsync(string codec, string video = "video.mp4")
    {
        var entry = Folder(_root.FullName, "Song" + Guid.NewGuid().ToString("N")[..6], null, ("guitar.ogg", Bytes(1000, 1)), (video, Bytes(5000, 3)));
        var probe = new FakeProbe();
        probe.Results[video] = new ProbeResult(codec, 1920, 1080, TimeSpan.FromSeconds(30));
        var assets = await AssetInspector.InspectAsync(entry, new AssetProbes(Video: probe), TestContext.CancellationToken);
        return assets;
    }

    private async Task<ChartPlan> PlanAsync(ChartAssets assets, OptimizeOptions options, FakeOptimizer optimizer) =>
        await OptimizationPlanner.PlanAsync(assets, options, new OptimizerSet(optimizer, optimizer, optimizer), TestContext.CancellationToken);

    [TestMethod]
    [DataRow("video/vp9")]
    [DataRow("video/av1")]
    [DataRow("video/hevc")]
    public async Task AnUnsupportedCodecIsLeftAloneUnlessConvertingForCompatibilityIsOn(string codec)
    {
        var assets = await InspectedAsync(codec);
        var optimizer = new FakeOptimizer();

        var plan = await PlanAsync(assets, VideoOptions(), optimizer);

        var action = ActionFor(plan, OptimizeStrategy.Video);
        Assert.AreEqual(OptimizeActionKind.Skip, action.Kind);
        Assert.AreEqual(OptimizeSkipReason.UnsupportedCodec, action.Reason);
        Assert.AreEqual(codec, action.CodecKey);
        Assert.AreEqual(0, optimizer.Estimated.Count, "It isn't estimated for the plan, as nothing would be done with the answer.");
        Assert.IsFalse(plan.HasWork);
    }

    [TestMethod]
    [DataRow("video/vp9", 1.5)]
    [DataRow("video/av1", 2.0)]
    [DataRow("video/hevc", 0.97)]
    [DataRow("video/vp9", 0.4)]
    public async Task ConvertingForCompatibilityIgnoresTheMarginAndTheLargerFileRule(string codec, double ratio)
    {
        var assets = await InspectedAsync(codec);
        var optimizer = new FakeOptimizer { Estimate = FakeOptimizer.DefaultEstimate(ratio) };

        var plan = await PlanAsync(assets, VideoOptions(margin: 0.5, convertUnsupported: true), optimizer);

        var action = ActionFor(plan, OptimizeStrategy.Video);
        Assert.AreEqual(OptimizeActionKind.Transcode, action.Kind);
        Assert.IsTrue(action.ForCompatibility);
        Assert.AreEqual("video.webm", action.OutputName);
        Assert.AreEqual((long)(5000 * ratio), action.EstimatedOutputBytes, "The estimate still shows the cost.");
        Assert.AreEqual(5000 - (long)(5000 * ratio), action.EstimatedSavingBytes);
        Assert.IsTrue(plan.HasWork);
    }

    [TestMethod]
    public async Task ConvertingForCompatibilityStillRefusesAFileItWouldOverwrite()
    {
        // video.webm is a VP9 file; the output name is its own, so it's replaced. A different file with that name is not.
        var entry = Folder(_root.FullName, "Taken", null, ("video.mp4", Bytes(5000, 3)), ("video.webm", Bytes(300, 4)));
        var probe = new FakeProbe();
        probe.Results["video.mp4"] = new ProbeResult("video/vp9", 1920, 1080, TimeSpan.FromSeconds(30));
        var assets = await AssetInspector.InspectAsync(entry, new AssetProbes(Video: probe), TestContext.CancellationToken);
        var optimizer = new FakeOptimizer { Estimate = FakeOptimizer.DefaultEstimate(1.2) };

        var plan = await PlanAsync(assets, VideoOptions(convertUnsupported: true), optimizer);

        Assert.AreEqual(OptimizeSkipReason.OutputNameTaken, plan.Actions.First(a => a.SourceNames.Contains("video.mp4")).Reason);
    }

    [TestMethod]
    public async Task AnAlreadyOptimalUnsupportedFileIsStillSkipped()
    {
        var assets = await InspectedAsync("video/vp9");
        var optimizer = new FakeOptimizer { Estimate = files => new OptimizeEstimate(files[0].Asset.Length, ".webm", AlreadyOptimal: true) };

        var plan = await PlanAsync(assets, VideoOptions(convertUnsupported: true), optimizer);

        Assert.AreEqual(OptimizeSkipReason.AlreadyOptimal, ActionFor(plan, OptimizeStrategy.Video).Reason);
    }

    [TestMethod]
    [DataRow("video/h264")]
    [DataRow("video/mpeg4")]
    [DataRow("video/vp8")]
    [DataRow("video/theora")]
    public async Task ASupportedCodecObeysTheLargerFileRuleWhateverConvertUnsupportedSays(string codec)
    {
        var assets = await InspectedAsync(codec);
        var optimizer = new FakeOptimizer { Estimate = FakeOptimizer.DefaultEstimate(1.3) };

        var plan = await PlanAsync(assets, VideoOptions(convertUnsupported: true), optimizer);

        var action = ActionFor(plan, OptimizeStrategy.Video);
        Assert.AreEqual(OptimizeSkipReason.WouldGrow, action.Reason);
        Assert.IsFalse(action.ForCompatibility);
    }

    [TestMethod]
    public async Task ASupportedCodecObeysTheMargin()
    {
        var assets = await InspectedAsync("video/h264");
        var optimizer = new FakeOptimizer { Estimate = FakeOptimizer.DefaultEstimate(0.95) };

        var plan = await PlanAsync(assets, VideoOptions(margin: 0.10), optimizer);
        var passing = await PlanAsync(assets, VideoOptions(margin: 0.02), optimizer);

        Assert.AreEqual(OptimizeSkipReason.BelowMargin, ActionFor(plan, OptimizeStrategy.Video).Reason);
        var action = ActionFor(passing, OptimizeStrategy.Video);
        Assert.AreEqual(OptimizeActionKind.Recompress, action.Kind);
        Assert.IsFalse(action.ForCompatibility);
    }

    [TestMethod]
    public async Task AnUnprobedVideoIsGroupedByItsExtensionAndObeysTheRules()
    {
        var entry = Folder(_root.FullName, "Unprobed", null, ("video.mp4", Bytes(5000, 3)));
        var assets = await AssetInspector.InspectAsync(entry, cancellationToken: TestContext.CancellationToken);
        var optimizer = new FakeOptimizer { Estimate = FakeOptimizer.DefaultEstimate(1.3) };

        var plan = await PlanAsync(assets, VideoOptions(convertUnsupported: true), optimizer);

        Assert.AreEqual("video/mp4", ActionFor(plan, OptimizeStrategy.Video).CodecKey);
        Assert.AreEqual(OptimizeSkipReason.WouldGrow, ActionFor(plan, OptimizeStrategy.Video).Reason);
    }

    [TestMethod]
    public async Task AnUnavailableVideoOptimizerLeavesAnUnsupportedVideoAsUnsupportedNotUnavailable()
    {
        var assets = await InspectedAsync("video/vp9");
        var optimizer = new FakeOptimizer { IsAvailable = false };

        var plan = await PlanAsync(assets, VideoOptions(convertUnsupported: true), optimizer);

        Assert.AreEqual(OptimizeSkipReason.OptimizerUnavailable, ActionFor(plan, OptimizeStrategy.Video).Reason);
    }

    [TestMethod]
    public async Task ARemovedVideoNeedsNoOptimizerWhateverItsCodec()
    {
        var assets = await InspectedAsync("video/vp9");
        var options = Only(OptimizeStrategy.Video) with { Video = new VideoOptimizeOptions(Enabled: true, Remove: true) };

        var plan = await PlanAsync(assets, options, new FakeOptimizer());

        Assert.AreEqual(OptimizeActionKind.Remove, ActionFor(plan, OptimizeStrategy.Video).Kind);
    }

    [TestMethod]
    public async Task AConvertedFileThatIsLargerIsKeptAndANormalOneThatIsLargerIsNot()
    {
        // Compatibility: the conversion writes more than it read, and it's kept.
        var compatibleAssets = await InspectedAsync("video/vp9");
        var options = VideoOptions(convertUnsupported: true);
        var optimizer = new FakeOptimizer { Estimate = FakeOptimizer.DefaultEstimate(0.5), WrittenLength = _ => 6500 };
        var plan = await PlanAsync(compatibleAssets, options, optimizer);

        var result = await OptimizeJob.RunAsync([plan], options, new OptimizerSet(optimizer, optimizer, optimizer), null, null, TestContext.CancellationToken);

        var item = result.Items.Single();
        Assert.AreEqual(OptimizeItemStatus.Succeeded, item.Status, item.ErrorMessage);
        Assert.AreEqual(AssetOutcome.Applied, item.Assets.Single(a => a.Strategy == OptimizeStrategy.Video).Outcome);
        Assert.AreSequenceEqual(["guitar.ogg", "song.ini", "video.webm"], Names(compatibleAssets.Entry));
        Assert.AreEqual(6500, (await ReadAsync(compatibleAssets.Entry, "video.webm"))!.Length);

        // Size: the estimate promised a saving, the file wasn't one, so it's left as it was.
        var normalAssets = await InspectedAsync("video/h264");
        var normalPlan = await PlanAsync(normalAssets, VideoOptions(), optimizer);
        Assert.IsTrue(normalPlan.HasWork);

        var normalResult = await OptimizeJob.RunAsync([normalPlan], VideoOptions(), new OptimizerSet(optimizer, optimizer, optimizer), null, null, TestContext.CancellationToken);

        Assert.AreEqual(AssetOutcome.Skipped, normalResult.Items.Single().Assets.Single(a => a.Strategy == OptimizeStrategy.Video).Outcome);
        Assert.AreSequenceEqual(["guitar.ogg", "song.ini", "video.mp4"], Names(normalAssets.Entry));
        Assert.AreEqual(0, Leftovers(_root.FullName).Count);
    }

    [TestMethod]
    public void OnlyVp9Av1AndHevcAreUnsupported()
    {
        foreach (string codec in new[] { "video/vp9", "video/av1", "video/hevc", "VIDEO/VP9" })
            Assert.IsTrue(VideoCodecs.IsUnsupported(codec), codec);
        foreach (string codec in new[] { "video/vp8", "video/h264", "video/mpeg4", "video/mpeg1video", "video/theora", "video/webm", "audio/opus", "" })
            Assert.IsFalse(VideoCodecs.IsUnsupported(codec), codec);
        Assert.IsFalse(VideoCodecs.IsUnsupported(null));
    }

    [TestMethod]
    public void TheDefaultCqIsThirtyTwo()
    {
        Assert.AreEqual(32, VideoOptimizeOptions.Default.Cq);
        Assert.AreEqual(VideoTarget.Fhd, VideoOptimizeOptions.Default.Target);
        Assert.IsFalse(VideoOptimizeOptions.Default.ConvertUnsupported);
    }

    [TestMethod]
    public async Task AnUnsupportedCodecIsKnownToSaveNothingUntilItsConvertedWhenItShowsWhatThatCosts()
    {
        var assets = await InspectedAsync("video/vp9");
        var optimizer = new FakeOptimizer { Estimate = FakeOptimizer.DefaultEstimate(1.5) };

        // Convert-for-compatibility is off: the video is left alone, which is known without measuring it.
        var left = OptimizationPlanner.UnitsOf(assets, VideoOptions()).Single();
        Assert.AreEqual(0, left.KnownSavingBytes);
        Assert.AreEqual(0, new SavingsModel(VideoOptions()).Predict([left]).TotalBytes);

        // On, it has to be measured, and the estimate is what converting costs.
        var converting = VideoOptions(convertUnsupported: true);
        Assert.IsTrue(OptimizationPlanner.UnitsOf(assets, converting).Single().NeedsMeasuring);
        var plan = await OptimizationPlanner.PlanAsync(assets, converting, new OptimizerSet(null, optimizer), TestContext.CancellationToken);
        Assert.AreEqual(-2500, SavingsModel.Measured(plan).TotalBytes);
    }
}
