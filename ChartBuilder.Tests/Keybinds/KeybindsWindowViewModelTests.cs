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

        /// <summary>When set, a choice isn't answered until the test completes it, so the window can change while one is asked.</summary>
        public TaskCompletionSource<ConfirmChoice>? Pending { get; set; }

        public bool Confirms { get; set; } = true;

        /// <summary>When set, a choice fails, as a dialog that couldn't be shown would.</summary>
        public bool Throws { get; set; }

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
            if (Throws)
                throw new InvalidOperationException("The dialog couldn't be shown.");
            return Pending?.Task ?? Task.FromResult(Choice);
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
            // Without the Settings window's and Help's, whose categories would only add a row to every count below.
            .. SharedKeys.Actions().Where(a => a.Id is not (SharedKeys.FocusSearch or SharedKeys.Help)),
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
    public async Task RemovingTheKeyBeingReplacedWhileTheClashIsAskedAboutChangesNothingMore()
    {
        _confirmation.Pending = new TaskCompletionSource<ConfirmChoice>();
        var vm = Open();

        var capture = Capture(vm, Row(vm, SharedKeys.Save), "Ctrl+F", Row(vm, SharedKeys.Save).Chips[0]);
        vm.Remove(Row(vm, SharedKeys.Save), Row(vm, SharedKeys.Save).Chips[0]);
        _confirmation.Pending.SetResult(ConfirmChoice.First);
        await capture;

        Assert.IsFalse(vm.IsCapturing);
        Assert.AreEqual(0, Row(vm, SharedKeys.Save).Chips.Count, "the key is removed, not replaced by the one that was awaited");
        Assert.AreEqual("Ctrl+F", Row(vm, "test.find").Chips.Single().Chord.ToString(), "and the other shortcut keeps its key");
    }

    [TestMethod]
    public async Task CancellingTheWaitWhileTheClashIsAskedAboutAddsNoKey()
    {
        _confirmation.Pending = new TaskCompletionSource<ConfirmChoice>();
        var vm = Open();

        var capture = Capture(vm, Row(vm, SharedKeys.Save), "Ctrl+F", Row(vm, SharedKeys.Save).Chips[0]);
        vm.CancelCapture();
        _confirmation.Pending.SetResult(ConfirmChoice.Second);
        await capture;

        Assert.AreEqual("Ctrl+S", Row(vm, SharedKeys.Save).Chips.Single().Chord.ToString());
        Assert.AreEqual("Ctrl+F", Row(vm, "test.find").Chips.Single().Chord.ToString());
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

    private static KeycapViewModel Cap(KeybindsWindowViewModel vm, string name) =>
        vm.Map.Rows.SelectMany(r => r.Keys).Single(k => k.IsKey && k.Key.Name == name);

    [TestMethod]
    public void CategoriesAreAFlatListOfTheTopLevelOnesUnderAllShortcuts()
    {
        var vm = Open();

        CollectionAssert.AreEqual(new[] { "All Shortcuts", "File", "Edit", "Playback", "Search", "Editor" }, Labels(vm.Categories));
        Assert.IsTrue(vm.Categories[0].IsAll);
        Assert.AreSame(vm.Categories[0], vm.SelectedCategory);
        Assert.AreEqual("All Shortcuts", vm.PageTitle);
    }

    [TestMethod]
    public void ACategoryNamesTheOnesNestedInItInItsPageHeader()
    {
        var vm = Open();

        var editor = vm.Categories.Single(c => c.Label == "Editor");
        Assert.AreEqual("Notes, View", editor.Subtitle);
        Assert.AreEqual("", vm.Categories.Single(c => c.Label == "File").Subtitle);

        vm.SelectedCategory = editor;
        Assert.AreEqual("Editor", vm.PageTitle);
        Assert.AreEqual("Notes, View", vm.PageSubtitle);
        Assert.IsTrue(vm.HasPageSubtitle);
    }

    [TestMethod]
    public void ACategoryShowsItsOwnShortcutsAndThoseNestedInItUnderTheRestOfTheirPath()
    {
        var vm = Open();

        vm.SelectedCategory = vm.Categories.Single(c => c.Label == "Editor");

        CollectionAssert.AreEquivalent(new[] { "test.hopo", "test.mouse", "test.snap", "test.measure" }, RowIds(vm));
        CollectionAssert.AreEqual(new[] { "Notes", "View", "" }, vm.Groups.Select(g => g.Title).ToArray(), "the page header says Editor, so its own shortcuts need no heading");
        Assert.IsFalse(vm.Groups[2].HasTitle);
    }

    [TestMethod]
    public void JumpingToAShortcutWithAllShortcutsPickedDoesNotNarrowTheList()
    {
        var vm = Open();
        var raised = new List<KeybindRowViewModel>();
        vm.RevealRequested += (_, row) => raised.Add(row);
        var hopo = Row(vm, "test.hopo");

        vm.Reveal(hopo);

        CollectionAssert.AreEqual(new[] { hopo }, raised);
        Assert.IsTrue(hopo.IsRevealed);
        Assert.AreEqual(7, vm.Groups.Count, "the list still shows every category");
        Assert.IsTrue(vm.SelectedCategory!.IsAll);
    }

    [TestMethod]
    public void JumpingToAShortcutInTheCurrentCategoryKeepsTheList()
    {
        var vm = Open();
        var editor = vm.Categories.Single(c => c.Label == "Editor");
        vm.SelectedCategory = editor;

        vm.Reveal(Row(vm, "test.snap"));

        Assert.AreSame(editor, vm.SelectedCategory);
        CollectionAssert.AreEquivalent(new[] { "test.hopo", "test.mouse", "test.snap", "test.measure" }, RowIds(vm));
        Assert.IsTrue(Row(vm, "test.snap").IsRevealed);
    }

    [TestMethod]
    public void JumpingToAShortcutOutsideTheCurrentCategoryPicksItsCategory()
    {
        var vm = Open();
        var hopo = Row(vm, "test.hopo");
        var snap = Row(vm, "test.snap");
        vm.SelectedCategory = vm.Categories.Single(c => c.Label == "File");

        vm.Reveal(hopo);

        Assert.AreEqual("Editor", vm.SelectedCategory!.Label);
        CollectionAssert.AreEquivalent(new[] { "test.hopo", "test.mouse", "test.snap", "test.measure" }, RowIds(vm));
        Assert.IsTrue(hopo.IsRevealed);
        Assert.IsFalse(snap.IsRevealed);
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
    public void SearchingNarrowsTheListOfCategoriesCountsTheMatchesAndPicksAllShortcuts()
    {
        var vm = Open();
        vm.SelectedCategory = vm.Categories.Single(c => c.Label == "File");

        vm.SearchText = "snap";

        CollectionAssert.AreEqual(new[] { "All Shortcuts", "Editor" }, Labels(vm.Categories));
        Assert.IsTrue(vm.SelectedCategory!.IsAll);
        CollectionAssert.AreEqual(new[] { "test.snap" }, RowIds(vm));
        Assert.AreEqual(1, vm.Categories[0].MatchCount);
        Assert.AreEqual(1, vm.Categories[1].MatchCount);

        vm.SearchText = "";
        Assert.AreEqual(6, vm.Categories.Count);
        Assert.IsNull(vm.Categories[0].MatchCount);
        Assert.IsFalse(vm.Categories[1].HasMatchCount);
    }

    [TestMethod]
    public async Task CategoriesCountTheShortcutsThatDifferFromTheDefaults()
    {
        var vm = Open();
        var save = Row(vm, SharedKeys.Save);
        var file = vm.Categories.Single(c => c.Label == "File");
        Assert.AreEqual(0, file.ModifiedCount);
        Assert.IsFalse(file.HasModified);

        await Capture(vm, save, "Ctrl+W", save.Chips[0]);

        Assert.AreEqual(1, file.ModifiedCount);
        Assert.IsTrue(file.HasModified);
        Assert.AreEqual(1, vm.Categories[0].ModifiedCount, "All Shortcuts counts every category's");
        Assert.AreEqual(0, vm.Categories.Single(c => c.Label == "Edit").ModifiedCount);

        save.ResetCommand.Execute(null);
        Assert.AreEqual(0, file.ModifiedCount);
    }

    [TestMethod]
    public void SearchingTakesTheWindowBackToTheListAndTheKeyboardViewClearsTheSearch()
    {
        var vm = Open();
        vm.IsKeyboardView = true;
        Assert.IsFalse(vm.IsListView);
        Assert.AreEqual("Keyboard Map", vm.PageTitle);

        vm.SearchText = "save";
        Assert.IsFalse(vm.IsKeyboardView);
        Assert.IsTrue(vm.IsListView);

        vm.IsKeyboardView = true;
        Assert.AreEqual("", vm.SearchText);
        Assert.AreEqual(6, vm.Categories.Count);

        vm.IsListView = true;
        Assert.IsFalse(vm.IsKeyboardView);
    }

    [TestMethod]
    public void TheKeyboardTintsTheKeysTheModifierLayerHas()
    {
        var vm = Open();

        Assert.IsTrue(Cap(vm, "H").IsBound, "Force HOPO is bare H");
        Assert.IsFalse(Cap(vm, "S").IsBound, "Ctrl+S isn't S on its own");

        vm.Map.Ctrl = true;

        Assert.IsTrue(Cap(vm, "S").IsBound);
        Assert.AreEqual("Save", Cap(vm, "S").Actions.Single().Name);
        Assert.AreEqual("Save", Cap(vm, "S").ToolTipText);
        Assert.IsFalse(Cap(vm, "H").IsBound);
        Assert.IsTrue(Cap(vm, "Z").IsFixed, "Undo can't be changed");
        Assert.IsFalse(Cap(vm, "S").IsFixed);
        Assert.IsFalse(Cap(vm, "S").IsClashing);
    }

    [TestMethod]
    public void KeysThatShortcutsLiveTogetherShareAreFlaggedAsClashing()
    {
        _live.Set("test.find", KeyChord.List("Ctrl+G"));
        var vm = Open();
        vm.Map.Ctrl = true;

        Assert.IsTrue(Cap(vm, "G").IsClashing, "Find and Go to measure are both Ctrl+G, in scopes that are live together");
        Assert.IsFalse(Cap(vm, "S").IsClashing);
    }

    [TestMethod]
    public void AShortcutOnAKeyTheKeyboardDoesNotDrawIsShownBesideIt()
    {
        _live.Set("test.hopo", KeyChord.List("NumPadAdd"));
        var vm = Open();

        Assert.IsTrue(vm.Map.HasOtherKeys);
        var other = vm.Map.OtherKeys.Single();
        Assert.AreEqual("Num +", other.Key.Name);
        Assert.IsTrue(other.IsBound);
        Assert.IsFalse(other.CanBind);

        other.SelectCommand.Execute(null);
        Assert.IsFalse(vm.Map.CanAssign, "there's no token to bind to a key that isn't drawn");
        Assert.AreEqual("Force HOPO", vm.Map.Bindings.Single().Name);

        vm.Map.Ctrl = true;
        Assert.IsFalse(vm.Map.HasOtherKeys);
    }

    [TestMethod]
    public void PickingAKeyListsWhatIsOnItInTheLayerShown()
    {
        var vm = Open();
        vm.Map.Ctrl = true;

        Cap(vm, "S").SelectCommand.Execute(null);

        Assert.IsTrue(vm.Map.HasSelection);
        Assert.AreEqual("Ctrl+S", vm.Map.SelectedChordText);
        Assert.AreEqual("Save", vm.Map.Bindings.Single().Name);
        Assert.IsTrue(Cap(vm, "S").IsSelected);
        Assert.IsFalse(vm.Map.IsUnboundSelection);

        vm.Map.Shift = true;
        Assert.AreEqual("Ctrl+Shift+S", vm.Map.SelectedChordText, "the pick stays while the layer changes");
        Assert.AreEqual(0, vm.Map.Bindings.Count);
        Assert.IsTrue(vm.Map.IsUnboundSelection);
        StringAssert.Contains(vm.Map.UnboundText, "Ctrl+Shift+S");
        Assert.IsTrue(vm.Map.CanAssign);
    }

    [TestMethod]
    public async Task AKeyPickedInTheKeyboardViewCanBeBoundToAShortcut()
    {
        var vm = Open();
        vm.Map.Ctrl = true;
        Cap(vm, "K").SelectCommand.Execute(null);
        var find = Row(vm, "test.find");
        CollectionAssert.Contains(vm.Map.AssignChoices.ToList(), find);
        Assert.IsFalse(vm.Map.AssignChoices.Any(r => !r.IsEditable), "a shortcut that can't change isn't offered");

        vm.Map.AssignTo = find;
        await Task.Yield();

        Assert.IsNull(vm.Map.AssignTo, "it clears, ready for the next");
        Assert.AreEqual(find, Cap(vm, "K").Actions.Single());
        Assert.AreEqual("Find", vm.Map.Bindings.Single().Name);
        Assert.IsTrue(find.Chips.Any(c => c.Chord.ToString() == "Ctrl+K"));
        Assert.AreEqual("Ctrl+F", _live.Display("test.find"), "staged until OK");
        Assert.IsFalse(vm.IsCapturing);
    }

    [TestMethod]
    public void TypingNarrowsTheShortcutsOfferedForAKeyByNameAndCategory()
    {
        var vm = Open();
        vm.Map.Ctrl = true;
        Cap(vm, "K").SelectCommand.Execute(null);
        var all = vm.Map.AssignChoices.Select(r => r.Action.Id).ToArray();
        CollectionAssert.AreEqual(all, vm.Map.FilteredAssignChoices.Select(r => r.Action.Id).ToArray(), "nothing typed offers every one");
        Assert.IsFalse(vm.Map.HasNoAssignMatches);

        vm.Map.AssignSearch = "FIND";
        CollectionAssert.AreEqual(new[] { "test.find" }, vm.Map.FilteredAssignChoices.Select(r => r.Action.Id).ToArray(), "case doesn't matter");

        vm.Map.AssignSearch = "playback next";
        CollectionAssert.AreEqual(new[] { SharedKeys.NextChange }, vm.Map.FilteredAssignChoices.Select(r => r.Action.Id).ToArray(), "every word, in the name or the category, in any order");

        vm.Map.AssignSearch = "zzzz";
        Assert.AreEqual(0, vm.Map.FilteredAssignChoices.Count);
        Assert.IsTrue(vm.Map.HasNoAssignMatches);
        Assert.AreEqual(all.Length, vm.Map.AssignChoices.Count, "the choices themselves are unchanged");
    }

    [TestMethod]
    public async Task BindingAShortcutClearsWhatWasTypedToFindIt()
    {
        var vm = Open();
        vm.Map.Ctrl = true;
        Cap(vm, "K").SelectCommand.Execute(null);
        vm.Map.AssignSearch = "find";

        vm.Map.AssignTo = Row(vm, "test.find");
        await Task.Yield();

        Assert.AreEqual("", vm.Map.AssignSearch);
        Assert.IsNull(vm.Map.AssignTo);
    }

    [TestMethod]
    public void TheChoicesAreFilteredAgainWhenAnotherKeyIsPicked()
    {
        var vm = Open();
        vm.Map.Ctrl = true;
        Cap(vm, "K").SelectCommand.Execute(null);
        vm.Map.AssignSearch = "find";
        Assert.AreEqual(1, vm.Map.FilteredAssignChoices.Count);

        Cap(vm, "S").SelectCommand.Execute(null);

        Assert.AreEqual(1, vm.Map.FilteredAssignChoices.Count, "Find is offered for Ctrl+S too, as it has room for another key");
        Assert.IsFalse(vm.Map.FilteredAssignChoices.Any(r => r.Action.Id == SharedKeys.Save), "but Save, which already has the key, isn't");
        vm.Map.AssignSearch = "save";
        Assert.IsTrue(vm.Map.HasNoAssignMatches);
    }

    [TestMethod]
    public async Task AKeyBoundFromTheKeyboardViewIsWrittenForTheShortcutsScope()
    {
        var vm = Open();
        Cap(vm, ";").SelectCommand.Execute(null);

        await vm.BindKeyAsync(Row(vm, "test.hopo"), new KeyChord(Cap(vm, ";").Key.TokenFor(KeyMatch.Physical), KeyMods.None));

        Assert.IsTrue(vm.Working.ChordsOf("test.hopo").Any(c => c.Key == "Semicolon"), "a canvas key is a physical one");
        Assert.AreEqual("Force HOPO", Cap(vm, ";").Actions.Single().Name);
    }

    [TestMethod]
    public async Task AClashFromTheKeyboardViewIsAskedAboutAsInTheList()
    {
        var vm = Open();
        _confirmation.Choice = ConfirmChoice.Cancel;

        await vm.BindKeyAsync(Row(vm, "test.find"), KeyChord.Parse("Ctrl+S"));

        Assert.AreEqual(1, _confirmation.Asked.Count);
        Assert.AreEqual("Ctrl+F", Row(vm, "test.find").Chips.Single().Chord.ToString());
        Assert.IsFalse(vm.IsCapturing);

        _confirmation.Choice = ConfirmChoice.Second;
        await vm.BindKeyAsync(Row(vm, "test.find"), KeyChord.Parse("Ctrl+S"));

        CollectionAssert.AreEquivalent(new[] { "Ctrl+F", "Ctrl+S" }, Row(vm, "test.find").Chips.Select(c => c.Chord.ToString()).ToArray(), "kept on both");
        Assert.AreEqual("Ctrl+S", Row(vm, SharedKeys.Save).Chips.Single().Chord.ToString());
    }

    [TestMethod]
    public async Task AKeyThatCannotBeUsedIsRefusedFromTheKeyboardViewWithAReasonAndNothingIsLeftWaiting()
    {
        var vm = Open();

        await vm.BindKeyAsync(Row(vm, "test.find"), KeyChord.Parse("Alt+F4"));

        Assert.IsNotNull(vm.StatusMessage);
        Assert.IsFalse(vm.IsCapturing);
        Assert.AreEqual("Ctrl+F", Row(vm, "test.find").Chips.Single().Chord.ToString());
    }

    [TestMethod]
    public void ATakenOffKeyIsRemovedFromTheShortcutAndTheKeyboard()
    {
        var vm = Open();
        vm.Map.Ctrl = true;
        Cap(vm, "S").SelectCommand.Execute(null);

        vm.Map.Bindings.Single().RemoveCommand.Execute(null);

        Assert.AreEqual(0, Row(vm, SharedKeys.Save).Chips.Count);
        Assert.IsFalse(Cap(vm, "S").IsBound);
        Assert.IsTrue(vm.Map.IsUnboundSelection);
    }

    [TestMethod]
    public void AFixedShortcutOnAPickedKeyCannotBeTakenOffIt()
    {
        var vm = Open();
        vm.Map.Ctrl = true;
        Cap(vm, "Z").SelectCommand.Execute(null);

        var binding = vm.Map.Bindings.Single();

        Assert.IsTrue(binding.IsLocked);
        Assert.IsFalse(binding.CanRemove);
        Assert.IsFalse(binding.RemoveCommand.CanExecute(null));
    }

    [TestMethod]
    public void ShowingAShortcutInTheListLeavesTheKeyboardAndJumpsToIt()
    {
        var vm = Open();
        var raised = new List<KeybindRowViewModel>();
        vm.RevealRequested += (_, row) => raised.Add(row);
        vm.IsKeyboardView = true;
        vm.Map.Ctrl = true;
        Cap(vm, "S").SelectCommand.Execute(null);

        vm.Map.Bindings.Single().ShowInListCommand.Execute(null);

        Assert.IsFalse(vm.IsKeyboardView);
        CollectionAssert.AreEqual(new[] { Row(vm, SharedKeys.Save) }, raised);
        Assert.IsTrue(Row(vm, SharedKeys.Save).IsRevealed);
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

    [TestMethod]
    public async Task TakingTheLastShortcutOffAKeyNotOnTheKeyboardDropsThePickForGood()
    {
        _live.Set("test.hopo", KeyChord.List("NumPadAdd"));
        var vm = Open();
        vm.Map.OtherKeys.Single().SelectCommand.Execute(null);

        vm.Map.Bindings.Single().RemoveCommand.Execute(null);

        Assert.IsFalse(vm.Map.HasSelection);
        Assert.IsFalse(vm.Map.HasOtherKeys);

        await Capture(vm, Row(vm, "test.hopo"), "NumPadAdd");

        Assert.IsTrue(vm.Map.HasOtherKeys);
        Assert.IsFalse(vm.Map.HasSelection, "binding the key again doesn't pick it again");
    }

    [TestMethod]
    public void TheCapsOfKeysNotOnTheKeyboardAreKeptAcrossARefresh()
    {
        _live.Set("test.hopo", KeyChord.List("NumPadAdd"));
        var vm = Open();
        var before = vm.Map.OtherKeys.Single();

        vm.Map.Refresh();
        Assert.AreSame(before, vm.Map.OtherKeys.Single());

        vm.Map.Shift = true;
        Assert.IsFalse(vm.Map.HasOtherKeys);
        vm.Map.Shift = false;
        Assert.AreSame(before, vm.Map.OtherKeys.Single(), "the same cap comes back with the layer");
    }

    [TestMethod]
    public void JumpingToAShortcutTheSearchHidesClearsTheSearch()
    {
        var vm = Open();
        var raised = new List<KeybindRowViewModel>();
        vm.RevealRequested += (_, row) => raised.Add(row);
        var hopo = Row(vm, "test.hopo");
        vm.SearchText = "find";
        Assert.IsTrue(vm.IsSearching);
        CollectionAssert.DoesNotContain(RowIds(vm), "test.hopo");

        vm.Reveal(hopo);

        Assert.AreEqual("", vm.SearchText);
        CollectionAssert.Contains(RowIds(vm), "test.hopo");
        Assert.IsTrue(hopo.IsRevealed);
        CollectionAssert.AreEqual(new[] { hopo }, raised);
    }

    [TestMethod]
    public void ACategoryNamesOnlyTheNestedOnesTheSearchShows()
    {
        var vm = Open();

        vm.SearchText = "snap";

        Assert.AreEqual("View", vm.Categories.Single(c => c.Label == "Editor").Subtitle);

        vm.SearchText = "";
        Assert.AreEqual("Notes, View", vm.Categories.Single(c => c.Label == "Editor").Subtitle);
    }

    [TestMethod]
    public void AKeyBoundOnlyWithOtherModifiersSaysSoRatherThanLookingFree()
    {
        var vm = Open();
        var s = Cap(vm, "S");

        Assert.IsFalse(s.IsBound, "Ctrl+S isn't S on its own");
        Assert.IsTrue(s.IsBoundElsewhere);
        StringAssert.Contains(s.ToolTipText, "Ctrl+S Save");
        Assert.IsFalse(Cap(vm, "J").IsBoundElsewhere, "nothing is bound to J in any layer");

        s.SelectCommand.Execute(null);
        Assert.IsTrue(vm.Map.HasElsewhere);
        StringAssert.Contains(vm.Map.ElsewhereText, "Ctrl+S Save");

        vm.Map.Ctrl = true;
        Assert.IsTrue(Cap(vm, "S").IsBound);
        Assert.IsFalse(Cap(vm, "S").IsBoundElsewhere, "it's bound in the layer shown");
    }

    [TestMethod]
    public async Task ABindingThatFailsFromTheKeyboardViewIsShownAndLeavesNothingWaiting()
    {
        _confirmation.Throws = true;
        var vm = Open();
        vm.Map.Ctrl = true;
        Cap(vm, "G").SelectCommand.Execute(null);

        vm.Map.AssignTo = Row(vm, "test.find");
        await Task.Yield();

        Assert.IsTrue(vm.HasStatus, "Ctrl+G is Go to measure's, so the dialog was asked, and failed");
        Assert.IsNull(vm.Map.AssignTo);
        Assert.IsFalse(vm.IsCapturing);
    }
}
