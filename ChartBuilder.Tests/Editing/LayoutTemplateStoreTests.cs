using ChartBuilder.Resources;
using ChartBuilder.Services;
using ChartShared.Core.Charting.Layout;
using ChartShared.Core.Charts;

namespace ChartBuilder.Tests.Editing;

/// <summary>
/// The layout templates (GitHub issue #263): named lane layouts, kept in the settings, read from and saved to a temp
/// folder. A template changes only when it's overwritten, never as the lanes it was saved from or loaded into change.
/// </summary>
[TestClass]
public sealed class LayoutTemplateStoreTests
{
    private static readonly LanePart Guitar = LanePart.Highway(ChartInstrument.Guitar, ChartDifficulty.Expert);
    private static readonly LanePart Bass = LanePart.Highway(ChartInstrument.Bass, ChartDifficulty.Expert);
    private static readonly LanePart Drums = LanePart.Highway(ChartInstrument.Drums, ChartDifficulty.Expert);

    private DirectoryInfo _root = null!;

    [TestInitialize]
    public void CreateRoot() => _root = Directory.CreateTempSubdirectory("chartpacker-layouts-");

    [TestCleanup]
    public void DeleteRoot() => _root.Delete(recursive: true);

    private string SettingsPath => Path.Combine(_root.FullName, "builder-settings.json");

    private BuilderSettingsService LoadSettings() => BuilderSettingsService.Load(SettingsPath);

    private static LaneDockState Lanes(params LanePart[] parts)
    {
        var layout = new LaneDockLayout();
        foreach (var part in parts)
            layout.Add(part);
        return layout.ToState();
    }

    private static IEnumerable<LanePart> PartsOf(LaneDockState? state) => LaneDockLayout.FromState(state)!.Lanes.Select(l => l.Part);

    [TestMethod]
    public void SettingsWithoutTemplatesStartWithTheDefault()
    {
        var store = new LayoutTemplateStore(LoadSettings());

        Assert.AreSequenceEqual([Strings.LayoutDefaultName], [.. store.Names]);
        Assert.AreSequenceEqual([Guitar, Drums], [.. PartsOf(store.Get(Strings.LayoutDefaultName))]);
    }

    [TestMethod]
    public void TheDefaultIsNotWrittenUntilSomethingChanges()
    {
        var settings = LoadSettings();
        _ = new LayoutTemplateStore(settings);

        Assert.IsNull(settings.Settings.LayoutTemplates);
    }

    [TestMethod]
    public void DeletingEveryTemplateLeavesNoneAfterARestart()
    {
        var store = new LayoutTemplateStore(LoadSettings());
        Assert.IsTrue(store.Delete(Strings.LayoutDefaultName));

        var reread = new LayoutTemplateStore(LoadSettings());

        Assert.IsEmpty(reread.Names);
    }

    [TestMethod]
    public void ANewTemplateGoesAtTheBottomAndIsKept()
    {
        var store = new LayoutTemplateStore(LoadSettings());
        int changes = 0;
        store.Changed += (_, _) => changes++;

        Assert.IsTrue(store.Add("  Bass only  ", Lanes(Bass)));

        Assert.AreEqual(1, changes);
        Assert.AreSequenceEqual([Strings.LayoutDefaultName, "Bass only"], [.. store.Names]);
        var reread = new LayoutTemplateStore(LoadSettings());
        Assert.AreSequenceEqual([Strings.LayoutDefaultName, "Bass only"], [.. reread.Names]);
        Assert.AreSequenceEqual([Bass], [.. PartsOf(reread.Get("bass ONLY"))]);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("default")]
    [DataRow(" DEFAULT ")]
    public void ABlankOrTakenNameIsRefused(string name)
    {
        var store = new LayoutTemplateStore(LoadSettings());

        Assert.IsNotNull(store.ValidateName(name));
        Assert.IsFalse(store.Add(name, Lanes(Bass)));
        Assert.AreSequenceEqual([Strings.LayoutDefaultName], [.. store.Names]);
    }

    [TestMethod]
    public void ATooLongNameIsRefused()
    {
        var store = new LayoutTemplateStore(LoadSettings());

        Assert.IsNotNull(store.ValidateName(new string('x', LayoutTemplateStore.MaxNameLength + 1)));
        Assert.IsNull(store.ValidateName(new string('x', LayoutTemplateStore.MaxNameLength)));
    }

    [TestMethod]
    public void NoMoreThanTheMostTemplatesCanBeAdded()
    {
        var store = new LayoutTemplateStore(LoadSettings());
        for (int i = store.Names.Count; i < LayoutTemplateStore.MaxTemplates; i++)
            Assert.IsTrue(store.Add($"Layout {i}", Lanes(Bass)));

        Assert.IsTrue(store.IsFull);
        Assert.IsNotNull(store.ValidateName("One more"));
        Assert.IsFalse(store.Add("One more", Lanes(Bass)));
        Assert.IsNull(store.ValidateName("Renamed", except: "Layout 1"), "A rename isn't an addition.");
    }

    [TestMethod]
    public void LanesThatAreNotALayoutAreNotAdded()
    {
        var store = new LayoutTemplateStore(LoadSettings());
        var broken = new LaneDockState { Root = new LaneDockNodeState { Instrument = "Banjo", Difficulty = "Expert" } };

        Assert.IsFalse(store.Add("Broken", broken));
        Assert.IsFalse(store.Contains("Broken"));
    }

    [TestMethod]
    public void ATemplateIsACopyOfTheLanesItWasSavedFrom()
    {
        var store = new LayoutTemplateStore(LoadSettings());
        var layout = LaneDockLayout.FromState(Lanes(Guitar, Bass))!;
        store.Add("Mine", layout.ToState());

        layout.Close(layout.Lanes.First());
        var loaded = store.Get("Mine")!;
        loaded.Root!.Children!.Clear();

        Assert.AreSequenceEqual([Guitar, Bass], [.. PartsOf(store.Get("Mine"))]);
    }

    [TestMethod]
    public void OverwritingPutsTheNewLanesInTheTemplate()
    {
        var store = new LayoutTemplateStore(LoadSettings());

        Assert.IsTrue(store.Overwrite(Strings.LayoutDefaultName, Lanes(Bass)));
        Assert.IsFalse(store.Overwrite("Missing", Lanes(Bass)));

        Assert.AreSequenceEqual([Bass], [.. PartsOf(new LayoutTemplateStore(LoadSettings()).Get(Strings.LayoutDefaultName))]);
    }

    [TestMethod]
    public void ATemplateCanBeRenamedToItsOwnNameInAnotherCase()
    {
        var store = new LayoutTemplateStore(LoadSettings());
        store.Add("Other", Lanes(Bass));

        Assert.IsTrue(store.Rename(Strings.LayoutDefaultName, Strings.LayoutDefaultName.ToUpperInvariant()));
        Assert.IsFalse(store.Rename("Other", Strings.LayoutDefaultName), "Another template's name is taken.");
        Assert.IsTrue(store.Rename("Other", " Renamed "));

        Assert.AreSequenceEqual([Strings.LayoutDefaultName.ToUpperInvariant(), "Renamed"], [.. store.Names]);
    }

    [TestMethod]
    public void MovingStopsAtEitherEnd()
    {
        var store = new LayoutTemplateStore(LoadSettings());
        store.Add("B", Lanes(Bass));
        store.Add("C", Lanes(Drums));
        string a = Strings.LayoutDefaultName;

        Assert.IsTrue(store.Move("C", -1));
        Assert.AreSequenceEqual([a, "C", "B"], [.. store.Names]);
        Assert.IsFalse(store.Move(a, -1));
        Assert.IsTrue(store.Move(a, 10));
        Assert.AreSequenceEqual(["C", "B", a], [.. store.Names]);
        Assert.IsFalse(store.Move("Missing", 1));
    }

    [TestMethod]
    public void DeletingRemovesOnlyThatTemplate()
    {
        var store = new LayoutTemplateStore(LoadSettings());
        store.Add("B", Lanes(Bass));

        Assert.IsTrue(store.Delete("b"));
        Assert.IsFalse(store.Delete("b"));
        Assert.AreSequenceEqual([Strings.LayoutDefaultName], [.. store.Names]);
    }

    [TestMethod]
    public void BadSavedTemplatesAreDropped()
    {
        var saved = new List<LayoutTemplate>
        {
            new() { Name = " Kept ", Lanes = Lanes(Bass) },
            null!,
            new() { Name = "  ", Lanes = Lanes(Bass) },
            new() { Name = "KEPT", Lanes = Lanes(Drums) },
            new() { Name = new string('x', LayoutTemplateStore.MaxNameLength + 1), Lanes = Lanes(Bass) },
            new() { Name = "No lanes" },
            new() { Name = "Broken", Lanes = new LaneDockState { Root = new LaneDockNodeState { Instrument = "Banjo", Difficulty = "Expert" } } },
            new() { Name = "Empty", Lanes = new LaneDockState() },
        };

        var kept = LayoutTemplateStore.Sanitize(saved)!;

        Assert.AreSequenceEqual(["Kept", "Empty"], [.. kept.Select(t => t.Name)]);
        Assert.AreSequenceEqual([Bass], [.. PartsOf(kept[0].Lanes)]);
        Assert.IsNull(LayoutTemplateStore.Sanitize(null));
    }

    [TestMethod]
    public void ASavedTemplateDroppedForItsLanesLeavesItsNameForTheNext()
    {
        var saved = new List<LayoutTemplate>
        {
            new() { Name = "Drums", Lanes = new LaneDockState { Root = new LaneDockNodeState { Instrument = "Banjo", Difficulty = "Expert" } } },
            new() { Name = "drums", Lanes = Lanes(Drums) },
        };

        var kept = LayoutTemplateStore.Sanitize(saved)!;

        Assert.AreSequenceEqual(["drums"], [.. kept.Select(t => t.Name)]);
    }

    [TestMethod]
    public void LanesLoadedFromATemplateAreThatTemplate()
    {
        var store = new LayoutTemplateStore(LoadSettings());
        var layout = new LaneDockLayout();

        Assert.IsTrue(layout.Load(store.Get(Strings.LayoutDefaultName)));
        Assert.IsTrue(store.HasLanes(layout.ToState()));

        layout.Close(layout.Lanes.First());
        Assert.IsFalse(store.HasLanes(layout.ToState()), "A lane closed since.");
        Assert.IsFalse(store.HasLanes(null));
    }

    [TestMethod]
    public void LanesSizedOrViewedDifferentlyAreNotTheTemplate()
    {
        var store = new LayoutTemplateStore(LoadSettings());
        var layout = new LaneDockLayout();
        layout.Load(store.Get(Strings.LayoutDefaultName));

        var row = (DockSplit)layout.Root!;
        layout.SetWeights(row, [1, 3]);
        Assert.IsFalse(store.HasLanes(layout.ToState()), "Resized.");

        layout.SetWeights(row, [1, 1]);
        Assert.IsTrue(store.HasLanes(layout.ToState()), "Sized back.");

        layout.SetLookAhead(layout.Lanes.First(), 3);
        Assert.IsFalse(store.HasLanes(layout.ToState()), "A lane's view changed.");
    }
}
