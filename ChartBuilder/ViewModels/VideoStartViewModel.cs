using System;
using System.Linq;
using Avalonia.Threading;
using ChartPacker.Core.Charting;
using ChartPacker.Core.Charting.Commands;
using ChartPacker.Core.Metadata;
using ChartPacker.UI.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChartBuilder.ViewModels
{
    /// <summary>
    /// The Art tab's video start box (charter-spec C1): the song's <c>video_start_time</c> in milliseconds, where in the
    /// background video the song starts. The video shows each value as it's typed or dragged, and once it's left alone a
    /// moment it's set as one undo step, as the delay box in Chart settings is.
    /// </summary>
    public sealed partial class VideoStartViewModel : ViewModelBase, IDisposable
    {
        private static readonly TimeSpan TypingDelay = TimeSpan.FromMilliseconds(600);

        private readonly EditorSong _song;
        private readonly Action<IChartCommand> _execute;
        private readonly DispatcherTimer _typingTimer;
        private bool _loading;
        private bool _typed;

        /// <param name="song">The song open.</param>
        /// <param name="execute">Makes an edit, as one undo step.</param>
        public VideoStartViewModel(EditorSong song, Action<IChartCommand> execute)
        {
            ArgumentNullException.ThrowIfNull(song);
            ArgumentNullException.ThrowIfNull(execute);
            _song = song;
            _execute = execute;
            _typingTimer = new DispatcherTimer(TypingDelay, DispatcherPriority.Background, (_, _) => ApplyTyped());
            _typingTimer.Stop();
            Load();
        }

        /// <summary>Raised as <see cref="PreviewStart"/> changes, so the video can show it before it's set.</summary>
        public event EventHandler? PreviewChanged;

        /// <summary>
        /// The <c>video_start_time</c> as the box shows it: the song's as the next save leaves it, or what's being typed.
        /// Positive skips that much of the video; negative holds its first frame that long.
        /// </summary>
        [ObservableProperty]
        public partial decimal? StartMs { get; set; }

        /// <summary>Whether the start has a pending change.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Marker))]
        [NotifyCanExecuteChangedFor(nameof(RevertCommand))]
        public partial bool IsChanged { get; private set; }

        /// <summary>Whether a save is running, which the start can't be changed during.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanEdit))]
        [NotifyCanExecuteChangedFor(nameof(RevertCommand))]
        public partial bool IsSaving { get; set; }

        public bool CanEdit => !IsSaving;

        /// <summary><c>*</c> beside the box while the start has a pending change, as an edited row has.</summary>
        public string? Marker => IsChanged ? "*" : null;

        // A day either way, as the delay box allows; well inside what the key is read as.
        public static decimal MinStartMs => -86_399_999;

        public static decimal MaxStartMs => 86_399_999;

        /// <summary>The start being typed, before it's set; null when nothing is.</summary>
        public TimeSpan? PreviewStart => _typed && StartMs is { } ms ? TimeSpan.FromMilliseconds((long)decimal.Round(ms, MidpointRounding.AwayFromZero)) : null;

        /// <summary>Shows the start as the song has it now, but the start while it's being typed, and whether it's changed.</summary>
        public void Load()
        {
            IsChanged = _song.PendingChanges().Any(c => c.Key.Equals(TimingOffsets.VideoKey, StringComparison.OrdinalIgnoreCase));
            if (_typed)
                return;

            _loading = true;
            try
            {
                StartMs = (long)Math.Round(VideoTimeline.From(_song.EffectivePairs()).Start.TotalMilliseconds, MidpointRounding.AwayFromZero);
            }
            finally
            {
                _loading = false;
            }
        }

        public void Dispose() => _typingTimer.Stop();

        /// <summary>Puts the start back as the song has it saved.</summary>
        [RelayCommand(CanExecute = nameof(CanRevert))]
        private void Revert()
        {
            _typingTimer.Stop();
            bool wasTyped = _typed;
            _typed = false;
            if (SongCommands.RevertToSaved(_song, TimingOffsets.VideoKey) is { } command)
                _execute(command);
            else
                Load();
            if (wasTyped)
                PreviewChanged?.Invoke(this, EventArgs.Empty);
        }

        private bool CanRevert() => IsChanged && CanEdit;

        partial void OnStartMsChanged(decimal? value)
        {
            if (_loading)
                return;
            _typed = true;
            PreviewChanged?.Invoke(this, EventArgs.Empty);
            _typingTimer.Stop();
            _typingTimer.Start();
        }

        /// <summary>Sets the start typed, as one undo step.</summary>
        private void ApplyTyped()
        {
            _typingTimer.Stop();
            if (IsSaving)
            {
                // Typed just as a save began: set once it's done.
                _typingTimer.Start();
                return;
            }

            bool typed = _typed;
            _typed = false;
            if (typed && StartMs is { } ms && SongCommands.SetVideoStart(_song, (long)decimal.Round(ms, MidpointRounding.AwayFromZero)) is { } command)
                _execute(command);
            else
                Load();
            if (typed)
                PreviewChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
