using ChartShared.Core.Settings;
using ChartShared.UI.Services;
using ChartShared.UI.ViewModels.Settings;

namespace ChartBuilder.Tests.Settings;

[TestClass]
public sealed class SettingsWindowViewModelTests
{
    private static readonly SettingCategory Playback = new("playback", "Playback");
    private static readonly SettingCategory Audio = new("playback.audio", "Audio", Playback);
    private static readonly SettingCategory Visualizers = new("playback.visualizers", "Visualizers", Playback);
    private static readonly SettingCategory General = new("general", "General");

    private sealed class NoFolderPicker : IFolderPickerService
    {
        public Task<string?> PickFolderAsync(string title, string? startFolder = null) => Task.FromResult<string?>(null);
    }

    private bool _pitch;
    private bool _lines = true;
    private bool _startup;
    private int _saved;

    private static ToggleSetting Toggle(string id, SettingCategory category, string label, bool defaultValue, Func<bool> get, Action<bool> set) =>
        new() { Id = id, Category = category, Label = label, DefaultValue = defaultValue, Get = get, Set = set };

    private SettingsWindowViewModel Open() => new(
        new SettingsCatalog(
            [Playback, Audio, Visualizers, General],
            [
                Toggle("playback.pitch", Audio, "Pitch Correction", false, () => _pitch, v => _pitch = v),
                Toggle("playback.lines", Visualizers, "Beat Lines", true, () => _lines, v => _lines = v),
                Toggle("general.startup", General, "Check For Updates", false, () => _startup, v => _startup = v),
            ]),
        new NoFolderPicker(),
        () => _saved++);

    private static ToggleSettingViewModel Toggle(SettingsWindowViewModel vm, string label) =>
        vm.Groups.SelectMany(g => g.Items).OfType<ToggleSettingViewModel>().Single(t => t.Label == label);

    [TestMethod]
    public void CategoriesAreAFlatListOfTheTopLevelOnesUnderAllSettings()
    {
        var vm = Open();

        CollectionAssert.AreEqual(new[] { "Playback", "General" }, vm.Categories.Skip(1).Select(c => c.Label).ToArray());
        Assert.IsNull(vm.Categories[0].Category);
        Assert.AreSame(vm.Categories[0], vm.SelectedCategory);
        Assert.AreEqual(vm.Categories[0].Label, vm.PageTitle);
        Assert.IsFalse(vm.HasPageSubtitle);
    }

    [TestMethod]
    public void ACategoryNamesItsSectionsInItsPageHeader()
    {
        var vm = Open();

        vm.SelectedCategory = vm.Categories.Single(c => c.Label == "Playback");

        Assert.AreEqual("Playback", vm.PageTitle);
        Assert.AreEqual("Audio, Visualizers", vm.PageSubtitle);
        Assert.IsTrue(vm.HasPageSubtitle);
    }

    [TestMethod]
    public void ASelectedCategoryHeadsItsSectionsByTheirOwnNameAndAllSettingsByTheWholePath()
    {
        var vm = Open();
        CollectionAssert.AreEqual(new[] { "Playback › Audio", "Playback › Visualizers", "General" }, vm.Groups.Select(g => g.Title).ToArray());

        vm.SelectedCategory = vm.Categories.Single(c => c.Label == "Playback");
        CollectionAssert.AreEqual(new[] { "Audio", "Visualizers" }, vm.Groups.Select(g => g.Title).ToArray());

        vm.SelectedCategory = vm.Categories.Single(c => c.Label == "General");
        Assert.AreEqual("", vm.Groups.Single().Title, "the page header already says General");
        Assert.IsFalse(vm.Groups.Single().HasTitle);
    }

    [TestMethod]
    public void CategoriesCountTheirSettingsWithChangesNotYetWritten()
    {
        var vm = Open();
        var playback = vm.Categories.Single(c => c.Label == "Playback");
        Assert.AreEqual(0, playback.ModifiedCount);
        Assert.IsFalse(vm.HasUnsaved);

        Toggle(vm, "Pitch Correction").Value = true;
        Toggle(vm, "Beat Lines").Value = false;
        Toggle(vm, "Check For Updates").Value = true;

        Assert.AreEqual(2, playback.ModifiedCount, "nested categories count toward their top-level one");
        Assert.IsTrue(playback.HasModified);
        Assert.AreEqual(1, vm.Categories.Single(c => c.Label == "General").ModifiedCount);
        Assert.AreEqual(3, vm.Categories[0].ModifiedCount);
        Assert.AreEqual(3, vm.UnsavedCount);
        Assert.AreEqual("3 Unsaved Changes", vm.UnsavedText);
        Assert.IsTrue(vm.HasUnsaved);

        Toggle(vm, "Beat Lines").Value = true;
        Toggle(vm, "Check For Updates").Value = false;
        Assert.AreEqual(1, vm.UnsavedCount);
        Assert.AreEqual("1 Unsaved Change", vm.UnsavedText);
    }

    [TestMethod]
    public void ApplyingWritesTheChangesSavesAndClearsTheCounts()
    {
        var vm = Open();
        Toggle(vm, "Pitch Correction").Value = true;
        Assert.IsFalse(_pitch, "staged until OK or Apply");

        vm.ApplyChanges();

        Assert.IsTrue(_pitch);
        Assert.AreEqual(1, _saved);
        Assert.AreEqual(0, vm.UnsavedCount);
        Assert.AreEqual(0, vm.Categories.Single(c => c.Label == "Playback").ModifiedCount);
        Assert.IsFalse(vm.HasUnsaved);
    }

    [TestMethod]
    public void SearchingNarrowsTheCategoriesCountsMatchesAndPicksAllSettings()
    {
        var vm = Open();
        vm.SelectedCategory = vm.Categories.Single(c => c.Label == "General");

        vm.SearchText = "beat";

        CollectionAssert.AreEqual(new[] { "Playback" }, vm.Categories.Skip(1).Select(c => c.Label).ToArray());
        Assert.IsTrue(vm.SelectedCategory!.Category is null);
        Assert.AreEqual(1, vm.Categories[0].MatchCount);
        Assert.AreEqual(1, vm.Categories[1].MatchCount);
        Assert.AreEqual("Beat Lines", vm.Groups.Single().Items.Single().Label);

        vm.SearchText = "zzzz";
        Assert.IsTrue(vm.HasNoMatches);

        vm.SearchText = "";
        Assert.AreEqual(3, vm.Categories.Count);
        Assert.IsNull(vm.Categories[1].MatchCount);
        Assert.IsFalse(vm.Categories[1].HasMatchCount);
    }

    [TestMethod]
    public void ChangesSurviveASearchAndStillCount()
    {
        var vm = Open();
        Toggle(vm, "Pitch Correction").Value = true;

        vm.SearchText = "check";
        Assert.AreEqual(1, vm.UnsavedCount, "a setting the search hides is still changed");

        vm.SearchText = "";
        Assert.AreEqual(1, vm.Categories.Single(c => c.Label == "Playback").ModifiedCount);
    }

    [TestMethod]
    public void WhileSearchingACategoryNamesOnlyTheSectionsWithAMatch()
    {
        var vm = Open();

        vm.SearchText = "beat";

        var playback = vm.Categories.Single(c => c.Label == "Playback");
        Assert.AreEqual("Visualizers", playback.Subtitle);
        vm.SelectedCategory = playback;
        Assert.AreEqual("Visualizers", vm.PageSubtitle);

        vm.SearchText = "";
        Assert.AreEqual("Audio, Visualizers", vm.Categories.Single(c => c.Label == "Playback").Subtitle);
    }

    [TestMethod]
    public void ACategoryNamesItsIconWithoutTheAppDrawingIt()
    {
        var view = new SettingCategory("view", "View", iconKey: "ViewStroke");
        var vm = new SettingsWindowViewModel(
            new SettingsCatalog([view], [Toggle("view.grid", view, "Grid", false, () => false, _ => { })]),
            new NoFolderPicker(),
            () => { });

        var category = vm.Categories.Single(c => c.Label == "View");

        Assert.AreEqual("ViewStroke", category.IconKey);
        Assert.IsTrue(category.HasIcon);
        Assert.IsNotNull(vm.Categories[0].IconKey, "All Settings has one too");
    }
}
