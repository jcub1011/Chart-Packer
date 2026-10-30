using System.Text.Json.Nodes;
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

    [TestMethod]
    public void AWriteStampsTheSchemaVersion()
    {
        Assert.IsTrue(new UpdateSettingsFile(_path).SetCheckForUpdates(false));

        Assert.AreEqual(UpdateSettingsFile.SchemaVersion, (int)Read()["SchemaVersion"]!);
        Assert.AreEqual(UpdateSettingsFile.SchemaVersion, new UpdateSettingsFile(_path).StoredSchemaVersion);
    }

    [TestMethod]
    public void ALegacyFileReadsAsVersionZeroAndIsStampedByTheNextWrite()
    {
        File.WriteAllText(_path, """{ "checkForUpdates": false }""");
        Assert.AreEqual(0, new UpdateSettingsFile(_path).StoredSchemaVersion);

        Assert.IsTrue(new UpdateSettingsFile(_path).SetCheckFrequency(UpdateCheckFrequency.Daily));

        Assert.AreEqual(UpdateSettingsFile.SchemaVersion, (int)Read()["SchemaVersion"]!);
    }

    [TestMethod]
    public void AFileFromANewerBuildIsLeftUntouched()
    {
        const string newer = """{ "checkForUpdates": false, "SchemaVersion": 7 }""";
        File.WriteAllText(_path, newer);
        var file = new UpdateSettingsFile(_path);

        Assert.IsFalse(file.SetCheckForUpdates(true));
        Assert.IsFalse(file.SetCheckFrequency(UpdateCheckFrequency.Hourly));
        Assert.IsFalse(file.SetIncludePrereleases(true));
        Assert.IsFalse(file.SetCached(DateTimeOffset.UtcNow, "v1.2.0", false, false));

        Assert.AreEqual(newer, File.ReadAllText(_path));
        Assert.AreEqual(7, file.StoredSchemaVersion);
    }

    private JsonObject Read() => (JsonObject)JsonNode.Parse(File.ReadAllText(_path))!;
}
