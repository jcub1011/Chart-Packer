using ChartBuilder.Services;

namespace ChartBuilder.Tests.Editing;

/// <summary>How the side panels' tabs and sections are arranged, and kept, by key (GitHub issue #128).</summary>
[TestClass]
public sealed class PanelLayoutStoreTests
{
    [TestMethod]
    public void NothingSavedIsTheDefaultOrder()
    {
        Assert.AreSequenceEqual(["a", "b", "c"], [.. PanelLayoutStore.Arrange(null, ["a", "b", "c"])]);
    }

    [TestMethod]
    public void TheSavedOrderIsKept()
    {
        Assert.AreSequenceEqual(["c", "a", "b"], [.. PanelLayoutStore.Arrange(["c", "a", "b"], ["a", "b", "c"])]);
    }

    [TestMethod]
    public void AKeyNoLongerThereIsDropped()
    {
        Assert.AreSequenceEqual(["c", "a"], [.. PanelLayoutStore.Arrange(["c", "gone", "a"], ["a", "c"])]);
    }

    [TestMethod]
    public void ANewKeyGoesAfterTheOneBeforeItByDefault()
    {
        // "b" is new, and comes after "a" by default, wherever "a" was moved to.
        Assert.AreSequenceEqual(["c", "a", "b"], [.. PanelLayoutStore.Arrange(["c", "a"], ["a", "b", "c"])]);
    }

    [TestMethod]
    public void ANewFirstKeyGoesFirst()
    {
        Assert.AreSequenceEqual(["new", "c", "b"], [.. PanelLayoutStore.Arrange(["c", "b"], ["new", "b", "c"])]);
    }

    [TestMethod]
    public void ARepeatedKeyIsTakenOnce()
    {
        Assert.AreSequenceEqual(["b", "a"], [.. PanelLayoutStore.Arrange(["b", "a", "b"], ["a", "b"])]);
    }

    [TestMethod]
    public void SectionsAreArrangedByTab()
    {
        var store = new PanelLayoutStore();
        store.SetSectionOrder("tempo-map", ["changes", "timing"]);

        Assert.AreSequenceEqual(["changes", "timing", "audio"], [.. store.SectionOrder("tempo-map", ["timing", "audio", "changes"])]);
        Assert.AreSequenceEqual(["parts", "metadata"], [.. store.SectionOrder("chart-settings", ["parts", "metadata"])]);
    }

    [TestMethod]
    public void ASectionCollapsedIsCollapsedInItsTabOnly()
    {
        var store = new PanelLayoutStore();
        store.SetCollapsed("art", "album-art", true);

        Assert.IsTrue(store.IsCollapsed("art", "album-art"));
        Assert.IsFalse(store.IsCollapsed("art", "background-image"));
        Assert.IsFalse(store.IsCollapsed("other", "album-art"));
    }

    [TestMethod]
    public void ChangesAreSaidWithTheirTabOrPanel()
    {
        var store = new PanelLayoutStore();
        var said = new List<string>();
        store.Changed += (_, key) => said.Add(key);

        store.SetCollapsed("art", "album-art", true);
        store.SetCollapsed("art", "album-art", true);
        store.SetSectionOrder("art", ["b", "a"]);
        store.SetSectionOrder("art", ["b", "a"]);
        store.SetTabOrder(PanelLayoutStore.LeftPanel, ["art", "chart-settings"]);

        Assert.AreSequenceEqual(["art", "art", PanelLayoutStore.LeftPanel], said);
    }

    [TestMethod]
    public void ResettingATabsSectionsForgetsTheirOrderAndCollapsing()
    {
        var store = new PanelLayoutStore();
        store.SetSectionOrder("art", ["b", "a"]);
        store.SetCollapsed("art", "a", true);
        store.SetCollapsed("artwork", "a", true);

        store.ResetSections("art");

        Assert.AreSequenceEqual(["a", "b"], [.. store.SectionOrder("art", ["a", "b"])]);
        Assert.IsFalse(store.IsCollapsed("art", "a"));
        Assert.IsTrue(store.IsCollapsed("artwork", "a"));
    }

    [TestMethod]
    public void ResettingAPanelsTabsForgetsTheirOrder()
    {
        var store = new PanelLayoutStore();
        store.SetTabOrder(PanelLayoutStore.RightPanel, ["difficulty", "properties"]);

        store.ResetTabs(PanelLayoutStore.RightPanel);

        Assert.AreSequenceEqual(["properties", "difficulty"], [.. store.TabOrder(PanelLayoutStore.RightPanel, ["properties", "difficulty"])]);
    }

    [TestMethod]
    public void TheLayoutGoesIntoTheSettingsAndBack()
    {
        var store = new PanelLayoutStore();
        store.SetTabOrder(PanelLayoutStore.LeftPanel, ["tempo-map", "chart-settings"]);
        store.SetTabOrder(PanelLayoutStore.RightPanel, ["findings", "properties"]);
        store.SetSectionOrder("tempo-map", ["changes", "timing"]);
        store.SetCollapsed("tempo-map", "audio", true);
        var settings = new BuilderSettings();

        store.WriteTo(settings);
        var back = PanelLayoutStore.From(settings);

        Assert.AreSequenceEqual(["tempo-map", "chart-settings"], [.. back.TabOrder(PanelLayoutStore.LeftPanel, ["chart-settings", "tempo-map"])]);
        Assert.AreSequenceEqual(["findings", "properties"], [.. back.TabOrder(PanelLayoutStore.RightPanel, ["properties", "findings"])]);
        Assert.AreSequenceEqual(["changes", "timing"], [.. back.SectionOrder("tempo-map", ["timing", "changes"])]);
        Assert.IsTrue(back.IsCollapsed("tempo-map", "audio"));
    }
}
