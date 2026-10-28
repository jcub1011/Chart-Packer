using ChartBuilder.Services;

namespace ChartBuilder.Tests.Editing;

/// <summary>The look-aheads of Chart Builder's highways, as saved in its settings, always in a temp folder.</summary>
[TestClass]
public sealed class LookAheadSettingTests
{
    private DirectoryInfo _root = null!;
    private string _path = null!;

    [TestInitialize]
    public void CreateRoot()
    {
        _root = Directory.CreateTempSubdirectory("chartpacker-lookahead-");
        _path = Path.Combine(_root.FullName, "builder-settings.json");
    }

    [TestCleanup]
    public void DeleteRoot() => _root.Delete(recursive: true);

    [TestMethod]
    public void AMissingFileHasEachHighwaysOwnDefault()
    {
        var settings = BuilderSettingsService.Load(_path).Settings;

        Assert.AreEqual(BuilderSettings.DefaultPreviewLookAhead, settings.PreviewLookAhead, 1e-9);
        Assert.AreEqual(1.5, settings.ReducePreviewLookAhead, 1e-9);
        Assert.AreEqual(BuilderSettings.DefaultGeneratePhrasesLookAhead, settings.GeneratePhrasesLookAhead, 1e-9);
    }

    [TestMethod]
    public void EveryHighwayAllowsHalfASecondToSixtySeconds()
    {
        File.WriteAllText(_path, """{ "PreviewLookAhead": 45, "ReducePreviewLookAhead": 60, "GeneratePhrasesLookAhead": 0.5 }""");
        var settings = BuilderSettingsService.Load(_path).Settings;

        Assert.AreEqual(45, settings.PreviewLookAhead, 1e-9);
        Assert.AreEqual(60, settings.ReducePreviewLookAhead, 1e-9);
        Assert.AreEqual(0.5, settings.GeneratePhrasesLookAhead, 1e-9);
    }

    [TestMethod]
    public void LookAheadsOutsideTheRangeAreClamped()
    {
        File.WriteAllText(_path, """{ "PreviewLookAhead": 500, "ReducePreviewLookAhead": 0, "GeneratePhrasesLookAhead": -1 }""");
        var settings = BuilderSettingsService.Load(_path).Settings;

        Assert.AreEqual(60, settings.PreviewLookAhead, 1e-9);
        Assert.AreEqual(0.5, settings.ReducePreviewLookAhead, 1e-9);
        Assert.AreEqual(0.5, settings.GeneratePhrasesLookAhead, 1e-9);
    }

    [TestMethod]
    public void EveryHighwaysLookAheadReadsBack()
    {
        var service = BuilderSettingsService.Load(_path);
        service.Settings.PreviewLookAhead = 12;
        service.Settings.ReducePreviewLookAhead = 30;
        service.Settings.GeneratePhrasesLookAhead = 7.5;
        service.Save();

        var settings = BuilderSettingsService.Load(_path).Settings;
        Assert.AreEqual(12, settings.PreviewLookAhead, 1e-9);
        Assert.AreEqual(30, settings.ReducePreviewLookAhead, 1e-9);
        Assert.AreEqual(7.5, settings.GeneratePhrasesLookAhead, 1e-9);
    }
}
