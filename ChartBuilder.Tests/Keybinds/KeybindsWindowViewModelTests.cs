using ChartShared.Core.Keybinds;
using ChartShared.UI.Keybinds;
using ChartShared.UI.Services;
using ChartShared.UI.ViewModels.Keybinds;

namespace ChartBuilder.Tests.Keybinds;

[TestClass]
public sealed class KeybindsWindowViewModelTests
{
    private sealed class FakeConfirmation : IConfirmationService
    {
        public ConfirmChoice Choice { get; set; } = ConfirmChoice.Cancel;

        public bool Confirms { get; set; } = true;

        public List<string> Asked { get; } = [];

        public List<string> Alerts { get; } = [];

        public Task<bool> ConfirmAsync(string title, string message, string confirmLabel, string? confirmIcon = null)
        {
            Asked.Add(message);
            return Task.FromResult(Confirms);
        }

        public Task<ConfirmChoice> ChooseAsync(string title, string message, string firstLabel, string secondLabel, string? firstIcon = null, string? secondIcon = null)
        {
            Asked.Add(message);
            return Task.FromResult(Choice);
        }

        public Task AlertAsync(string title, string message)
        {
            Alerts.Add(message);
            return Task.CompletedTask;
        }
    }

    private const string Editor = "Editor";
    private const string Notes = Editor + KeyAction.CategorySeparator + "Notes";

    private static readonly KeyScope Window = new("test.window");
    private static readonly KeyScope Canvas = new("test.canvas", Window, KeyMatch.Physical);

    private DirectoryInfo _root = null!;
    private FakeConfirmation _confirmation = null!;
    private KeyMap _live = null!;
    private int _saved;
    private bool _saveSucceeds;
    private string? _importPath;
    private string? _exportPath;

    [TestInitialize]
    public void Create()
    {
        _root = Directory.CreateTempSubdirectory("chartpacker-keybinds-window-");
        _confirmation = new FakeConfirmation();
        _saved = 0;
        _saveSucceeds = true;
        _importPath = null;
        _exportPath = null;
        _live = new KeyMap(new KeybindCatalog(
        [
            // Without the Settings window's, whose category would only add a row to every count below.
            .. SharedKeys.Actions().Where(a => a.Id != SharedKeys.FocusSearch),
            new KeyAction { Id = "test.find", Name = "Find", Category = "Search", Scope = Window, Defaults = KeyChord.List("Ctrl+F") },
            new KeyAction { Id = "test.hopo", Name = "Force HOPO", Category = Notes, Scope = Canvas, AllowBare = true, Defaults = KeyChord.List("KeyH") },
            new KeyAction { Id = "test.mouse", Name = "Add to selection", Category = Notes, Scope = Canvas, Locked = true, DisplayOverride = "Shift+Click" },
            new KeyAction { Id = "test.snap", Name = "Coarser snap", Category = Editor + KeyAction.CategorySeparator + "View", Scope = Canvas, AllowBare = true, Defaults = KeyChord.List("BracketLeft") },
            new KeyAction { Id = "test.measure", Name = "Go to measure", Category = Editor, Scope = Window, Defaults = KeyChord.List("Ctrl+G") },
        ]));
    }

    [TestCleanup]
    public void Delete() => _root.Delete(recursive: true);

    private KeybindsWindowViewModel Open() => new(
        _live,
        () => Task.FromResult(_importPath),
        () => Task.FromResult(_exportPath),
        _confirmation,
        () =>
        {
            _saved++;
            return _saveSucceeds;
        });

    private static KeybindRowViewModel Row(KeybindsWindowViewModel vm, string id) =>
        vm.Groups.SelectMany(g => g.Rows).Single(r => r.Action.Id == id);

    private static async Task Capture(KeybindsWindowViewModel vm, KeybindRowViewModel row, string chord, KeyChipViewModel? replacing = null)
    {
        vm.BeginCapture(row, replacing);
        await vm.CaptureAsync(KeyChord.Parse(chord));
    }

    [TestMethod]
    public void ShortcutsAreListedByCategoryAndFixedOnesAreNotEditable()
    {
        var vm = Open();

        CollectionAssert.AreEqual(
            new[] { "File", "Edit", "Playback", "Search", "Editor › Notes", "Editor › View", "Editor" },
            vm.Groups.Select(g => g.Title).ToArray());
        Assert.IsFalse(Row(vm, SharedKeys.Undo).IsEditable);
        Assert.IsFalse(Row(vm, "test.mouse").IsEditable);
        Assert.AreEqual("Shift+Click", Row(vm, "test.mouse").OverrideText);
        Assert.IsTrue(Row(vm, SharedKeys.Save).IsEditable);
    }

    [TestMethod]
    public void ARowShowsEachOfItsKeys()
    {
        var vm = Open();

        CollectionAssert.AreEqual(
            new[] { "Ctrl+Y", "Ctrl+Shift+Z" },
            Row(vm, SharedKeys.Redo).Chips.Select(c => c.Chord.ToString()).ToArray());
    }

    [TestMethod]
    public async Task ACapturedKeyReplacesTheOneChosenAndOnlyReachesTheAppOnOk()
    {
        var vm = Open();
        var save = Row(vm, SharedKeys.Save);

        await Capture(vm, save, "Ctrl+W", save.Chips[0]);

        Assert.IsFalse(vm.IsCapturing);
        Assert.AreEqual("Ctrl+W", save.Chips.Single().Chord.ToString());
        Assert.IsTrue(save.IsModified);
        Assert.AreEqual("Ctrl+S", _live.Display(SharedKeys.Save));
        Assert.AreEqual(0, _saved);

        await vm.ApplyAsync();

        Assert.AreEqual("Ctrl+W", _live.Display(SharedKeys.Save));
        Assert.AreEqual(1, _saved);
        Assert.AreEqual(0, _confirmation.Alerts.Count);
    }

    [TestMethod]
    public async Task AFailedSaveIsToldToTheUserAndTheKeysStillApplyThisSession()
    {
        var vm = Open();
        _saveSucceeds = false;
        await Capture(vm, Row(vm, SharedKeys.Save), "Ctrl+W", Row(vm, SharedKeys.Save).Chips[0]);

        await vm.ApplyAsync();

        Assert.AreEqual(1, _saved);
        Assert.AreEqual(1, _confirmation.Alerts.Count);
        Assert.AreEqual("Ctrl+W", _live.Display(SharedKeys.Save));
    }

    [TestMethod]
    public async Task ACapturedKeyCanBeAddedAsAnAlternate()
    {
        var vm = Open();
        var find = Row(vm, "test.find");

        await Capture(vm, find, "Ctrl+Alt+KeyF");

        CollectionAssert.AreEqual(new[] { "Ctrl+F", "Ctrl+Alt+KeyF" }, find.Chips.Select(c => c.Chord.ToString()).ToArray());
    }

    [TestMethod]
    public async Task CancellingTheWaitLeavesTheShortcutAsItWas()
    {
        var vm = Open();
        var save = Row(vm, SharedKeys.Save);

        vm.BeginCapture(save, save.Chips[0]);
        Assert.IsTrue(vm.IsCapturing);
        Assert.IsTrue(save.Chips[0].IsCapturing);
        vm.CancelCapture();

        Assert.IsFalse(vm.IsCapturing);
        Assert.AreEqual("Ctrl+S", save.Chips[0].Chord.ToString());
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task AModifierOnItsOwnKeepsWaiting()
    {
        var vm = Open();

        vm.BeginCapture(Row(vm, SharedKeys.Save), null);
        await vm.CaptureAsync(new KeyChord("LeftCtrl", KeyMods.Ctrl));

        Assert.IsTrue(vm.IsCapturing);
        Assert.IsFalse(vm.HasStatus);
    }

    [TestMethod]
    public async Task AKeyTheSystemTakesIsRefusedWithAReason()
    {
        var vm = Open();

        await Capture(vm, Row(vm, SharedKeys.Save), "Alt+F4");

        Assert.IsTrue(vm.IsCapturing);
        Assert.IsTrue(vm.HasStatus);
        Assert.AreEqual("Ctrl+S", Row(vm, SharedKeys.Save).Chips.Single().Chord.ToString());
    }

    [TestMethod]
    public async Task ABareKeyIsRefusedWhereItWouldBeTypedAndAllowedWhereItWouldNot()
    {
        var vm = Open();

        await Capture(vm, Row(vm, SharedKeys.Save), "KeyG");
        Assert.IsTrue(vm.IsCapturing, "a window command can't be a bare key");
        vm.CancelCapture();

        await Capture(vm, Row(vm, "test.hopo"), "KeyG");
        Assert.IsFalse(vm.IsCapturing);
        CollectionAssert.AreEqual(new[] { "KeyH", "KeyG" }, Row(vm, "test.hopo").Chips.Select(c => c.Chord.ToString()).ToArray());
    }

    [TestMethod]
    public async Task AKeyAFixedShortcutHasIsRefusedWithoutAsking()
    {
        var vm = Open();

        await Capture(vm, Row(vm, SharedKeys.Save), "Ctrl+Z");

        Assert.IsTrue(vm.IsCapturing);
        Assert.AreEqual(0, _confirmation.Asked.Count);
        StringAssert.Contains(vm.StatusMessage, "Undo");
    }

    [TestMethod]
    public async Task AKeyOneOfTheShortcutsOwnKeysAlreadyIsRefused()
    {
        var vm = Open();
        var find = Row(vm, "test.find");

        await Capture(vm, find, "Ctrl+F");

        Assert.IsTrue(vm.IsCapturing);
        Assert.AreEqual(1, find.Chips.Count);
    }

    [TestMethod]
    public async Task MovingAKeyTakesItFromTheOtherShortcut()
    {
        _confirmation.Choice = ConfirmChoice.First;
        var vm = Open();

        await Capture(vm, Row(vm, SharedKeys.Save), "Ctrl+F", Row(vm, SharedKeys.Save).Chips[0]);

        Assert.AreEqual(1, _confirmation.Asked.Count);
        Assert.AreEqual("Ctrl+F", Row(vm, SharedKeys.Save).Chips.Single().Chord.ToString());
        Assert.AreEqual(0, Row(vm, "test.find").Chips.Count);
        Assert.IsTrue(Row(vm, "test.find").IsUnbound);
        Assert.IsFalse(Row(vm, SharedKeys.Save).HasConflict);
    }

    [TestMethod]
    public async Task KeepingBothLeavesTheKeyOnBothAndSaysSo()
    {
        _confirmation.Choice = ConfirmChoice.Second;
        var vm = Open();

        await Capture(vm, Row(vm, SharedKeys.Save), "Ctrl+F", Row(vm, SharedKeys.Save).Chips[0]);

        Assert.AreEqual("Ctrl+F", Row(vm, "test.find").Chips.Single().Chord.ToString());
        StringAssert.Contains(Row(vm, SharedKeys.Save).ConflictNote, "Find");
        StringAssert.Contains(Row(vm, "test.find").ConflictNote, "Save");
    }

    [TestMethod]
    public async Task CancellingTheClashChangesNothingAndStopsWaiting()
    {
        _confirmation.Choice = ConfirmChoice.Cancel;
        var vm = Open();

        await Capture(vm, Row(vm, SharedKeys.Save), "Ctrl+F", Row(vm, SharedKeys.Save).Chips[0]);

        Assert.IsFalse(vm.IsCapturing);
        Assert.AreEqual("Ctrl+S", Row(vm, SharedKeys.Save).Chips.Single().Chord.ToString());
        Assert.AreEqual("Ctrl+F", Row(vm, "test.find").Chips.Single().Chord.ToString());
    }

    [TestMethod]
    public async Task ScopesThatCantBeLiveTogetherDontClash()
    {
        var other = new KeyScope("test.other");
        var live = new KeyMap(new KeybindCatalog(
        [
            new KeyAction { Id = "a", Name = "A", Category = "X", Scope = Window, Defaults = KeyChord.List("Ctrl+K") },
            new KeyAction { Id = "b", Name = "B", Category = "X", Scope = other, Defaults = KeyChord.List("Ctrl+L") },
        ]));
        var vm = new KeybindsWindowViewModel(live, () => Task.FromResult<string?>(null), () => Task.FromResult<string?>(null), _confirmation, () => true);

        await Capture(vm, vm.Groups.Single().Rows.Single(r => r.Action.Id == "b"), "Ctrl+K");

        Assert.AreEqual(0, _confirmation.Asked.Count);
        Assert.IsFalse(vm.Groups.Single().Rows.Any(r => r.HasConflict));
        Assert.IsFalse(vm.IsCapturing);
    }

    [TestMethod]
    public async Task RemovingAKeyUnbindsTheShortcutAndResetRestoresIt()
    {
        var vm = Open();
        var find = Row(vm, "test.find");

        find.Chips[0].RemoveCommand.Execute(null);
        Assert.IsTrue(find.IsUnbound);
        Assert.IsTrue(find.ResetCommand.CanExecute(null));

        find.ResetCommand.Execute(null);
        Assert.AreEqual("Ctrl+F", find.Chips.Single().Chord.ToString());
        Assert.IsFalse(find.IsModified);
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task ResetAllAsksFirstAndRestoresEveryShortcut()
    {
        var vm = Open();
        await Capture(vm, Row(vm, SharedKeys.Save), "Ctrl+W", Row(vm, SharedKeys.Save).Chips[0]);

        _confirmation.Confirms = false;
        vm.ResetAllCommand.Execute(null);
        await Task.Yield();
        Assert.AreEqual("Ctrl+W", Row(vm, SharedKeys.Save).Chips.Single().Chord.ToString());

        _confirmation.Confirms = true;
        await vm.ResetAllCommand.ExecuteAsync(null);
        Assert.AreEqual("Ctrl+S", Row(vm, SharedKeys.Save).Chips.Single().Chord.ToString());
    }

    [TestMethod]
    public void SearchNarrowsByNameCategoryAndKey()
    {
        var vm = Open();

        vm.SearchText = "hopo";
        CollectionAssert.AreEqual(new[] { "test.hopo" }, vm.Groups.SelectMany(g => g.Rows).Select(r => r.Action.Id).ToArray());

        vm.SearchText = "ctrl+f";
        CollectionAssert.AreEqual(new[] { "test.find" }, vm.Groups.SelectMany(g => g.Rows).Select(r => r.Action.Id).ToArray());

        vm.SearchText = "Playback";
        CollectionAssert.AreEqual(new[] { SharedKeys.PlayPause, SharedKeys.NextChange, SharedKeys.PreviousChange }, vm.Groups.SelectMany(g => g.Rows).Select(r => r.Action.Id).ToArray());

        vm.SearchText = "zzzz";
        Assert.IsTrue(vm.HasNoMatches);

        vm.SearchText = "";
        Assert.IsFalse(vm.HasNoMatches);
        Assert.AreEqual(7, vm.Groups.Count);
    }

    private static string[] Labels(IEnumerable<KeybindNodeViewModel> categories) => [.. categories.Select(c => c.Label)];

    private static string[] RowIds(KeybindsWindowViewModel vm) => [.. vm.Groups.SelectMany(g => g.Rows).Select(r => r.Action.Id)];

    [TestMethod]
    public void CategoriesNestInATreeUnderAllShortcuts()
    {
        var vm = Open();

        CollectionAssert.AreEqual(new[] { "All Shortcuts", "File", "Edit", "Playback", "Search", "Editor" }, Labels(vm.Categories));
        var editor = vm.Categories.Single(c => c.Label == "Editor");
        CollectionAssert.AreEqual(new[] { "Notes", "View", "Go to measure" }, Labels(editor.Children), "categories first, then the category's own shortcuts");
        Assert.AreEqual("Editor › Notes", editor.Children[0].Path);
        Assert.IsTrue(vm.Categories[0].IsAll);
        Assert.AreSame(vm.Categories[0], vm.SelectedCategory);
    }

    [TestMethod]
    public void EachShortcutIsAnEntryUnderItsCategory()
    {
        var vm = Open();

        var file = vm.Categories.Single(c => c.Label == "File");
        CollectionAssert.AreEqual(new[] { "Save", "Open Settings" }, Labels(file.Children));
        Assert.IsTrue(file.Children.All(c => c.IsShortcut));
        Assert.IsFalse(file.IsShortcut);
        Assert.AreEqual("File", file.Children[0].Path);
        Assert.AreSame(Row(vm, SharedKeys.Save), file.Children[0].Shortcut);

        var notes = vm.Categories.Single(c => c.Label == "Editor").Children.Single(c => c.Label == "Notes");
        CollectionAssert.AreEqual(new[] { "Force HOPO", "Add to selection" }, Labels(notes.Children), "fixed shortcuts are entries too");
    }

    [TestMethod]
    public void CategoriesStartClosedOpenWhileSearchingAndOpenWhenPicked()
    {
        var vm = Open();
        var editor = vm.Categories.Single(c => c.Label == "Editor");
        Assert.IsTrue(vm.Categories.Where(c => !c.IsAll).All(c => !c.IsExpanded));

        vm.SelectedCategory = editor;
        Assert.IsTrue(editor.IsExpanded);

        vm.SearchText = "e";
        Assert.IsTrue(vm.Categories.Where(c => !c.IsAll).All(c => c.IsExpanded));
        Assert.IsTrue(vm.Categories.Single(c => c.Label == "Editor").Children.Where(c => !c.IsShortcut).All(c => c.IsExpanded));
    }

    [TestMethod]
    public void PickingAShortcutJumpsToItWithoutNarrowingTheList()
    {
        var vm = Open();
        var raised = new List<KeybindRowViewModel>();
        vm.RevealRequested += (_, row) => raised.Add(row);
        var hopo = Row(vm, "test.hopo");

        vm.SelectedCategory = vm.Categories.Single(c => c.Label == "Editor").Children.Single(c => c.Label == "Notes").Children[0];

        CollectionAssert.AreEqual(new[] { hopo }, raised);
        Assert.IsTrue(hopo.IsRevealed);
        Assert.AreEqual(7, vm.Groups.Count, "the list still shows every category");
    }

    [TestMethod]
    public void JumpingToAShortcutInTheCurrentCategoryKeepsTheList()
    {
        var vm = Open();
        var editor = vm.Categories.Single(c => c.Label == "Editor");
        vm.SelectedCategory = editor;

        vm.SelectedCategory = editor.Children.Single(c => c.Label == "View").Children[0];

        CollectionAssert.AreEquivalent(new[] { "test.hopo", "test.mouse", "test.snap", "test.measure" }, RowIds(vm));
        Assert.IsTrue(Row(vm, "test.snap").IsRevealed);
    }

    [TestMethod]
    public void JumpingToAShortcutOutsideTheCurrentCategoryShowsItsCategory()
    {
        var vm = Open();
        vm.SelectedCategory = vm.Categories.Single(c => c.Label == "File");

        vm.SelectedCategory = vm.Categories.Single(c => c.Label == "Editor").Children.Single(c => c.Label == "View").Children[0];

        CollectionAssert.AreEqual(new[] { "test.snap" }, RowIds(vm));
        Assert.IsTrue(Row(vm, "test.snap").IsRevealed);
    }

    [TestMethod]
    public void JumpingAgainToTheSameShortcutScrollsToItAgain()
    {
        var vm = Open();
        int raised = 0;
        vm.RevealRequested += (_, _) => raised++;
        var save = Row(vm, SharedKeys.Save);

        vm.Reveal(save);
        vm.Reveal(save);

        Assert.AreEqual(2, raised);
        Assert.IsTrue(save.IsRevealed);
    }

    [TestMethod]
    public void OnlyTheLatestShortcutJumpedToIsOutlinedAndPickingACategoryClearsIt()
    {
        var vm = Open();

        vm.Reveal(Row(vm, SharedKeys.Save));
        vm.Reveal(Row(vm, "test.find"));
        Assert.IsFalse(Row(vm, SharedKeys.Save).IsRevealed);
        Assert.IsTrue(Row(vm, "test.find").IsRevealed);

        vm.SelectedCategory = vm.Categories.Single(c => c.Label == "Search");
        Assert.IsFalse(Row(vm, "test.find").IsRevealed);
    }

    [TestMethod]
    public void SearchingLeavesOnlyTheMatchingShortcutsInTheTree()
    {
        var vm = Open();

        vm.SearchText = "snap";

        var view = vm.Categories.Single(c => c.Label == "Editor").Children.Single();
        CollectionAssert.AreEqual(new[] { "Coarser snap" }, Labels(view.Children));
        Assert.IsTrue(vm.SelectedCategory!.IsAll);
    }

    [TestMethod]
    public void AParentCategoryShowsItsOwnShortcutsAndThoseNestedInIt()
    {
        var vm = Open();

        vm.SelectedCategory = vm.Categories.Single(c => c.Label == "Editor");

        CollectionAssert.AreEquivalent(new[] { "test.hopo", "test.mouse", "test.snap", "test.measure" }, RowIds(vm));
    }

    [TestMethod]
    public void AChildCategoryShowsOnlyItsOwn()
    {
        var vm = Open();

        vm.SelectedCategory = vm.Categories.Single(c => c.Label == "Editor").Children.Single(c => c.Label == "Notes");

        CollectionAssert.AreEquivalent(new[] { "test.hopo", "test.mouse" }, RowIds(vm));
        Assert.AreEqual("Editor › Notes", vm.Groups.Single().Title);
    }

    [TestMethod]
    public void ACategoryNamedLikeAnotherPrefixIsNotMistakenForItsParent()
    {
        var live = new KeyMap(new KeybindCatalog(
        [
            new KeyAction { Id = "a", Name = "A", Category = "Edit", Scope = Window, Defaults = KeyChord.List("Ctrl+K") },
            new KeyAction { Id = "b", Name = "B", Category = "Editor", Scope = Window, Defaults = KeyChord.List("Ctrl+L") },
        ]));
        var vm = new KeybindsWindowViewModel(live, () => Task.FromResult<string?>(null), () => Task.FromResult<string?>(null), _confirmation, () => true);

        vm.SelectedCategory = vm.Categories.Single(c => c.Label == "Edit");

        CollectionAssert.AreEqual(new[] { "a" }, RowIds(vm));
    }

    [TestMethod]
    public void SearchingNarrowsTheTreeAndSelectsAllShortcuts()
    {
        var vm = Open();
        vm.SelectedCategory = vm.Categories.Single(c => c.Label == "File");

        vm.SearchText = "snap";

        CollectionAssert.AreEqual(new[] { "All Shortcuts", "Editor" }, Labels(vm.Categories));
        CollectionAssert.AreEqual(new[] { "View" }, Labels(vm.Categories[1].Children));
        Assert.IsTrue(vm.SelectedCategory!.IsAll);
        CollectionAssert.AreEqual(new[] { "test.snap" }, RowIds(vm));

        vm.SearchText = "";
        Assert.AreEqual(6, vm.Categories.Count);
    }

    [TestMethod]
    public async Task EditingKeepsTheSelectedCategory()
    {
        var vm = Open();
        var editor = vm.Categories.Single(c => c.Label == "Editor");
        vm.SelectedCategory = editor;

        await Capture(vm, Row(vm, "test.measure"), "Ctrl+Alt+KeyG", Row(vm, "test.measure").Chips[0]);

        Assert.AreSame(editor, vm.SelectedCategory);
        Assert.AreEqual(4, RowIds(vm).Length);
    }

    [TestMethod]
    public async Task AnExportedLayoutImportsIntoAnotherWindow()
    {
        var vm = Open();
        await Capture(vm, Row(vm, SharedKeys.Save), "Ctrl+W", Row(vm, SharedKeys.Save).Chips[0]);
        _exportPath = Path.Combine(_root.FullName, "layout.json");
        await vm.ExportCommand.ExecuteAsync(null);
        Assert.IsTrue(File.Exists(_exportPath));

        _importPath = _exportPath;
        _confirmation.Choice = ConfirmChoice.Second;
        var other = Open();
        await other.ImportCommand.ExecuteAsync(null);

        Assert.AreEqual("Ctrl+W", Row(other, SharedKeys.Save).Chips.Single().Chord.ToString());
        Assert.AreEqual("Ctrl+S", _live.Display(SharedKeys.Save), "an import is staged until OK");
        StringAssert.Contains(other.StatusMessage, "Imported");
    }

    [TestMethod]
    public async Task AFileThatIsNotALayoutIsReportedAndChangesNothing()
    {
        _importPath = Path.Combine(_root.FullName, "not-keys.json");
        File.WriteAllText(_importPath, """{ "Name": "x" }""");
        var vm = Open();

        await vm.ImportCommand.ExecuteAsync(null);

        Assert.AreEqual(1, _confirmation.Alerts.Count);
        Assert.AreEqual(0, _confirmation.Asked.Count);
        Assert.AreEqual("Ctrl+S", Row(vm, SharedKeys.Save).Chips.Single().Chord.ToString());
    }

    [TestMethod]
    public async Task CancellingThePickersDoesNothing()
    {
        var vm = Open();

        await vm.ImportCommand.ExecuteAsync(null);
        await vm.ExportCommand.ExecuteAsync(null);

        Assert.AreEqual(0, _confirmation.Alerts.Count);
        Assert.AreEqual(0, _confirmation.Asked.Count);
    }

    [TestMethod]
    public async Task ACanvasShortcutIsCapturedAsAPhysicalKey()
    {
        var vm = Open();

        vm.BeginCapture(Row(vm, "test.hopo"), null);

        Assert.AreEqual(KeyMatch.Physical, vm.CaptureAction!.Scope.Match);
        await Task.CompletedTask;
    }
}
