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
        public string Title { get; } = title;

        public IReadOnlyList<KeybindRowViewModel> Rows { get; } = rows;
    }

    /// <summary>
    /// An entry in the keyboard shortcuts window's tree: a category, which shows its shortcuts; one shortcut, under its
    /// category, which jumps to it in the list; or the "All shortcuts" entry at the top.
    /// </summary>
    public sealed partial class KeybindNodeViewModel : ObservableObject
    {
        internal KeybindNodeViewModel(string label, string path, IReadOnlyList<KeybindNodeViewModel> children, bool isExpanded)
        {
            Label = label;
            Path = path;
            Children = children;
            IsExpanded = isExpanded;
        }

        private KeybindNodeViewModel(KeybindRowViewModel shortcut)
        {
            Label = shortcut.Name;
            Path = shortcut.Category;
            Children = [];
            Shortcut = shortcut;
        }

        /// <summary>The entry that shows every shortcut, so there's always a way back from a selected category.</summary>
        internal static KeybindNodeViewModel All() => new(SharedStrings.KeybindsAllCategories, "", [], isExpanded: false);

        /// <summary>The entry for one shortcut, in the tree under its category.</summary>
        internal static KeybindNodeViewModel ForShortcut(KeybindRowViewModel shortcut) => new(shortcut);

        public string Label { get; }

        /// <summary>
        /// The names from the root down, joined by <see cref="KeyAction.CategorySeparator"/>: the category's own, or for a
        /// shortcut the category it's in; empty for "All shortcuts".
        /// </summary>
        public string Path { get; }

        /// <summary>The shortcut this entry jumps to; null for a category or "All shortcuts".</summary>
        public KeybindRowViewModel? Shortcut { get; }

        public bool IsShortcut => Shortcut is not null;

        public bool IsAll => Path.Length == 0;

        public IReadOnlyList<KeybindNodeViewModel> Children { get; }

        [ObservableProperty]
        public partial bool IsExpanded { get; set; }
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

        private readonly KeybindNodeViewModel _all = KeybindNodeViewModel.All();

        // The category whose shortcuts the list shows, with those nested under it; null for every one. It follows a category
        // picked in the tree, and moves to a shortcut's category only when the shortcut jumped to isn't in the list.
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
            RefreshAll();
            RebuildTree();
        }

        /// <summary>The category tree; while searching, only the categories with a match.</summary>
        [ObservableProperty]
        public partial IReadOnlyList<KeybindNodeViewModel> Categories { get; set; } = [];

        /// <summary>
        /// The entry picked in the tree. A category shows its shortcuts, with those nested under it; "All shortcuts" (or none)
        /// shows every category; a shortcut jumps to it in the list (<see cref="RevealRequested"/>).
        /// </summary>
        [ObservableProperty]
        public partial KeybindNodeViewModel? SelectedCategory { get; set; }

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

        partial void OnSearchTextChanged(string value)
        {
            IsSearching = !string.IsNullOrWhiteSpace(value);
            RebuildTree();
        }

        partial void OnSelectedCategoryChanged(KeybindNodeViewModel? value)
        {
            if (value?.Shortcut is { } shortcut)
            {
                Reveal(shortcut);
                return;
            }

            // A category opens as it's picked, so what's in it is in view.
            if (value is { IsAll: false })
                value.IsExpanded = true;
            _scope = value is null or { IsAll: true } ? null : value.Path;
            ClearRevealed();
            RebuildGroups();
        }

        /// <summary>
        /// Jumps to a shortcut: makes sure the list shows it, outlines it until something else is chosen, and has the list
        /// scroll to it. The list keeps what it shows if the shortcut is in it already, so jumping about within a category,
        /// or from "All shortcuts", doesn't narrow it.
        /// </summary>
        public void Reveal(KeybindRowViewModel row)
        {
            if (_scope is not null && !InCategory(row, _scope))
            {
                _scope = row.Category;
                RebuildGroups();
            }

            ClearRevealed();
            row.IsRevealed = true;
            RevealRequested?.Invoke(this, row);
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

            RebuildGroups();
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

        // Rebuilds the tree for the search, and selects "All shortcuts" so every match shows until a category is picked.
        private void RebuildTree()
        {
            // Categories open while searching, so every match is in sight; otherwise they start closed, as a long list
            // of shortcuts would fill the tree.
            var items = Searched().Select(row => (Path: row.Category.Split(KeyAction.CategorySeparator), Row: row)).ToList();
            Categories = [_all, .. Nest(items, 0, "", expanded: IsSearching)];

            if (SelectedCategory == _all)
            {
                ClearRevealed();
                RebuildGroups();
            }
            else
            {
                SelectedCategory = _all;
            }
        }

        // The entries at one depth of the tree, in the order they were registered: the categories, each with its own entries
        // beneath, then the shortcuts that sit in this category itself.
        private static List<KeybindNodeViewModel> Nest(List<(string[] Path, KeybindRowViewModel Row)> items, int depth, string parent, bool expanded)
        {
            var nodes = new List<KeybindNodeViewModel>();
            foreach (var group in items.Where(item => item.Path.Length > depth).GroupBy(item => item.Path[depth]))
            {
                string path = parent.Length == 0 ? group.Key : parent + KeyAction.CategorySeparator + group.Key;
                nodes.Add(new KeybindNodeViewModel(group.Key, path, Nest([.. group], depth + 1, path, expanded), expanded));
            }

            nodes.AddRange(items.Where(item => item.Path.Length == depth).Select(item => KeybindNodeViewModel.ForShortcut(item.Row)));
            return nodes;
        }

        private void RebuildGroups()
        {
            var shown = Searched();
            if (_scope is { } scope)
                shown = [.. shown.Where(row => InCategory(row, scope))];

            // Groups in the order their first shortcut was registered, each under its whole path as a heading.
            Groups =
            [
                .. shown.GroupBy(row => row.Category).Select(group => new KeybindGroupViewModel(group.Key, [.. group])),
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
