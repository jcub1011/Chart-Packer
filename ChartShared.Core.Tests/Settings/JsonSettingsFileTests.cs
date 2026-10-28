using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ChartShared.Core.Settings;

namespace ChartShared.Core.Tests.Settings;

[TestClass]
public sealed class JsonSettingsFileTests
{
    private DirectoryInfo _root = null!;
    private string _path = null!;

    [TestInitialize]
    public void CreateRoot()
    {
        _root = Directory.CreateTempSubdirectory("chartpacker-settings-");
        _path = Path.Combine(_root.FullName, "settings.json");
    }

    [TestCleanup]
    public void DeleteRoot() => _root.Delete(recursive: true);

    [TestMethod]
    public void AMissingFileLoadsDefaults()
    {
        var settings = Open().Load();

        Assert.AreEqual(new TestSettings().Name, settings.Name);
        Assert.AreEqual(new TestSettings().Count, settings.Count);
    }

    [TestMethod]
    public void ACorruptFileLoadsDefaults()
    {
        File.WriteAllText(_path, "{ not json");

        Assert.AreEqual(new TestSettings().Count, Open().Load().Count);
    }

    [TestMethod]
    public void SettingsReadBack()
    {
        var file = Open();
        var settings = file.Load();
        settings.Name = "Charter";
        settings.Count = 7;

        file.Save(settings);
        var read = Open().Load();

        Assert.AreEqual("Charter", read.Name);
        Assert.AreEqual(7, read.Count);
    }

    [TestMethod]
    public void TwoProcessesKeepEachOthersChanges()
    {
        var first = Open();
        var second = Open();
        var firstSettings = first.Load();
        var secondSettings = second.Load();

        secondSettings.Name = "From the second";
        second.Save(secondSettings);
        firstSettings.Count = 3;
        first.Save(firstSettings);

        var read = Open().Load();
        Assert.AreEqual("From the second", read.Name);
        Assert.AreEqual(3, read.Count);
    }

    [TestMethod]
    public void ASettingChangedAgainIsWrittenAgain()
    {
        var file = Open();
        var settings = file.Load();
        settings.Count = 3;
        file.Save(settings);
        settings.Count = 4;

        file.Save(settings);

        Assert.AreEqual(4, Open().Load().Count);
    }

    [TestMethod]
    public void UnknownKeysAreKept()
    {
        File.WriteAllText(_path, """{ "Name": "Old", "FromANewerVersion": [1, 2] }""");
        var file = Open();
        var settings = file.Load();
        settings.Name = "New";

        file.Save(settings);

        var json = JsonNode.Parse(File.ReadAllText(_path))!.AsObject();
        Assert.AreEqual("New", (string?)json["Name"]);
        Assert.IsTrue(JsonNode.DeepEquals(JsonNode.Parse("[1, 2]"), json["FromANewerVersion"]));
    }

    [TestMethod]
    public void NoTempFileIsLeftBehind()
    {
        var file = Open();
        var settings = file.Load();
        settings.Count = 2;

        file.Save(settings);

        // The lock file is deleted as it's let go on Windows only.
        Assert.AreSequenceEqual(new[] { "settings.json" }, [.. _root.GetFiles().Select(f => f.Name).Where(n => !n.EndsWith(".lock", StringComparison.Ordinal))]);
    }

    [TestMethod]
    public void ASaveSaysWhetherItWasWritten()
    {
        var file = Open();
        var settings = file.Load();
        settings.Count = 2;
        Assert.IsTrue(file.Save(settings));

        // A folder where the file goes can't be renamed over.
        File.Delete(_path);
        Directory.CreateDirectory(_path);
        settings.Count = 3;

        Assert.IsFalse(file.Save(settings));
    }

    [TestMethod]
    public void ASettingNotWrittenIsWrittenByTheNextSave()
    {
        var file = Open();
        var settings = file.Load();
        settings.Count = 3;
        using (new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            Assert.IsFalse(file.Save(settings));

        Assert.IsTrue(file.Save(settings));

        Assert.AreEqual(3, Open().Load().Count);
    }

    [TestMethod]
    public void TwoProcessesKeepEachOthersEntries()
    {
        var first = Open();
        var second = Open();
        var firstSettings = first.Load();
        var secondSettings = second.Load();

        firstSettings.Counts = new() { ["A"] = 1 };
        first.Save(firstSettings);
        secondSettings.Counts = new() { ["B"] = 2 };
        second.Save(secondSettings);

        Assert.AreEquivalent(new Dictionary<string, int> { ["A"] = 1, ["B"] = 2 }, Open().Load().Counts!);
    }

    [TestMethod]
    public void AnEntryRemovedIsRemovedAndAnotherProcesssChangeIsKept()
    {
        var setup = Open();
        var settings = setup.Load();
        settings.Counts = new() { ["A"] = 1, ["B"] = 2 };
        setup.Save(settings);

        var first = Open();
        var second = Open();
        var firstSettings = first.Load();
        var secondSettings = second.Load();

        secondSettings.Counts!["B"] = 3;
        second.Save(secondSettings);
        firstSettings.Counts!.Remove("A");
        first.Save(firstSettings);

        Assert.AreEquivalent(new Dictionary<string, int> { ["B"] = 3 }, Open().Load().Counts!);
    }

    [TestMethod]
    public void ASaveWithNothingChangedLeavesTheFileAlone()
    {
        var file = Open();
        var settings = file.Load();
        settings.Count = 2;
        file.Save(settings);
        var written = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(_path, written);

        Assert.IsTrue(file.Save(settings));

        Assert.AreEqual(written, File.GetLastWriteTimeUtc(_path));
    }

    [TestMethod]
    public void ASaveWithNothingChangedStillMakesAMissingFile()
    {
        var file = Open();
        var settings = file.Load();

        Assert.IsTrue(file.Save(settings));

        Assert.IsTrue(File.Exists(_path));
    }

    private JsonSettingsFile<TestSettings> Open() => new(_path, TestSettingsJsonContext.Default.TestSettings, [nameof(TestSettings.Counts)]);
}

public sealed class TestSettings
{
    public string Name { get; set; } = "Default";

    public int Count { get; set; } = 1;

    public Dictionary<string, int>? Counts { get; set; }
}

[JsonSerializable(typeof(TestSettings))]
internal sealed partial class TestSettingsJsonContext : JsonSerializerContext
{
}
