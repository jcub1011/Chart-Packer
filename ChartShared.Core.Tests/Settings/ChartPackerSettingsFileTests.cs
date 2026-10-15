using ChartShared.Core.Jobs;
using ChartShared.Core.Scanning;
using ChartShared.Core.Settings;

namespace ChartShared.Core.Tests.Settings;

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
    public void ThePackSettingsAreChartPackersOutputFolderPolicyAndLayout()
    {
        File.WriteAllText(_path, """{ "LastFolder": "D:\\Songs", "OutputFolder": "E:\\Packed", "OverwritePolicy": "Rename", "FlatPackOutput": true }""");

        var settings = ChartPackerSettingsFile.PackSettings(_path);

        Assert.AreEqual(new ChartPackerPackSettings(@"E:\Packed", OverwritePolicy.Rename, true, @"D:\Songs"), settings);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("{ not json")]
    [DataRow("""{ "OverwritePolicy": "Explode", "FlatPackOutput": "yes" }""")]
    public void MissingPackSettingsAreChartPackersDefaults(string? json)
    {
        if (json is not null)
            File.WriteAllText(_path, json);

        Assert.AreEqual(new ChartPackerPackSettings(null, OverwritePolicy.Skip, false, null), ChartPackerSettingsFile.PackSettings(_path));
    }

    [TestMethod]
    public void ASongInTheLibraryPacksAsChartPackerWouldAndOneOutsideItPacksBesideItself()
    {
        string library = Path.Combine(_root.FullName, "Library");
        var inLibrary = new SongEntry(SongEntryKind.SongFolder, Path.Combine(library, "Band", "Song"), "Band/Song", "Song");
        var outside = new SongEntry(SongEntryKind.SongFolder, Path.Combine(_root.FullName, "Elsewhere", "Song"), ".", "Song");
        var settings = new ChartPackerPackSettings(null, OverwritePolicy.Skip, false, library);

        Assert.AreEqual((library, library, OutputLayout.Mirror), settings.PackOf(inLibrary));
        string parent = Path.Combine(_root.FullName, "Elsewhere");
        Assert.AreEqual((parent, parent, OutputLayout.Mirror), settings.PackOf(outside));
        Assert.AreEqual(OutputLayout.Flat, (settings with { Flat = true, OutputFolder = "X:\\Out" }).PackOf(inLibrary).Layout);
        Assert.AreEqual("X:\\Out", (settings with { OutputFolder = "X:\\Out" }).PackOf(inLibrary).Destination);
    }

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
