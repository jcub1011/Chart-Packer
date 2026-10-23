using ChartBuilder.Services;

namespace ChartBuilder.Tests.Editing;

/// <summary>How the side panels' tabs and sections are arranged, and kept, by key (GitHub issue #128).</summary>
[TestClass]
public sealed class PanelLayoutStoreTests
{
    [TestMethod]
    public void NothingSavedIsTheDefaultOrder()
    {
        CollectionAssert.AreEqual(new[] { "a", "b", "c" }, PanelLayoutStore.Arrange(null, ["a", "b", "c"]).ToArray());
    }

    [TestMethod]
    public void TheSavedOrderIsKept()
    {
        CollectionAssert.AreEqual(new[] { "c", "a", "b" }, PanelLayoutStore.Arrange(["c", "a", "b"], ["a", "b", "c"]).ToArray());
    }

    [TestMethod]
    public void AKeyNoLongerThereIsDropped()
    {
        CollectionAssert.AreEqual(new[] { "c", "a" }, PanelLayoutStore.Arrange(["c", "gone", "a"], ["a", "c"]).ToArray());
    }

    [TestMethod]
    public void ANewKeyGoesAfterTheOneBeforeItByDefault()
    {
        // "b" is new, and comes after "a" by default, wherever "a" was moved to.
        CollectionAssert.AreEqual(new[] { "c", "a", "b" }, PanelLayoutStore.Arrange(["c", "a"], ["a", "b", "c"]).ToArray());
    }

    [TestMethod]
    public void ANewFirstKeyGoesFirst()
    {
        CollectionAssert.AreEqual(new[] { "new", "c", "b" }, PanelLayoutStore.Arrange(["c", "b"], ["new", "b", "c"]).ToArray());
    }

    [TestMethod]
    public void ARepeatedKeyIsTakenOnce()
    {
        CollectionAssert.AreEqual(new[] { "b", "a" }, PanelLayoutStore.Arrange(["b", "a", "b"], ["a", "b"]).ToArray());
    }

    [TestMethod]
    public void SectionsAreArrangedByTab()
    {
        var store = new PanelLayoutStore();
        store.SetSectionOrder("tempo-map", ["changes", "timing"]);

        CollectionAssert.AreEqual(new[] { "changes", "timing", "audio" }, store.SectionOrder("tempo-map", ["timing", "audio", "changes"]).ToArray());
        CollectionAssert.AreEqual(new[] { "parts", "metadata" }, store.SectionOrder("chart-settings", ["parts", "metadata"]).ToArray());
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

        CollectionAssert.AreEqual(new[] { "art", "art", PanelLayoutStore.LeftPanel }, said);
    }

    [TestMethod]
    public void ResettingATabsSectionsForgetsTheirOrderAndCollapsing()
    {
        var store = new PanelLayoutStore();
        store.SetSectionOrder("art", ["b", "a"]);
        store.SetCollapsed("art", "a", true);
        store.SetCollapsed("artwork", "a", true);

        store.ResetSections("art");

        CollectionAssert.AreEqual(new[] { "a", "b" }, store.SectionOrder("art", ["a", "b"]).ToArray());
        Assert.IsFalse(store.IsCollapsed("art", "a"));
        Assert.IsTrue(store.IsCollapsed("artwork", "a"));
    }

    [TestMethod]
    public void ResettingAPanelsTabsForgetsTheirOrder()
    {
        var store = new PanelLayoutStore();
        store.SetTabOrder(PanelLayoutStore.RightPanel, ["difficulty", "properties"]);

        store.ResetTabs(PanelLayoutStore.RightPanel);

        CollectionAssert.AreEqual(new[] { "properties", "difficulty" }, store.TabOrder(PanelLayoutStore.RightPanel, ["properties", "difficulty"]).ToArray());
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

        CollectionAssert.AreEqual(new[] { "tempo-map", "chart-settings" }, back.TabOrder(PanelLayoutStore.LeftPanel, ["chart-settings", "tempo-map"]).ToArray());
        CollectionAssert.AreEqual(new[] { "findings", "properties" }, back.TabOrder(PanelLayoutStore.RightPanel, ["properties", "findings"]).ToArray());
        CollectionAssert.AreEqual(new[] { "changes", "timing" }, back.SectionOrder("tempo-map", ["timing", "changes"]).ToArray());
        Assert.IsTrue(back.IsCollapsed("tempo-map", "audio"));
    }
}
