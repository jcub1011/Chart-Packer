using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ChartShared.Core.Charts;
using ChartShared.Core.Library;
using ChartShared.Core.Metadata;
using ChartShared.UI.Resources;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChartShared.UI.ViewModels
{
    /// <summary>A charted difficulty's letter under an instrument's icon, coloured by <see cref="Difficulty"/>.</summary>
    public sealed record ChartedDifficultyLetter(ChartDifficulty Difficulty, string Text);

    /// <summary>A line of an instrument's note counts: a difficulty or harmony line, and how many notes it has.</summary>
    public sealed record NoteCountRow(string Label, string Count);

    /// <summary>One of the difficulties an instrument's flyout offers, coloured by <see cref="Tier"/>.</summary>
    /// <param name="Tier">The tier it sets, or <see langword="null"/> for unset.</param>
    public sealed partial class DifficultyPresetViewModel(int? tier, string text, IRelayCommand command) : ObservableObject
    {
        public int? Tier { get; } = tier;

        public string Text { get; } = text;

        public IRelayCommand Command { get; } = command;

        /// <summary>It's the difficulty every song shown has.</summary>
        [ObservableProperty]
        public partial bool IsCurrent { get; set; }
    }

    /// <summary>
    /// An instrument's icon above the metadata rows: its song.ini difficulty, merged across the songs shown, as a badge;
    /// its name and the difficulties its chart has notes for as a caption; and a flyout that sets the difficulty through
    /// <see cref="MetadataEditorViewModel"/>.
    /// </summary>
    public sealed partial class InstrumentDifficultyViewModel : ObservableObject, IDifficultyPicker
    {
        /// <summary>The tiers offered as buttons; any other is typed.</summary>
        private const int MaxPresetTier = 6;

        private readonly MetadataEditorViewModel _owner;

        public InstrumentDifficultyViewModel(MetadataEditorViewModel owner, DifficultySlot slot)
        {
            _owner = owner;
            Slot = slot;
            Name = ChartLabels.SlotName(slot);
            Tag = ChartLabels.ModifierTag(slot.Modifier);
            Presets =
            [
                new DifficultyPresetViewModel(null, SharedStrings.DifficultyUnsetBadge, UnsetCommand),
                .. Enumerable.Range(0, MaxPresetTier + 1).Select(t => new DifficultyPresetViewModel(t, t.ToString(CultureInfo.CurrentCulture), SetCommand)),
            ];
        }

        public DifficultySlot Slot { get; }

        public string Key => Slot.Key;

        public string Name { get; }

        public DifficultyGlyph Glyph => Slot.Glyph;

        /// <summary>The variant's tag on the icon, such as <c>PRO</c>, or <see langword="null"/> for none.</summary>
        public string? Tag { get; }

        public bool HasTag => Tag is not null;

        /// <summary>What the key does, from the known keys.</summary>
        public string? KeyDescription => KnownKeys.TryGet(Key, out var known) ? known.Description : null;

        public IReadOnlyList<DifficultyPresetViewModel> Presets { get; }

        /// <summary>The merged row for the key, or <see langword="null"/> when no song shown has it.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsDistinct), nameof(Tier), nameof(BadgeText), nameof(ValueNote), nameof(HasValueNote), nameof(IsEdited), nameof(AccessibleName))]
        [NotifyCanExecuteChangedFor(nameof(RevertCommand))]
        public partial BulkMetadataRow? Row { get; private set; }

        /// <summary>No song shown will have the difficulty set: each lacks it or holds a negative one.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsDistinct), nameof(BadgeText), nameof(ValueNote), nameof(HasValueNote), nameof(AccessibleName))]
        public partial bool IsAllUnset { get; private set; } = true;

        /// <summary>Whether the chart has notes for the part; <see langword="true"/> when that isn't known.</summary>
        [ObservableProperty]
        public partial bool IsCharted { get; private set; } = true;

        /// <summary>The charted difficulties' letters; none when they aren't known, or the part has none.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasLetters))]
        public partial IReadOnlyList<ChartedDifficultyLetter> Letters { get; private set; } = [];

        /// <summary>Tucked behind Show all: not charted, with no difficulty set, and nothing pending.</summary>
        [ObservableProperty]
        public partial bool IsHidden { get; private set; }

        /// <summary>Whether the icon is shown: it isn't hidden, or every icon is being shown.</summary>
        [ObservableProperty]
        public partial bool IsShown { get; private set; } = true;

        /// <summary>The number box's value, for a tier past the buttons.</summary>
        [ObservableProperty]
        public partial decimal? OtherValue { get; set; }

        /// <summary>The part's notes on each difficulty, hardest first, or harmony line; none when they aren't known.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasNoteCounts))]
        public partial IReadOnlyList<NoteCountRow> NoteCounts { get; private set; } = [];

        public bool HasLetters => Letters.Count > 0;

        public bool HasNoteCounts => NoteCounts.Count > 0;

        /// <summary>The songs shown don't all share one value, and not all of them leave it unset.</summary>
        public bool IsDistinct => Row is { IsDistinct: true } && !IsAllUnset;

        /// <summary>The tier every song shown has, or <see langword="null"/> when it's unset, not a number, or differs.</summary>
        public int? Tier => Row is { Value: { } value } ? MetadataDifficulty.TierOf(value) : null;

        public string BadgeText => IsDistinct ? SharedStrings.DifficultyDistinctBadge
            : Tier is { } tier ? tier.ToString(CultureInfo.CurrentCulture)
            : SharedStrings.DifficultyUnsetBadge;

        /// <summary>A note on the value for the tooltip: it differs, or what's stored isn't a difficulty.</summary>
        public string? ValueNote =>
            IsDistinct ? SharedStrings.DifficultyDistinctToolTip
            : IsAllUnset ? SharedStrings.DifficultyUnsetToolTip
            : Row is { Value: { } value } && Tier is null ? string.Format(CultureInfo.CurrentCulture, SharedStrings.DifficultyStoredValueFormat, value)
            : null;

        public bool HasValueNote => ValueNote is not null;

        /// <summary>A song shown has a pending change to the difficulty.</summary>
        public bool IsEdited => Row is { State: not MetadataRowState.Unchanged };

        public bool CanEdit => _owner.CanEdit && _owner.CanEditKey(Key);

        public string AccessibleName => string.Format(CultureInfo.CurrentCulture, SharedStrings.InstrumentAccessibleNameFormat, Name, BadgeText);

        /// <summary>
        /// Takes the merged state after the drafts or the chart changed. <paramref name="parts"/> is
        /// <see langword="null"/> when what the chart has isn't known, as for several songs.
        /// </summary>
        public void Update(BulkMetadataRow? row, bool isAllUnset, IReadOnlyDictionary<string, ChartPart>? parts)
        {
            Row = row;
            IsAllUnset = isAllUnset;
            if (parts is null)
            {
                IsCharted = true;
                Letters = [];
                NoteCounts = [];
            }
            else
            {
                ChartPart? read = null;
                bool charted = Slot.PartId is { } part ? parts.TryGetValue(part, out read) : parts.Count > 0;
                IsCharted = charted;
                var letters = (read?.Difficulties ?? LibraryDifficulties.None).ToDifficulties()
                    .Select(d => new ChartedDifficultyLetter(d, ChartLabels.DifficultyLetter(d)))
                    .ToList();
                if (!letters.SequenceEqual(Letters))
                    Letters = letters;

                // Hardest first; harmony lines in order.
                var counts = (read?.NoteCounts ?? [])
                    .OrderByDescending(c => c.Difficulty)
                    .ThenBy(c => c.Line)
                    .Select(c => new NoteCountRow(ChartLabels.NoteCountLabel(c), ChartLabels.NoteCountText(c.Count)))
                    .ToList();
                if (!counts.SequenceEqual(NoteCounts))
                    NoteCounts = counts;
            }

            // Without the chart's parts, as for several songs, only the value counts.
            IsHidden = IsAllUnset && !IsEdited && (parts is null || !IsCharted);
            IsShown = !IsHidden || _owner.ShowAllInstruments;
            OtherValue = Tier;
            foreach (var preset in Presets)
                preset.IsCurrent = preset.Tier is { } tier ? tier == Tier : IsAllUnset;
        }

        /// <summary>Shows or hides the icon after Show all was toggled.</summary>
        public void RefreshShown() => IsShown = !IsHidden || _owner.ShowAllInstruments;

        /// <summary>Re-evaluates the commands after the editor's editability changed.</summary>
        public void RefreshCommands()
        {
            OnPropertyChanged(nameof(CanEdit));
            SetCommand.NotifyCanExecuteChanged();
            UnsetCommand.NotifyCanExecuteChanged();
            ApplyOtherCommand.NotifyCanExecuteChanged();
            RevertCommand.NotifyCanExecuteChanged();
        }

        [RelayCommand(CanExecute = nameof(CanEdit))]
        private void Set(int? tier)
        {
            if (tier is { } t && t >= 0)
                _owner.SetValue(Key, t.ToString(CultureInfo.InvariantCulture));
        }

        [RelayCommand(CanExecute = nameof(CanEdit))]
        private void Unset() => _owner.UnsetDifficulty(Key);

        /// <summary>Sets the number box's tier.</summary>
        [RelayCommand(CanExecute = nameof(CanEdit))]
        private void ApplyOther()
        {
            if (OtherValue is { } value && value >= 0 && value <= int.MaxValue)
                Set((int)decimal.Truncate(value));
        }

        private bool CanRevert() => CanEdit && IsEdited;

        [RelayCommand(CanExecute = nameof(CanRevert))]
        private void Revert() => _owner.RevertKey(Key);
    }
}
