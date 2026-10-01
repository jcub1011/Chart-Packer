using System;
using System.Globalization;
using ChartPacker.Core.Metadata;
using ChartPacker.Core.Text;
using ChartPacker.Resources;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChartPacker.ViewModels
{
    /// <summary>
    /// A metadata row in the detail panel, merged across every selected song. Clicking the value
    /// edits it; the change goes into the songs' drafts through <see cref="SongDetailViewModel"/>.
    /// </summary>
    public sealed partial class MetadataRowViewModel : ObservableObject
    {
        private readonly SongDetailViewModel _owner;

        public MetadataRowViewModel(SongDetailViewModel owner, BulkMetadataRow row)
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
        public bool IsSongLength => Key.Equals(SongDetailViewModel.SongLengthKey, StringComparison.OrdinalIgnoreCase);

        /// <summary>The value as stored, or <see langword="null"/> when the selected songs don't share one.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsDistinct), nameof(DisplayValue), nameof(ToolTip), nameof(EditPlaceholder), nameof(ShowRichValue), nameof(ShowDistinct))]
        public partial string? Value { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Marker), nameof(IsRemoved), nameof(IsChanged), nameof(CanRemove))]
        [NotifyCanExecuteChangedFor(nameof(BeginEditCommand), nameof(RemoveCommand), nameof(RevertCommand))]
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

        public bool IsDistinct => Value is null;

        /// <summary>The value to show. Times are formatted as HH:MM:SS:mmm; rich-text tags are left for the view to render.</summary>
        public string DisplayValue
        {
            get
            {
                if (Value is null)
                    return Strings.DistinctValues;
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
                    return string.Format(CultureInfo.CurrentCulture, Strings.StoredMillisecondsFormat, Value);
                return RichText.HasMarkup(Value) ? Value : null;
            }
        }

        /// <summary>Shown in the empty edit box of a distinct row, so it's clear typing replaces every value.</summary>
        public string? EditPlaceholder => IsDistinct ? Strings.DistinctValues : null;

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

        /// <summary>Takes the merged state after the drafts changed, keeping this row's identity so its view survives.</summary>
        public MetadataRowViewModel Update(BulkMetadataRow row)
        {
            Value = row.Value;
            State = row.State;
            return this;
        }

        /// <summary>Re-evaluates the commands after the panel's editability changed.</summary>
        public void RefreshCommands()
        {
            BeginEditCommand.NotifyCanExecuteChanged();
            RemoveCommand.NotifyCanExecuteChanged();
            RevertCommand.NotifyCanExecuteChanged();
        }

        private bool CanBeginEdit() => _owner.CanEdit && !IsRemoved;

        [RelayCommand(CanExecute = nameof(CanBeginEdit))]
        private void BeginEdit()
        {
            _owner.CancelOtherEdits(this);
            EditText = Value ?? "";
            Error = null;
            IsEditing = true;
        }

        /// <summary>Puts the edit box's text into the drafts. Invalid text stays in the box with an error.</summary>
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

            if (SongDetailViewModel.ValidateValue(text) is { } error)
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

        private bool CanRemoveRow() => _owner.CanEdit && CanRemove;

        [RelayCommand(CanExecute = nameof(CanRemoveRow))]
        private void Remove()
        {
            Cancel();
            _owner.RemoveKey(Key);
        }

        private bool CanRevert() => _owner.CanEdit && IsChanged;

        [RelayCommand(CanExecute = nameof(CanRevert))]
        private void Revert()
        {
            Cancel();
            _owner.RevertKey(Key);
        }
    }
}
