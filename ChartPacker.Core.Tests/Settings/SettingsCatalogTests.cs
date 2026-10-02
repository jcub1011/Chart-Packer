using ChartPacker.Core.Settings;

namespace ChartPacker.Core.Tests.Settings;

[TestClass]
public sealed class SettingsCatalogTests
{
    private static readonly SettingCategory Library = new("library", "Library");
    private static readonly SettingCategory Scanning = new("library.scanning", "Scanning", Library);
    private static readonly SettingCategory Output = new("output", "Output");
    private static readonly SettingCategory Packing = new("output.packing", "Pack & Unpack", Output);

    private static ToggleSetting Toggle(string id, SettingCategory category, string label, string description = "", string[]? keywords = null)
    {
        bool value = false;
        return new ToggleSetting
        {
            Id = id,
            Category = category,
            Label = label,
            Description = description,
            Keywords = keywords ?? [],
            DefaultValue = false,
            Get = () => value,
            Set = v => value = v,
        };
    }

    private static SettingsCatalog Sample() => new(
        [Library, Scanning, Output, Packing],
        [
            Toggle("depth", Scanning, "Scan depth", "How many folder levels below the picked folder are searched."),
            Toggle("flat", Packing, "Flat pack output", "Writes every .sng directly in the output folder.", ["mirror", "layout"]),
            Toggle("overwrite", Packing, "If the output exists", "Skip, overwrite or rename."),
            Toggle("café", Output, "Café mode", "A setting with an accent."),
        ]);

    private static string[] Ids(IEnumerable<SettingDefinition> definitions) => [.. definitions.Select(d => d.Id)];

    [TestMethod]
    public void CategoryPathRunsFromTheTopLevelDown()
    {
        Assert.AreSequenceEqual(["Output", "Pack & Unpack"], [.. Packing.Path]);
    }

    [TestMethod]
    public void TreeKeepsDeclaredOrder()
    {
        var catalog = Sample();

        Assert.AreSequenceEqual([Library, Output], [.. catalog.RootCategories]);
        Assert.AreSequenceEqual([Packing], [.. catalog.ChildrenOf(Output)]);
        Assert.AreSequenceEqual(["flat", "overwrite"], Ids(catalog.DefinitionsIn(Packing)));
        Assert.IsEmpty(catalog.DefinitionsIn(Library));
    }

    [TestMethod]
    public void DefinitionsAreInTreeOrder()
    {
        // "café" was declared last but sits directly in Output, so it comes before Output's children.
        Assert.AreSequenceEqual(["depth", "café", "flat", "overwrite"], Ids(Sample().Definitions));
    }

    [TestMethod]
    public void DuplicateSettingIdThrows()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new SettingsCatalog(
            [Library],
            [Toggle("same", Library, "One"), Toggle("same", Library, "Two")]));
    }

    [TestMethod]
    public void RequiringASettingThatIsNotListedThrows()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new SettingsCatalog(
            [Library],
            [new ToggleSetting { Id = "lanes", Category = Library, Label = "Lanes", RequiresId = "missing", DefaultValue = false, Get = () => false, Set = _ => { } }]));
    }

    [TestMethod]
    public void RequiringASettingThatIsNotAToggleThrows()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new SettingsCatalog(
            [Library],
            [
                new IntegerSetting { Id = "depth", Category = Library, Label = "Depth", Minimum = 0, Maximum = 5, DefaultValue = 1, Get = () => 1, Set = _ => { } },
                new ToggleSetting { Id = "lanes", Category = Library, Label = "Lanes", RequiresId = "depth", DefaultValue = false, Get = () => false, Set = _ => { } },
            ]));
    }

    [TestMethod]
    public void RequiringItselfThrows()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new SettingsCatalog(
            [Library],
            [new ToggleSetting { Id = "lanes", Category = Library, Label = "Lanes", RequiresId = "lanes", DefaultValue = false, Get = () => false, Set = _ => { } }]));
    }

    [TestMethod]
    public void RequiringItselfThroughAnotherThrows()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new SettingsCatalog(
            [Library],
            [
                new ToggleSetting { Id = "a", Category = Library, Label = "A", RequiresId = "b", DefaultValue = false, Get = () => false, Set = _ => { } },
                new ToggleSetting { Id = "b", Category = Library, Label = "B", RequiresId = "c", DefaultValue = false, Get = () => false, Set = _ => { } },
                new ToggleSetting { Id = "c", Category = Library, Label = "C", RequiresId = "a", DefaultValue = false, Get = () => false, Set = _ => { } },
            ]));
    }

    [TestMethod]
    public void AChainOfRequirementsIsAllowed()
    {
        var catalog = new SettingsCatalog(
            [Library],
            [
                new ToggleSetting { Id = "a", Category = Library, Label = "A", RequiresId = "b", DefaultValue = false, Get = () => false, Set = _ => { } },
                new ToggleSetting { Id = "b", Category = Library, Label = "B", RequiresId = "c", DefaultValue = false, Get = () => false, Set = _ => { } },
                new ToggleSetting { Id = "c", Category = Library, Label = "C", DefaultValue = false, Get = () => false, Set = _ => { } },
            ]);

        Assert.HasCount(3, catalog.Definitions);
    }

    [TestMethod]
    public void DuplicateCategoryIdThrows()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new SettingsCatalog(
            [Library, new SettingCategory("library", "Again")],
            []));
    }

    [TestMethod]
    public void SettingInUnlistedCategoryThrows()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new SettingsCatalog([Library], [Toggle("x", Output, "X")]));
    }

    [TestMethod]
    public void ChildListedBeforeItsParentThrows()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new SettingsCatalog([Scanning, Library], []));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    public void BlankQueryMatchesEverything(string? query)
    {
        var catalog = Sample();

        Assert.AreSequenceEqual(Ids(catalog.Definitions), Ids(catalog.Search(query)));
    }

    [TestMethod]
    [DataRow("DEPTH", "depth")]           // label, ignoring case
    [DataRow("levels", "depth")]          // description
    [DataRow("mirror", "flat")]           // keyword
    [DataRow("scanning", "depth")]        // category
    [DataRow("cafe", "café")]             // ignoring accents
    public void SearchMatchesEachField(string query, string expectedId)
    {
        Assert.AreSequenceEqual([expectedId], Ids(Sample().Search(query)));
    }

    [TestMethod]
    public void SearchMatchesParentCategoryLabels()
    {
        Assert.AreSequenceEqual(["café", "flat", "overwrite"], Ids(Sample().Search("output")));
    }

    [TestMethod]
    public void EveryTermMustMatch()
    {
        var catalog = Sample();

        Assert.AreSequenceEqual(["overwrite"], Ids(catalog.Search("pack  rename")));
        Assert.IsEmpty(catalog.Search("depth rename"));
    }

    [TestMethod]
    public void QueryWithNoMatchesReturnsNothing()
    {
        Assert.IsEmpty(Sample().Search("zzzz"));
    }
}
