using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using ChartShared.Core.Keybinds;
using ChartShared.UI.Resources;
using ChartShared.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChartShared.UI.ViewModels.Keybinds
{
    /// <summary>One category of shortcuts in the keyboard shortcuts window, under its name as a heading.</summary>
    public sealed class KeybindGroupViewModel(string title, IReadOnlyList<KeybindRowViewModel> rows)
    {
        /// <summary>The heading, or empty for a category's own shortcuts under its page header.</summary>
        public string Title { get; } = title;

        public bool HasTitle => Title.Length > 0;

        public IReadOnlyList<KeybindRowViewModel> Rows { get; } = rows;
    }

    /// <summary>
    /// An entry in the keyboard shortcuts window's list of categories: a top-level category, which shows its shortcuts and
    /// those of the categories nested in it, or the "All Shortcuts" entry at the top.
    /// </summary>
    public sealed partial class KeybindNodeViewModel : ObservableObject
    {
        internal KeybindNodeViewModel(string label, string path, string subtitle, int? matchCount)
        {
            Label = label;
            Path = path;
            Subtitle = subtitle;
            MatchCount = matchCount;
        }

        /// <summary>The entry that shows every shortcut, so there's always a way back from a selected category.</summary>
        internal static KeybindNodeViewModel All(int? matchCount) => new(SharedStrings.KeybindsAllCategories, "", "", matchCount);

        public string Label { get; }

        /// <summary>The category's name; empty for "All Shortcuts".</summary>
        public string Path { get; }

        public bool IsAll => Path.Length == 0;

        /// <summary>The page header's second line: the categories nested in this one, comma separated.</summary>
        public string Subtitle { get; }

        /// <summary>How many shortcuts match the search, or <see langword="null"/> while there isn't one.</summary>
        public int? MatchCount { get; }

        public bool HasMatchCount => MatchCount is not null;

        /// <summary>How many of its shortcuts have keys that differ from the defaults.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasModified))]
        public partial int ModifiedCount { get; set; }

        public bool HasModified => ModifiedCount > 0;
    }

    /// <summary>One key of a shortcut, as a button: it opens a menu to change the key or remove it.</summary>
    public sealed partial class KeyChipViewModel : ObservableObject
    {
        private readonly KeybindsWindowViewModel _owner;

        internal KeyChipViewModel(KeybindRowViewModel row, KeyChord chord, KeybindsWindowViewModel owner)
        {
            Row = row;
            Chord = chord;
            _owner = owner;
        }

        public KeybindRowViewModel Row { get; }

        public KeyChord Chord { get; }

        /// <summary>The key as it's labelled, or a prompt while the window waits for the key to put in its place.</summary>
        public string Label => IsCapturing ? SharedStrings.KeybindsPressKey : Chord.ToDisplayString();

        /// <summary>Whether the window is waiting for the key to replace this one.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Label))]
        public partial bool IsCapturing { get; set; }

        [RelayCommand]
        private void Change() => _owner.BeginCapture(Row, this);

        [RelayCommand]
        private void Remove() => _owner.Remove(Row, this);
    }

    /// <summary>One shortcut in the window: its name, its keys, and the buttons that change them.</summary>
    public sealed partial class KeybindRowViewModel : ObservableObject
    {
        private readonly KeybindsWindowViewModel _owner;

        internal KeybindRowViewModel(KeyAction action, KeybindsWindowViewModel owner)
        {
            Action = action;
            _owner = owner;
        }

        public KeyAction Action { get; }

        public string Name => Action.Name;

        public string Category => Action.Category;

        /// <summary>Whether the shortcut is listed only, never changed.</summary>
        public bool IsLocked => Action.Locked;

        public bool IsEditable => !Action.Locked;

        /// <summary>What a row that isn't a key shows in place of one (<c>Shift+Click</c>); null for a row of keys.</summary>
        public string? OverrideText => Action.DisplayOverride;

        public bool HasOverrideText => Action.DisplayOverride is not null;

        public ObservableCollection<KeyChipViewModel> Chips { get; } = [];

        /// <summary>Whether the shortcut has no key to show.</summary>
        public bool IsUnbound => Chips.Count == 0 && !HasOverrideText && !IsCapturingNew;

        public bool CanAdd => IsEditable && Chips.Count < KeyMap.MaxChordsPerAction;

        /// <summary>Whether the keys differ from the defaults, which a button then restores.</summary>
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(ResetCommand))]
        public partial bool IsModified { get; set; }

        /// <summary>Whether the window is waiting for a key to add to this shortcut.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsUnbound))]
        public partial bool IsCapturingNew { get; set; }

        /// <summary>Which other shortcuts share a key with this one while they can be live together; null for none.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasConflict))]
        public partial string? ConflictNote { get; set; }

        public bool HasConflict => ConflictNote is not null;

        /// <summary>Whether the shortcut was just jumped to from the tree, which outlines it until something else is chosen.</summary>
        [ObservableProperty]
        public partial bool IsRevealed { get; set; }

        [RelayCommand]
        private void Add() => _owner.BeginCapture(this, null);

        [RelayCommand(CanExecute = nameof(IsModified))]
        private void Reset() => _owner.Reset(this);

        internal void Refresh(KeyMap map, string? conflictNote)
        {
            Chips.Clear();
            foreach (var chord in map.ChordsOf(Action.Id))
                Chips.Add(new KeyChipViewModel(this, chord, _owner));
            IsModified = map.IsCustomized(Action.Id);
            ConflictNote = conflictNote;
            OnPropertyChanged(nameof(IsUnbound));
            OnPropertyChanged(nameof(CanAdd));
        }
    }

    /// <summary>
    /// The keyboard shortcuts window: every shortcut by category with its keys, to change, add to, remove or reset, and to
    /// import and export as a layout file. Edits are made to a copy of the app's <see cref="KeyMap"/> and only reach it, and
    /// <c>keybinds.json</c>, when OK is chosen (<see cref="Apply"/>).
    /// </summary>
    /// <remarks>
    /// Changing a key waits for the next key press (<see cref="BeginCapture"/> then <see cref="CaptureAsync"/>, which the
    /// window calls from its key handler). A key another shortcut has, while the two can be live together, asks whether to
    /// move it or keep it on both; one a fixed shortcut has, or the system or a text box would take, is refused.
    /// </remarks>
    public sealed partial class KeybindsWindowViewModel : ViewModelBase
    {
        private readonly KeyMap _live;
        private readonly KeyMap _working;
        private readonly List<KeybindRowViewModel> _rows;
        private readonly Func<Task<string?>> _pickImport;
        private readonly Func<Task<string?>> _pickExport;
        private readonly IConfirmationService _confirmation;
        private readonly Func<bool> _save;

        private KeybindNodeViewModel _all = KeybindNodeViewModel.All(null);

        // The top-level category whose shortcuts the list shows, with those nested under it; null for every one. It follows
        // the category picked, and moves to a shortcut's category only when the shortcut jumped to isn't in the list.
        private string? _scope;

        private KeybindRowViewModel? _captureRow;
        private KeyChipViewModel? _captureChip;
        private bool _resolving;

        /// <param name="live">The app's keys, which OK changes.</param>
        /// <param name="pickImport">Asks the user for a layout file to read; null when they cancel.</param>
        /// <param name="pickExport">Asks the user where to write the layout; null when they cancel.</param>
        /// <param name="save">Writes <c>keybinds.json</c>, called after OK has changed <paramref name="live"/>; false when it couldn't.</param>
        public KeybindsWindowViewModel(
            KeyMap live,
            Func<Task<string?>> pickImport,
            Func<Task<string?>> pickExport,
            IConfirmationService confirmation,
            Func<bool> save)
        {
            ArgumentNullException.ThrowIfNull(live);
            _live = live;
            _working = live.Clone();
            _pickImport = pickImport;
            _pickExport = pickExport;
            _confirmation = confirmation;
            _save = save;
            _rows = [.. live.Catalog.Select(action => new KeybindRowViewModel(action, this))];
            Map = new KeyboardMapViewModel(this, _rows);
            RefreshAll();
            RebuildCategories();
        }

        /// <summary>The keyboard view, which shows the same keys the list does.</summary>
        public KeyboardMapViewModel Map { get; }

        /// <summary>The top-level categories; while searching, only the ones with a match. "All Shortcuts" comes first.</summary>
        [ObservableProperty]
        public partial IReadOnlyList<KeybindNodeViewModel> Categories { get; set; } = [];

        /// <summary>The category picked. It shows its shortcuts, with those nested under it; "All Shortcuts" (or none) shows every category.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(PageTitle), nameof(PageSubtitle), nameof(HasPageSubtitle))]
        public partial KeybindNodeViewModel? SelectedCategory { get; set; }

        /// <summary>Whether the window shows the keys of a keyboard rather than the list of shortcuts.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsListView), nameof(PageTitle), nameof(PageSubtitle), nameof(HasPageSubtitle))]
        public partial bool IsKeyboardView { get; set; }

        /// <summary>Whether the window shows the list; the other half of <see cref="IsKeyboardView"/>, for the toggle's other button.</summary>
        public bool IsListView
        {
            get => !IsKeyboardView;
            set => IsKeyboardView = !value;
        }

        /// <summary>The page header: the category picked, or the keyboard's.</summary>
        public string PageTitle => IsKeyboardView ? SharedStrings.KeybindsMapTitle : SelectedCategory?.Label ?? _all.Label;

        /// <summary>What the page holds: the nested categories of the one picked, or how to use the keyboard.</summary>
        public string PageSubtitle => IsKeyboardView ? SharedStrings.KeybindsMapSubtitle : SelectedCategory?.Subtitle ?? "";

        public bool HasPageSubtitle => PageSubtitle.Length > 0;

        /// <summary>Raised when the list should scroll to a shortcut, which it has been made to show and has outlined.</summary>
        public event EventHandler<KeybindRowViewModel>? RevealRequested;

        /// <summary>The shortcuts that match the search, in the selected category, by category.</summary>
        [ObservableProperty]
        public partial IReadOnlyList<KeybindGroupViewModel> Groups { get; set; } = [];

        [ObservableProperty]
        public partial string SearchText { get; set; } = "";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasNoMatches))]
        [NotifyPropertyChangedFor(nameof(NoMatchesText))]
        public partial bool IsSearching { get; set; }

        /// <summary>A line under the list: why a key was refused, what an import did, or a note on the key just set.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasStatus))]
        public partial string? StatusMessage { get; set; }

        public bool HasStatus => !string.IsNullOrEmpty(StatusMessage);

        public bool HasNoMatches => IsSearching && Groups.Count == 0;

        public string NoMatchesText => string.Format(CultureInfo.CurrentCulture, SharedStrings.KeybindsNoMatchesFormat, SearchText);

        /// <summary>Whether the window is waiting for a key press.</summary>
        public bool IsCapturing => _captureRow is not null;

        /// <summary>The shortcut the awaited key is for, whose scope says how the key is matched; null when none is awaited.</summary>
        public KeyAction? CaptureAction => _captureRow?.Action;

        /// <summary>The window's copy of the keys, which the rows show; the app's are untouched until <see cref="Apply"/>.</summary>
        internal KeyMap Working => _working;

        // Searching is of the list, so it takes the window back to it; the keyboard view has no search, so it clears one.
        partial void OnSearchTextChanged(string value)
        {
            IsSearching = !string.IsNullOrWhiteSpace(value);
            if (IsSearching)
                IsKeyboardView = false;
            RebuildCategories();
        }

        partial void OnIsKeyboardViewChanged(bool value)
        {
            CancelCapture();
            if (value && IsSearching)
                SearchText = "";
        }

        partial void OnSelectedCategoryChanged(KeybindNodeViewModel? value)
        {
            _scope = value is null or { IsAll: true } ? null : value.Path;
            ClearRevealed();
            RebuildGroups();
        }

        /// <summary>
        /// Jumps to a shortcut: makes sure the list shows it, outlines it until something else is chosen, and has the list
        /// scroll to it. The list keeps what it shows if the shortcut is in it already, so jumping about within a category,
        /// or from "All Shortcuts", doesn't narrow it. A search that doesn't match the shortcut is cleared, as the shortcut
        /// must be in the list to be scrolled to.
        /// </summary>
        public void Reveal(KeybindRowViewModel row)
        {
            if (IsSearching && !Searched().Contains(row))
                SearchText = "";

            if (_scope is not null && !InCategory(row, _scope))
                SelectedCategory = Categories.FirstOrDefault(c => !c.IsAll && InCategory(row, c.Path)) ?? _all;

            ClearRevealed();
            row.IsRevealed = true;
            RevealRequested?.Invoke(this, row);
        }

        /// <summary>Takes the window to the list, scrolled to a shortcut that was picked in the keyboard view.</summary>
        internal void ShowInList(KeybindRowViewModel row)
        {
            IsKeyboardView = false;
            Reveal(row);
        }

        /// <summary>
        /// Binds <paramref name="row"/> to <paramref name="chord"/>, a key picked in the keyboard view, as a key pressed for it in
        /// the list would be: a clash is asked about, and a key that can't be used is refused with a line saying why.
        /// </summary>
        internal async Task BindKeyAsync(KeybindRowViewModel row, KeyChord chord)
        {
            BeginCapture(row, null);
            if (!ReferenceEquals(_captureRow, row))
                return;

            try
            {
                await CaptureAsync(chord);
            }
            finally
            {
                // A key that was refused leaves the wait on, for the list's sake; there's no key to wait for here.
                if (ReferenceEquals(_captureRow, row))
                    CancelCapture();
            }
        }

        private void ClearRevealed()
        {
            foreach (var row in _rows)
                row.IsRevealed = false;
        }

        /// <summary>
        /// Gives the app's keys what was chosen here, and has them written to <c>keybinds.json</c>. When it can't be written the
        /// keys still apply, until the app closes, and the user is told.
        /// </summary>
        public async Task ApplyAsync()
        {
            CancelCapture();
            _live.CopyFrom(_working);
            if (!_save())
                await _confirmation.AlertAsync(SharedStrings.KeybindsSaveFailedTitle, SharedStrings.KeybindsSaveFailed);
        }

        /// <summary>Starts waiting for the key to replace <paramref name="chip"/>, or, with null, to add to <paramref name="row"/>.</summary>
        internal void BeginCapture(KeybindRowViewModel row, KeyChipViewModel? chip)
        {
            if (!row.IsEditable || _resolving)
                return;

            CancelCapture();
            _captureRow = row;
            _captureChip = chip;
            if (chip is not null)
                chip.IsCapturing = true;
            else
                row.IsCapturingNew = true;
            StatusMessage = null;
            OnPropertyChanged(nameof(IsCapturing));
        }

        /// <summary>Stops waiting for a key, leaving the shortcut as it was.</summary>
        public void CancelCapture()
        {
            if (_captureRow is null)
                return;

            if (_captureChip is not null)
                _captureChip.IsCapturing = false;
            _captureRow.IsCapturingNew = false;
            _captureRow = null;
            _captureChip = null;
            OnPropertyChanged(nameof(IsCapturing));
        }

        /// <summary>
        /// Takes <paramref name="chord"/>, the key just pressed, as the awaited one. A key that can't be used leaves the window
        /// waiting, with a line saying why; one that can ends the wait, once any clash is settled.
        /// </summary>
        public async Task CaptureAsync(KeyChord chord)
        {
            if (_captureRow is not { } row || _resolving)
                return;

            // The chip being replaced, as it was pressed on: the wait can end while a clash is asked about.
            var chip = _captureChip;
            var action = row.Action;
            var problem = KeyMap.Validate(action, chord);
            if (problem == ChordProblem.ModifierOnly)
                return;

            string key = chord.ToDisplayString();
            if (problem == ChordProblem.OsReserved)
            {
                StatusMessage = string.Format(CultureInfo.CurrentCulture, SharedStrings.KeybindsOsReservedFormat, key);
                return;
            }

            if (problem == ChordProblem.NeedsModifier)
            {
                StatusMessage = string.Format(CultureInfo.CurrentCulture, SharedStrings.KeybindsNeedsModifierFormat, key);
                return;
            }

            var own = _working.ChordsOf(action.Id);
            if (own.Contains(chord) && !(chip is { } replaced && replaced.Chord == chord))
            {
                StatusMessage = string.Format(CultureInfo.CurrentCulture, SharedStrings.KeybindsDuplicateFormat, key);
                return;
            }

            var conflicts = _working.FindConflicts(action.Id, chord);
            if (conflicts.FirstOrDefault(c => c.Locked) is { } fixedAction)
            {
                StatusMessage = string.Format(CultureInfo.CurrentCulture, SharedStrings.KeybindsLockedConflictFormat, key, fixedAction.Name);
                return;
            }

            if (conflicts.Count > 0)
            {
                _resolving = true;
                try
                {
                    string names = string.Join(", ", conflicts.Select(c => c.Name));
                    var choice = await _confirmation.ChooseAsync(
                        SharedStrings.KeybindsConflictTitle,
                        string.Format(CultureInfo.CurrentCulture, SharedStrings.KeybindsConflictFormat, key, names, action.Name),
                        SharedStrings.KeybindsMoveButton,
                        SharedStrings.KeybindsKeepBothButton,
                        "ArrowRightStroke");
                    if (choice == ConfirmChoice.Cancel)
                    {
                        CancelCapture();
                        return;
                    }

                    // Cancelled, removed or reset while the question was up: nothing is waiting for this key any more.
                    if (!ReferenceEquals(_captureRow, row) || !ReferenceEquals(_captureChip, chip))
                        return;

                    if (choice == ConfirmChoice.First)
                    {
                        string conflictKey = chord.ConflictKey;
                        foreach (var other in conflicts)
                            _working.Set(other.Id, _working.ChordsOf(other.Id).Where(c => c.ConflictKey != conflictKey).ToList());
                    }
                }
                finally
                {
                    _resolving = false;
                }
            }

            var chords = _working.ChordsOf(action.Id).ToList();
            int index = chip is null ? -1 : chords.IndexOf(chip.Chord);
            if (index >= 0)
                chords[index] = chord;
            else
                chords.Add(chord);

            CancelCapture();
            _working.Set(action.Id, chords);
            RefreshAll();

            // Ctrl and Alt together are what Alt Gr sends on many layouts, where such a key types a character.
            bool typed = chord.Mods.HasFlag(KeyMods.Ctrl) && chord.Mods.HasFlag(KeyMods.Alt) && key.Length > 0 && char.IsLetterOrDigit(key[^1]);
            StatusMessage = typed ? string.Format(CultureInfo.CurrentCulture, SharedStrings.KeybindsAltGrFormat, key) : null;
        }

        internal void Remove(KeybindRowViewModel row, KeyChipViewModel chip)
        {
            CancelCapture();
            _working.Set(row.Action.Id, _working.ChordsOf(row.Action.Id).Where(c => c != chip.Chord).ToList());
            StatusMessage = null;
            RefreshAll();
        }

        internal void Reset(KeybindRowViewModel row)
        {
            CancelCapture();
            _working.Reset(row.Action.Id);
            StatusMessage = null;
            RefreshAll();
        }

        [RelayCommand]
        private async Task ResetAllAsync()
        {
            CancelCapture();
            if (!await _confirmation.ConfirmAsync(
                    SharedStrings.KeybindsResetAllTitle, SharedStrings.KeybindsResetAllMessage, SharedStrings.KeybindsResetAllButton, "UndoStroke"))
                return;

            _working.ResetAll();
            StatusMessage = null;
            RefreshAll();
        }

        [RelayCommand]
        private async Task ImportAsync()
        {
            CancelCapture();
            if (await _pickImport() is not { } path)
                return;

            var read = KeybindsLayout.Read(path);
            if (read.Bindings is null)
            {
                await _confirmation.AlertAsync(SharedStrings.KeybindsImportTitle, FailureText(read.Failure));
                return;
            }

            var choice = await _confirmation.ChooseAsync(
                SharedStrings.KeybindsImportTitle, SharedStrings.KeybindsImportChoice, SharedStrings.KeybindsMergeButton, SharedStrings.KeybindsReplaceButton);
            if (choice == ConfirmChoice.Cancel)
                return;

            var applied = _working.ApplyLayout(read.Bindings, replace: choice == ConfirmChoice.Second);
            RefreshAll();
            StatusMessage = string.Format(CultureInfo.CurrentCulture, SharedStrings.KeybindsImportedFormat, applied.Applied, applied.Ignored);
        }

        [RelayCommand]
        private async Task ExportAsync()
        {
            CancelCapture();
            if (await _pickExport() is not { } path)
                return;

            if (!KeybindsLayout.Write(path, _working))
                await _confirmation.AlertAsync(SharedStrings.KeybindsExportTitle, SharedStrings.KeybindsExportFailed);
        }

        private static string FailureText(LayoutFailure failure) => failure switch
        {
            LayoutFailure.TooLarge => SharedStrings.KeybindsImportTooLarge,
            LayoutFailure.NotKeybinds => SharedStrings.KeybindsImportNotKeybinds,
            _ => SharedStrings.KeybindsImportUnreadable,
        };

        // Shows every row as the working keys have it, with which others share a key, and redraws the list.
        private void RefreshAll()
        {
            var shared = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var conflict in _working.AllConflicts())
            {
                AddNote(shared, conflict.First.Id, conflict.Second.Name);
                AddNote(shared, conflict.Second.Id, conflict.First.Name);
            }

            foreach (var row in _rows)
            {
                string? note = shared.TryGetValue(row.Action.Id, out var names)
                    ? string.Format(CultureInfo.CurrentCulture, SharedStrings.KeybindsAlsoUsedFormat, string.Join(", ", names.Distinct()))
                    : null;
                row.Refresh(_working, note);
            }

            Map.Refresh();
            RefreshCounts();
            RebuildGroups();
        }

        // How many shortcuts in each category differ from the defaults, which the list of categories shows.
        private void RefreshCounts()
        {
            foreach (var node in Categories)
                node.ModifiedCount = _rows.Count(row => row.IsModified && (node.IsAll || InCategory(row, node.Path)));
        }

        private static void AddNote(Dictionary<string, List<string>> notes, string id, string other)
        {
            if (!notes.TryGetValue(id, out var names))
                notes[id] = names = [];
            names.Add(other);
        }

        private List<KeybindRowViewModel> Searched()
        {
            string search = SearchText.Trim();
            return search.Length == 0 ? _rows : [.. _rows.Where(row => Matches(row, search))];
        }

        // Rebuilds the list of categories for the search, and picks "All Shortcuts" so every match shows until a category is.
        private void RebuildCategories()
        {
            var searched = Searched();
            string[] roots = [.. searched.Select(row => RootOf(row.Category)).Distinct()];

            // A new "All Shortcuts" entry each time, to carry the match count, so it's never the one already picked and the
            // list is rebuilt for the new matches.
            _all = KeybindNodeViewModel.All(IsSearching ? searched.Count : null);
            Categories =
            [
                _all,
                .. roots.Select(root => new KeybindNodeViewModel(root, root, SubtitleOf(searched, root), IsSearching ? searched.Count(row => InCategory(row, root)) : null)),
            ];
            RefreshCounts();
            SelectedCategory = _all;
        }

        private static string RootOf(string category) => category.Split(KeyAction.CategorySeparator)[0];

        // The categories nested directly in a top-level one that the search shows, in the order they were registered.
        private static string SubtitleOf(List<KeybindRowViewModel> searched, string root) =>
            string.Join(
                ", ",
                searched.Where(row => InCategory(row, root) && row.Category != root)
                    .Select(row => row.Category.Split(KeyAction.CategorySeparator)[1])
                    .Distinct());

        // A group's heading: its category's path, or under a top-level category (where its page header says that much) the rest of it.
        private string TitleOf(string category) =>
            _scope is not { } scope ? category
            : category == scope ? ""
            : category[(scope.Length + KeyAction.CategorySeparator.Length)..];

        private void RebuildGroups()
        {
            var shown = Searched();
            if (_scope is { } scope)
                shown = [.. shown.Where(row => InCategory(row, scope))];

            // Groups in the order their first shortcut was registered, each under its path as a heading.
            Groups =
            [
                .. shown.GroupBy(row => row.Category).Select(group => new KeybindGroupViewModel(TitleOf(group.Key), [.. group])),
            ];
            OnPropertyChanged(nameof(HasNoMatches));
        }

        // Whether the shortcut is in the category or one nested under it.
        private static bool InCategory(KeybindRowViewModel row, string path) =>
            row.Category == path || row.Category.StartsWith(path + KeyAction.CategorySeparator, StringComparison.Ordinal);

        private static bool Matches(KeybindRowViewModel row, string search) =>
            Contains(row.Name, search) || Contains(row.Category, search) || Contains(row.OverrideText, search)
            || row.Chips.Any(chip => Contains(chip.Chord.ToDisplayString(), search));

        private static bool Contains(string? text, string search) => text?.Contains(search, StringComparison.CurrentCultureIgnoreCase) == true;
    }
}
