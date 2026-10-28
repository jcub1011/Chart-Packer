using System.Text.Json.Nodes;
using ChartBuilder.Services;

namespace ChartBuilder.Tests.Settings;

/// <summary>Each settings file Chart Builder owns records an integer schema version (charter-spec §10), in temp folders.</summary>
[TestClass]
public sealed class SettingsSchemaVersionTests
{
    private DirectoryInfo _root = null!;
    private string _builder = null!;
    private string _input = null!;

    [TestInitialize]
    public void CreateRoot()
    {
        _root = Directory.CreateTempSubdirectory("chartpacker-schema-");
        _builder = Path.Combine(_root.FullName, "builder-settings.json");
        _input = Path.Combine(_root.FullName, "input-bindings.json");
    }

    [TestCleanup]
    public void DeleteRoot() => _root.Delete(recursive: true);

    [TestMethod]
    public void BuilderSettingsAreStampedWithTheSchemaVersionByTheNextSave()
    {
        File.WriteAllText(_builder, """{ "WheelScrollDirection": 0 }""");

        BuilderSettingsService.Load(_builder).Save();

        Assert.AreEqual(BuilderSettingsService.SchemaVersion, VersionOf(_builder));
    }

    [TestMethod]
    public void InputBindingsAreStampedWithTheSchemaVersionByTheNextSave()
    {
        File.WriteAllText(_input, """{ "FiveFret": [] }""");

        Assert.IsTrue(InputSettingsService.Load(_input).Save());

        Assert.AreEqual(InputSettingsService.SchemaVersion, VersionOf(_input));
    }

    [TestMethod]
    public void AFileFromANewerBuildIsLeftUntouched()
    {
        const string newer = """{ "SchemaVersion": 7, "WheelScrollDirection": 0 }""";
        File.WriteAllText(_builder, newer);

        var service = BuilderSettingsService.Load(_builder);
        service.Settings.WheelScrollDirection = WheelScrollDirection.DownIsLater;
        service.Save();

        Assert.AreEqual(newer, File.ReadAllText(_builder));
        Assert.AreEqual(7, VersionOf(_builder));
    }

    private static int VersionOf(string path) =>
        JsonNode.Parse(File.ReadAllText(path))!["SchemaVersion"] is JsonValue value && value.TryGetValue<int>(out var version)
            ? version
            : 0;
}
