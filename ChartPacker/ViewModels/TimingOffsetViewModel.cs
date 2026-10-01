using System;
using System.Collections.Generic;
using ChartPacker.Core.Metadata;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChartPacker.ViewModels
{
    /// <summary>
    /// An offset box for a song.ini timing key, as <see cref="TimingOffsets"/> reads and sets it: how far the one
    /// selected song's draft moves the audio, or the background video, later when positive.
    /// </summary>
    /// <remarks>
    /// The box is edited straight into the song's <see cref="MetadataDraft"/>, as the key's row would be, and
    /// <see cref="OffsetChanged"/> tells the details panel so. The draft's change to the key is all there is,
    /// so editing the row shows here too.
    /// </remarks>
    public sealed partial class TimingOffsetViewModel(string key) : ViewModelBase
    {
        private MetadataDraft? _draft;
        private IReadOnlyList<KeyValuePair<string, string>> _original = [];
        private TimeSpan _fallback;

        // Set while the offset is changed from here, so that doesn't read as the user changing it.
        private bool _isSyncing;

        /// <summary>Raised when the offset is set in the draft of the song shown.</summary>
        public event EventHandler? OffsetChanged;

        /// <summary>The key the offset changes.</summary>
        public string Key { get; } = key;

        /// <summary>How far the draft moves what the key places, in milliseconds, later when positive; the offset box.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasOffset))]
        [NotifyPropertyChangedFor(nameof(Marker))]
        [NotifyCanExecuteChangedFor(nameof(RevertOffsetCommand))]
        public partial decimal? Offset { get; set; } = 0;

        /// <summary>Whether the offset can be changed: one song is shown, and it can be edited.</summary>
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(RevertOffsetCommand))]
        public partial bool CanEdit { get; set; }

        /// <summary>The lowest offset the box takes: past it, the key would be a value it isn't read as.</summary>
        [ObservableProperty]
        public partial decimal MinOffset { get; set; } = int.MinValue;

        /// <summary>The highest offset the box takes, as <see cref="MinOffset"/> is the lowest.</summary>
        [ObservableProperty]
        public partial decimal MaxOffset { get; set; } = int.MaxValue;

        public bool HasOffset => Offset is { } offset && offset != 0;

        public string? Marker => HasOffset ? "*" : null;

        /// <summary>
        /// Shows the offset <paramref name="draft"/> makes to <paramref name="original"/>, as <paramref name="drafted"/>
        /// has it; <see langword="null"/> shows none, as for no song or several.
        /// </summary>
        /// <param name="fallback">What the key counts as when it isn't set: for <c>delay</c>, the chart's own offset.</param>
        public void Sync(MetadataDraft? draft, IReadOnlyList<KeyValuePair<string, string>> original, IReadOnlyList<KeyValuePair<string, string>> drafted, TimeSpan fallback)
        {
            _draft = draft;
            _original = original;
            _fallback = fallback;
            _isSyncing = true;
            try
            {
                // The limits first, so the box doesn't hold the offset to the last song's.
                (int min, int max) = draft is null ? (int.MinValue, int.MaxValue) : TimingOffsets.RangeOf(Key, original, fallback);
                MinOffset = min;
                MaxOffset = max;
                Offset = draft is null ? 0 : TimingOffsets.OffsetOf(Key, original, drafted, fallback);
            }
            finally
            {
                _isSyncing = false;
            }
        }

        private bool CanRevertOffset() => HasOffset && CanEdit;

        [RelayCommand(CanExecute = nameof(CanRevertOffset))]
        private void RevertOffset() => Offset = 0;

        // No limit of its own, as a song can be off by any amount; only what the key can be read as.
        partial void OnOffsetChanged(decimal? value)
        {
            if (_isSyncing || _draft is not { } draft)
                return;

            // The box is disabled then; this puts back a value that got in anyway, as the draft has it now.
            if (!CanEdit)
            {
                Sync(draft, _original, draft.ApplyTo(_original), _fallback);
                return;
            }

            TimingOffsets.Set(draft, Key, _original, _fallback, (int)Math.Round(Math.Clamp(value ?? 0, MinOffset, MaxOffset)));
            OffsetChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
