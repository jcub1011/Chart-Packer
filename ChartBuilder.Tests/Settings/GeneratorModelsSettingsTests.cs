using ChartBuilder.Services;
using ChartShared.Core.Generation;
using ChartShared.Core.Settings;
using ChartShared.UI.Services;

namespace ChartBuilder.Tests.Settings;

/// <summary>The generator model storage setting (issue #154), as both apps' Settings show it, always in temp folders.</summary>
[TestClass]
public sealed class GeneratorModelsSettingsTests
{
    private DirectoryInfo _root = null!;
    private string _settingsPath = null!;

    [TestInitialize]
    public void CreateRoot()
    {
        _root = Directory.CreateTempSubdirectory("chartpacker-generator-settings-");
        _settingsPath = Path.Combine(_root.FullName, "builder-settings.json");
    }

    [TestCleanup]
    public void DeleteRoot() => _root.Delete(recursive: true);

    private GeneratorModelStore Store() => new(
        new GeneratorModelFolderFile(Path.Combine(_root.FullName, "generator-model-settings.json")),
        Path.Combine(_root.FullName, "GeneratorModels"));

    [TestMethod]
    public void TheCatalogManagesTheSharedModels()
    {
        var settings = BuilderSettingsService.Load(_settingsPath);
        var latency = new AudioLatencyService(new AudioLatencyFile(Path.Combine(_root.FullName, "latency.json")));
        bool managed = false;
        var store = Store();
        var catalog = BuilderSettingsCatalog.Create(
            settings, latency, () => { }, () => { }, () => { }, () => { }, () => managed = true, store);

        var models = catalog.Definitions.Single(d => d.Id == "charter.generatorModels");
        Assert.AreEqual("charter.generatorModels", models.Category.Id);
        Assert.IsNotNull(models.Label);
        Assert.IsTrue(models.Description.Contains(store.ResolvedFolder, StringComparison.Ordinal),
            "The row names the folder it manages.");
        Assert.AreEqual(1, models.Actions.Count);

        models.Actions[0].Run();
        Assert.IsTrue(managed, "Manage opens the storage dialog.");
    }
}
