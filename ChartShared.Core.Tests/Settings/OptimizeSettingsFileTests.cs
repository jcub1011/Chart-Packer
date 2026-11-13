using ChartShared.Core.Optimization;
using ChartShared.Core.Settings;

namespace ChartShared.Core.Tests.Settings;

/// <summary><c>optimizer-settings.json</c>: the defaults the Library Optimization Tool starts from.</summary>
[TestClass]
public sealed class OptimizeSettingsFileTests
{
    private DirectoryInfo _root = null!;
    private string _path = null!;

    [TestInitialize]
    public void CreateRoot()
    {
        _root = Directory.CreateTempSubdirectory("chartpacker-optimize-settings-");
        _path = Path.Combine(_root.FullName, "optimizer-settings.json");
    }

    [TestCleanup]
    public void DeleteRoot() => _root.Delete(recursive: true);

    [TestMethod]
    public void WithNoFileTheSettingsAreTheShippedOnes()
    {
        var settings = new OptimizeSettingsFile(_path).Load();

        Assert.IsFalse(settings.AudioEnabled);
        Assert.AreEqual(128, settings.AudioBitrateKbps);
        Assert.AreEqual(32, settings.VideoCq);
        Assert.AreEqual(VideoTarget.Fhd, settings.VideoTarget);
        Assert.AreEqual(0.10, settings.SavingsMargin);
        Assert.IsTrue(settings.UseOutputFolder, "the library isn't overwritten until the user chooses that");
        Assert.IsNull(settings.OutputFolder);
        Assert.IsFalse(File.Exists(_path));
    }

    [TestMethod]
    public void SettingsSavedAreReadBack()
    {
        var saved = new OptimizeSettings
        {
            AudioEnabled = true,
            AudioBitrateKbps = 80,
            VideoEnabled = true,
            VideoTarget = VideoTarget.Uhd,
            VideoCq = 40,
            VideoConvertUnsupported = true,
            AlbumArtMaxWidth = null,
            BackgroundArtQuality = 70,
            SavingsMargin = 0.25,
            UseOutputFolder = false,
            OutputFolder = @"D:\Optimized",
            Parallelism = 3,
        };
        Assert.IsTrue(new OptimizeSettingsFile(_path).Save(saved));

        var read = new OptimizeSettingsFile(_path).Load();

        Assert.IsTrue(read.AudioEnabled);
        Assert.AreEqual(80, read.AudioBitrateKbps);
        Assert.AreEqual(VideoTarget.Uhd, read.VideoTarget);
        Assert.AreEqual(40, read.VideoCq);
        Assert.IsTrue(read.VideoConvertUnsupported);
        Assert.IsNull(read.AlbumArtMaxWidth);
        Assert.AreEqual(512, read.AlbumArtMaxHeight);
        Assert.AreEqual(70, read.BackgroundArtQuality);
        Assert.AreEqual(0.25, read.SavingsMargin);
        Assert.IsFalse(read.UseOutputFolder);
        Assert.AreEqual(@"D:\Optimized", read.OutputFolder);
        Assert.AreEqual(3, read.Parallelism);
        StringAssert.Contains(File.ReadAllText(_path), "\"Uhd\"", "enums are written by name");
    }

    [TestMethod]
    [DataRow(VideoTarget.Sd)]
    [DataRow(VideoTarget.Hd)]
    [DataRow(VideoTarget.Qhd)]
    public void EveryVideoTargetIsReadBack(VideoTarget target)
    {
        Assert.IsTrue(new OptimizeSettingsFile(_path).Save(new OptimizeSettings { VideoTarget = target }));

        Assert.AreEqual(target, new OptimizeSettingsFile(_path).Load().VideoTarget);
    }

    [TestMethod]
    public void AnUnreadableFileGivesTheShippedSettings()
    {
        File.WriteAllText(_path, "{ not json");

        var settings = new OptimizeSettingsFile(_path).Load();

        Assert.AreEqual(OptimizeSettings.Shipped.VideoCq, settings.VideoCq);
        Assert.IsFalse(settings.VideoEnabled);
    }

    [TestMethod]
    public void AnUnknownEnumNameGivesTheShippedSettings()
    {
        File.WriteAllText(_path, """{ "VideoTarget": "Cinema", "AudioEnabled": true }""");

        var settings = new OptimizeSettingsFile(_path).Load();

        Assert.AreEqual(VideoTarget.Fhd, settings.VideoTarget);
    }

    [TestMethod]
    public void ValuesOutOfRangeAreKeptToWhatTheyCanBe()
    {
        File.WriteAllText(_path, """
            { "VideoCq": 99, "AudioBitrateKbps": 1, "AlbumArtQuality": 500, "BackgroundArtMaxWidth": 1,
              "SavingsMargin": 5, "Parallelism": -2 }
            """);

        var settings = new OptimizeSettingsFile(_path).Load();

        Assert.AreEqual(OptimizeSettings.MaxCq, settings.VideoCq);
        Assert.AreEqual(OptimizeSettings.MinBitrateKbps, settings.AudioBitrateKbps);
        Assert.AreEqual(100, settings.AlbumArtQuality);
        Assert.AreEqual(OptimizeSettings.MinImageSize, settings.BackgroundArtMaxWidth);
        Assert.AreEqual(0.9, settings.SavingsMargin);
        Assert.AreEqual(0, settings.Parallelism);
    }

    [TestMethod]
    public void TheOptionsCarryEverySettingAndTheOutputFolder()
    {
        var settings = new OptimizeSettings
        {
            MetadataEnabled = true,
            PruneEmptyParts = false,
            AudioEnabled = true,
            AudioBitrateKbps = 80,
            StemMixdownEnabled = true,
            StemMixdownBitrateKbps = 96,
            VideoEnabled = true,
            VideoTarget = VideoTarget.Uhd,
            VideoCq = 28,
            VideoConvertUnsupported = true,
            VideoSpeed = VideoEncodeSpeed.Best,
            AlbumArtEnabled = true,
            AlbumArtQuality = 60,
            AlbumArtMaxWidth = 300,
            AlbumArtMaxHeight = null,
            BackgroundArtEnabled = true,
            SavingsMargin = 0.2,
            Parallelism = 4,
        };

        var options = settings.ToOptions(@"C:\Out");

        Assert.IsTrue(options.Metadata.Enabled);
        Assert.IsFalse(options.Metadata.PruneEmptyParts);
        Assert.AreEqual(new AudioOptimizeOptions(true, 80), options.Audio);
        Assert.AreEqual(new AudioOptimizeOptions(true, 96), options.StemMixdown);
        Assert.AreEqual(new VideoOptimizeOptions(true, VideoTarget.Uhd, 28, false, true, VideoEncodeSpeed.Best), options.Video);
        Assert.AreEqual(new ImageOptimizeOptions(true, 60, 300, null), options.AlbumArt);
        Assert.IsTrue(options.BackgroundArt.Enabled);
        Assert.AreEqual(1920, options.BackgroundArt.MaxWidth);
        Assert.AreEqual(0.2, options.SavingsMargin);
        Assert.AreEqual(@"C:\Out", options.OutputFolder);
        Assert.AreEqual(4, options.Parallelism);
    }

    [TestMethod]
    public void NoOutputFolderMeansInPlaceAndNoParallelismTakesTheRecommendedOne()
    {
        var options = OptimizeSettings.Shipped.ToOptions("  ");

        Assert.IsNull(options.OutputFolder);
        Assert.AreEqual(OptimizeOptions.RecommendedParallelism, options.Parallelism);
        foreach (var strategy in Enum.GetValues<OptimizeStrategy>())
            Assert.IsFalse(options.IsEnabled(strategy), strategy.ToString());
    }
}
