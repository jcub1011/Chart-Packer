using ChartBuilder.Services;
using ChartShared.Core.Generation.Neural;
using ChartShared.Core.Settings;

namespace ChartBuilder.Tests.Editing;

/// <summary>
/// The Reduce dialog's default method, kept in <c>generation-defaults.json</c> beside Chart Builder's settings, which Chart
/// Packer reads too; always in a temp folder.
/// </summary>
[TestClass]
public sealed class ReduceMethodSettingTests
{
    private DirectoryInfo _root = null!;
    private string _path = null!;

    [TestInitialize]
    public void CreateRoot()
    {
        _root = Directory.CreateTempSubdirectory("chartpacker-reduce-method-");
        _path = Path.Combine(_root.FullName, "builder-settings.json");
    }

    [TestCleanup]
    public void DeleteRoot() => _root.Delete(recursive: true);

    [TestMethod]
    public void AMissingFileHasTheRulesMethod()
    {
        Assert.AreEqual(GenerationDefaults.RulesMethodId, BuilderSettingsService.Load(_path).Generation.Load().MethodId);
    }

    [TestMethod]
    public void TheDefaultsLiveBesideTheSettingsFile()
    {
        var service = BuilderSettingsService.Load(_path);
        var defaults = service.Generation.Load();
        defaults.MethodId = NeuralDifficultyGenerator.NeuralMethodId;
        defaults.Pack = "studio";
        service.Generation.Save(defaults);

        Assert.IsTrue(File.Exists(Path.Combine(_root.FullName, "generation-defaults.json")));
        var read = new GenerationDefaultsFile(Path.Combine(_root.FullName, "generation-defaults.json")).Load();
        Assert.IsTrue(read.IsNeural);
        Assert.AreEqual("studio", read.Pack);
    }
}
