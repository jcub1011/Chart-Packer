using ChartShared.Core.Optimization;
using ChartShared.Core.Scanning;
using ChartShared.Core.Sng;
using static ChartShared.Core.Tests.Optimization.OptimizationTestSupport;

namespace ChartShared.Core.Tests.Optimization;

/// <summary>
/// Album art and background images through <see cref="OptimizationPlanner"/> and <see cref="OptimizeJob"/> with a fake
/// <see cref="IImageOptimizer"/>: the skip rules, the strategies' own switches, removal, and the rename of a <c>.png</c>
/// that becomes a <c>.jpg</c> in folders and sngs, with the metadata that names it. The real encoder is
/// <c>docs/optimizer-image-manual-checks.md</c>'s.
/// </summary>
[TestClass]
public sealed class OptimizeJobImageTests
{
    private DirectoryInfo _root = null!;

    [TestInitialize]
    public void CreateRoot() => _root = Directory.CreateTempSubdirectory("chartpacker-optimg-");

    [TestCleanup]
    public void DeleteRoot() => _root.Delete(recursive: true);

    public TestContext TestContext { get; set; }

    private const string Ini = "; by hand\n[song]\nname = Song\nartist =\nbackground = background.png\ncover = Album.PNG\nicon = gh\n";

    private static (string, byte[])[] Files() =>
    [
        ("guitar.ogg", Bytes(1000, 1)), ("album.png", Bytes(800, 2)), ("background.png", Bytes(6000, 3)), ("highway.png", Bytes(300, 4)),
    ];

    private static IReadOnlyList<KeyValuePair<string, string>> SngMetadata() =>
    [
        KeyValuePair.Create("name", "Song"), KeyValuePair.Create("artist", ""), KeyValuePair.Create("background", "background.png"),
        KeyValuePair.Create("cover", "Album.PNG"), KeyValuePair.Create("icon", "gh"),
    ];

    /// <summary>Halves an image and, as the real optimizer does for an opaque PNG, stores it as a <c>.jpg</c>.</summary>
    private static Func<IReadOnlyList<AssetFile>, OptimizeEstimate> ToJpg(double ratio) => files =>
    {
        var asset = files[0].Asset;
        string extension = asset.Extension == ".png" ? ".jpg" : asset.Extension;
        return new OptimizeEstimate((long)(asset.Length * ratio), extension);
    };

    private static OptimizeOptions Art(bool album, bool background, double margin = 0.1, string? outputFolder = null)
    {
        var all = AllOn(margin, outputFolder);
        return all with
        {
            Metadata = all.Metadata with { Enabled = false },
            Audio = all.Audio with { Enabled = false },
            Video = all.Video with { Enabled = false },
            AlbumArt = all.AlbumArt with { Enabled = album },
            BackgroundArt = all.BackgroundArt with { Enabled = background },
        };
    }

    private Task<SongEntry> FolderChartAsync(string name = "Folder", string ini = Ini, params (string, byte[])[] files) =>
        Task.FromResult(Folder(_root.FullName, name, ini, files.Length == 0 ? Files() : files));

    private Task<SongEntry> SngChartAsync(string name = "Packed", params (string, byte[])[] files) =>
        SngAsync(_root.FullName, name, SngMetadata(), files.Length == 0 ? Files() : files);

    private async Task<ChartPlan> PlanAsync(SongEntry entry, OptimizeOptions options, FakeOptimizer optimizer)
    {
        var chart = await AssetInspector.InspectAsync(entry, cancellationToken: TestContext.CancellationToken);
        return await OptimizationPlanner.PlanAsync(chart, options, new OptimizerSet(null, null, optimizer), TestContext.CancellationToken);
    }

    private async Task<OptimizeItemResult> RunAsync(ChartPlan plan, OptimizeOptions options, FakeOptimizer optimizer)
    {
        var result = await OptimizeJob.RunAsync([plan], options, new OptimizerSet(null, null, optimizer), cancellationToken: TestContext.CancellationToken);
        return result.Items.Single();
    }

    private static string IniOf(SongEntry entry) => File.ReadAllText(Path.Combine(entry.FullPath, "song.ini")).ReplaceLineEndings("\n");

    private async Task<Dictionary<string, string>> SngMetadataOfAsync(string path)
    {
        var read = await SngReader.ReadAsync(path, TestContext.CancellationToken);
        Assert.IsFalse(read.HasErrors);
        return read.Metadata.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
    }

    // ---- Folders and sngs ------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task AFoldersPngsBecomeJpgsAndTheSongIniFollows()
    {
        var entry = await FolderChartAsync();
        var optimizer = new FakeOptimizer { Estimate = ToJpg(0.25) };
        var options = Art(true, true);

        var item = await RunAsync(await PlanAsync(entry, options, optimizer), options, optimizer);

        Assert.AreEqual(OptimizeItemStatus.Succeeded, item.Status, item.ErrorMessage);
        Assert.AreSequenceEqual(["album.jpg", "background.jpg", "guitar.ogg", "highway.png", "song.ini"], Names(entry));
        Assert.AreEqual(200, (await ReadAsync(entry, "album.jpg"))!.Length);
        Assert.AreEqual(1500, (await ReadAsync(entry, "background.jpg"))!.Length);
        Assert.AreSequenceEqual(Bytes(300, 4), await ReadAsync(entry, "highway.png"), "highway.* is left alone.");
        Assert.AreSequenceEqual(Bytes(1000, 1), await ReadAsync(entry, "guitar.ogg"), "Audio isn't this strategy's.");
        Assert.AreEqual("; by hand\n[song]\nname = Song\nartist =\nbackground = background.jpg\ncover = album.jpg\nicon = gh\n", IniOf(entry),
            "Only the values naming a renamed file change; the comment, spacing and other keys stay.");
        Assert.AreEqual(0, Leftovers(_root.FullName).Count);
    }

    [TestMethod]
    public async Task ASngsPngsBecomeJpgsAndItsMetadataFollows()
    {
        var entry = await SngChartAsync();
        var optimizer = new FakeOptimizer { Estimate = ToJpg(0.25) };
        var options = Art(true, true);

        var item = await RunAsync(await PlanAsync(entry, options, optimizer), options, optimizer);

        Assert.AreEqual(OptimizeItemStatus.Succeeded, item.Status, item.ErrorMessage);
        Assert.AreSequenceEqual(["album.jpg", "background.jpg", "guitar.ogg", "highway.png"], Names(entry));
        Assert.AreEqual(200, (await ReadAsync(entry, "album.jpg"))!.Length);
        Assert.AreSequenceEqual(Bytes(300, 4), await ReadAsync(entry, "highway.png"));
        var metadata = await SngMetadataOfAsync(entry.FullPath);
        Assert.AreEqual("background.jpg", metadata["background"]);
        Assert.AreEqual("album.jpg", metadata["cover"]);
        Assert.AreEqual("Song", metadata["name"]);
        Assert.AreEqual("gh", metadata["icon"]);
        Assert.AreEqual(0, Leftovers(_root.FullName).Count);
    }

    [TestMethod]
    public async Task ARenameIntoAnOutputFolderLeavesTheOriginalAlone()
    {
        var folder = await FolderChartAsync();
        var sng = await SngChartAsync();
        string output = Path.Combine(_root.FullName, "Out");
        var optimizer = new FakeOptimizer { Estimate = ToJpg(0.25) };
        var options = Art(true, true, outputFolder: output);
        string iniBefore = IniOf(folder);
        byte[] sngBefore = await File.ReadAllBytesAsync(sng.FullPath, TestContext.CancellationToken);

        var folderItem = await RunAsync(await PlanAsync(folder, options, optimizer), options, optimizer);
        var sngItem = await RunAsync(await PlanAsync(sng, options, optimizer), options, optimizer);

        Assert.AreEqual(OptimizeItemStatus.Succeeded, folderItem.Status, folderItem.ErrorMessage);
        Assert.AreEqual(OptimizeItemStatus.Succeeded, sngItem.Status, sngItem.ErrorMessage);
        Assert.AreEqual(iniBefore, IniOf(folder));
        Assert.AreSequenceEqual(sngBefore, await File.ReadAllBytesAsync(sng.FullPath, TestContext.CancellationToken));
        Assert.AreSequenceEqual(["album.png", "background.png", "guitar.ogg", "highway.png", "song.ini"], Names(folder));

        var copy = new SongEntry(SongEntryKind.SongFolder, Path.Combine(output, "Folder"), "Folder", "Folder");
        Assert.AreSequenceEqual(["album.jpg", "background.jpg", "guitar.ogg", "highway.png", "song.ini"], Names(copy));
        StringAssert.Contains(IniOf(copy), "background = background.jpg");
        var sngCopy = new SongEntry(SongEntryKind.SngFile, Path.Combine(output, "Packed.sng"), "Packed.sng", "Packed");
        Assert.AreSequenceEqual(["album.jpg", "background.jpg", "guitar.ogg", "highway.png"], Names(sngCopy));
        Assert.AreEqual("album.jpg", (await SngMetadataOfAsync(sngCopy.FullPath))["cover"]);
        Assert.AreEqual(0, Leftovers(_root.FullName).Count);
    }

    [TestMethod]
    public async Task AJpgKeepsItsNameAndTheSongIniIsNotTouched()
    {
        var entry = await FolderChartAsync(files: [("album.jpg", Bytes(800, 2)), ("background.jpeg", Bytes(6000, 3))]);
        var optimizer = new FakeOptimizer { Estimate = ToJpg(0.5) };
        var options = Art(true, true);
        byte[] iniBefore = await File.ReadAllBytesAsync(Path.Combine(entry.FullPath, "song.ini"), TestContext.CancellationToken);

        var item = await RunAsync(await PlanAsync(entry, options, optimizer), options, optimizer);

        Assert.AreEqual(OptimizeItemStatus.Succeeded, item.Status, item.ErrorMessage);
        Assert.AreSequenceEqual(["album.jpg", "background.jpeg", "song.ini"], Names(entry));
        Assert.AreEqual(400, (await ReadAsync(entry, "album.jpg"))!.Length);
        Assert.AreEqual(3000, (await ReadAsync(entry, "background.jpeg"))!.Length);
        Assert.AreSequenceEqual(iniBefore, await File.ReadAllBytesAsync(Path.Combine(entry.FullPath, "song.ini"), TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task CleanupAndARenameBothReachTheSongIni()
    {
        var entry = await FolderChartAsync();
        var optimizer = new FakeOptimizer { Estimate = ToJpg(0.25) };
        var options = Art(false, true) with
        {
            Metadata = new MetadataCleanupOptions(Enabled: true, PruneEmptyParts: false, StripEditorEvents: false, DropEmptyValues: true, ReplaceLegacyKeys: false),
        };

        var item = await RunAsync(await PlanAsync(entry, options, optimizer), options, optimizer);

        Assert.AreEqual(OptimizeItemStatus.Succeeded, item.Status, item.ErrorMessage);
        Assert.AreEqual("; by hand\n[song]\nname = Song\nbackground = background.jpg\ncover = Album.PNG\nicon = gh\n", IniOf(entry),
            "The empty artist is dropped and the background renamed; the album art, not optimized, keeps its name.");
    }

    [TestMethod]
    public async Task ARenameIsUndoneWhenTheSongIniCantBeReadSoNothingPointsAtTheMissingFile()
    {
        // A song.ini with no [song] section can't be read, so it can't be pointed at album.jpg: the image stays as it is.
        const string unreadable = "[other]\nbackground = background.png\ncover = album.png\n";
        var entry = await FolderChartAsync(ini: unreadable);
        var optimizer = new FakeOptimizer { Estimate = ToJpg(0.25) };
        var options = Art(true, true);

        var item = await RunAsync(await PlanAsync(entry, options, optimizer), options, optimizer);

        Assert.AreEqual(OptimizeItemStatus.Failed, item.Status);
        Assert.AreSequenceEqual(["album.png", "background.png", "guitar.ogg", "highway.png", "song.ini"], Names(entry));
        Assert.AreSequenceEqual(Bytes(800, 2), await ReadAsync(entry, "album.png"));
        Assert.AreEqual(unreadable, IniOf(entry));
        Assert.IsTrue(item.Assets.Where(a => a.Strategy is OptimizeStrategy.AlbumArt or OptimizeStrategy.BackgroundArt).All(a => a.Outcome == AssetOutcome.Failed));
        Assert.AreEqual(0, Leftovers(_root.FullName).Count);
    }

    [TestMethod]
    public async Task OnlyTheRenamedImageIsUndoneWhenTheMetadataCantBeUpdatedAndAnotherImageKeepsItsName()
    {
        var entry = await FolderChartAsync(ini: "[other]\nbackground = x\n", files: [("album.png", Bytes(800, 2)), ("background.jpg", Bytes(6000, 3))]);
        var optimizer = new FakeOptimizer { Estimate = ToJpg(0.25) };
        var options = Art(true, true);

        var item = await RunAsync(await PlanAsync(entry, options, optimizer), options, optimizer);

        Assert.AreEqual(OptimizeItemStatus.Succeeded, item.Status, item.ErrorMessage);
        Assert.AreSequenceEqual(["album.png", "background.jpg", "song.ini"], Names(entry));
        Assert.AreEqual(1500, (await ReadAsync(entry, "background.jpg"))!.Length, "A .jpg keeps its name, so it needs no reference to change.");
        Assert.AreEqual(AssetOutcome.Failed, item.Assets.Single(a => a.StoredName == "album.png").Outcome);
    }

    // ---- Skip rules ------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task ASavingBelowTheMarginLeavesTheImageAlone()
    {
        var entry = await FolderChartAsync();
        var optimizer = new FakeOptimizer { Estimate = ToJpg(0.95) };
        var options = Art(true, true, margin: 0.10);

        var plan = await PlanAsync(entry, options, optimizer);
        var item = await RunAsync(plan, options, optimizer);

        Assert.IsTrue(plan.Actions.All(a => a.Reason == OptimizeSkipReason.BelowMargin));
        Assert.AreEqual(OptimizeItemStatus.Skipped, item.Status);
        Assert.AreSequenceEqual(["album.png", "background.png", "guitar.ogg", "highway.png", "song.ini"], Names(entry));
        Assert.AreEqual(Ini.ReplaceLineEndings("\n"), IniOf(entry));
    }

    [TestMethod]
    [DataRow(1.0)]
    [DataRow(1.4)]
    public async Task AResultAsLargeAsTheImageIsNotKept(double ratio)
    {
        var entry = await FolderChartAsync();
        var optimizer = new FakeOptimizer { Estimate = ToJpg(ratio) };
        var options = Art(true, true, margin: 0);

        var plan = await PlanAsync(entry, options, optimizer);

        Assert.IsTrue(plan.Actions.All(a => a.Reason == OptimizeSkipReason.WouldGrow));
        Assert.IsFalse(plan.HasWork);
    }

    [TestMethod]
    public async Task AnImageThatIsAlreadyOptimalIsSkipped()
    {
        var entry = await FolderChartAsync();
        var optimizer = new FakeOptimizer { Estimate = files => new OptimizeEstimate(files[0].Asset.Length, files[0].Asset.Extension, AlreadyOptimal: true) };
        var options = Art(true, true);

        var plan = await PlanAsync(entry, options, optimizer);

        Assert.IsTrue(plan.Actions.All(a => a.Reason == OptimizeSkipReason.AlreadyOptimal));
        Assert.IsFalse(plan.HasWork);
    }

    [TestMethod]
    public async Task AWrittenImageThatIsntSmallerIsNotKept()
    {
        var entry = await FolderChartAsync();
        // The estimate was a guess: the encoder wrote more than the image was.
        var optimizer = new FakeOptimizer { Estimate = ToJpg(0.25), WrittenLength = files => files[0].Asset.Length + 10 };
        var options = Art(true, false);

        var item = await RunAsync(await PlanAsync(entry, options, optimizer), options, optimizer);

        Assert.AreEqual(OptimizeItemStatus.Skipped, item.Status);
        Assert.AreSequenceEqual(["album.png", "background.png", "guitar.ogg", "highway.png", "song.ini"], Names(entry));
        Assert.AreEqual(0, Leftovers(_root.FullName).Count);
    }

    [TestMethod]
    public async Task AnImageThatFailsToDecodeIsSkippedAndTheRestOfTheChartIsDone()
    {
        var entry = await FolderChartAsync();
        var optimizer = new FakeOptimizer { Estimate = ToJpg(0.25), FailEstimateWhen = files => files[0].Asset.StoredName == "background.png" };
        var options = Art(true, true);

        var plan = await PlanAsync(entry, options, optimizer);
        var item = await RunAsync(plan, options, optimizer);

        Assert.AreEqual(OptimizeSkipReason.EstimateFailed, ActionFor(plan, OptimizeStrategy.BackgroundArt).Reason);
        Assert.AreEqual(OptimizeItemStatus.Succeeded, item.Status, item.ErrorMessage);
        Assert.AreSequenceEqual(["album.jpg", "background.png", "guitar.ogg", "highway.png", "song.ini"], Names(entry));
        StringAssert.Contains(IniOf(entry), "background = background.png");
        StringAssert.Contains(IniOf(entry), "cover = album.jpg");
    }

    [TestMethod]
    public async Task ARenameOntoAnExistingNameIsSkipped()
    {
        // album.jpg is already there: album.png -> album.jpg would replace a different file.
        var entry = await FolderChartAsync(files: [("album.png", Bytes(800, 2)), ("album.jpg", Bytes(900, 5))]);
        var optimizer = new FakeOptimizer { Estimate = ToJpg(0.25) };
        var options = Art(true, false);

        var plan = await PlanAsync(entry, options, optimizer);

        Assert.AreEqual(OptimizeSkipReason.OutputNameTaken, ActionFor(plan, OptimizeStrategy.AlbumArt, "album.png").Reason);
        Assert.AreEqual(OptimizeActionKind.Recompress, ActionFor(plan, OptimizeStrategy.AlbumArt, "album.jpg").Kind);
    }

    // ---- The strategies are separate -------------------------------------------------------------------------------

    [TestMethod]
    public async Task EachStrategyRunsOnlyWhenItsOn()
    {
        var optimizer = new FakeOptimizer { Estimate = ToJpg(0.25) };
        var albumOnly = Art(true, false);
        var backgroundOnly = Art(false, true);
        var neither = Art(false, false);

        var a = await PlanAsync(await FolderChartAsync("A"), albumOnly, optimizer);
        var b = await PlanAsync(await FolderChartAsync("B"), backgroundOnly, optimizer);
        var n = await PlanAsync(await FolderChartAsync("N"), neither, optimizer);

        Assert.AreSequenceEqual(["album.png"], a.Work.SelectMany(w => w.SourceNames));
        Assert.AreSequenceEqual(["background.png"], b.Work.SelectMany(w => w.SourceNames));
        Assert.AreEqual(0, n.Actions.Count);
    }

    [TestMethod]
    public async Task AnUnavailableImageOptimizerLeavesTheImagesAlone()
    {
        var entry = await FolderChartAsync();
        var optimizer = new FakeOptimizer { IsAvailable = false };

        var plan = await PlanAsync(entry, Art(true, true), optimizer);

        Assert.IsTrue(plan.Actions.All(a => a.Reason == OptimizeSkipReason.OptimizerUnavailable));
        Assert.AreEqual(0, optimizer.Estimated.Count);
    }

    // ---- Removal ---------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task RemovingAnImageDeletesItAndAsksNothingOfTheOptimizer()
    {
        var folder = await FolderChartAsync();
        var sng = await SngChartAsync();
        var optimizer = new FakeOptimizer();
        var options = Art(true, true) with { BackgroundArt = ImageOptimizeOptions.BackgroundDefault with { Enabled = true, Remove = true } };

        var folderItem = await RunAsync(await PlanAsync(folder, options, optimizer), options, optimizer);
        var sngItem = await RunAsync(await PlanAsync(sng, options, optimizer), options, optimizer);

        Assert.AreEqual(OptimizeItemStatus.Succeeded, folderItem.Status, folderItem.ErrorMessage);
        Assert.AreEqual(OptimizeItemStatus.Succeeded, sngItem.Status, sngItem.ErrorMessage);
        Assert.IsFalse(Names(folder).Contains("background.png"));
        Assert.IsFalse(Names(sng).Contains("background.png"));
        Assert.IsTrue(Names(folder).Contains("album.png"), "Album art, not set to be removed, is recompressed under its own name.");
        Assert.IsFalse(optimizer.Estimated.Contains("background.png"));
        Assert.IsFalse(optimizer.Optimized.Contains("background.png"));
        Assert.AreEqual(0, Leftovers(_root.FullName).Count);
    }

    [TestMethod]
    public async Task AlbumArtCanBeRemovedWhileTheBackgroundIsRecompressed()
    {
        var entry = await FolderChartAsync();
        var optimizer = new FakeOptimizer { Estimate = ToJpg(0.25) };
        var options = Art(true, true) with { AlbumArt = ImageOptimizeOptions.AlbumArtDefault with { Enabled = true, Remove = true } };

        var item = await RunAsync(await PlanAsync(entry, options, optimizer), options, optimizer);

        Assert.AreEqual(OptimizeItemStatus.Succeeded, item.Status, item.ErrorMessage);
        Assert.AreSequenceEqual(["background.jpg", "guitar.ogg", "highway.png", "song.ini"], Names(entry));
    }

    // ---- Estimates -------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task ImageEstimatesAreGroupedBySourceFormatAndNeverNeedAChart()
    {
        var charts = new List<ChartAssets>();
        var probes = new AssetProbes(null, null, new ImageHeaderProbe());
        foreach (var (name, file, bytes) in new[]
        {
            ("A", "album.png", ImageOptimizationTests.Png(600, 600)), ("B", "album.jpg", ImageOptimizationTests.Jpeg(600, 600, 0xC0, false)),
            ("C", "album.jpeg", ImageOptimizationTests.Jpeg(300, 300, 0xC2, false)),
        })
        {
            var entry = Folder(_root.FullName, name, null, (file, Pad(bytes, 1000)));
            charts.Add(await AssetInspector.InspectAsync(entry, probes, TestContext.CancellationToken));
        }

        var estimate = await SavingsEstimator.EstimateAsync(
            OptimizeStrategy.AlbumArt, charts, Art(true, false), new OptimizerSet(null, null, new FakeOptimizer { Estimate = ToJpg(0.5) }),
            SamplingMode.AllCharts, cancellationToken: TestContext.CancellationToken);

        Assert.AreSequenceEqual(["image/jpeg", "image/png"], estimate.Codecs.Select(c => c.CodecKey));
        Assert.AreEqual(2, estimate.Codecs.Single(c => c.CodecKey == "image/jpeg").AssetsTotal);
        Assert.AreEqual(1, estimate.Codecs.Single(c => c.CodecKey == "image/png").AssetsTotal);
        Assert.AreEqual(500, estimate.Codecs.Single(c => c.CodecKey == "image/png").EstimatedSavingBytes);
    }

    private static byte[] Pad(byte[] header, int length)
    {
        var bytes = new byte[length];
        header.CopyTo(bytes, 0);
        return bytes;
    }
}
