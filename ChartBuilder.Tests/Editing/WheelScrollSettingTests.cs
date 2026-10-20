using ChartBuilder.Services;

namespace ChartBuilder.Tests.Editing;

/// <summary>Which way the wheel scrolls, as saved in Chart Builder's settings, always in a temp folder.</summary>
[TestClass]
public sealed class WheelScrollSettingTests
{
    private DirectoryInfo _root = null!;
    private string _path = null!;

    [TestInitialize]
    public void CreateRoot()
    {
        _root = Directory.CreateTempSubdirectory("chartpacker-wheel-");
        _path = Path.Combine(_root.FullName, "builder-settings.json");
    }

    [TestCleanup]
    public void DeleteRoot() => _root.Delete(recursive: true);

    [TestMethod]
    public void AMissingFileHasTheWheelTurnedUpLater()
    {
        Assert.AreEqual(WheelScrollDirection.UpIsLater, BuilderSettingsService.Load(_path).Settings.WheelScrollDirection);
    }

    [TestMethod]
    public void AnUnknownDirectionGoesBackToUpIsLater()
    {
        File.WriteAllText(_path, """{ "WheelScrollDirection": 7 }""");

        Assert.AreEqual(WheelScrollDirection.UpIsLater, BuilderSettingsService.Load(_path).Settings.WheelScrollDirection);
    }

    [TestMethod]
    public void DownIsLaterReadsBack()
    {
        var service = BuilderSettingsService.Load(_path);
        service.Settings.WheelScrollDirection = WheelScrollDirection.DownIsLater;
        service.Save();

        Assert.AreEqual(WheelScrollDirection.DownIsLater, BuilderSettingsService.Load(_path).Settings.WheelScrollDirection);
    }
}
