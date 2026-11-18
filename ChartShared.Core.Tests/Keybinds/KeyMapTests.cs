using ChartShared.Core.Keybinds;

namespace ChartShared.Core.Tests.Keybinds;

[TestClass]
public sealed class KeyMapTests
{
    private static readonly KeyScope Editor = new("test.editor");
    private static readonly KeyScope Canvas = new("test.canvas", Editor, KeyMatch.Physical);
    private static readonly KeyScope Preview = new("test.preview");

    private static KeyAction Action(string id, KeyScope scope, string defaults, bool locked = false, bool bare = false) =>
        new()
        {
            Id = id,
            Name = id,
            Category = "Test",
            Scope = scope,
            Defaults = defaults.Length == 0 ? [] : KeyChord.List(defaults.Split(',')),
            Locked = locked,
            AllowBare = bare,
        };

    private static KeyMap Map() => new(new KeybindCatalog(
    [
        Action("file.save", KeyScope.Global, "Ctrl+S"),
        Action("edit.redo", Editor, "Ctrl+Y,Ctrl+Shift+Z", locked: true),
        Action("tool.select", Canvas, "Alt+Digit1,Alt+KeyQ"),
        Action("note.hopo", Canvas, "KeyH", bare: true),
        Action("preview.play", Preview, "Space", bare: true),
        Action("editor.play", Editor, "Space", bare: true),
    ]));

    [TestMethod]
    public void AnActionStartsWithItsDefaultsInOrder()
    {
        var map = Map();

        Assert.AreSequenceEqual([.. KeyChord.List("Ctrl+Y", "Ctrl+Shift+Z")], [.. map.ChordsOf("edit.redo")]);
        Assert.AreEqual("Ctrl+Y", map.Display("edit.redo"));
        Assert.IsFalse(map.IsCustomized("edit.redo"));
    }

    [TestMethod]
    public void AnActionCanHaveSeveralKeysAndTheFirstIsShown()
    {
        var map = Map();

        Assert.IsTrue(map.Set("tool.select", KeyChord.List("Alt+KeyQ", "Alt+Digit1", "Ctrl+Alt+KeyW")));

        Assert.HasCount(3, map.ChordsOf("tool.select"));
        Assert.AreEqual("Alt+Q", map["tool.select"]);
    }

    [TestMethod]
    public void ALockedActionCantBeChanged()
    {
        var map = Map();

        Assert.IsFalse(map.Set("edit.redo", KeyChord.List("Ctrl+R")));

        Assert.HasCount(2, map.ChordsOf("edit.redo"));
    }

    [TestMethod]
    public void SettingTheDefaultsAgainIsNotACustomisation()
    {
        var map = Map();
        map.Set("file.save", KeyChord.List("Ctrl+W"));

        map.Set("file.save", KeyChord.List("Ctrl+S"));

        Assert.IsFalse(map.IsCustomized("file.save"));
    }

    [TestMethod]
    public void NoKeysUnbindsTheActionAndResetRestoresIt()
    {
        var map = Map();

        map.Set("file.save", []);
        Assert.AreEqual("", map.Display("file.save"));
        Assert.IsTrue(map.IsCustomized("file.save"));

        map.Reset("file.save");
        Assert.AreEqual("Ctrl+S", map.Display("file.save"));
    }

    [TestMethod]
    public void UnusableChordsAreDroppedWhenSet()
    {
        var map = Map();

        // No modifier on a window command, an OS chord, a repeat, and a modifier on its own.
        map.Set("file.save", [KeyChord.Parse("F5"), KeyChord.Parse("Alt+F4"), KeyChord.Parse("Ctrl+W"), KeyChord.Parse("Ctrl+w"), default]);

        Assert.AreSequenceEqual([.. KeyChord.List("Ctrl+W")], [.. map.ChordsOf("file.save")]);
    }

    [TestMethod]
    public void NoMoreThanTheLimitAreKept()
    {
        var map = Map();

        map.Set("tool.select", Enumerable.Range(1, 20).Select(i => KeyChord.Parse($"Ctrl+Alt+D{i % 10}Z{i}")));

        Assert.HasCount(KeyMap.MaxChordsPerAction, map.ChordsOf("tool.select"));
    }

    [TestMethod]
    public void ValidationSaysWhatIsWrong()
    {
        var map = Map();

        Assert.AreEqual(ChordProblem.None, KeyMap.Validate(map.Catalog["file.save"], KeyChord.Parse("Ctrl+W")));
        Assert.AreEqual(ChordProblem.NeedsModifier, KeyMap.Validate(map.Catalog["file.save"], KeyChord.Parse("W")));
        Assert.AreEqual(ChordProblem.None, KeyMap.Validate(map.Catalog["note.hopo"], KeyChord.Parse("KeyW")));
        Assert.AreEqual(ChordProblem.OsReserved, KeyMap.Validate(map.Catalog["file.save"], KeyChord.Parse("Alt+F4")));
        Assert.AreEqual(ChordProblem.ModifierOnly, KeyMap.Validate(map.Catalog["file.save"], default));
    }

    [TestMethod]
    public void AChangeRaisesTheIndexerNotification()
    {
        var map = Map();
        var raised = new List<string?>();
        map.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        map.Set("file.save", KeyChord.List("Ctrl+W"));

        CollectionAssert.Contains(raised, "Item[]");
    }

    [TestMethod]
    public void ResolveGoesInnermostScopeFirst()
    {
        var map = Map();

        var space = KeyChord.Parse("Space");
        var inCanvas = map.Resolve(Canvas, space, KeyChord.Parse("Space"));
        var inPreview = map.Resolve(Preview, space, KeyChord.Parse("Space"));

        Assert.AreSequenceEqual(["editor.play"], [.. inCanvas.Select(a => a.Id)]);
        Assert.AreSequenceEqual(["preview.play"], [.. inPreview.Select(a => a.Id)]);
    }

    [TestMethod]
    public void ACanvasKeyMatchesThePhysicalKeyAndAWindowKeyTheLogicalOne()
    {
        var map = Map();

        var physicalH = map.Resolve(Canvas, KeyChord.Parse("X"), KeyChord.Parse("KeyH"));
        var logicalSave = map.Resolve(Canvas, KeyChord.Parse("Ctrl+S"), KeyChord.Parse("Ctrl+KeyX"));
        var wrongKind = map.Resolve(Canvas, KeyChord.Parse("KeyH"), KeyChord.Parse("X"));

        Assert.AreSequenceEqual(["note.hopo"], [.. physicalH.Select(a => a.Id)]);
        Assert.AreSequenceEqual(["file.save"], [.. logicalSave.Select(a => a.Id)]);
        Assert.IsEmpty(wrongKind);
    }

    [TestMethod]
    public void AnActionOutsideTheActiveScopesDoesNotAnswer()
    {
        var map = Map();

        var hits = map.Resolve(Preview, KeyChord.Parse("Ctrl+Y"), KeyChord.Parse("Ctrl+KeyY"));

        Assert.IsEmpty(hits);
    }

    [TestMethod]
    public void AnActionAnswersToEachOfItsKeys()
    {
        var map = Map();

        foreach (string text in new[] { "Ctrl+Y", "Ctrl+Shift+Z" })
        {
            var hits = map.Resolve(Editor, KeyChord.Parse(text), default);
            Assert.AreSequenceEqual(["edit.redo"], [.. hits.Select(a => a.Id)], text);
        }
    }

    [TestMethod]
    public void OverlappingScopesConflictAndSiblingsDoNot()
    {
        var map = Map();

        // Space is bound in the editor, an ancestor of the canvas: a canvas action taking it would clash.
        Assert.HasCount(1, map.FindConflicts("note.hopo", KeyChord.Parse("Space")));
        // The preview window is a sibling of the editor, so its Space is no clash with the editor's.
        Assert.IsEmpty(map.FindConflicts("preview.play", KeyChord.Parse("Space")));
        Assert.AreEqual(0, map.AllConflicts().Count(c => c.First.Id == "preview.play" || c.Second.Id == "preview.play"));
    }

    [TestMethod]
    public void AConflictAcrossLogicalAndPhysicalScopesIsFound()
    {
        var map = Map();

        // Global Ctrl+S (logical) against a canvas action given Ctrl+KeyS (physical).
        var clash = map.FindConflicts("tool.select", KeyChord.Parse("Ctrl+KeyS"));

        Assert.AreSequenceEqual(["file.save"], [.. clash.Select(a => a.Id)]);
    }

    [TestMethod]
    public void ALockedActionIsReportedAsAConflict()
    {
        var map = Map();

        var clash = map.FindConflicts("file.save", KeyChord.Parse("Ctrl+Y"));

        Assert.IsTrue(clash.Single().Locked);
    }

    [TestMethod]
    public void KeepingBothIsAllowedAndBothResolve()
    {
        var map = Map();
        map.Set("note.hopo", KeyChord.List("Space"));

        var hits = map.Resolve(Canvas, KeyChord.Parse("Space"), KeyChord.Parse("Space"));

        Assert.AreSequenceEqual(["note.hopo", "editor.play"], [.. hits.Select(a => a.Id)]);
        Assert.HasCount(1, map.AllConflicts());
    }

    [TestMethod]
    public void ReservedPlaytestKeysFollowTheActionsThatReserveThem()
    {
        var catalog = new KeybindCatalog(
        [
            new KeyAction
            {
                Id = "play", Name = "Play", Category = "Test", Scope = Editor, AllowBare = true, ReservedDuringPlaytest = true,
                Defaults = KeyChord.List("Space"),
            },
        ]);
        var map = new KeyMap(catalog);

        Assert.IsTrue(map.IsReservedDuringPlaytest(KeyChord.Parse("Space")));
        Assert.IsFalse(map.IsReservedDuringPlaytest(KeyChord.Parse("Ctrl+Space")));

        map.Set("play", KeyChord.List("KeyP"));

        Assert.IsFalse(map.IsReservedDuringPlaytest(KeyChord.Parse("Space")));
        Assert.IsTrue(map.IsReservedDuringPlaytest(KeyChord.Parse("P")));
    }

    [TestMethod]
    public void ReservedPlaytestKeysAreRemadeWhenTheKeysChangeInAnyWay()
    {
        var catalog = new KeybindCatalog(
        [
            new KeyAction
            {
                Id = "play", Name = "Play", Category = "Test", Scope = Editor, AllowBare = true, ReservedDuringPlaytest = true,
                Defaults = KeyChord.List("Space"),
            },
        ]);
        var map = new KeyMap(catalog);
        Assert.IsTrue(map.IsReservedDuringPlaytest(KeyChord.Parse("Space")));

        map.Set("play", KeyChord.List("KeyP"));
        Assert.IsTrue(map.IsReservedDuringPlaytest(KeyChord.Parse("P")));

        map.Reset("play");
        Assert.IsTrue(map.IsReservedDuringPlaytest(KeyChord.Parse("Space")));
        Assert.IsFalse(map.IsReservedDuringPlaytest(KeyChord.Parse("P")));

        var edited = new KeyMap(catalog);
        edited.Set("play", KeyChord.List("KeyN"));
        map.CopyFrom(edited);
        Assert.IsTrue(map.IsReservedDuringPlaytest(KeyChord.Parse("N")));
        Assert.IsFalse(map.IsReservedDuringPlaytest(KeyChord.Parse("Space")));

        map.ApplyLayout(new Dictionary<string, string[]> { ["play"] = ["KeyM"] }, replace: false);
        Assert.IsTrue(map.IsReservedDuringPlaytest(KeyChord.Parse("M")));
        Assert.IsFalse(map.IsReservedDuringPlaytest(KeyChord.Parse("N")));

        map.ResetAll();
        Assert.IsTrue(map.IsReservedDuringPlaytest(KeyChord.Parse("Space")));
    }

    [TestMethod]
    public void ALayoutIsAppliedWithBadEntriesIgnored()
    {
        var map = Map();
        var layout = new Dictionary<string, string[]>
        {
            ["file.save"] = ["Ctrl+W"],
            ["edit.redo"] = ["Ctrl+R"],
            ["no.such.action"] = ["Ctrl+Q"],
            ["tool.select"] = ["not a chord", "Meta+Q"],
            ["note.hopo"] = [],
        };

        var applied = map.ApplyLayout(layout, replace: false);

        Assert.AreEqual(2, applied.Applied);
        Assert.AreEqual(3, applied.Ignored);
        Assert.AreEqual("Ctrl+W", map.Display("file.save"));
        Assert.HasCount(2, map.ChordsOf("edit.redo"));
        Assert.HasCount(2, map.ChordsOf("tool.select"));
        Assert.IsEmpty(map.ChordsOf("note.hopo"));
    }

    [TestMethod]
    public void ALayoutCantGiveAnActionAKeyALockedActionHas()
    {
        var map = Map();
        var layout = new Dictionary<string, string[]>
        {
            ["file.save"] = ["Ctrl+Z", "Ctrl+Y"],
            ["tool.select"] = ["Ctrl+Shift+Z", "Alt+KeyZ"],
            ["note.hopo"] = ["Ctrl+Y"],
        };

        var applied = map.ApplyLayout(layout, replace: false);

        Assert.AreEqual(2, applied.Applied, "the entry left with no usable key is ignored");
        Assert.AreEqual(1, applied.Ignored);
        Assert.AreSequenceEqual([.. KeyChord.List("Ctrl+Z")], [.. map.ChordsOf("file.save")]);
        Assert.AreSequenceEqual([.. KeyChord.List("Alt+KeyZ")], [.. map.ChordsOf("tool.select")]);
        Assert.AreSequenceEqual([.. KeyChord.List("KeyH")], [.. map.ChordsOf("note.hopo")]);
        var hits = map.Resolve(Editor, KeyChord.Parse("Ctrl+Y"), KeyChord.Parse("Ctrl+Y"));
        Assert.AreSequenceEqual(["edit.redo"], [.. hits.Select(a => a.Id)]);
    }

    [TestMethod]
    public void SettingAKeyALockedActionHasIsRefusedToo()
    {
        var map = Map();

        map.Set("file.save", KeyChord.List("Ctrl+Y", "Ctrl+W"));

        Assert.AreSequenceEqual([.. KeyChord.List("Ctrl+W")], [.. map.ChordsOf("file.save")]);
    }

    [TestMethod]
    public void ReplacingALayoutPutsTheRestBackToDefaults()
    {
        var map = Map();
        map.Set("file.save", KeyChord.List("Ctrl+W"));

        map.ApplyLayout(new Dictionary<string, string[]> { ["tool.select"] = ["Alt+KeyZ"] }, replace: true);

        Assert.IsFalse(map.IsCustomized("file.save"));
        Assert.AreEqual("Alt+Z", map.Display("tool.select"));
    }

    [TestMethod]
    public void AnExportedLayoutAppliedElsewhereGivesTheSameKeys()
    {
        var map = Map();
        map.Set("file.save", KeyChord.List("Ctrl+W", "Ctrl+Alt+KeyE"));
        map.Set("note.hopo", []);

        var copy = Map();
        copy.ApplyLayout(map.Export(), replace: true);

        foreach (var action in map.Catalog)
            Assert.AreSequenceEqual([.. map.ChordsOf(action.Id)], [.. copy.ChordsOf(action.Id)], action.Id);
        Assert.IsFalse(map.Export().ContainsKey("edit.redo"), "locked actions are not part of a layout");
    }

    [TestMethod]
    public void AnEditedCloneLeavesTheOriginalUntilItIsCopiedBack()
    {
        var map = Map();
        var edit = map.Clone();
        bool raised = false;
        map.Changed += (_, _) => raised = true;

        edit.Set("file.save", KeyChord.List("Ctrl+W"));
        Assert.AreEqual("Ctrl+S", map.Display("file.save"));
        Assert.IsFalse(raised);

        map.CopyFrom(edit);
        Assert.AreEqual("Ctrl+W", map.Display("file.save"));
        Assert.IsTrue(raised);
    }

    [TestMethod]
    public void ADisplayOverrideStandsInForAKey()
    {
        var map = new KeyMap(new KeybindCatalog(
        [
            new KeyAction { Id = "mouse.select", Name = "Add", Category = "Mouse", Scope = Canvas, Locked = true, DisplayOverride = "Shift+Click" },
        ]));

        Assert.AreEqual("Shift+Click", map["mouse.select"]);
        Assert.IsEmpty(map.Resolve(Canvas, KeyChord.Parse("Shift+Click"), KeyChord.Parse("Shift+Click")));
    }

    [TestMethod]
    public void AnUnknownActionHasNoLabelAndNoKeys()
    {
        var map = Map();

        Assert.AreEqual("", map["nothing"]);
        Assert.IsEmpty(map.ChordsOf("nothing"));
    }

    [TestMethod]
    public void ACatalogRefusesADuplicateId()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new KeybindCatalog(
            [Action("a", KeyScope.Global, "Ctrl+A"), Action("a", KeyScope.Global, "Ctrl+B")]));
    }
}
