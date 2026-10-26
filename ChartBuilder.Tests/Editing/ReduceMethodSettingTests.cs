using ChartBuilder.Services;

namespace ChartBuilder.Tests.Editing;

/// <summary>The Reduce dialog's method, as saved in Chart Builder's settings, always in a temp folder.</summary>
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
        Assert.AreEqual(BuilderSettings.DefaultReduceMethodId, BuilderSettingsService.Load(_path).Settings.ReduceMethodId);
    }

    [TestMethod]
    public void ABlankMethodFallsBackToRules()
    {
        File.WriteAllText(_path, """{ "ReduceMethodId": "" }""");
        Assert.AreEqual(BuilderSettings.DefaultReduceMethodId, BuilderSettingsService.Load(_path).Settings.ReduceMethodId);
    }

    [TestMethod]
    public void AMethodReadsBack()
    {
        var service = BuilderSettingsService.Load(_path);
        service.Settings.ReduceMethodId = BuilderSettings.DefaultReduceMethodId;
        service.Save();

        Assert.AreEqual(BuilderSettings.DefaultReduceMethodId, BuilderSettingsService.Load(_path).Settings.ReduceMethodId);
    }

    [TestMethod]
    public void MethodSettingsReadBack()
    {
        var service = BuilderSettingsService.Load(_path);
        service.Settings.ReduceMethodSettings = new Dictionary<string, string> { ["neural.model"] = "bigru-crf" };
        service.Save();

        Assert.AreEqual("bigru-crf", BuilderSettingsService.Load(_path).Settings.ReduceMethodSettings!["neural.model"]);
    }

    [TestMethod]
    public void BlankMethodSettingsKeysAreDropped()
    {
        File.WriteAllText(_path, """{ "ReduceMethodSettings": { "": "x", "neural.model": "bigru-crf" } }""");
        var settings = BuilderSettingsService.Load(_path).Settings;

        Assert.IsFalse(settings.ReduceMethodSettings!.ContainsKey(""));
        Assert.AreEqual("bigru-crf", settings.ReduceMethodSettings["neural.model"]);
    }
}
