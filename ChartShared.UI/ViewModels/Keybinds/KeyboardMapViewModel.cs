using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using ChartShared.Core.Keybinds;
using ChartShared.UI.Resources;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChartShared.UI.ViewModels.Keybinds
{
    /// <summary>One cap of the keyboard view: a key, what is bound to it in the modifier layer shown, and whether it's picked.</summary>
    public sealed partial class KeycapViewModel : ObservableObject
    {
        private readonly KeyboardMapViewModel _owner;

        internal KeycapViewModel(KeyboardKey key, bool canBind, KeyboardMapViewModel owner)
        {
            Key = key;
            CanBind = canBind;
            _owner = owner;
        }

        public KeyboardKey Key { get; }

        /// <summary>Whether the key is on the keyboard drawn, so a shortcut can be bound to it here, rather than only shown.</summary>
        public bool CanBind { get; }

        public string Caption => Key.Caption;

        public bool IsGap => Key.IsGap;

        public bool IsKey => !Key.IsGap;

        /// <summary>The width in pixels. Every cap and gap is one less than its share of the row, which the row's spacing makes up.</summary>
        public double Width => (Key.Width * KeyboardMapViewModel.Pitch) - KeyboardMapViewModel.Spacing;

        /// <summary>The shortcuts that have this key, with the modifiers shown.</summary>
        [ObservableProperty]
        public partial IReadOnlyList<KeybindRowViewModel> Actions { get; set; } = [];

        [ObservableProperty]
        public partial bool IsBound { get; set; }

        /// <summary>Whether shortcuts that can be live together share the key.</summary>
        [ObservableProperty]
        public partial bool IsClashing { get; set; }

        /// <summary>Whether the key has nothing in the layer shown but has shortcuts in others, so it isn't taken for free.</summary>
        [ObservableProperty]
        public partial bool IsBoundElsewhere { get; set; }

        /// <summary>What the key has in the other layers, for its tooltip and the panel of the key picked; null when nothing.</summary>
        [ObservableProperty]
        public partial string? OtherLayersText { get; set; }

        /// <summary>Whether every shortcut on the key is fixed.</summary>
        [ObservableProperty]
        public partial bool IsFixed { get; set; }

        [ObservableProperty]
        public partial bool IsSelected { get; set; }

        /// <summary>The names of what's bound, for the cap's tooltip; null when nothing is.</summary>
        [ObservableProperty]
        public partial string? ToolTipText { get; set; }

        [RelayCommand]
        private void Select() => _owner.Select(this);

        internal void Update(IReadOnlyList<KeybindRowViewModel> actions, IReadOnlyList<(KeybindRowViewModel Row, KeyChord Chord)> elsewhere, bool isSelected)
        {
            Actions = actions;
            IsBound = actions.Count > 0;
            IsBoundElsewhere = actions.Count == 0 && elsewhere.Count > 0;
            OtherLayersText = elsewhere.Count == 0
                ? null
                : string.Format(CultureInfo.CurrentCulture, SharedStrings.KeybindsMapElsewhereFormat, KeyboardMapViewModel.Describe(elsewhere));
            IsFixed = actions.Count > 0 && actions.All(a => a.IsLocked);
            IsClashing = actions.Any(a => actions.Any(b => !ReferenceEquals(a, b) && a.Action.Scope.Overlaps(b.Action.Scope)));
            IsSelected = isSelected;
            string here = string.Join(", ", actions.Select(a => a.Name));
            ToolTipText = here.Length == 0 ? OtherLayersText : OtherLayersText is null ? here : here + "\n" + OtherLayersText;
        }
    }

    /// <summary>One row of the keyboard drawn.</summary>
    public sealed class KeycapRowViewModel(IReadOnlyList<KeycapViewModel> keys)
    {
        public IReadOnlyList<KeycapViewModel> Keys { get; } = keys;
    }

    /// <summary>One shortcut on the picked key: its name, a button to take the key off it, and one to find it in the list.</summary>
    public sealed partial class MapBindingViewModel : ObservableObject
    {
        private readonly KeyboardMapViewModel _owner;
        private readonly KeyChipViewModel _chip;

        internal MapBindingViewModel(KeybindRowViewModel row, KeyChipViewModel chip, KeyboardMapViewModel owner)
        {
            Row = row;
            _chip = chip;
            _owner = owner;
        }

        public KeybindRowViewModel Row { get; }

        public string Name => Row.Name;

        public string Category => Row.Category;

        public bool IsLocked => Row.IsLocked;

        public bool CanRemove => Row.IsEditable;

        [RelayCommand(CanExecute = nameof(CanRemove))]
        private void Remove() => _owner.Remove(Row, _chip);

        [RelayCommand]
        private void ShowInList() => _owner.ShowInList(Row);
    }

    /// <summary>
    /// The keyboard shortcuts window's keyboard view: the keys of a keyboard, tinted by what has them in one layer of
    /// modifiers (<see cref="Ctrl"/>, <see cref="Shift"/>, <see cref="Alt"/>), and for the key picked, what is on it. A shortcut
    /// can be bound to the key from here, which goes the way a key awaited in the list does (clashes asked about, a key that
    /// can't be used refused), so the two views change the same copy of the keys.
    /// </summary>
    public sealed partial class KeyboardMapViewModel : ObservableObject
    {
        /// <summary>How far apart caps are, in pixels, from the start of one to the start of the next.</summary>
        internal const double Pitch = 34;

        /// <summary>The gap between caps, in pixels.</summary>
        internal const double Spacing = 4;

        private readonly KeybindsWindowViewModel _owner;
        private readonly IReadOnlyList<KeybindRowViewModel> _rows;
        private readonly Dictionary<string, KeycapViewModel> _otherCaps = new(StringComparer.OrdinalIgnoreCase);
        private string? _selectedName;
        private bool _assigning;

        internal KeyboardMapViewModel(KeybindsWindowViewModel owner, IReadOnlyList<KeybindRowViewModel> rows)
        {
            _owner = owner;
            _rows = rows;
            Rows = [.. KeyboardLayout.Rows.Select(row => new KeycapRowViewModel([.. row.Select(key => new KeycapViewModel(key, canBind: true, this))]))];
        }

        /// <summary>The caps, by row of the keyboard.</summary>
        public IReadOnlyList<KeycapRowViewModel> Rows { get; }

        /// <summary>Caps for keys the keyboard drawn hasn't, such as a key of the number pad, where a shortcut has one in this layer.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasOtherKeys))]
        public partial IReadOnlyList<KeycapViewModel> OtherKeys { get; set; } = [];

        public bool HasOtherKeys => OtherKeys.Count > 0;

        [ObservableProperty]
        public partial bool Ctrl { get; set; }

        [ObservableProperty]
        public partial bool Shift { get; set; }

        [ObservableProperty]
        public partial bool Alt { get; set; }

        /// <summary>The key whose shortcuts are listed, or null when none is picked.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasSelection), nameof(IsUnboundSelection), nameof(SelectedChordText), nameof(CanAssign), nameof(UnboundText), nameof(AssignText), nameof(ElsewhereText), nameof(HasElsewhere))]
        public partial KeycapViewModel? SelectedKey { get; set; }

        /// <summary>The shortcuts on the picked key.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsUnboundSelection))]
        public partial IReadOnlyList<MapBindingViewModel> Bindings { get; set; } = [];

        /// <summary>The shortcuts that could be bound to the picked key: ones that can change and have room for another key.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanAssign))]
        public partial IReadOnlyList<KeybindRowViewModel> AssignChoices { get; set; } = [];

        /// <summary>What's typed to narrow <see cref="AssignChoices"/>, by name and category.</summary>
        [ObservableProperty]
        public partial string AssignSearch { get; set; } = "";

        /// <summary>The choices that match <see cref="AssignSearch"/>, which the list shows.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasNoAssignMatches))]
        public partial IReadOnlyList<KeybindRowViewModel> FilteredAssignChoices { get; set; } = [];

        public bool HasNoAssignMatches => FilteredAssignChoices.Count == 0;

        /// <summary>Set to a shortcut to bind it to the picked key; it clears itself, ready for the next.</summary>
        [ObservableProperty]
        public partial KeybindRowViewModel? AssignTo { get; set; }

        public bool HasSelection => SelectedKey is not null;

        public bool IsUnboundSelection => SelectedKey is not null && Bindings.Count == 0;

        public bool CanAssign => SelectedKey is { CanBind: true } && AssignChoices.Count > 0;

        /// <summary>What the picked key has with other modifiers than the layer shown; null when nothing.</summary>
        public string? ElsewhereText => SelectedKey?.OtherLayersText;

        public bool HasElsewhere => ElsewhereText is not null;

        /// <summary>The picked key with the modifiers shown, as a shortcut to it is labelled (<c>Ctrl+Shift+S</c>).</summary>
        public string SelectedChordText => SelectedKey is { } cap ? ChordOf(cap.Key.Logical.Length > 0 ? cap.Key.Logical : cap.Key.Name).ToDisplayString() : "";

        public string UnboundText => string.Format(CultureInfo.CurrentCulture, SharedStrings.KeybindsMapUnboundFormat, SelectedChordText);

        public string AssignText => string.Format(CultureInfo.CurrentCulture, SharedStrings.KeybindsMapAssignFormat, SelectedChordText);

        private KeyMods Layer => (Ctrl ? KeyMods.Ctrl : 0) | (Shift ? KeyMods.Shift : 0) | (Alt ? KeyMods.Alt : 0);

        private KeyChord ChordOf(string token) => new(token, Layer);

        partial void OnCtrlChanged(bool value) => Refresh();

        partial void OnShiftChanged(bool value) => Refresh();

        partial void OnAltChanged(bool value) => Refresh();

        partial void OnAssignChoicesChanged(IReadOnlyList<KeybindRowViewModel> value) => FilterAssignChoices();

        partial void OnAssignSearchChanged(string value) => FilterAssignChoices();

        // Every word typed has to be in the name or the category, in any order, ignoring case.
        private void FilterAssignChoices()
        {
            string[] terms = AssignSearch.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            FilteredAssignChoices = terms.Length == 0
                ? AssignChoices
                : [.. AssignChoices.Where(row => terms.All(term =>
                    row.Name.Contains(term, StringComparison.CurrentCultureIgnoreCase)
                    || row.Category.Contains(term, StringComparison.CurrentCultureIgnoreCase)))];
        }

        partial void OnAssignToChanged(KeybindRowViewModel? value)
        {
            if (value is not null && !_assigning)
                _ = AssignAsync(value);
        }

        internal void Select(KeycapViewModel cap)
        {
            _selectedName = cap.Key.Name;
            Refresh();
        }

        internal void Remove(KeybindRowViewModel row, KeyChipViewModel chip) => _owner.Remove(row, chip);

        internal void ShowInList(KeybindRowViewModel row) => _owner.ShowInList(row);

        // Runs without anyone awaiting it, so a failure is shown here: nothing else would see it.
        private async Task AssignAsync(KeybindRowViewModel row)
        {
            _assigning = true;
            try
            {
                if (SelectedKey is { CanBind: true } cap)
                    await _owner.BindKeyAsync(row, ChordOf(cap.Key.TokenFor(row.Action.Scope.Match)));
            }
            catch (Exception)
            {
                _owner.StatusMessage = SharedStrings.KeybindsMapAssignFailed;
            }
            finally
            {
                AssignTo = null;
                AssignSearch = "";
                _assigning = false;
            }
        }

        /// <summary>Shows what the keys are now: after a shortcut's keys change, or another layer is chosen.</summary>
        internal void Refresh()
        {
            var layer = Layer;

            // The chords in this layer, by the key they're on, so a cap is looked up rather than every shortcut searched for each;
            // and those with other modifiers, so a key that looks free here can say it isn't.
            var onKey = new Dictionary<string, List<KeybindRowViewModel>>(StringComparer.OrdinalIgnoreCase);
            var elsewhere = new Dictionary<string, List<(KeybindRowViewModel Row, KeyChord Chord)>>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in _rows)
            {
                foreach (var chip in row.Chips)
                {
                    string name = KeyNames.Display(chip.Chord.Key);
                    if (chip.Chord.Mods != layer)
                    {
                        if (!elsewhere.TryGetValue(name, out var others))
                            elsewhere[name] = others = [];
                        others.Add((row, chip.Chord));
                        continue;
                    }

                    if (!onKey.TryGetValue(name, out var list))
                        onKey[name] = list = [];
                    if (!list.Contains(row))
                        list.Add(row);
                }
            }

            IReadOnlyList<KeybindRowViewModel> ActionsOn(string name) => onKey.TryGetValue(name, out var list) ? list : [];

            IReadOnlyList<(KeybindRowViewModel Row, KeyChord Chord)> ElsewhereOn(string name) => elsewhere.TryGetValue(name, out var list) ? list : [];

            var caps = new List<KeycapViewModel>();
            foreach (var cap in Rows.SelectMany(row => row.Keys).Where(cap => cap.IsKey))
            {
                cap.Update(ActionsOn(cap.Key.Name), ElsewhereOn(cap.Key.Name), string.Equals(cap.Key.Name, _selectedName, StringComparison.OrdinalIgnoreCase));
                caps.Add(cap);
            }

            // The caps of keys not on the keyboard drawn are kept by name, so a refresh doesn't rebuild the ones still shown.
            var extra = new List<KeycapViewModel>();
            foreach (string name in onKey.Keys.Where(name => !KeyboardLayout.Contains(name)))
            {
                if (!_otherCaps.TryGetValue(name, out var other))
                    _otherCaps[name] = other = new KeycapViewModel(new KeyboardKey(name, name, name, name, name.Length <= 3 ? 1.5 : 2.5), canBind: false, this);
                extra.Add(other);
            }

            if (!OtherKeys.SequenceEqual(extra))
                OtherKeys = extra;
            foreach (var cap in extra)
            {
                cap.Update(ActionsOn(cap.Key.Name), ElsewhereOn(cap.Key.Name), string.Equals(cap.Key.Name, _selectedName, StringComparison.OrdinalIgnoreCase));
                caps.Add(cap);
            }

            // A key that has gone from the view (its last shortcut taken off) is no longer picked, so adding one back later
            // doesn't pick it again.
            SelectedKey = caps.FirstOrDefault(cap => cap.IsSelected);
            if (SelectedKey is null)
                _selectedName = null;
            Bindings =
            [
                .. SelectedKey?.Actions
                    .Select(row => (Row: row, Chip: row.Chips.FirstOrDefault(chip => chip.Chord.Mods == layer && string.Equals(KeyNames.Display(chip.Chord.Key), SelectedKey.Key.Name, StringComparison.OrdinalIgnoreCase))))
                    .Where(pair => pair.Chip is not null)
                    .Select(pair => new MapBindingViewModel(pair.Row, pair.Chip!, this))
                    ?? [],
            ];
            AssignChoices = SelectedKey is { CanBind: true } picked
                ? [.. _rows.Where(row => row.IsEditable && row.CanAdd && !picked.Actions.Contains(row))]
                : [];

            OnPropertyChanged(nameof(SelectedChordText));
            OnPropertyChanged(nameof(UnboundText));
            OnPropertyChanged(nameof(AssignText));
            OnPropertyChanged(nameof(ElsewhereText));
            OnPropertyChanged(nameof(HasElsewhere));
        }

        /// <summary>The chords as a list, each a key with its modifiers and the shortcut's name (<c>Ctrl+S Save</c>).</summary>
        internal static string Describe(IEnumerable<(KeybindRowViewModel Row, KeyChord Chord)> entries) =>
            string.Join(", ", entries.Select(entry => $"{entry.Chord.ToDisplayString()} {entry.Row.Name}"));
    }
}
