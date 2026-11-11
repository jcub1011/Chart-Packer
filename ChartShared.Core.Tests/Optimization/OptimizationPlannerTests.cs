using ChartShared.Core.Optimization;
using static ChartShared.Core.Tests.Optimization.OptimizationTestSupport;

namespace ChartShared.Core.Tests.Optimization;

/// <summary><see cref="OptimizationPlanner"/>'s skip rules and what it plans, with fake optimizers.</summary>
[TestClass]
public sealed class OptimizationPlannerTests
{
    private DirectoryInfo _root = null!;

    [TestInitialize]
    public void CreateRoot() => _root = Directory.CreateTempSubdirectory("chartpacker-opt-");

    [TestCleanup]
    public void DeleteRoot() => _root.Delete(recursive: true);

    public TestContext TestContext { get; set; }

    private async Task<ChartPlan> PlanAsync(OptimizeOptions options, FakeOptimizer? optimizer, params (string Name, int Length)[] files)
    {
        var entry = Folder(_root.FullName, "Song" + Guid.NewGuid().ToString("N")[..6], null, [.. files.Select(f => (f.Name, Bytes(f.Length, 1)))]);
        var chart = await AssetInspector.InspectAsync(entry, cancellationToken: TestContext.CancellationToken);
        var set = optimizer is null ? OptimizerSet.None : new OptimizerSet(optimizer, optimizer, optimizer);
        return await OptimizationPlanner.PlanAsync(chart, options, set, TestContext.CancellationToken);
    }

    [TestMethod]
    public async Task AFileSavingEnoughIsTranscodedToTheEstimatedName()
    {
        var plan = await PlanAsync(Only(OptimizeStrategy.Audio), new FakeOptimizer(), ("guitar.ogg", 1000));

        var action = ActionFor(plan, OptimizeStrategy.Audio);
        Assert.AreEqual(OptimizeActionKind.Transcode, action.Kind);
        Assert.AreEqual("guitar.opus", action.OutputName);
        Assert.AreEqual(1000, action.InputBytes);
        Assert.AreEqual(500, action.EstimatedOutputBytes);
        Assert.AreEqual(500, action.EstimatedSavingBytes);
        Assert.AreEqual("audio/ogg", action.CodecKey);
    }

    [TestMethod]
    public async Task ASavingBelowTheMarginIsSkipped()
    {
        var optimizer = new FakeOptimizer { Estimate = FakeOptimizer.DefaultEstimate(0.95) };

        var plan = await PlanAsync(Only(OptimizeStrategy.Audio, margin: 0.10), optimizer, ("guitar.ogg", 1000));

        var action = ActionFor(plan, OptimizeStrategy.Audio);
        Assert.AreEqual(OptimizeActionKind.Skip, action.Kind);
        Assert.AreEqual(OptimizeSkipReason.BelowMargin, action.Reason);
    }

    [TestMethod]
    public async Task ASavingJustAtTheMarginIsKept()
    {
        var optimizer = new FakeOptimizer { Estimate = FakeOptimizer.DefaultEstimate(0.90) };

        var plan = await PlanAsync(Only(OptimizeStrategy.Audio, margin: 0.10), optimizer, ("guitar.ogg", 1000));

        Assert.AreEqual(OptimizeActionKind.Transcode, ActionFor(plan, OptimizeStrategy.Audio).Kind);
    }

    [TestMethod]
    [DataRow(1.0)]
    [DataRow(1.5)]
    public async Task AnOutputAsLargeAsTheInputIsSkippedEvenWithNoMargin(double ratio)
    {
        var optimizer = new FakeOptimizer { Estimate = FakeOptimizer.DefaultEstimate(ratio) };

        var plan = await PlanAsync(Only(OptimizeStrategy.Video, margin: 0), optimizer, ("video.mp4", 1000));

        var action = ActionFor(plan, OptimizeStrategy.Video);
        Assert.AreEqual(OptimizeSkipReason.WouldGrow, action.Reason);
        Assert.AreEqual((long)(1000 * ratio), action.EstimatedOutputBytes, "The estimate is kept, so the report can show the loss.");
    }

    [TestMethod]
    public async Task AnAlreadyOptimalFileIsSkippedWhateverItWouldSave()
    {
        var optimizer = new FakeOptimizer { Estimate = files => new OptimizeEstimate(1, ".opus", AlreadyOptimal: true) };

        var plan = await PlanAsync(Only(OptimizeStrategy.Audio), optimizer, ("guitar.opus", 1000));

        Assert.AreEqual(OptimizeSkipReason.AlreadyOptimal, ActionFor(plan, OptimizeStrategy.Audio).Reason);
    }

    [TestMethod]
    public async Task AMissingOrUnavailableOptimizerSkipsItsFiles()
    {
        var plan = await PlanAsync(Only(OptimizeStrategy.Audio), null, ("guitar.ogg", 1000));
        Assert.AreEqual(OptimizeSkipReason.OptimizerUnavailable, ActionFor(plan, OptimizeStrategy.Audio).Reason);

        plan = await PlanAsync(Only(OptimizeStrategy.Audio), new FakeOptimizer { IsAvailable = false }, ("guitar.ogg", 1000));
        Assert.AreEqual(OptimizeSkipReason.OptimizerUnavailable, ActionFor(plan, OptimizeStrategy.Audio).Reason);
    }

    [TestMethod]
    public async Task AFileThatCantBeReadIsSkippedNotFatal()
    {
        var optimizer = new FakeOptimizer { FailEstimateWhen = _ => true };

        var plan = await PlanAsync(Only(OptimizeStrategy.Audio), optimizer, ("guitar.ogg", 1000), ("bass.ogg", 1000));

        Assert.IsTrue(plan.Actions.All(a => a.Reason == OptimizeSkipReason.EstimateFailed));
    }

    [TestMethod]
    public async Task OnlyEnabledStrategiesAreEstimated()
    {
        var optimizer = new FakeOptimizer();

        var plan = await PlanAsync(Only(OptimizeStrategy.Video), optimizer, ("guitar.ogg", 1000), ("video.mp4", 1000), ("album.png", 1000));

        Assert.AreEqual(1, plan.Actions.Count);
        Assert.AreEqual(OptimizeStrategy.Video, plan.Actions[0].Strategy);
        Assert.AreEqual(1, optimizer.Estimated.Count);
    }

    [TestMethod]
    public async Task HighwayImagesChartsAndUnregisteredFilesGetNoAction()
    {
        var optimizer = new FakeOptimizer();

        var plan = await PlanAsync(AllOn() with { Metadata = MetadataCleanupOptions.Default },
            optimizer, ("highway.png", 1000), ("notes.chart", 1000), ("foo.ogg", 1000), ("readme.txt", 1000));

        Assert.AreEqual(0, plan.Actions.Count);
        Assert.AreEqual(0, optimizer.Estimated.Count);
    }

    [TestMethod]
    public async Task ImagesAndVideosEachGetTheirOwnStrategy()
    {
        var plan = await PlanAsync(AllOn() with { Metadata = MetadataCleanupOptions.Default }, new FakeOptimizer(),
            ("album.png", 1000), ("background.jpg", 1000), ("video.mp4", 1000));

        Assert.AreEqual("album.png", ActionFor(plan, OptimizeStrategy.AlbumArt).OutputName);
        Assert.AreEqual(OptimizeActionKind.Recompress, ActionFor(plan, OptimizeStrategy.BackgroundArt).Kind);
        Assert.AreEqual("video.webm", ActionFor(plan, OptimizeStrategy.Video).OutputName);
    }

    [TestMethod]
    public async Task RemovingNeedsNoOptimizer()
    {
        var options = Only(OptimizeStrategy.Video) with { Video = new VideoOptimizeOptions(Enabled: true, Remove: true) };

        var plan = await PlanAsync(options, null, ("video.mp4", 1000));

        var action = ActionFor(plan, OptimizeStrategy.Video);
        Assert.AreEqual(OptimizeActionKind.Remove, action.Kind);
        Assert.AreEqual(0, action.EstimatedOutputBytes);
        Assert.IsNull(action.OutputName);
    }

    [TestMethod]
    public async Task AnOutputNameAnotherFileHasIsSkipped()
    {
        var plan = await PlanAsync(Only(OptimizeStrategy.Audio), new FakeOptimizer(), ("guitar.ogg", 1000), ("guitar.opus", 10));

        var action = ActionFor(plan, OptimizeStrategy.Audio, "guitar.ogg");
        Assert.AreEqual(OptimizeSkipReason.OutputNameTaken, action.Reason);
        Assert.AreEqual(1, plan.Actions.Count, "A stem with two formats is planned once.");
    }

    [TestMethod]
    public async Task TwoFilesRecompressedToOneNameAreNotBothPlanned()
    {
        var optimizer = new FakeOptimizer { Estimate = files => new OptimizeEstimate(files[0].Asset.Length / 2, ".webp") };

        var plan = await PlanAsync(Only(OptimizeStrategy.AlbumArt), optimizer, ("album.png", 1000), ("album.jpg", 1000));

        Assert.AreEqual(2, plan.Actions.Count);
        Assert.AreEqual(1, plan.Actions.Count(a => !a.IsSkip), "Only one of them gets album.webp.");
        Assert.AreEqual(OptimizeSkipReason.OutputNameTaken, plan.Actions.Single(a => a.IsSkip).Reason);
        Assert.AreEqual(1, plan.Actions.Where(a => !a.IsSkip).Select(a => a.OutputName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [TestMethod]
    public async Task AnOptimizerFailingInAnyWayOnlySkipsItsFile()
    {
        var optimizer = new FakeOptimizer { Estimate = _ => throw new InvalidOperationException("A native binding failed.") };

        var plan = await PlanAsync(Only(OptimizeStrategy.Audio), optimizer, ("guitar.ogg", 1000), ("bass.ogg", 1000));

        Assert.AreEqual(2, plan.Actions.Count);
        Assert.IsTrue(plan.Actions.All(a => a.Reason == OptimizeSkipReason.EstimateFailed));
    }

    [TestMethod]
    public async Task ARecompressionKeepingItsNameIsNotAClash()
    {
        var plan = await PlanAsync(Only(OptimizeStrategy.AlbumArt), new FakeOptimizer(), ("album.png", 1000));

        Assert.AreEqual(OptimizeActionKind.Recompress, ActionFor(plan, OptimizeStrategy.AlbumArt).Kind);
    }

    [TestMethod]
    public async Task StemsAreMixedDownToOneSongAndNotAlsoTranscoded()
    {
        var optimizer = new FakeOptimizer();
        var options = AllOn() with { StemMixdown = new AudioOptimizeOptions(Enabled: true), Metadata = MetadataCleanupOptions.Default };

        var plan = await PlanAsync(options, optimizer, ("guitar.ogg", 1000), ("rhythm.ogg", 1000), ("drums.wav", 4000), ("guitar.wav", 9000), ("preview.ogg", 500));

        var mix = ActionFor(plan, OptimizeStrategy.StemMixdown);
        Assert.AreEqual(OptimizeActionKind.MixDown, mix.Kind);
        Assert.AreEqual("song.opus", mix.OutputName);
        CollectionAssert.AreEquivalent(new[] { "guitar.ogg", "rhythm.ogg", "drums.wav", "guitar.wav" }, mix.SourceNames.ToArray(), "Every format of a mixed stem goes.");
        Assert.AreEqual(15000, mix.InputBytes);
        CollectionAssert.Contains(optimizer.Estimated, "guitar.ogg+rhythm.ogg+drums.wav", "One file of each stem is mixed.");

        var audio = plan.Actions.Where(a => a.Strategy == OptimizeStrategy.Audio).ToList();
        Assert.AreEqual(1, audio.Count, "Only the preview is left to transcode.");
        Assert.AreEqual("preview.ogg", audio[0].SourceNames[0]);
    }

    [TestMethod]
    public async Task AChartWithOneStemIsNotMixedDown()
    {
        var plan = await PlanAsync(Only(OptimizeStrategy.StemMixdown) with { StemMixdown = new AudioOptimizeOptions(Enabled: true) },
            new FakeOptimizer(), ("song.ogg", 1000), ("preview.ogg", 100));

        Assert.AreEqual(0, plan.Actions.Count);
    }

    [TestMethod]
    public async Task AMixdownThatSavesTooLittleLeavesTheStemsToBeTranscoded()
    {
        var optimizer = new FakeOptimizer
        {
            Estimate = files => files.Count > 1
                ? new OptimizeEstimate(files.Sum(f => f.Asset.Length), ".opus")
                : new OptimizeEstimate(files[0].Asset.Length / 2, ".opus"),
        };
        var options = AllOn() with { StemMixdown = new AudioOptimizeOptions(Enabled: true), Metadata = MetadataCleanupOptions.Default };

        var plan = await PlanAsync(options, optimizer, ("guitar.ogg", 1000), ("bass.ogg", 1000));

        Assert.AreEqual(OptimizeSkipReason.WouldGrow, ActionFor(plan, OptimizeStrategy.StemMixdown).Reason);
        Assert.AreEqual(2, plan.Actions.Count(a => a.Strategy == OptimizeStrategy.Audio && a.Kind == OptimizeActionKind.Transcode));
    }

    [TestMethod]
    public async Task MetadataCleanupIsExemptFromTheMargin()
    {
        string ini = "[song]\nname = Song\nartist =\nfrets = Someone\n";
        var entry = Folder(_root.FullName, "Meta", ini);
        var chart = await AssetInspector.InspectAsync(entry, cancellationToken: TestContext.CancellationToken);

        var plan = await OptimizationPlanner.PlanAsync(chart, Only(OptimizeStrategy.Metadata, margin: 0.9), OptimizerSet.None, TestContext.CancellationToken);

        var action = ActionFor(plan, OptimizeStrategy.Metadata);
        Assert.AreEqual(OptimizeActionKind.CleanMetadata, action.Kind);
        Assert.IsNotNull(plan.Metadata);
        Assert.IsTrue(action.EstimatedSavingBytes >= 0);
    }

    [TestMethod]
    public async Task ACleanChartHasNothingToClean()
    {
        var plan = await PlanAsync(Only(OptimizeStrategy.Metadata), null, ("guitar.ogg", 10));

        Assert.AreEqual(OptimizeSkipReason.NothingToClean, ActionFor(plan, OptimizeStrategy.Metadata).Reason);
        Assert.IsFalse(plan.HasWork);
        Assert.IsNull(plan.Metadata);
    }
}
