using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ChartPacker.Core.Settings;

namespace ChartPacker.Core.Tests.Settings;

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

        CollectionAssert.AreEqual(new[] { "settings.json" }, _root.GetFiles().Select(f => f.Name).ToArray());
    }

    private JsonSettingsFile<TestSettings> Open() => new(_path, TestSettingsJsonContext.Default.TestSettings);
}

public sealed class TestSettings
{
    public string Name { get; set; } = "Default";

    public int Count { get; set; } = 1;
}

[JsonSerializable(typeof(TestSettings))]
internal sealed partial class TestSettingsJsonContext : JsonSerializerContext
{
}
