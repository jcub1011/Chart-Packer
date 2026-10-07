using ChartPacker.Core.Settings;

namespace ChartPacker.Core.Tests.Settings;

[TestClass]
public sealed class ChartPackerSettingsFileTests
{
    private DirectoryInfo _root = null!;
    private string _path = null!;

    [TestInitialize]
    public void CreateRoot()
    {
        _root = Directory.CreateTempSubdirectory("chartpacker-appsettings-");
        _path = Path.Combine(_root.FullName, "settings.json");
    }

    [TestCleanup]
    public void DeleteRoot() => _root.Delete(recursive: true);

    [TestMethod]
    public void TheLibraryRootIsChartPackersLastFolder()
    {
        File.WriteAllText(_path, """{ "LastFolder": "D:\\Songs", "OutputFolder": null }""");

        Assert.AreEqual(@"D:\Songs", ChartPackerSettingsFile.LibraryRoot(_path));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("{ not json")]
    [DataRow("[]")]
    [DataRow("""{ "OutputFolder": "D:\\Out" }""")]
    [DataRow("""{ "LastFolder": null }""")]
    [DataRow("""{ "LastFolder": " " }""")]
    [DataRow("""{ "LastFolder": 3 }""")]
    public void AMissingOrUnusableFileOrSettingGivesNone(string? json)
    {
        if (json is not null)
            File.WriteAllText(_path, json);

        Assert.IsNull(ChartPackerSettingsFile.LibraryRoot(_path));
    }

    [TestMethod]
    public void ReadingNeverWritesTheFile()
    {
        string json = """{ "LastFolder": "D:\\Songs" }""";
        File.WriteAllText(_path, json);

        ChartPackerSettingsFile.LibraryRoot(_path);

        Assert.AreEqual(json, File.ReadAllText(_path));
        Assert.HasCount(1, _root.GetFiles());
    }
}
