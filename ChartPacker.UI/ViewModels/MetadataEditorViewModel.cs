using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Collections;
using ChartPacker.Core.Metadata;
using ChartPacker.Core.Validation;
using ChartPacker.Core.Validation.QuickFixes;
using ChartPacker.Resources;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChartPacker.UI.ViewModels
{
    /// <summary>Why a key that has no metadata row, such as a difficulty, fails validation.</summary>
    /// <param name="QuickFixDescription">What the quick fix does, when that's one thing.</param>
    /// <param name="QuickFixCommand">Applies the key's quick fixes from its right-click menu; disabled when it has none.</param>
    public sealed record MetadataIssueRow(string Key, string Message, string? QuickFixDescription, IRelayCommand QuickFixCommand)
    {
        /// <summary>The right-click menu's Quick Fix item, saying what it does when that's one thing.</summary>
        public string QuickFixHeader => QuickFixDescription is { } description
            ? string.Format(CultureInfo.CurrentCulture, SharedStrings.QuickFixMenuFormat, description)
            : SharedStrings.QuickFixMenu;
    }

    /// <summary>
    /// The metadata section of a song inspector, Chart Packer's and Chart Builder's: an icon per song.ini difficulty,
    /// a row per other key, each edited in place, the failures of keys without a row, and a row to add a key. What's
    /// shown and where edits go is <see cref="IMetadataEditorHost"/>'s, which calls <see cref="Refresh"/> after a change.
    /// </summary>
    public sealed partial class MetadataEditorViewModel : ObservableObject
    {
        private readonly IMetadataEditorHost _host;

        // The Quick Fix commands of keys without a row, and the keys some song shown has a quick fix for.
        private readonly Dictionary<string, IRelayCommand> _quickFixKeyCommands = new(StringComparer.OrdinalIgnoreCase);
        private HashSet<string> _keysWithQuickFix = new(StringComparer.OrdinalIgnoreCase);

        /// <param name="showDifficulties">Whether the difficulties are shown as icons above the rows; Chart Builder edits them with its parts.</param>
        /// <param name="actionsInContextMenu">Whether a row's Revert and Remove are on its right-click menu rather than buttons on it.</param>
        public MetadataEditorViewModel(IMetadataEditorHost host, bool showDifficulties = true, bool actionsInContextMenu = false)
        {
            _host = host;
            ShowDifficulties = showDifficulties;
            ActionsInContextMenu = actionsInContextMenu;
            Instruments = [.. DifficultySlots.All.Select(s => new InstrumentDifficultyViewModel(this, s))];
        }

        /// <summary>Whether the difficulties are shown as icons above the rows, which leave them out either way.</summary>
        public bool ShowDifficulties { get; }

        /// <summary>Whether a row's Revert and Remove are on its right-click menu rather than buttons on it.</summary>
        public bool ActionsInContextMenu { get; }

        public AvaloniaList<MetadataRowViewModel> Metadata { get; } = [];

        /// <summary>An icon per song.ini difficulty, above the metadata rows, which leave the difficulties out.</summary>
        public IReadOnlyList<InstrumentDifficultyViewModel> Instruments { get; }

        /// <summary>Validation failures of keys that have no row, such as a difficulty, shown under their icons.</summary>
        public AvaloniaList<MetadataIssueRow> HiddenIssues { get; } = [];

        /// <summary>Whether the icons of instruments that aren't charted and have no difficulty are shown too.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(InstrumentsToggleText))]
        public partial bool ShowAllInstruments { get; set; }

        /// <summary>Why there is no metadata table, e.g. the folder has no song.ini.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasMetadataNote))]
        public partial string? MetadataNote { get; set; }

        /// <summary>The new-row editor is open.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ShowAddRow))]
        public partial bool IsAdding { get; set; }

        [ObservableProperty]
        public partial string NewKey { get; set; } = "";

        [ObservableProperty]
        public partial string NewValue { get; set; } = "";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasNewRowError))]
        public partial string? NewRowError { get; set; }

        /// <summary>Known keys to offer for a new row: every one the songs don't already use.</summary>
        [ObservableProperty]
        public partial IReadOnlyList<KnownKey> AvailableKeys { get; set; } = [];

        /// <summary>The instruments' icons are shown: some song's metadata was read, and the editor shows them.</summary>
        public bool ShowInstruments => ShowDifficulties && _host.Items.Count > 0;

        /// <summary>Some icons are hidden until Show all.</summary>
        public bool HasHiddenInstruments => Instruments.Any(i => i.IsHidden);

        public string InstrumentsToggleText => ShowAllInstruments
            ? SharedStrings.InstrumentsShowFewer
            : string.Format(CultureInfo.CurrentCulture, SharedStrings.InstrumentsShowAllFormat, Instruments.Count(i => i.IsHidden));

        public bool HasHiddenIssues => HiddenIssues.Count > 0;

        /// <summary>How many keys, shown as rows or not, fail validation as drafted.</summary>
        public int FailingFieldCount => Metadata.Count(r => r.HasIssue) + HiddenIssues.Count;

        /// <summary>Some song's metadata, as drafted, fails validation.</summary>
        public bool HasValidationIssues => FailingFieldCount > 0;

        public bool HasMetadataNote => MetadataNote is not null;

        public bool HasNewRowError => NewRowError is not null;

        public string MetadataHeader => string.Format(CultureInfo.CurrentCulture, SharedStrings.MetadataHeaderFormat, Metadata.Count);

        public bool CanEdit => _host.CanEdit;

        public bool ShowAddRow => CanEdit && !IsAdding;

        /// <summary>Works out <c>song_length</c> from the songs' files; null where the host doesn't offer it.</summary>
        public IRelayCommand? AutoSetSongLengthCommand => _host.AutoSetSongLengthCommand;

        /// <summary>No song has a <c>song_length</c> row, so Auto Set is offered beside Add row instead.</summary>
        public bool ShowAutoSetLengthFallback => CanEdit && AutoSetSongLengthCommand is not null && !Metadata.Any(r => r.IsSongLength);

        /// <summary>A row is being edited, or a key added, which a change from elsewhere shouldn't land under.</summary>
        public bool IsEditingAny => IsAdding || Metadata.Any(r => r.IsEditing);

        /// <summary>Whether <paramref name="key"/> can be changed: the songs can be edited, and nothing else decides it.</summary>
        public bool CanEditKey(string key) => CanEdit && _host.CanEditKey(key);

        /// <summary>
        /// Brings the rows, icons, validation and known keys up to date with the host's songs, updating the existing row
        /// view models in place so that a row being clicked or edited isn't replaced under the pointer.
        /// </summary>
        public void Refresh()
        {
            var items = _host.Items;
            var byKey = Metadata.ToDictionary(r => r.Key, StringComparer.OrdinalIgnoreCase);
            var next = BulkMetadata.BuildRows(items, DifficultySlots.Contains)
                .Select(r => byKey.TryGetValue(r.Key, out var existing) ? existing.Update(r) : new MetadataRowViewModel(this, r))
                .ToList();

            for (int i = 0; i < next.Count; i++)
            {
                if (i < Metadata.Count && ReferenceEquals(Metadata[i], next[i]))
                    continue;

                int from = Metadata.IndexOf(next[i]);
                if (from >= 0)
                    Metadata.Move(from, i);
                else
                    Metadata.Insert(i, next[i]);
            }

            while (Metadata.Count > next.Count)
                Metadata.RemoveAt(Metadata.Count - 1);

            RefreshInstruments();
            MetadataNote = _host.LoadNote ?? (Metadata.Count == 0 && Instruments.All(i => i.IsAllUnset) ? SharedStrings.NoMetadata : null);
            RefreshAvailableKeys();
            RefreshValidation();
            OnPropertyChanged(nameof(MetadataHeader));
            OnPropertyChanged(nameof(ShowInstruments));
            OnPropertyChanged(nameof(ShowAutoSetLengthFallback));
        }

        /// <summary>
        /// Brings the instruments' icons up to date with the songs and, for one song, with what its chart has.
        /// </summary>
        public void RefreshInstruments()
        {
            var items = _host.Items;
            var parts = _host.IsBulk ? null : _host.ChartParts;
            foreach (var instrument in Instruments)
                instrument.Update(BulkMetadata.RowOf(items, instrument.Key), MetadataDifficulty.IsUnsetIn(items, instrument.Key), parts);
            OnPropertyChanged(nameof(HasHiddenInstruments));
            OnPropertyChanged(nameof(InstrumentsToggleText));
        }

        /// <summary>Empties the section, for songs about to be shown.</summary>
        public void Clear()
        {
            Metadata.Clear();
            HiddenIssues.Clear();
            MetadataNote = null;
            CancelAdd();
            OnPropertyChanged(nameof(HasHiddenIssues));
            OnPropertyChanged(nameof(FailingFieldCount));
            OnPropertyChanged(nameof(HasValidationIssues));
            OnPropertyChanged(nameof(MetadataHeader));
            OnPropertyChanged(nameof(ShowInstruments));
        }

        /// <summary>Re-evaluates what can be done after the host's editability changed.</summary>
        public void RefreshCommands()
        {
            OnPropertyChanged(nameof(CanEdit));
            OnPropertyChanged(nameof(ShowAddRow));
            OnPropertyChanged(nameof(ShowAutoSetLengthFallback));
            BeginAddCommand.NotifyCanExecuteChanged();
            foreach (var row in Metadata)
                row.RefreshCommands();
            foreach (var instrument in Instruments)
                instrument.RefreshCommands();
            RefreshQuickFixKeyCommands();
        }

        /// <summary>Ends every row's edit and the new row, dropping text that wasn't committed.</summary>
        public void CancelEdits()
        {
            CancelOtherEdits(null);
            CancelAdd();
        }

        /// <summary>Ends editing on every row but <paramref name="row"/>, dropping text that wasn't committed.</summary>
        public void CancelOtherEdits(MetadataRowViewModel? row)
        {
            foreach (var other in Metadata)
            {
                if (other != row && other.IsEditing)
                    other.CancelCommand.Execute(null);
            }
        }

        public void SetValue(string key, string value) => _host.SetValue(key, value);

        public void RemoveKey(string key) => _host.RemoveKey(key);

        public void RevertKey(string key) => _host.RevertKey(key);

        public void UnsetDifficulty(string key) => _host.UnsetDifficulty(key);

        public void ApplyQuickFixes(string key) => _host.ApplyQuickFixes(key);

        partial void OnShowAllInstrumentsChanged(bool value)
        {
            foreach (var instrument in Instruments)
                instrument.RefreshShown();
        }

        [RelayCommand]
        private void ToggleShowAllInstruments() => ShowAllInstruments = !ShowAllInstruments;

        [RelayCommand(CanExecute = nameof(CanEdit))]
        private void BeginAdd()
        {
            NewKey = "";
            NewValue = "";
            NewRowError = null;
            RefreshAvailableKeys();
            IsAdding = true;
        }

        [RelayCommand]
        private void CommitAdd()
        {
            if (!IsAdding)
                return;

            string key = NewKey.Trim();
            string value = NewValue.Trim();

            // A hidden unset difficulty isn't a row, so adding it edits the stored value.
            string? error = MetadataEditRules.ValidateKey(key)
                ?? (Metadata.Any(r => !r.IsRemoved && r.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) ? SharedStrings.KeyExists : null)
                ?? MetadataEditRules.ValidateValue(value);
            if (error is not null)
            {
                NewRowError = error;
                return;
            }

            CancelAdd();
            SetValue(key, value);
        }

        [RelayCommand]
        private void CancelAdd()
        {
            IsAdding = false;
            NewRowError = null;
        }

        /// <summary>
        /// The Quick Fix command of a key without a row, kept per key so a listed failure keeps its command, and the
        /// list its rows, while the key's validation stays the same.
        /// </summary>
        private IRelayCommand QuickFixCommandFor(string key)
        {
            if (!_quickFixKeyCommands.TryGetValue(key, out var command))
            {
                command = new RelayCommand(() => ApplyQuickFixes(key), () => CanEdit && _keysWithQuickFix.Contains(key));
                _quickFixKeyCommands[key] = command;
            }

            return command;
        }

        private void RefreshQuickFixKeyCommands()
        {
            foreach (var command in _quickFixKeyCommands.Values)
                command.NotifyCanExecuteChanged();
        }

        /// <summary>
        /// Checks each song's metadata as drafted, so what's marked is what saving would write: each row gets its key's
        /// failures and hints, and the failures of keys without a row are listed apart.
        /// </summary>
        private void RefreshValidation()
        {
            // Per key, every failing finding with the song it's in, the first hint, and what its quick fixes do.
            var items = _host.Items;
            var failing = new Dictionary<string, List<(int Song, Finding Finding)>>(StringComparer.OrdinalIgnoreCase);
            var hints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var fixes = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                var pairs = item.Draft.ApplyTo(item.Original);
                foreach (var finding in MetadataValidator.Validate(pairs, item.RemainingDuplicateKeys()))
                {
                    string key = finding.Key ?? "";
                    if (QuickFixRegistry.Propose(finding, pairs) is { } fix)
                    {
                        if (!fixes.TryGetValue(key, out var descriptions))
                            fixes[key] = descriptions = new HashSet<string>(StringComparer.Ordinal);
                        descriptions.Add(fix.Description);
                    }

                    if (MetadataValidator.IsFailing(finding))
                    {
                        if (!failing.TryGetValue(key, out var list))
                            failing[key] = list = [];
                        list.Add((i, finding));
                    }
                    else
                    {
                        hints.TryAdd(key, finding.Message);
                    }
                }
            }

            _keysWithQuickFix = new HashSet<string>(fixes.Keys, StringComparer.OrdinalIgnoreCase);

            // A key's fixes are named when they're one; several, e.g. different values across songs, are just "Quick Fix".
            (bool Available, string? Description) QuickFixOf(string key) => fixes.TryGetValue(key, out var descriptions)
                ? (true, descriptions.Count == 1 ? descriptions.First() : null)
                : (false, null);

            foreach (var row in Metadata)
                row.SetValidation(DescribeIssue(failing.GetValueOrDefault(row.Key), items.Count), hints.GetValueOrDefault(row.Key), QuickFixOf(row.Key));

            var rowKeys = Metadata.Select(r => r.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var hidden = failing
                .Where(f => !rowKeys.Contains(f.Key))
                .Select(f => new MetadataIssueRow(f.Key, DescribeIssue(f.Value, items.Count)!, QuickFixOf(f.Key).Description, QuickFixCommandFor(f.Key)))
                .ToList();
            if (!hidden.SequenceEqual(HiddenIssues))
            {
                HiddenIssues.Clear();
                HiddenIssues.AddRange(hidden);
            }

            OnPropertyChanged(nameof(HasHiddenIssues));
            OnPropertyChanged(nameof(FailingFieldCount));
            OnPropertyChanged(nameof(HasValidationIssues));
            RefreshQuickFixKeyCommands();
        }

        /// <summary>A key's failures as one message: each distinct one on its own line, and for several songs, how many fail.</summary>
        private string? DescribeIssue(List<(int Song, Finding Finding)>? failures, int songCount)
        {
            if (failures is null or [])
                return null;

            string message = string.Join(Environment.NewLine, failures.Select(f => f.Finding.Message).Distinct(StringComparer.Ordinal));
            int songs = failures.Select(f => f.Song).Distinct().Count();
            return _host.IsBulk ? string.Format(CultureInfo.CurrentCulture, SharedStrings.IssueSongCountFormat, message, songs, songCount) : message;
        }

        /// <summary>
        /// Offers the known keys that aren't a row yet. A removed row's key is offered again, since adding it back is
        /// allowed. The difficulties are left out, as their icons set them. Apart from that, this is the same test
        /// <see cref="CommitAdd"/> uses to reject a key.
        /// </summary>
        private void RefreshAvailableKeys()
        {
            var used = Metadata.Where(r => !r.IsRemoved).Select(r => r.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var available = KnownKeys.All.Where(k => !used.Contains(k.Key) && !DifficultySlots.Contains(k.Key)).ToList();
            if (!available.SequenceEqual(AvailableKeys))
                AvailableKeys = available;
        }
    }
}
