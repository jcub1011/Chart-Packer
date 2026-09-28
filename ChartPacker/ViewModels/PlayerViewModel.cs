using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Collections;
using Avalonia.Threading;
using ChartPacker.Audio.Mixing;
using ChartPacker.Audio.Playback;
using ChartPacker.Core.Scanning;
using ChartPacker.Resources;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChartPacker.ViewModels
{
    /// <summary>One of the selected song's audio tracks, with its mute and solo switches.</summary>
    public sealed partial class TrackRowViewModel : ViewModelBase
    {
        private readonly PlayerViewModel _owner;

        internal TrackRowViewModel(PlayerViewModel owner, SongTrack track)
        {
            _owner = owner;
            Track = track;

            // The preview is a separate clip, not part of the song, so it's left out of the mix until asked for.
            _isMuted = track.Stem == AudioStems.Preview;
        }

        public SongTrack Track { get; }

        public string Name => Track.StoredName;

        /// <summary><c>+</c> for a track a staged change adds, like an added file.</summary>
        public string? Marker => Track.IsStaged ? "+" : null;

        [ObservableProperty]
        private bool _isMuted;

        /// <summary>While any track is soloed, only soloed tracks are heard.</summary>
        [ObservableProperty]
        private bool _isSolo;

        /// <summary>Why the track can't be played, once that's known.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasError))]
        private string? _error;

        public bool HasError => Error is not null;

        partial void OnIsMutedChanged(bool value) => _owner.ApplySwitches(this);

        partial void OnIsSoloChanged(bool value) => _owner.ApplySwitches(this);
    }

    /// <summary>
    /// Plays the selected song's audio tracks together, in step, with each track muted or soloed
    /// as asked. Nothing is opened until the first play; <see cref="Unload"/> closes every file.
    /// </summary>
    public sealed partial class PlayerViewModel : ViewModelBase, IDisposable
    {
        private readonly Func<int, IAudioOutput> _openOutput;
        private readonly DispatcherTimer _timer;
        private SongEntry? _entry;
        private SongAudio? _audio;
        private StemPlayer? _player;

        // Bumped by every load and unload, so a slow open for an earlier song is dropped.
        private int _generation;

        // Set while the position is being updated from playback rather than by the user.
        private bool _isUpdatingPosition;

        /// <param name="openOutput">Opens the audio device at a sample rate.</param>
        public PlayerViewModel(Func<int, IAudioOutput> openOutput)
        {
            _openOutput = openOutput;
            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(33) };
            _timer.Tick += (_, _) => RefreshPosition();
        }

        public AvaloniaList<TrackRowViewModel> Tracks { get; } = new();

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(PlayPauseToolTip))]
        private bool _isPlaying;

        /// <summary>The audio is being opened for the first play.</summary>
        [ObservableProperty]
        private bool _isOpening;

        /// <summary>Why playback isn't possible or stopped.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasError))]
        private string? _error;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(PositionText))]
        private double _positionSeconds;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(LengthText))]
        private double _lengthSeconds;

        /// <summary>From 0 to 1.</summary>
        [ObservableProperty]
        private double _volume = 1;

        /// <summary>Where the song's <c>preview_start_time</c> puts the preview, if it sets one.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasPreviewStart))]
        [NotifyCanExecuteChangedFor(nameof(JumpToPreviewCommand))]
        private TimeSpan? _previewStart;

        public bool HasTracks => Tracks.Count > 0;

        /// <summary>The seek slider is held; see <see cref="BeginScrub"/>.</summary>
        public bool IsScrubbing { get; private set; }

        public string TracksHeader => string.Format(CultureInfo.CurrentCulture, Strings.TracksHeaderFormat, Tracks.Count);

        public bool HasError => Error is not null;

        public bool HasPreviewStart => PreviewStart is not null;

        public string PositionText => FormatTime(PositionSeconds);

        public string LengthText => FormatTime(LengthSeconds);

        public string PlayPauseToolTip => IsPlaying ? Strings.PauseToolTip : Strings.PlayToolTip;

        /// <summary>
        /// Shows <paramref name="tracks"/> of <paramref name="entry"/>. When they're the tracks
        /// already shown, nothing changes and playback carries on; otherwise playback stops, and
        /// each track keeps the switches its stem had.
        /// </summary>
        public void Load(SongEntry entry, IReadOnlyList<SongTrack> tracks, TimeSpan? previewStart)
        {
            PreviewStart = previewStart;
            if (entry == _entry && tracks.SequenceEqual(Tracks.Select(t => t.Track)))
                return;

            var switches = entry == _entry
                ? Tracks.GroupBy(t => t.Track.Stem).ToDictionary(g => g.Key, g => (g.First().IsMuted, g.First().IsSolo))
                : [];

            Close();
            _entry = entry;
            Tracks.Clear();
            foreach (var track in tracks)
            {
                var row = new TrackRowViewModel(this, track);
                if (switches.TryGetValue(track.Stem, out var s))
                {
                    row.IsMuted = s.IsMuted;
                    row.IsSolo = s.IsSolo;
                }

                Tracks.Add(row);
            }

            OnPropertyChanged(nameof(HasTracks));
            OnPropertyChanged(nameof(TracksHeader));
            RefreshCommands();
        }

        /// <summary>Stops playback, closes every file and forgets the song.</summary>
        public void Unload()
        {
            Close();
            _entry = null;
            Tracks.Clear();
            PreviewStart = null;
            OnPropertyChanged(nameof(HasTracks));
            OnPropertyChanged(nameof(TracksHeader));
            RefreshCommands();
        }

        /// <summary>The seek slider is held: the position stops following playback until <see cref="EndScrub"/>.</summary>
        public void BeginScrub() => IsScrubbing = true;

        /// <summary>The seek slider was let go: playback moves to where it was left.</summary>
        public void EndScrub()
        {
            IsScrubbing = false;
            _player?.Seek(TimeSpan.FromSeconds(PositionSeconds));
        }

        public void Dispose()
        {
            _timer.Stop();
            Close();
        }

        internal void ApplySwitches(TrackRowViewModel row)
        {
            if (_player is null)
                return;

            foreach (var stem in _player.Mixer.Stems.Where(s => s.Name == row.Name))
            {
                stem.IsMuted = row.IsMuted;
                stem.IsSolo = row.IsSolo;
            }
        }

        private bool CanPlay() => HasTracks && !IsOpening;

        [RelayCommand(CanExecute = nameof(CanPlay))]
        private async Task PlayPauseAsync()
        {
            if (IsPlaying)
            {
                _player?.Pause();
                SetPlaying(false);
                return;
            }

            if (await EnsureOpenAsync() is not { } player)
                return;

            player.Play();
            SetPlaying(player.IsPlaying);
        }

        [RelayCommand(CanExecute = nameof(CanPlay))]
        private void Stop()
        {
            if (_player is null)
                return;

            _player.Pause();
            _player.Seek(TimeSpan.Zero);
            SetPlaying(false);
            SetPosition(0);
        }

        private bool CanJumpToPreview() => PreviewStart is not null && CanPlay();

        /// <summary>Plays from the song's preview start.</summary>
        [RelayCommand(CanExecute = nameof(CanJumpToPreview))]
        private async Task JumpToPreviewAsync()
        {
            if (PreviewStart is not { } start || await EnsureOpenAsync() is not { } player)
                return;

            player.Seek(start);
            SetPosition(start.TotalSeconds);
            player.Play();
            SetPlaying(player.IsPlaying);
        }

        partial void OnIsOpeningChanged(bool value) => RefreshCommands();

        private void RefreshCommands()
        {
            PlayPauseCommand.NotifyCanExecuteChanged();
            StopCommand.NotifyCanExecuteChanged();
            JumpToPreviewCommand.NotifyCanExecuteChanged();
        }

        partial void OnVolumeChanged(double value)
        {
            if (_player is not null)
                _player.Volume = (float)value;
        }

        // A change from the slider (a click on its track, or the keyboard) seeks; a drag seeks when it ends.
        partial void OnPositionSecondsChanged(double value)
        {
            if (!_isUpdatingPosition && !IsScrubbing)
                _player?.Seek(TimeSpan.FromSeconds(value));
        }

        /// <summary>Opens the song's audio and the device, the first time it's played.</summary>
        /// <returns>The player, or <see langword="null"/> when nothing can be played, with <see cref="Error"/> saying why.</returns>
        private async Task<StemPlayer?> EnsureOpenAsync()
        {
            if (_player is not null)
                return _player;
            if (_entry is not { } entry)
                return null;

            int generation = ++_generation;
            var tracks = Tracks.Select(t => t.Track).ToList();
            IsOpening = true;
            Error = null;
            try
            {
                var (audio, player, error) = await Task.Run(() => Open(entry, tracks));
                if (generation != _generation)
                {
                    // The song changed while it was opening.
                    player?.Dispose();
                    audio?.Dispose();
                    return null;
                }

                Error = error;
                foreach (var (row, opened) in Tracks.Zip(audio?.Tracks ?? []))
                    row.Error = opened.Error is null ? null : string.Format(CultureInfo.CurrentCulture, Strings.TrackUnplayableFormat, opened.Error);

                // Nothing to play: the files are closed again, and the next play tries afresh.
                if (player is null)
                {
                    audio?.Dispose();
                    return null;
                }

                _audio = audio;
                _player = player;
                player.Volume = (float)Volume;
                foreach (var row in Tracks)
                    ApplySwitches(row);
                player.Stopped += OnPlayerStopped;
                LengthSeconds = player.Length.TotalSeconds;
                return player;
            }
            finally
            {
                if (generation == _generation)
                    IsOpening = false;
            }
        }

        /// <summary>Opens the tracks and the device; off the UI thread, since it reads every file's headers.</summary>
        private (SongAudio? Audio, StemPlayer? Player, string? Error) Open(SongEntry entry, IReadOnlyList<SongTrack> tracks)
        {
            SongAudio audio;
            try
            {
                audio = SongAudio.Open(entry, tracks);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return (null, null, string.Format(CultureInfo.CurrentCulture, Strings.SongAudioUnreadableFormat, ex.Message));
            }

            var sources = audio.Sources;
            if (sources.Count == 0)
                return (audio, null, Strings.NoPlayableTracks);

            var mixer = new StemMixer(sources);
            try
            {
                return (audio, new StemPlayer(mixer, _openOutput(mixer.OutputRate)), null);
            }
            catch (AudioDeviceException ex)
            {
                return (audio, null, string.Format(CultureInfo.CurrentCulture, Strings.AudioDeviceFailedFormat, ex.Message));
            }
        }

        private void OnPlayerStopped(object? sender, EventArgs e) =>
            Dispatcher.UIThread.Post(() =>
            {
                if (!ReferenceEquals(sender, _player) || _player is null)
                    return;

                SetPlaying(false);
                RefreshPosition();

                // After a failure (the device went away, say) everything is closed, so the next play starts afresh.
                if (_player.Error is { } error)
                {
                    Close();
                    Error = string.Format(CultureInfo.CurrentCulture, Strings.PlaybackFailedFormat, error);
                }
            });

        /// <summary>Stops playback and closes the device and every file.</summary>
        private void Close()
        {
            _generation++;
            SetPlaying(false);
            if (_player is not null)
            {
                _player.Stopped -= OnPlayerStopped;
                _player.Dispose();
                _player = null;
            }

            _audio?.Dispose();
            _audio = null;
            IsOpening = false;
            Error = null;
            SetPosition(0);
            LengthSeconds = 0;
        }

        private void SetPlaying(bool playing)
        {
            IsPlaying = playing;
            if (playing)
                _timer.Start();
            else
                _timer.Stop();
        }

        private void RefreshPosition()
        {
            if (_player is null || IsScrubbing)
                return;

            SetPosition(_player.Position.TotalSeconds);

            // A stem found to be damaged while playing goes quiet; its row says why.
            foreach (var row in Tracks)
            {
                if (row.Error is null && _player.Mixer.Stems.FirstOrDefault(s => s.Name == row.Name)?.Error is { } error)
                    row.Error = string.Format(CultureInfo.CurrentCulture, Strings.TrackUnplayableFormat, error);
            }
        }

        private void SetPosition(double seconds)
        {
            _isUpdatingPosition = true;
            try
            {
                PositionSeconds = seconds;
            }
            finally
            {
                _isUpdatingPosition = false;
            }
        }

        private static string FormatTime(double seconds)
        {
            var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
            return time.TotalHours >= 1
                ? time.ToString(@"h\:mm\:ss", CultureInfo.CurrentCulture)
                : time.ToString(@"m\:ss", CultureInfo.CurrentCulture);
        }
    }
}
