using ChartShared.Core.Updating;

namespace ChartShared.Core.Tests.Updating;

[TestClass]
public sealed class UpdateSettingsFileTests
{
    private DirectoryInfo _root = null!;
    private string _path = "";

    [TestInitialize]
    public void CreateRoot()
    {
        _root = Directory.CreateTempSubdirectory("chartpacker-update-");
        _path = Path.Combine(_root.FullName, "update-settings.json");
    }

    [TestCleanup]
    public void DeleteRoot()
    {
        try { _root.Delete(recursive: true); } catch (Exception) { }
    }

    [TestMethod]
    public void DefaultsToCheckEnabledDailyNoPrereleasesAndNothingCached()
    {
        var settings = new UpdateSettingsFile(_path).Read();
        Assert.IsTrue(settings.CheckForUpdates);
        Assert.AreEqual(UpdateCheckFrequency.Daily, settings.CheckFrequency);
        Assert.IsFalse(settings.IncludePrereleases);
        Assert.IsNull(settings.LastCheckedUtc);
        Assert.IsNull(settings.CachedTag);
        Assert.IsFalse(settings.CachedPrerelease);
        Assert.IsNull(settings.CachedIncludePrereleases);
    }

    [TestMethod]
    public void RoundTripsPreferencesAndCache()
    {
        var file = new UpdateSettingsFile(_path);
        Assert.IsTrue(file.SetCheckForUpdates(false));
        Assert.IsFalse(new UpdateSettingsFile(_path).Read().CheckForUpdates);
        Assert.IsTrue(file.SetCheckForUpdates(true));
        Assert.IsTrue(new UpdateSettingsFile(_path).Read().CheckForUpdates);
        Assert.IsTrue(file.SetCheckFrequency(UpdateCheckFrequency.OnStartup));
        Assert.AreEqual(UpdateCheckFrequency.OnStartup, new UpdateSettingsFile(_path).Read().CheckFrequency);
        Assert.IsTrue(file.SetCheckFrequency(UpdateCheckFrequency.Hourly));
        Assert.AreEqual(UpdateCheckFrequency.Hourly, new UpdateSettingsFile(_path).Read().CheckFrequency);
        Assert.IsTrue(file.SetIncludePrereleases(true));
        Assert.IsTrue(new UpdateSettingsFile(_path).Read().IncludePrereleases);

        var stamp = DateTimeOffset.UtcNow;
        Assert.IsTrue(file.SetCached(stamp, "v1.2.0", prerelease: true, includePrereleases: true));
        var settings = new UpdateSettingsFile(_path).Read();
        Assert.IsNotNull(settings.LastCheckedUtc);
        Assert.AreEqual("v1.2.0", settings.CachedTag);
        Assert.IsTrue(settings.CachedPrerelease);
        Assert.IsTrue(settings.CachedIncludePrereleases);

        Assert.IsTrue(file.SetCached(stamp, null, prerelease: false, includePrereleases: false));
        settings = new UpdateSettingsFile(_path).Read();
        Assert.IsNull(settings.CachedTag);
        Assert.IsFalse(settings.CachedIncludePrereleases);
        Assert.IsTrue(settings.IncludePrereleases, "Caching leaves the preferences alone.");
    }

    [TestMethod]
    public void UnreadableFileReadsAsDefaults()
    {
        Directory.CreateDirectory(_root.FullName);
        File.WriteAllText(_path, "{ not json");
        var settings = new UpdateSettingsFile(_path).Read();
        Assert.IsTrue(settings.CheckForUpdates);
        Assert.AreEqual(UpdateCheckFrequency.Daily, settings.CheckFrequency);
        Assert.IsFalse(settings.IncludePrereleases);
        Assert.IsNull(settings.CachedTag);
        Assert.IsNull(settings.CachedIncludePrereleases);
    }

    [TestMethod]
    public void UnknownFrequencyReadsAsDaily()
    {
        Directory.CreateDirectory(_root.FullName);
        File.WriteAllText(_path, """{ "checkFrequency": "fortnightly" }""");
        Assert.AreEqual(UpdateCheckFrequency.Daily, new UpdateSettingsFile(_path).Read().CheckFrequency);
    }
}
