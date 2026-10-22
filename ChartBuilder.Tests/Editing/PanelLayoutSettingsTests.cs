using ChartBuilder.Services;

namespace ChartBuilder.Tests.Editing;

/// <summary>The side panels' auto-hide and arrangement, as saved in Chart Builder's settings, always in a temp folder.</summary>
[TestClass]
public sealed class PanelLayoutSettingsTests
{
    private DirectoryInfo _root = null!;
    private string _path = null!;

    [TestInitialize]
    public void CreateRoot()
    {
        _root = Directory.CreateTempSubdirectory("chartpacker-panels-");
        _path = Path.Combine(_root.FullName, "builder-settings.json");
    }

    [TestCleanup]
    public void DeleteRoot() => _root.Delete(recursive: true);

    [TestMethod]
    public void AMissingFileHasThePanelsPinnedInTheirDefaultLayout()
    {
        var settings = BuilderSettingsService.Load(_path).Settings;

        Assert.IsFalse(settings.EditorLeftPanelAutoHide);
        Assert.IsFalse(settings.EditorRightPanelAutoHide);
        Assert.IsNull(settings.EditorLeftTabs);
        Assert.IsNull(settings.EditorSectionOrder);
        Assert.IsNull(settings.EditorCollapsedSections);
    }

    [TestMethod]
    public void APanelCollapsedBeforeAutoHideAutoHides()
    {
        File.WriteAllText(_path, """{ "EditorLeftPanelCollapsed": true, "EditorRightPanelCollapsed": false }""");

        var settings = BuilderSettingsService.Load(_path).Settings;

        Assert.IsTrue(settings.EditorLeftPanelAutoHide);
        Assert.IsFalse(settings.EditorRightPanelAutoHide);
    }

    [TestMethod]
    public void AutoHideSavedWinsOverTheOldCollapsedFlag()
    {
        File.WriteAllText(_path, """{ "EditorLeftPanelCollapsed": true, "EditorLeftPanelAutoHide": false }""");

        Assert.IsFalse(BuilderSettingsService.Load(_path).Settings.EditorLeftPanelAutoHide);
    }

    [TestMethod]
    public void BlankAndRepeatedKeysAreDropped()
    {
        File.WriteAllText(_path, """
            {
              "EditorLeftTabs": [ "art", "", "art", "  ", "sections" ],
              "EditorSectionOrder": { "tempo-map": [ "changes", "changes", "timing" ], "": [ "x" ], "empty": [ " " ] },
              "EditorCollapsedSections": [ "art/album-art", "art/album-art" ]
            }
            """);

        var settings = BuilderSettingsService.Load(_path).Settings;

        CollectionAssert.AreEqual(new[] { "art", "sections" }, settings.EditorLeftTabs);
        Assert.AreEqual(1, settings.EditorSectionOrder!.Count);
        CollectionAssert.AreEqual(new[] { "changes", "timing" }, settings.EditorSectionOrder["tempo-map"]);
        CollectionAssert.AreEqual(new[] { "art/album-art" }, settings.EditorCollapsedSections);
    }

    [TestMethod]
    public void TheLayoutReadsBack()
    {
        var service = BuilderSettingsService.Load(_path);
        service.Settings.EditorRightPanelAutoHide = true;
        service.Settings.EditorRightTabs = ["difficulty", "properties"];
        service.Settings.EditorSectionOrder = new() { ["difficulty"] = ["graph", "rating"] };
        service.Settings.EditorCollapsedSections = ["difficulty/lines"];
        service.Save();

        var settings = BuilderSettingsService.Load(_path).Settings;

        Assert.IsTrue(settings.EditorRightPanelAutoHide);
        CollectionAssert.AreEqual(new[] { "difficulty", "properties" }, settings.EditorRightTabs);
        CollectionAssert.AreEqual(new[] { "graph", "rating" }, settings.EditorSectionOrder!["difficulty"]);
        CollectionAssert.AreEqual(new[] { "difficulty/lines" }, settings.EditorCollapsedSections);
    }
}
