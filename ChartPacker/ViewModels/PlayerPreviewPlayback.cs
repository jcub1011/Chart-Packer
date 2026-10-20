using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using ChartPacker.Resources;
using ChartShared.Core.Charts;
using ChartShared.UI.Controls;
using ChartShared.UI.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChartPacker.ViewModels
{
    /// <summary>
    /// A <see cref="PlayerViewModel"/> as a reduction's preview plays it (<see cref="IReductionPreviewPlayback"/>), in the
    /// Generate difficulties dialog: the song shown, its chart's beat lines (<see cref="SetChart"/>), and its speed in
    /// percents.
    /// </summary>
    public sealed partial class PlayerPreviewPlayback : ObservableObject, IReductionPreviewPlayback, IDisposable
    {
        private readonly PlayerViewModel _player;
        private TempoMap? _tempo;
        private TimeSpan _delay;

        public PlayerPreviewPlayback(PlayerViewModel player)
        {
            ArgumentNullException.ThrowIfNull(player);
            _player = player;
            _player.PropertyChanged += OnPlayerChanged;
            _player.PlayPauseCommand.CanExecuteChanged += OnCanPlayChanged;
        }

        public bool IsReady => _player.PlayPauseCommand.CanExecute(null);

        public bool IsPlaying => _player.IsPlaying;

        public double PositionSeconds
        {
            get => _player.PositionSeconds;
            set => _player.PositionSeconds = Math.Max(0, value);
        }

        public double LengthSeconds => _player.TimelineSeconds;

        public string PositionText => string.Format(CultureInfo.CurrentCulture, Strings.GenerateDifficultiesPositionFormat, _player.PositionText, _player.LengthText);

        /// <summary>The beat lines of the chart shown, as far as the song goes.</summary>
        [ObservableProperty]
        public partial IReadOnlyList<TimelineBeat> Beats { get; private set; } = [];

        public Func<TimeSpan> Clock => () => _player.DrawnPosition;

        public IReadOnlyList<int> SpeedPercents { get; } = [25, 50, 75, 100, 125, 150, 200];

        public int SpeedPercent
        {
            get => (int)Math.Round(_player.PlaybackSpeed * 100);
            set => _player.PlaybackSpeed = value / 100.0;
        }

        public void PlayPause() => _player.PlayPauseCommand.Execute(null);

        public void SeekTo(double seconds) => PositionSeconds = seconds;

        /// <summary>The chart of the song shown, whose beat lines the change strip draws: its tempo map and audio delay.</summary>
        public void SetChart(TempoMap? tempo, TimeSpan delay)
        {
            _tempo = tempo;
            _delay = delay;
            RefreshBeats();
        }

        public void Dispose()
        {
            _player.PropertyChanged -= OnPlayerChanged;
            _player.PlayPauseCommand.CanExecuteChanged -= OnCanPlayChanged;
        }

        private void RefreshBeats() =>
            Beats = _tempo is { } tempo && LengthSeconds > 0 ? TimelineBeats.Of(tempo, _delay, LengthSeconds) : [];

        private void OnCanPlayChanged(object? sender, EventArgs e) => OnPropertyChanged(nameof(IsReady));

        private void OnPlayerChanged(object? sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(PlayerViewModel.IsPlaying):
                    OnPropertyChanged(nameof(IsPlaying));
                    break;
                case nameof(PlayerViewModel.PositionSeconds):
                    OnPropertyChanged(nameof(PositionSeconds));
                    OnPropertyChanged(nameof(PositionText));
                    break;
                case nameof(PlayerViewModel.TimelineSeconds):
                    OnPropertyChanged(nameof(LengthSeconds));
                    OnPropertyChanged(nameof(PositionText));
                    RefreshBeats();
                    break;
                case nameof(PlayerViewModel.PlaybackSpeed):
                    OnPropertyChanged(nameof(SpeedPercent));
                    break;
            }
        }
    }
}
