using Avalonia.Input;
using ChartBuilder.Resources;
using ChartBuilder.Services;
using ChartShared.Core.Keybinds;
using ChartShared.UI.Keybinds;

namespace ChartBuilder.Tests.Keybinds;

/// <summary>Chart Builder's actions and keys (charter-spec §9): the catalog is consistent, and every default key does what it did before keys could be rebound.</summary>
[TestClass]
public sealed class BuilderKeysTests
{
    private static KeybindCatalog Catalog => BuilderKeys.Catalog();

    private static KeyMap DefaultMap() => new(BuilderKeys.Catalog());

    [TestMethod]
    public void EveryActionHasANameAndACategory()
    {
        foreach (var action in Catalog)
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(action.Name), action.Id);
            Assert.IsFalse(string.IsNullOrWhiteSpace(action.Category), action.Id);
        }
    }

    [TestMethod]
    public void AFixedActionHasKeysOrSaysWhatItIs()
    {
        foreach (var action in Catalog.Where(a => a.Locked))
            Assert.IsTrue(action.Defaults.Count > 0 || action.DisplayOverride is not null, action.Id);

        foreach (var action in Catalog.Where(a => a.DisplayOverride is not null))
        {
            Assert.IsTrue(action.Locked, action.Id);
            Assert.AreEqual(0, action.Defaults.Count, action.Id);
        }
    }

    [TestMethod]
    public void NoDefaultKeyClashesWithAnother()
    {
        var conflicts = DefaultMap().AllConflicts();

        Assert.AreEqual(0, conflicts.Count, string.Join("; ", conflicts.Select(c => $"{c.First.Id} and {c.Second.Id} on {c.Chord}")));
    }

    [TestMethod]
    public void EveryDefaultKeyIsOneItsScopeCanReadAndItsActionMayHave()
    {
        foreach (var action in Catalog)
        {
            foreach (var chord in action.Defaults)
            {
                if (action.Scope.Match == KeyMatch.Physical)
                    Assert.IsNotNull(KeyInput.ToPhysicalKey(chord), $"{action.Id}: {chord} isn't a physical key");
                else
                    Assert.IsNotNull(KeyInput.ToKey(chord), $"{action.Id}: {chord} isn't a key");

                Assert.AreEqual(ChordProblem.None, KeyMap.Validate(action, chord), $"{action.Id}: {chord}");

                // AltGr is Ctrl+Alt on many layouts, so such a chord would steal typed characters.
                Assert.AreNotEqual(KeyMods.Ctrl | KeyMods.Alt, chord.Mods & (KeyMods.Ctrl | KeyMods.Alt), $"{action.Id}: {chord}");
            }
        }
    }

    [TestMethod]
    public void TheCanvasKeysAreLiveOnlyInsideTheEditorAndSiblingScopesNeverClash()
    {
        Assert.IsTrue(BuilderKeys.Editor.IsSameOrAncestorOf(BuilderKeys.Canvas));
        Assert.IsTrue(BuilderKeys.Tools.IsSameOrAncestorOf(BuilderKeys.Canvas));
        Assert.IsFalse(BuilderKeys.Canvas.Overlaps(BuilderKeys.Preview));
        Assert.IsFalse(BuilderKeys.Canvas.Overlaps(BuilderKeys.GeneratePhrases));
        Assert.IsFalse(BuilderKeys.Canvas.Overlaps(SharedKeys.ReductionPreview));
    }

    // One row for each key as it was before keys could be rebound: the scope it is pressed in, what the key types (as a
    // logical token), where it is (as a physical one), and the action that does what the old code did.
    private static readonly (string Scope, string Typed, string Where, string Action)[] OldKeys =
    [
        // The window.
        ("editor", "Ctrl+N", "Ctrl+N", BuilderKeys.NewChart),
        ("editor", "Ctrl+O", "Ctrl+O", BuilderKeys.OpenChart),
        ("editor", "Ctrl+OemComma", "Ctrl+Comma", SharedKeys.Settings),
        ("editor", "Ctrl+S", "Ctrl+S", SharedKeys.Save),
        ("editor", "Ctrl+Z", "Ctrl+Z", SharedKeys.Undo),
        ("editor", "Ctrl+Y", "Ctrl+Y", SharedKeys.Redo),
        ("editor", "Ctrl+Shift+Z", "Ctrl+Shift+Z", SharedKeys.Redo),
        ("editor", "Ctrl+G", "Ctrl+G", BuilderKeys.GoToMeasure),
        ("editor", "Ctrl+F", "Ctrl+F", BuilderKeys.Find),
        ("editor", "F3", "F3", BuilderKeys.FindNext),
        ("editor", "Shift+F3", "Shift+F3", BuilderKeys.FindPrevious),
        ("editor", "Space", "Space", SharedKeys.PlayPause),

        // Wherever focus is: the tools, and L.
        ("tools", "Alt+D1", "Alt+Digit1", BuilderKeys.ToolSelect),
        ("tools", "Alt+D2", "Alt+Digit2", BuilderKeys.ToolPlace),
        ("tools", "Alt+D3", "Alt+Digit3", BuilderKeys.ToolErase),
        ("tools", "Alt+D4", "Alt+Digit4", BuilderKeys.ToolPhrase),
        ("tools", "Alt+D5", "Alt+Digit5", BuilderKeys.ToolTempo),
        ("tools", "Alt+D6", "Alt+Digit6", BuilderKeys.ToolSync),
        ("tools", "L", "L", BuilderKeys.LoopTool),

        // The lane canvas, which EditorKeys switched on the physical key.
        ("canvas", "P", "P", BuilderKeys.PlaytestToggle),
        ("canvas", "N", "N", BuilderKeys.PlaytestNext),
        ("canvas", "Shift+N", "Shift+N", BuilderKeys.PlaytestPrevious),
        ("canvas", "R", "R", BuilderKeys.RecordToggle),
        ("canvas", "Up", "ArrowUp", BuilderKeys.CursorUp),
        ("canvas", "Down", "ArrowDown", BuilderKeys.CursorDown),
        ("canvas", "Left", "ArrowLeft", BuilderKeys.CursorLeft),
        ("canvas", "Right", "ArrowRight", BuilderKeys.CursorRight),
        ("canvas", "Shift+Up", "Shift+ArrowUp", BuilderKeys.CursorUp),
        ("canvas", "Shift+Down", "Shift+ArrowDown", BuilderKeys.CursorDown),
        ("canvas", "Shift+Left", "Shift+ArrowLeft", BuilderKeys.CursorLeft),
        ("canvas", "Shift+Right", "Shift+ArrowRight", BuilderKeys.CursorRight),
        ("canvas", "Ctrl+Up", "Ctrl+ArrowUp", BuilderKeys.SectionNext),
        ("canvas", "Ctrl+Down", "Ctrl+ArrowDown", BuilderKeys.SectionPrevious),
        ("canvas", "Ctrl+Shift+Up", "Ctrl+Shift+ArrowUp", BuilderKeys.TempoChangeNext),
        ("canvas", "Ctrl+Shift+Down", "Ctrl+Shift+ArrowDown", BuilderKeys.TempoChangePrevious),
        ("canvas", "Alt+Up", "Alt+ArrowUp", BuilderKeys.SelectionMoveUp),
        ("canvas", "Alt+Down", "Alt+ArrowDown", BuilderKeys.SelectionMoveDown),
        ("canvas", "Alt+Left", "Alt+ArrowLeft", BuilderKeys.SelectionMoveLeft),
        ("canvas", "Alt+Right", "Alt+ArrowRight", BuilderKeys.SelectionMoveRight),
        ("canvas", "Alt+Shift+Up", "Alt+Shift+ArrowUp", BuilderKeys.SelectionShiftUp),
        ("canvas", "Alt+Shift+Down", "Alt+Shift+ArrowDown", BuilderKeys.SelectionShiftDown),
        ("canvas", "Alt+Shift+Left", "Alt+Shift+ArrowLeft", BuilderKeys.SelectionShiftLeft),
        ("canvas", "Alt+Shift+Right", "Alt+Shift+ArrowRight", BuilderKeys.SelectionShiftRight),
        ("canvas", "PageUp", "PageUp", BuilderKeys.MeasureNext),
        ("canvas", "PageDown", "PageDown", BuilderKeys.MeasurePrevious),
        ("canvas", "Home", "Home", BuilderKeys.GoToStart),
        ("canvas", "End", "End", BuilderKeys.GoToEnd),
        ("canvas", "OemOpenBrackets", "BracketLeft", BuilderKeys.SnapCoarser),
        ("canvas", "OemCloseBrackets", "BracketRight", BuilderKeys.SnapFiner),
        ("canvas", "OemPlus", "Equal", BuilderKeys.LookAheadLess),
        ("canvas", "Shift+OemPlus", "Shift+Equal", BuilderKeys.LookAheadLess),
        ("canvas", "Add", "NumPadAdd", BuilderKeys.LookAheadLess),
        ("canvas", "Shift+Add", "Shift+NumPadAdd", BuilderKeys.LookAheadLess),
        ("canvas", "OemMinus", "Minus", BuilderKeys.LookAheadMore),
        ("canvas", "Subtract", "NumPadSubtract", BuilderKeys.LookAheadMore),
        ("canvas", "W", "W", BuilderKeys.AudioBackdrop),
        ("canvas", "O", "O", BuilderKeys.SnapToOnset),
        ("canvas", "Shift+O", "Shift+O", BuilderKeys.NudgeToOnsets),
        ("canvas", "V", "V", BuilderKeys.ToggleView),
        ("canvas", "H", "H", BuilderKeys.NoteForceHopo),
        ("canvas", "G", "G", BuilderKeys.NoteForceStrum),
        ("canvas", "T", "T", BuilderKeys.NoteTap),
        ("canvas", "C", "C", BuilderKeys.NoteCymbal),
        ("canvas", "A", "A", BuilderKeys.NoteAccent),
        ("canvas", "Shift+A", "Shift+A", BuilderKeys.NoteGhost),
        ("canvas", "S", "S", BuilderKeys.NoteStarPower),
        ("canvas", "B", "B", BuilderKeys.TapTempo),
        ("canvas", "D0", "Digit0", BuilderKeys.NoteOpen),
        ("canvas", "NumPad0", "NumPad0", BuilderKeys.NoteOpen),
        ("canvas", "Shift+D0", "Shift+Digit0", BuilderKeys.NoteDoubleKick),
        ("canvas", "Shift+NumPad0", "Shift+NumPad0", BuilderKeys.NoteDoubleKick),
        ("canvas", "D1", "Digit1", BuilderKeys.NoteLane1),
        ("canvas", "D2", "Digit2", BuilderKeys.NoteLane2),
        ("canvas", "D3", "Digit3", BuilderKeys.NoteLane3),
        ("canvas", "D4", "Digit4", BuilderKeys.NoteLane4),
        ("canvas", "D5", "Digit5", BuilderKeys.NoteLane5),
        ("canvas", "D6", "Digit6", BuilderKeys.NoteLane6),
        ("canvas", "NumPad1", "NumPad1", BuilderKeys.NoteLane1),
        ("canvas", "NumPad2", "NumPad2", BuilderKeys.NoteLane2),
        ("canvas", "NumPad3", "NumPad3", BuilderKeys.NoteLane3),
        ("canvas", "NumPad4", "NumPad4", BuilderKeys.NoteLane4),
        ("canvas", "NumPad5", "NumPad5", BuilderKeys.NoteLane5),
        ("canvas", "NumPad6", "NumPad6", BuilderKeys.NoteLane6),
        ("canvas", "Ctrl+D1", "Ctrl+Digit1", BuilderKeys.DifficultyEasy),
        ("canvas", "Ctrl+D2", "Ctrl+Digit2", BuilderKeys.DifficultyMedium),
        ("canvas", "Ctrl+D3", "Ctrl+Digit3", BuilderKeys.DifficultyHard),
        ("canvas", "Ctrl+D4", "Ctrl+Digit4", BuilderKeys.DifficultyExpert),
        ("canvas", "Ctrl+A", "Ctrl+A", BuilderKeys.SelectAll),
        ("canvas", "Ctrl+Shift+A", "Ctrl+Shift+A", BuilderKeys.SelectSection),
        ("canvas", "Ctrl+Q", "Ctrl+Q", BuilderKeys.Quantize),
        ("canvas", "Ctrl+X", "Ctrl+X", BuilderKeys.Cut),
        ("canvas", "Ctrl+C", "Ctrl+C", BuilderKeys.Copy),
        ("canvas", "Ctrl+V", "Ctrl+V", BuilderKeys.Paste),
        ("canvas", "Ctrl+Shift+V", "Ctrl+Shift+V", BuilderKeys.PasteMerge),
        ("canvas", "Delete", "Delete", BuilderKeys.Delete),
        ("canvas", "Back", "Backspace", BuilderKeys.Delete),
        ("canvas", "Escape", "Escape", BuilderKeys.Cancel),

        // The other windows.
        ("preview", "F11", "F11", BuilderKeys.PreviewFullScreen),
        ("preview", "Escape", "Escape", BuilderKeys.PreviewExitFullScreen),
        ("preview", "Space", "Space", SharedKeys.PlayPause),
        ("generate", "OemOpenBrackets", "BracketLeft", BuilderKeys.PreviousRegion),
        ("generate", "OemCloseBrackets", "BracketRight", BuilderKeys.NextRegion),
        ("generate", "Ctrl+Enter", "Ctrl+Enter", BuilderKeys.ApplyPhrases),
        ("generate", "Space", "Space", SharedKeys.PlayPause),
        ("difficulty", "Space", "Space", SharedKeys.PlayPause),
    ];

    private static KeyScope ScopeOf(string name) => name switch
    {
        "editor" => BuilderKeys.Editor,
        "tools" => BuilderKeys.Tools,
        "canvas" => BuilderKeys.Canvas,
        "preview" => BuilderKeys.Preview,
        "generate" => BuilderKeys.GeneratePhrases,
        _ => KeyScope.Global,
    };

    [TestMethod]
    public void EveryKeyThatDidSomethingBeforeStillDoesIt()
    {
        var map = DefaultMap();

        foreach (var (scope, typed, where, expected) in OldKeys)
        {
            var hits = map.Resolve(ScopeOf(scope), KeyChord.Parse(typed), KeyChord.Parse(where));

            Assert.IsTrue(hits.Count > 0, $"{where} in {scope} does nothing now");
            Assert.AreEqual(expected, hits[0].Id, $"{where} in {scope}");
        }
    }

    // The table above writes a pressed key as a binding is written. What a window gets is the token of an Avalonia Key, which
    // is Key.ToString(), and that isn't always the same for a key with two names (Key.Enter is "Return", Key.OemOpenBrackets
    // is "Oem4"), so a default is pressed here the way the keyboard does it.
    [TestMethod]
    public void EveryLogicalDefaultKeyIsAnsweredByTheKeyAsAvaloniaNamesIt()
    {
        var map = DefaultMap();

        foreach (var action in Catalog.Where(a => a.Scope.Match == KeyMatch.Logical))
        {
            foreach (var chord in action.Defaults)
            {
                var key = KeyInput.ToKey(chord);
                Assert.IsNotNull(key, $"{action.Id}: {chord}");

                var pressed = new KeyChord(key.Value.ToString(), chord.Mods);
                var hits = map.Resolve(action.Scope, pressed, default);

                Assert.IsTrue(hits.Any(h => h.Id == action.Id), $"{action.Id}: {chord} isn't answered when pressed as {pressed.Key}");
            }
        }
    }

    [TestMethod]
    public void TheGeneratePhrasesKeysAnswerTheKeysThatArePressed()
    {
        var map = DefaultMap();

        Assert.AreEqual(BuilderKeys.PreviousRegion, map.Resolve(BuilderKeys.GeneratePhrases, new KeyChord(Key.OemOpenBrackets.ToString(), KeyMods.None), default).Single().Id);
        Assert.AreEqual(BuilderKeys.NextRegion, map.Resolve(BuilderKeys.GeneratePhrases, new KeyChord(Key.OemCloseBrackets.ToString(), KeyMods.None), default).Single().Id);
        Assert.AreEqual(BuilderKeys.ApplyPhrases, map.Resolve(BuilderKeys.GeneratePhrases, new KeyChord(Key.Enter.ToString(), KeyMods.Ctrl), default).Single().Id);
    }

    [TestMethod]
    public void EveryDefaultKeyOfTheCanvasIsInTheTable()
    {
        var table = OldKeys.Where(k => k.Scope == "canvas").Select(k => (k.Action, Chord: KeyChord.Parse(k.Where))).ToHashSet();

        foreach (var action in Catalog.InScope(BuilderKeys.Canvas))
        {
            foreach (var chord in action.Defaults)
                Assert.IsTrue(table.Contains((action.Id, chord)), $"{action.Id} has {chord}, which the table of old keys doesn't");
        }
    }

    [TestMethod]
    public void TheKeysTheCanvasTakesAreLockedAsTheEditMenuShowsThem()
    {
        var catalog = Catalog;
        foreach (string id in new[] { BuilderKeys.Cut, BuilderKeys.Copy, BuilderKeys.Paste, BuilderKeys.SelectAll, BuilderKeys.Delete, BuilderKeys.Cancel })
            Assert.IsTrue(catalog[id].Locked, id);

        foreach (string id in new[] { BuilderKeys.PasteMerge, BuilderKeys.Quantize, BuilderKeys.SelectSection })
            Assert.IsFalse(catalog[id].Locked, id);
    }

    [TestMethod]
    public void OnlyTheKeysOfPlaybackPlaytestAndCancelAreReservedDuringAPlaytest()
    {
        var map = DefaultMap();

        var reserved = Enum.GetValues<PhysicalKey>()
            .Where(k => map.IsReservedDuringPlaytest(new KeyChord(k.ToString(), KeyMods.None)))
            .ToHashSet();

        CollectionAssert.AreEquivalent(new[] { PhysicalKey.Space, PhysicalKey.Escape, PhysicalKey.P, PhysicalKey.N }, reserved.ToArray());
    }

    [TestMethod]
    public void ModifiedKeysAreNeverReservedDuringAPlaytest()
    {
        var map = DefaultMap();

        Assert.IsFalse(map.IsReservedDuringPlaytest(KeyChord.Parse("Shift+N")));
        Assert.IsFalse(map.IsReservedDuringPlaytest(KeyChord.Parse("Ctrl+P")));
    }

    [TestMethod]
    public void RebindingThePlaytestKeyMovesTheReservation()
    {
        var map = DefaultMap();

        Assert.IsTrue(map.Set(BuilderKeys.PlaytestToggle, KeyChord.List("Q")));

        Assert.IsFalse(map.IsReservedDuringPlaytest(KeyChord.Parse("P")), "P is free for an instrument now");
        Assert.IsTrue(map.IsReservedDuringPlaytest(KeyChord.Parse("Q")));
        Assert.IsTrue(map.IsReservedDuringPlaytest(KeyChord.Parse("Space")));
    }

    // Keys named in a resource string drift from the real ones once they can be rebound, so the strings of actions with keys
    // leave them to the key map. What's named here are the keys that are fixed on purpose: Esc closes a flyout or stops a
    // bind, which no action does, and a wheel or a click is no key.
    private static readonly string[] KeysNamedOnPurpose =
    [
        "NavCloseToolTip",
        "InputBindPromptFormat",
        "ExportSngPolicyRename", // a file name, "Name (2).sng"
    ];

    [TestMethod]
    public void NoStringStillNamesTheKeyOfAnAction()
    {
        var staleKey = new System.Text.RegularExpressions.Regex(
            @"\((?:(?:Ctrl|Alt|Shift|Cmd)\+[^)]*|Space|Esc|Del|F\d{1,2}|[A-Z0-9\[\]=\-+])\)",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        var set = Strings.ResourceManager.GetResourceSet(System.Globalization.CultureInfo.InvariantCulture, createIfNotExists: true, tryParents: false)!;
        var stale = new List<string>();
        foreach (System.Collections.DictionaryEntry entry in set)
        {
            if (entry.Value is not string text || KeysNamedOnPurpose.Contains((string)entry.Key))
                continue;
            if (staleKey.Match(text) is { Success: true } hit)
                stale.Add($"{entry.Key}: {hit.Value}");
        }

        Assert.AreEqual(0, stale.Count, "Strings that still name a key: " + string.Join("; ", stale));
    }
}
