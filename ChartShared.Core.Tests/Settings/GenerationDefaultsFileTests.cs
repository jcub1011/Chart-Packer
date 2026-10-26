using ChartShared.Core.Charting.Authoring;
using ChartShared.Core.Generation;
using ChartShared.Core.Generation.Neural;
using ChartShared.Core.Settings;

namespace ChartShared.Core.Tests.Settings;

/// <summary><c>generation-defaults.json</c>, always in a temp folder.</summary>
[TestClass]
public sealed class GenerationDefaultsFileTests
{
    private DirectoryInfo _root = null!;
    private string _path = null!;

    [TestInitialize]
    public void CreateRoot()
    {
        _root = Directory.CreateTempSubdirectory("chartpacker-generation-defaults-");
        _path = Path.Combine(_root.FullName, "generation-defaults.json");
    }

    [TestCleanup]
    public void DeleteRoot() => _root.Delete(recursive: true);

    [TestMethod]
    public void AMissingFileIsRules()
    {
        var defaults = new GenerationDefaultsFile(_path).Load();

        Assert.AreEqual(GenerationDefaults.RulesMethodId, defaults.MethodId);
        Assert.IsFalse(defaults.IsNeural);
        Assert.IsNull(defaults.Generator(null, ReductionDefaults.Shipped));
    }

    [TestMethod]
    public void TheDefaultsReadBack()
    {
        var file = new GenerationDefaultsFile(_path);
        var defaults = file.Load();
        defaults.MethodId = NeuralDifficultyGenerator.NeuralMethodId;
        defaults.Pack = "studio";
        defaults.Style = "Harmonix";
        defaults.Density = 0.5;
        defaults.Chained = false;
        defaults.CapLanes = false;
        Assert.IsTrue(file.Save(defaults));

        var read = new GenerationDefaultsFile(_path).Load();

        Assert.IsTrue(read.IsNeural);
        Assert.AreEqual("studio", read.Pack);
        Assert.AreEqual("Harmonix", read.Style);
        Assert.AreEqual(0.5, read.Density);
        Assert.IsFalse(read.Chained);
        Assert.IsFalse(read.CapLanes);
        Assert.AreEqual(0.5 * GenerationDefaults.DensityScale, read.Options().DensityBias);
    }

    [TestMethod]
    public void ValuesAreKeptToWhatTheyCanBe()
    {
        File.WriteAllText(_path, """{ "MethodId": "something-else", "Style": " ", "Density": 9 }""");

        var read = new GenerationDefaultsFile(_path).Load();

        Assert.AreEqual(GenerationDefaults.RulesMethodId, read.MethodId);
        Assert.AreEqual("Any", read.Style);
        Assert.AreEqual(1.0, read.Density);
    }

    [TestMethod]
    public void NeuralDefaultsPickTheNeuralGenerator()
    {
        var store = new GeneratorModelStore(new GeneratorModelFolderFile(Path.Combine(_root.FullName, "settings.json")), Path.Combine(_root.FullName, "models"));
        var defaults = new GenerationDefaults { MethodId = NeuralDifficultyGenerator.NeuralMethodId };

        Assert.IsInstanceOfType<NeuralDifficultyGenerator>(defaults.Generator(store, ReductionDefaults.Shipped));
        Assert.IsNull(defaults.Generator(null, ReductionDefaults.Shipped), "No store, no packs: Rules.");
    }
}
