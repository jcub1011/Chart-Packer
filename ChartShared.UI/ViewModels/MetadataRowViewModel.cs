using System;
using System.Globalization;
using ChartShared.Core.Metadata;
using ChartShared.Core.Text;
using ChartShared.UI.Resources;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChartShared.UI.ViewModels
{
    /// <summary>
    /// A metadata row, merged across every song the editor shows. Clicking the value edits it; the change goes to the
    /// songs through <see cref="MetadataEditorViewModel"/>.
    /// </summary>
    public sealed partial class MetadataRowViewModel : ObservableObject
    {
        private readonly MetadataEditorViewModel _owner;

        public MetadataRowViewModel(MetadataEditorViewModel owner, BulkMetadataRow row)
        {
            _owner = owner;
            Key = row.Key;
            Update(row);
        }

        public string Key { get; }

        /// <summary>What the key does, when it's a known key; otherwise <see langword="null"/>.</summary>
        public string? KeyDescription => KnownKeys.TryGet(Key, out var known) ? known.Description : null;

        public bool HasKeyDescription => KeyDescription is not null;

        /// <summary>The row is <c>song_length</c>, which can be worked out from the song's files.</summary>
        public bool IsSongLength => Key.Equals(MetadataEditRules.SongLengthKey, StringComparison.OrdinalIgnoreCase);

        /// <summary>The value as stored, or <see langword="null"/> when the songs shown don't share one.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsDistinct), nameof(DisplayValue), nameof(ToolTip), nameof(EditPlaceholder), nameof(ShowRichValue), nameof(ShowDistinct))]
        public partial string? Value { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Marker), nameof(IsRemoved), nameof(IsChanged), nameof(CanRemove), nameof(ShowRevertButton), nameof(ShowRemoveButton))]
        [NotifyCanExecuteChangedFor(nameof(BeginEditCommand), nameof(RemoveCommand), nameof(RevertCommand), nameof(QuickFixCommand))]
        public partial MetadataRowState State { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ShowRichValue), nameof(ShowDistinct))]
        public partial bool IsEditing { get; set; }

        /// <summary>The text in the edit box: the stored value, or empty for a distinct row.</summary>
        [ObservableProperty]
        public partial string EditText { get; set; } = "";

        /// <summary>Why the edit box's text can't be committed.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasError))]
        public partial string? Error { get; set; }

        /// <summary>Why the value as drafted fails validation, e.g. a type mismatch; the row is marked while it's set.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasIssue))]
        public partial string? Issue { get; set; }

        /// <summary>A suggestion that doesn't count as a failure, e.g. the key that replaced a legacy one.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasHint))]
        public partial string? Hint { get; set; }

        /// <summary>Some song shown has a quick fix for the key, a failure's or a hint's.</summary>
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(QuickFixCommand))]
        public partial bool HasQuickFix { get; set; }

        /// <summary>What the quick fix does, when every song it applies to would have the same one; otherwise <see langword="null"/>.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(QuickFixHeader))]
        public partial string? QuickFixDescription { get; set; }

        /// <summary>The right-click menu's Quick Fix item, saying what it does when that's one thing.</summary>
        public string QuickFixHeader => QuickFixDescription is { } description
            ? string.Format(CultureInfo.CurrentCulture, SharedStrings.QuickFixMenuFormat, description)
            : SharedStrings.QuickFixMenu;

        public bool HasIssue => Issue is not null;

        public bool HasHint => Hint is not null;

        public bool IsDistinct => Value is null;

        /// <summary>The value to show. Times are formatted as HH:MM:SS:mmm; rich-text tags are left for the view to render.</summary>
        public string DisplayValue
        {
            get
            {
                if (Value is null)
                    return SharedStrings.DistinctValues;
                return MetadataTimes.TryFormat(Key, Value, out string? time) ? time : Value;
            }
        }

        /// <summary>The stored value, when what's shown differs from it; otherwise <see langword="null"/>.</summary>
        public string? ToolTip
        {
            get
            {
                if (Value is null)
                    return null;
                if (MetadataTimes.TryFormat(Key, Value, out _))
                    return string.Format(CultureInfo.CurrentCulture, SharedStrings.StoredMillisecondsFormat, Value);
                return RichText.HasMarkup(Value) ? Value : null;
            }
        }

        /// <summary>Shown in the empty edit box of a distinct row, so it's clear typing replaces every value.</summary>
        public string? EditPlaceholder => IsDistinct ? SharedStrings.DistinctValues : null;

        /// <summary><c>*</c> for an edited row, <c>+</c> for an added one. Removed rows are struck through instead.</summary>
        public string Marker => State switch
        {
            MetadataRowState.Edited => "*",
            MetadataRowState.Added => "+",
            _ => "",
        };

        public bool IsRemoved => State == MetadataRowState.Removed;

        public bool IsChanged => State != MetadataRowState.Unchanged;

        public bool CanRemove => State != MetadataRowState.Removed;

        public bool HasError => Error is not null;

        public bool ShowRichValue => !IsEditing && !IsDistinct;

        public bool ShowDistinct => !IsEditing && IsDistinct;

        /// <summary>Whether the row's actions are shown: the songs can be edited.</summary>
        public bool ShowActions => _owner.CanEdit;

        /// <summary>The Revert button: the row has a change, and the editor doesn't put Revert on the right-click menu.</summary>
        public bool ShowRevertButton => IsChanged && !_owner.ActionsInContextMenu;

        /// <summary>The Remove button: the key isn't removed, and the editor doesn't put Remove on the right-click menu.</summary>
        public bool ShowRemoveButton => CanRemove && !_owner.ActionsInContextMenu;

        /// <summary>Whether the row's right-click menu has Revert and Remove: the editor puts them there, and the songs can be edited.</summary>
        public bool ShowMenuActions => _owner.ActionsInContextMenu && _owner.CanEdit;

        /// <summary>The row's Auto Set button: Auto Set for a <c>song_length</c> row, where the editor offers it.</summary>
        public bool ShowAutoSetLength => IsSongLength && _owner.AutoSetSongLengthCommand is not null;

        /// <summary>Works out <c>song_length</c> from the songs' files; none where the editor doesn't offer it.</summary>
        public IRelayCommand? AutoSetSongLengthCommand => _owner.AutoSetSongLengthCommand;

        /// <summary>Takes the merged state after the drafts changed, keeping this row's identity so its view survives.</summary>
        public MetadataRowViewModel Update(BulkMetadataRow row)
        {
            Value = row.Value;
            State = row.State;
            return this;
        }

        /// <summary>Takes the row's validation, from the drafted metadata of every song shown.</summary>
        /// <param name="quickFix">Whether there's a quick fix, and what it does when that's one thing.</param>
        public void SetValidation(string? issue, string? hint, (bool Available, string? Description) quickFix)
        {
            Issue = issue;
            Hint = hint;
            HasQuickFix = quickFix.Available;
            QuickFixDescription = quickFix.Available ? quickFix.Description : null;
        }

        /// <summary>Re-evaluates the commands after the editor's editability changed.</summary>
        public void RefreshCommands()
        {
            OnPropertyChanged(nameof(ShowActions));
            OnPropertyChanged(nameof(ShowMenuActions));
            BeginEditCommand.NotifyCanExecuteChanged();
            RemoveCommand.NotifyCanExecuteChanged();
            RevertCommand.NotifyCanExecuteChanged();
            QuickFixCommand.NotifyCanExecuteChanged();
        }

        // A key the songs decide otherwise, such as a chart's drum layout, reads as any other but can't be changed.
        private bool CanChange() => _owner.CanEdit && _owner.CanEditKey(Key);

        private bool CanBeginEdit() => CanChange() && !IsRemoved;

        [RelayCommand(CanExecute = nameof(CanBeginEdit))]
        private void BeginEdit()
        {
            _owner.CancelOtherEdits(this);
            EditText = Value ?? "";
            Error = null;
            IsEditing = true;
        }

        /// <summary>
        /// Puts the edit box's text into the drafts. Text song.ini can't hold stays in the box with an error; text
        /// that only fails validation, such as a word for a number, goes in, and the row is marked instead.
        /// </summary>
        [RelayCommand]
        private void Commit()
        {
            if (!IsEditing)
                return;

            string text = EditText.Trim();
            if (IsDistinct && text.Length == 0)
            {
                Cancel();
                return;
            }

            if (MetadataEditRules.ValidateValue(text) is { } error)
            {
                Error = error;
                return;
            }

            IsEditing = false;
            Error = null;
            if (text != Value)
                _owner.SetValue(Key, text);
        }

        [RelayCommand]
        private void Cancel()
        {
            IsEditing = false;
            Error = null;
        }

        private bool CanRemoveRow() => CanChange() && CanRemove;

        [RelayCommand(CanExecute = nameof(CanRemoveRow))]
        private void Remove()
        {
            Cancel();
            _owner.RemoveKey(Key);
        }

        private bool CanRevert() => CanChange() && IsChanged;

        [RelayCommand(CanExecute = nameof(CanRevert))]
        private void Revert()
        {
            Cancel();
            _owner.RevertKey(Key);
        }

        private bool CanQuickFix() => CanChange() && HasQuickFix && !IsRemoved;

        /// <summary>Applies the key's quick fixes to the songs' drafts. Nothing is saved.</summary>
        [RelayCommand(CanExecute = nameof(CanQuickFix))]
        private void QuickFix()
        {
            Cancel();
            _owner.ApplyQuickFixes(Key);
        }
    }
}
