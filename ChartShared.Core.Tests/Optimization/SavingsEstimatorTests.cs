using ChartShared.Core.Optimization;
using ChartShared.Core.Scanning;
using static ChartShared.Core.Tests.Optimization.OptimizationTestSupport;

namespace ChartShared.Core.Tests.Optimization;

/// <summary>How charts are sampled for a savings estimate, and how the estimate is grouped by codec.</summary>
[TestClass]
public sealed class SavingsEstimatorTests
{
    private DirectoryInfo _root = null!;

    [TestInitialize]
    public void CreateRoot() => _root = Directory.CreateTempSubdirectory("chartpacker-opt-");

    [TestCleanup]
    public void DeleteRoot() => _root.Delete(recursive: true);

    public TestContext TestContext { get; set; }

    private static ChartAssets Chart(string relative) =>
        Assets(new SongEntry(SongEntryKind.SongFolder, Path.Combine("C:", "Lib", relative), relative, Path.GetFileName(relative)));

    private static IReadOnlyList<ChartAssets> Library(int count) => [.. Enumerable.Range(0, count).Select(i => Chart($"Artist {i % 7}/Song {i}"))];

    [TestMethod]
    public void ARandomSampleIsTheSameEveryTimeAndWhateverTheOrder()
    {
        var library = Library(60);

        var first = SampleSelector.Select(library, SamplingMode.RandomSample, sampleSize: 8);
        var again = SampleSelector.Select(library, SamplingMode.RandomSample, sampleSize: 8);
        var reversed = SampleSelector.Select([.. library.Reverse()], SamplingMode.RandomSample, sampleSize: 8);

        Assert.AreEqual(8, first.Count);
        Assert.AreSequenceEqual(first.Select(c => c.Entry.RelativePath), again.Select(c => c.Entry.RelativePath));
        Assert.AreSequenceEqual(first.Select(c => c.Entry.RelativePath), reversed.Select(c => c.Entry.RelativePath));
    }

    [TestMethod]
    public void TheSampleDependsOnRelativePathsNotWhereTheLibraryIs()
    {
        var here = Library(40);
        var elsewhere = Library(40).Select(c => c with { Entry = c.Entry with { FullPath = Path.Combine("D:", "Other", c.Entry.RelativePath) } }).ToList();

        var a = SampleSelector.Select(here, SamplingMode.RandomSample, sampleSize: 5);
        var b = SampleSelector.Select(elsewhere, SamplingMode.RandomSample, sampleSize: 5);

        Assert.AreSequenceEqual(a.Select(c => c.Entry.RelativePath), b.Select(c => c.Entry.RelativePath));
    }

    [TestMethod]
    public void SeparatorsDontChangeTheSample()
    {
        var slash = Library(30);
        var backslash = slash.Select(c => c with { Entry = c.Entry with { RelativePath = c.Entry.RelativePath.Replace('/', '\\') } }).ToList();

        var a = SampleSelector.Select(slash, SamplingMode.RandomSample, sampleSize: 6);
        var b = SampleSelector.Select(backslash, SamplingMode.RandomSample, sampleSize: 6);

        Assert.AreSequenceEqual(a.Select(c => c.Entry.RelativePath.Replace('\\', '/')), b.Select(c => c.Entry.RelativePath.Replace('\\', '/')));
    }

    [TestMethod]
    public void ASampleLargerThanTheLibraryIsTheWholeLibraryAndAllChartsIsAlwaysAll()
    {
        var library = Library(5);

        Assert.AreEqual(5, SampleSelector.Select(library, SamplingMode.RandomSample, sampleSize: 50).Count);
        Assert.AreEqual(5, SampleSelector.Select(library, SamplingMode.AllCharts, sampleSize: 1).Count);
    }

    [TestMethod]
    public void ASingleChartIsTheSelectedOne()
    {
        var library = Library(5);

        var chosen = SampleSelector.Select(library, SamplingMode.SingleChart, library[3].Entry);

        Assert.AreEqual(library[3], chosen.Single());
        Assert.ThrowsExactly<ArgumentException>(() => SampleSelector.Select(library, SamplingMode.SingleChart));
        Assert.ThrowsExactly<ArgumentException>(() => SampleSelector.Select(library, SamplingMode.SingleChart, Chart("Nowhere").Entry));
    }

    [TestMethod]
    public async Task ImagesRejectASingleChart()
    {
        var entry = Folder(_root.FullName, "Song", null, ("album.png", Bytes(100, 1)));
        var chart = await AssetInspector.InspectAsync(entry, cancellationToken: TestContext.CancellationToken);
        var optimizers = new OptimizerSet(null, null, new FakeOptimizer());

        foreach (var strategy in new[] { OptimizeStrategy.AlbumArt, OptimizeStrategy.BackgroundArt })
        {
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => SavingsEstimator.EstimateAsync(
                strategy, [chart], OptimizeOptions.Default, optimizers, SamplingMode.SingleChart, entry, cancellationToken: TestContext.CancellationToken));
        }

        var sampled = await SavingsEstimator.EstimateAsync(
            OptimizeStrategy.AlbumArt, [chart], OptimizeOptions.Default, optimizers, SamplingMode.RandomSample, cancellationToken: TestContext.CancellationToken);
        Assert.AreEqual(1, sampled.ChartsSampled);
    }

    [TestMethod]
    public async Task MetadataHasNoEstimate()
    {
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => SavingsEstimator.EstimateAsync(
            OptimizeStrategy.Metadata, [], OptimizeOptions.Default, OptimizerSet.None, SamplingMode.AllCharts, cancellationToken: TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task AnUnavailableOptimizerCantEstimate()
    {
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => SavingsEstimator.EstimateAsync(
            OptimizeStrategy.Video, [], OptimizeOptions.Default, new OptimizerSet(null, new FakeOptimizer { IsAvailable = false }), SamplingMode.AllCharts,
            cancellationToken: TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task EstimatesAreGroupedByCodecAndExtrapolatedFromTheSample()
    {
        var probe = new FakeProbe();
        probe.Results["video.webm"] = new ProbeResult("video/vp9");
        probe.Results["video.mp4"] = new ProbeResult("video/h264");
        var inspected = new List<ChartAssets>();
        // Two VP9 charts (one sampled), and two H.264 charts.
        foreach (var (name, file, length) in new[] { ("A", "video.webm", 1000), ("B", "video.webm", 3000), ("C", "video.mp4", 2000), ("D", "video.mp4", 4000) })
        {
            var entry = Folder(_root.FullName, name, null, (file, Bytes(length, 1)));
            inspected.Add(await AssetInspector.InspectAsync(entry, new AssetProbes(null, probe, null), TestContext.CancellationToken));
        }

        // VP9 grows when "compressed"; H.264 halves.
        var optimizer = new FakeOptimizer
        {
            Estimate = files => new OptimizeEstimate(files[0].Asset.Probe!.CodecKey == "video/vp9" ? files[0].Asset.Length * 2 : files[0].Asset.Length / 2, ".webm"),
        };

        var estimate = await SavingsEstimator.EstimateAsync(
            OptimizeStrategy.Video, inspected, OptimizeOptions.Default, new OptimizerSet(null, optimizer), SamplingMode.AllCharts, cancellationToken: TestContext.CancellationToken);

        Assert.AreEqual(4, estimate.ChartsSampled);
        var vp9 = estimate.Codecs.Single(c => c.CodecKey == "video/vp9");
        var h264 = estimate.Codecs.Single(c => c.CodecKey == "video/h264");
        Assert.AreEqual(4000, vp9.TotalInputBytes);
        Assert.AreEqual(-4000, vp9.EstimatedSavingBytes, "A codec that gets larger shows as a negative saving.");
        Assert.AreEqual(3000, h264.EstimatedSavingBytes);
        Assert.AreEqual(2, h264.AssetsTotal);
        Assert.AreEqual(2, h264.AssetsSampled);
        Assert.AreEqual(-1000, estimate.EstimatedSavingBytes);
        Assert.AreSequenceEqual(["video/h264", "video/vp9"], estimate.Codecs.Select(c => c.CodecKey));
    }

    [TestMethod]
    public async Task ASampleIsExtrapolatedOverTheWholeLibrary()
    {
        var inspected = new List<ChartAssets>();
        for (int i = 0; i < 6; i++)
            inspected.Add(await AssetInspector.InspectAsync(Folder(_root.FullName, "S" + i, null, ("video.mp4", Bytes(1000, 1))), cancellationToken: TestContext.CancellationToken));
        var optimizer = new FakeOptimizer();

        var estimate = await SavingsEstimator.EstimateAsync(
            OptimizeStrategy.Video, inspected, OptimizeOptions.Default, new OptimizerSet(null, optimizer), SamplingMode.RandomSample, sampleSize: 2, cancellationToken: TestContext.CancellationToken);

        Assert.AreEqual(2, optimizer.Estimated.Count, "Only the sample is estimated.");
        var codec = estimate.Codecs.Single();
        Assert.AreEqual(2, codec.AssetsSampled);
        Assert.AreEqual(6, codec.AssetsTotal);
        Assert.AreEqual(6000, codec.TotalInputBytes);
        Assert.AreEqual(3000, codec.EstimatedSavingBytes);
        Assert.AreEqual(0.5, codec.Ratio);
    }

    [TestMethod]
    public async Task AFileThatCantBeReadIsLeftOutOfItsCodec()
    {
        var inspected = new List<ChartAssets>();
        foreach (var name in new[] { "A", "B" })
            inspected.Add(await AssetInspector.InspectAsync(Folder(_root.FullName, name, null, ("video.mp4", Bytes(1000, 1))), cancellationToken: TestContext.CancellationToken));
        var optimizer = new FakeOptimizer { FailEstimateWhen = files => files[0].Path.Contains(Path.DirectorySeparatorChar + "A" + Path.DirectorySeparatorChar) };

        var estimate = await SavingsEstimator.EstimateAsync(
            OptimizeStrategy.Video, inspected, OptimizeOptions.Default, new OptimizerSet(null, optimizer), SamplingMode.AllCharts, cancellationToken: TestContext.CancellationToken);

        Assert.AreEqual(1, estimate.Codecs.Single().AssetsSampled);
        Assert.AreEqual(2, estimate.Codecs.Single().AssetsTotal);
    }

    [TestMethod]
    public async Task ARemovalSavesTheWholeFileAndNeedsNoOptimizer()
    {
        var entry = Folder(_root.FullName, "S", null, ("video.mp4", Bytes(1000, 1)));
        var chart = await AssetInspector.InspectAsync(entry, cancellationToken: TestContext.CancellationToken);
        var options = OptimizeOptions.Default with { Video = new VideoOptimizeOptions(Enabled: true, Remove: true) };

        var estimate = await SavingsEstimator.EstimateAsync(
            OptimizeStrategy.Video, [chart], options, OptimizerSet.None, SamplingMode.AllCharts, cancellationToken: TestContext.CancellationToken);

        Assert.AreEqual(1000, estimate.EstimatedSavingBytes);
    }

    [TestMethod]
    public async Task ASngSampleIsExtractedToTempAndLeavesNothingBehind()
    {
        var entry = await SngAsync(_root.FullName, "Packed", null, ("video.mp4", Bytes(1000, 1)));
        var chart = await AssetInspector.InspectAsync(entry, cancellationToken: TestContext.CancellationToken);
        var optimizer = new FakeOptimizer();

        var estimate = await SavingsEstimator.EstimateAsync(
            OptimizeStrategy.Video, [chart], OptimizeOptions.Default, new OptimizerSet(null, optimizer), SamplingMode.AllCharts, cancellationToken: TestContext.CancellationToken);

        Assert.AreEqual(500, estimate.EstimatedSavingBytes);
        Assert.AreEqual(0, Leftovers(_root.FullName).Count);
        Assert.AreSequenceEqual(["Packed.sng"], Directory.GetFileSystemEntries(_root.FullName).Select(Path.GetFileName));
    }

    [TestMethod]
    public async Task AMixdownIsEstimatedOncePerChartUnderItsOwnKey()
    {
        var entry = Folder(_root.FullName, "Stems", null, ("guitar.ogg", Bytes(1000, 1)), ("bass.ogg", Bytes(1000, 2)), ("preview.ogg", Bytes(500, 3)));
        var chart = await AssetInspector.InspectAsync(entry, cancellationToken: TestContext.CancellationToken);
        var optimizer = new FakeOptimizer();

        var estimate = await SavingsEstimator.EstimateAsync(
            OptimizeStrategy.StemMixdown, [chart], OptimizeOptions.Default, new OptimizerSet(optimizer), SamplingMode.AllCharts, cancellationToken: TestContext.CancellationToken);

        Assert.AreSequenceEqual(["bass.ogg+guitar.ogg"], optimizer.Estimated.Select(e => string.Join("+", e.Split('+').Order())));
        var codec = estimate.Codecs.Single();
        Assert.AreEqual("audio/mixdown", codec.CodecKey);
        Assert.AreEqual(2000, codec.TotalInputBytes);
    }
}
