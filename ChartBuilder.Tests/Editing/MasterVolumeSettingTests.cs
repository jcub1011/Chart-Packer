using ChartBuilder.Services;

namespace ChartBuilder.Tests.Editing;

/// <summary>The master volume, as saved in Chart Builder's settings, always in a temp folder.</summary>
[TestClass]
public sealed class MasterVolumeSettingTests
{
    private DirectoryInfo _root = null!;
    private string _path = null!;

    [TestInitialize]
    public void CreateRoot()
    {
        _root = Directory.CreateTempSubdirectory("chartpacker-volume-");
        _path = Path.Combine(_root.FullName, "builder-settings.json");
    }

    [TestCleanup]
    public void DeleteRoot() => _root.Delete(recursive: true);

    [TestMethod]
    public void AMissingFileHasFullVolume()
    {
        Assert.AreEqual(1, BuilderSettingsService.Load(_path).Settings.MasterVolume);
    }

    [TestMethod]
    public void VolumesOutsideSilenceToFullAreClamped()
    {
        File.WriteAllText(_path, """{ "MasterVolume": 2 }""");
        Assert.AreEqual(1, BuilderSettingsService.Load(_path).Settings.MasterVolume);

        File.WriteAllText(_path, """{ "MasterVolume": -0.5 }""");
        Assert.AreEqual(0, BuilderSettingsService.Load(_path).Settings.MasterVolume);
    }

    [TestMethod]
    public void AVolumeReadsBack()
    {
        var service = BuilderSettingsService.Load(_path);
        service.Settings.MasterVolume = 0.42;
        service.Save();

        Assert.AreEqual(0.42, BuilderSettingsService.Load(_path).Settings.MasterVolume, 1e-9);
    }
}
