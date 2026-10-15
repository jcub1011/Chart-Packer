using System;
using System.Collections.Generic;
using System.Linq;
using ChartPacker.UI.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChartPacker.ViewModels
{
    /// <summary>
    /// A lane's waveform picker: which of the song's tracks its waveform is drawn from, its own or another. Only
    /// the user picking one calls back; <see cref="Sync"/> showing what's picked doesn't.
    /// </summary>
    public sealed partial class LaneWaveformPickerViewModel : ViewModelBase
    {
        private readonly Action _picked;

        // Set while the options and the pick are shown from here, so that doesn't read as the user picking.
        private bool _isSyncing;

        /// <param name="picked">Called when the user picks a track; <see cref="Stem"/> is the one picked.</param>
        internal LaneWaveformPickerViewModel(Action picked) => _picked = picked;

        /// <summary>The tracks the waveform can be drawn from, the lane's own first.</summary>
        [ObservableProperty]
        public partial IReadOnlyList<WaveformOption> Options { get; private set; } = [];

        [ObservableProperty]
        public partial int Index { get; set; }

        /// <summary>The stem picked, or <see langword="null"/> for the lane's own.</summary>
        public string? Stem => Index >= 0 && Index < Options.Count ? Options[Index].Stem : null;

        /// <summary>Shows <paramref name="options"/>, with <paramref name="chosen"/> picked, or the lane's own when the song hasn't that track.</summary>
        internal void Sync(IReadOnlyList<WaveformOption> options, string? chosen)
        {
            _isSyncing = true;
            try
            {
                if (!options.SequenceEqual(Options))
                    Options = options;
                Index = LaneWaveformChoice.IndexOf(options, chosen);
            }
            finally
            {
                _isSyncing = false;
            }
        }

        partial void OnIndexChanged(int value)
        {
            // A new list of options clears the picker's selection first.
            if (!_isSyncing && value >= 0)
                _picked();
        }
    }
}
