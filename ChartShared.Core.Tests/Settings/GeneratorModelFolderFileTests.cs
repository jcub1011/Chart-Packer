using ChartShared.Core.Settings;

namespace ChartShared.Core.Tests.Settings;

[TestClass]
public sealed class GeneratorModelFolderFileTests
{
    private DirectoryInfo _root = null!;
    private string _path = null!;

    [TestInitialize]
    public void CreateRoot()
    {
        _root = Directory.CreateTempSubdirectory("chartpacker-generator-folder-");
        _path = Path.Combine(_root.FullName, "generator-model-settings.json");
    }

    [TestCleanup]
    public void DeleteRoot() => _root.Delete(recursive: true);

    [TestMethod]
    public void WithNoFileTheFolderIsTheDefault()
    {
        Assert.IsNull(new GeneratorModelFolderFile(_path).LoadFolder());
        Assert.IsFalse(File.Exists(_path));
    }

    [TestMethod]
    public void AFoldersSavedByOneIsReadByAnother()
    {
        Assert.IsTrue(new GeneratorModelFolderFile(_path).SaveFolder(@"D:\Models"));

        Assert.AreEqual(@"D:\Models", new GeneratorModelFolderFile(_path).LoadFolder());
    }

    [TestMethod]
    public void ABlankFolderMeansTheDefault()
    {
        var file = new GeneratorModelFolderFile(_path);
        file.SaveFolder(@"D:\Models");

        Assert.IsTrue(file.SaveFolder("  "));
        Assert.IsNull(file.LoadFolder());
    }

    [TestMethod]
    public void AnUnreadableFileMeansTheDefault()
    {
        File.WriteAllText(_path, "{ not json");

        Assert.IsNull(new GeneratorModelFolderFile(_path).LoadFolder());
    }
}
