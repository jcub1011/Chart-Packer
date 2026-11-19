using ChartShared.Core.Optimization;
using ChartShared.Core.Scanning;
using static ChartShared.Core.Tests.Optimization.OptimizationTestSupport;

namespace ChartShared.Core.Tests.Optimization;

/// <summary>What <see cref="AssetInspector"/> makes of a chart's files, in a folder and in a <c>.sng</c>.</summary>
[TestClass]
public sealed class AssetInspectorTests
{
    private DirectoryInfo _root = null!;

    [TestInitialize]
    public void CreateRoot() => _root = Directory.CreateTempSubdirectory("chartpacker-opt-");

    [TestCleanup]
    public void DeleteRoot() => _root.Delete(recursive: true);

    public TestContext TestContext { get; set; }

    private static (string, byte[])[] Files() =>
    [
        ("guitar.ogg", Bytes(100, 1)), ("Song.OPUS", Bytes(50, 2)), ("preview.mp3", Bytes(10, 3)), ("foo.ogg", Bytes(5, 4)),
        ("album.PNG", Bytes(20, 5)), ("background.jpg", Bytes(30, 6)), ("video.webm", Bytes(400, 7)),
        ("highway.png", Bytes(8, 8)), ("notes.chart", Bytes(9, 9)),
    ];

    [TestMethod]
    public async Task AFolderIsClassifiedBySizeExtensionAndKind()
    {
        var entry = Folder(_root.FullName, "Folder", null, Files());

        var chart = await AssetInspector.InspectAsync(entry, cancellationToken: TestContext.CancellationToken);

        AssertClassified(chart);
        Assert.IsNotNull(chart.Stamp);
    }

    [TestMethod]
    public async Task ASngIsClassifiedTheSameWay()
    {
        var entry = await SngAsync(_root.FullName, "Packed", null, Files());

        var chart = await AssetInspector.InspectAsync(entry, cancellationToken: TestContext.CancellationToken);

        AssertClassified(chart);
    }

    private static void AssertClassified(ChartAssets chart)
    {
        var byName = chart.Assets.ToDictionary(a => a.StoredName, StringComparer.OrdinalIgnoreCase);
        Assert.AreEqual(AssetKind.Audio, byName["guitar.ogg"].Kind);
        Assert.AreEqual("guitar", byName["guitar.ogg"].Stem);
        Assert.AreEqual(100, byName["guitar.ogg"].Length);
        Assert.AreEqual(".ogg", byName["guitar.ogg"].Extension);
        Assert.AreEqual("song", byName["Song.OPUS"].Stem);
        Assert.AreEqual(".opus", byName["Song.OPUS"].Extension);
        Assert.AreEqual(AssetKind.Audio, byName["preview.mp3"].Kind);
        Assert.AreEqual(AssetKind.Other, byName["foo.ogg"].Kind, "Audio that isn't a registered stem is left alone.");
        Assert.AreEqual(AssetKind.AlbumArt, byName["album.PNG"].Kind);
        Assert.AreEqual(AssetKind.BackgroundImage, byName["background.jpg"].Kind);
        Assert.AreEqual(AssetKind.Video, byName["video.webm"].Kind);
        Assert.AreEqual(AssetKind.HighwayImage, byName["highway.png"].Kind);
        Assert.AreEqual(AssetKind.Chart, byName["notes.chart"].Kind);
        if (chart.Entry.Kind == SongEntryKind.SongFolder)
            Assert.AreEqual(AssetKind.SongIni, byName["song.ini"].Kind);
    }

    [TestMethod]
    public async Task ProbesGiveCodecDimensionsAndDuration()
    {
        var entry = Folder(_root.FullName, "Folder", null, Files());
        var probe = new FakeProbe();
        probe.Results["guitar.ogg"] = new ProbeResult("audio/ogg", Duration: TimeSpan.FromSeconds(90));
        probe.Results["video.webm"] = new ProbeResult("video/vp9", 1920, 1080, TimeSpan.FromSeconds(200));
        probe.Results["album.PNG"] = new ProbeResult("image/png", 512, 512);

        var chart = await AssetInspector.InspectAsync(entry, new AssetProbes(probe, probe, probe), TestContext.CancellationToken);

        var video = chart.Assets.Single(a => a.Kind == AssetKind.Video);
        Assert.AreEqual("video/vp9", video.CodecKey);
        Assert.AreEqual(1920, video.Probe!.Width);
        Assert.AreEqual(1080, video.Probe.Height);
        Assert.AreEqual(TimeSpan.FromSeconds(200), video.Probe.Duration);
        Assert.AreEqual(TimeSpan.FromSeconds(90), chart.Assets.Single(a => a.StoredName == "guitar.ogg").Probe!.Duration);
        Assert.AreEqual("image/png", chart.Assets.Single(a => a.Kind == AssetKind.AlbumArt).CodecKey);
        Assert.IsNull(chart.Assets.Single(a => a.StoredName == "Song.OPUS").Probe, "A file no probe knows is unprobed.");
        Assert.AreEqual("audio/opus", chart.Assets.Single(a => a.StoredName == "Song.OPUS").CodecKey, "Its codec falls back to its extension.");
    }

    [TestMethod]
    public async Task HighwayImagesChartsAndOtherFilesAreNeverProbed()
    {
        var entry = await SngAsync(_root.FullName, "Packed", null, Files());
        var probe = new FakeProbe();

        await AssetInspector.InspectAsync(entry, new AssetProbes(probe, probe, probe), TestContext.CancellationToken);

        CollectionAssert.DoesNotContain(probe.Probed, "highway.png");
        CollectionAssert.DoesNotContain(probe.Probed, "notes.chart");
        CollectionAssert.DoesNotContain(probe.Probed, "foo.ogg");
        CollectionAssert.Contains(probe.Probed, "guitar.ogg");
        CollectionAssert.Contains(probe.Probed, "video.webm");
    }

    [TestMethod]
    public void HighwayImagesAreClassifiedByStem()
    {
        Assert.AreEqual(AssetKind.HighwayImage, AssetInspector.Classify("highway.jpg"));
        Assert.AreEqual(AssetKind.HighwayImage, AssetInspector.Classify("HIGHWAY.JPEG"));
        Assert.AreEqual(AssetKind.Other, AssetInspector.Classify("highway.bmp"));
        Assert.AreEqual(AssetKind.Other, AssetInspector.Classify("art/album.png"), "A file in a folder is never one of the chart's.");
    }
}
