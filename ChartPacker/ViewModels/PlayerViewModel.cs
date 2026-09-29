using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Collections;
using Avalonia.Threading;
using ChartPacker.Audio.Mixing;
using ChartPacker.Audio.Playback;
using ChartPacker.Audio.Waveforms;
using ChartPacker.Core.Scanning;
using ChartPacker.Resources;
using ChartPacker.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PreviewStartTime = ChartPacker.Core.Metadata.PreviewStart;

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

        /// <summary>While any track is soloed, only soloed tracks are heard. A track is muted or soloed, never both.</summary>
        [ObservableProperty]
        private bool _isSolo;

        /// <summary>How loud the track is in the mix, from 0 to 1. Remembered for the song until the app closes.</summary>
        [ObservableProperty]
        private double _volume = 1;

        /// <summary>Why the track can't be played, once that's known.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasError))]
        private string? _error;

        public bool HasError => Error is not null;

        // Solo overrides mute in the mix, so having both on would read as if both applied. Turning
        // one on turns the other off.
        partial void OnIsMutedChanged(bool value)
        {
            if (value)
                IsSolo = false;
            _owner.ApplySwitches(this);
        }

        partial void OnIsSoloChanged(bool value)
        {
            if (value)
                IsMuted = false;
            _owner.ApplySwitches(this);
        }

        partial void OnVolumeChanged(double value)
        {
            _owner.RememberVolume(this);
            _owner.ApplySwitches(this);
        }
    }

    /// <summary>
    /// Plays the selected song's audio tracks together, in step, with each track muted or soloed
    /// as asked. Nothing is opened until the first play; <see cref="Unload"/> closes every file.
    /// </summary>
    public sealed partial class PlayerViewModel : ViewModelBase, IDisposable
    {
        private static readonly TimeSpan PeaksDelay = TimeSpan.FromMilliseconds(300);

        private readonly Func<int, IAudioOutput> _openOutput;
        private readonly DispatcherTimer _timer;
        private SongEntry? _entry;
        private SongAudio? _audio;
        private StemPlayer? _player;

        // Bumped by every load and unload, so a slow open for an earlier song is dropped.
        private int _generation;

        // Set while the position is being updated from playback rather than by the user.
        private bool _isUpdatingPosition;

        // The waveform's per-track peaks, read on a background thread as the tracks are loaded; by stored name.
        private readonly Dictionary<string, StemPeaks> _peaks = new(StringComparer.Ordinal);
        private CancellationTokenSource? _peaksCts;
        private Task? _peaksTask;

        // A song the library asked to play, to be started when the detail view gives it its tracks.
        private SongEntry? _previewRequest;

        // Each song's track volumes, by song path and stem, for as long as the app runs. Nothing is saved to disk.
        private readonly Dictionary<(string Path, string Stem), double> _trackVolumes = [];

        private readonly SettingsService _settings;

        /// <param name="settings">Where the master volume is kept between runs.</param>
        /// <param name="openOutput">Opens the audio device at a sample rate.</param>
        public PlayerViewModel(SettingsService settings, Func<int, IAudioOutput> openOutput)
        {
            _settings = settings;
            _volume = settings.Settings.MasterVolume;
            _openOutput = openOutput;
            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(33) };
            _timer.Tick += (_, _) => RefreshPosition();
        }

        public AvaloniaList<TrackRowViewModel> Tracks { get; } = new();

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(PlayPauseToolTip))]
        [NotifyPropertyChangedFor(nameof(ActiveEntry))]
        private bool _isPlaying;

        /// <summary>What is heard from the selected tracks, as a waveform; empty until their audio has been read.</summary>
        [ObservableProperty]
        private Waveform _waveform = Waveform.Empty;

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

        /// <summary>The master volume, from 0 to 1. Kept in the settings, so it carries over to the next run.</summary>
        [ObservableProperty]
        private double _volume;

        /// <summary>Where the song's <c>preview_start_time</c> puts the preview, if it sets one.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasPreviewStart))]
        [NotifyCanExecuteChangedFor(nameof(JumpToPreviewCommand))]
        private TimeSpan? _previewStart;

        public bool HasTracks => Tracks.Count > 0;

        /// <summary>The song that is playing, or <see langword="null"/> when nothing is.</summary>
        public SongEntry? ActiveEntry => IsPlaying ? _entry : null;

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
            {
                StartRequestedPreview(entry);
                return;
            }

            var switches = entry == _entry
                ? Tracks.GroupBy(t => t.Track.Stem).ToDictionary(g => g.Key, g => (g.First().IsMuted, g.First().IsSolo))
                : [];

            // A request for another song is of no use once this one is shown.
            if (entry != _previewRequest)
                _previewRequest = null;

            CancelPeaks();
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

                if (_trackVolumes.TryGetValue((entry.FullPath, track.Stem), out double volume))
                    row.Volume = volume;

                Tracks.Add(row);
            }

            OnPropertyChanged(nameof(HasTracks));
            OnPropertyChanged(nameof(TracksHeader));
            RefreshCommands();
            StartPeaks(entry, [.. tracks]);
            StartRequestedPreview(entry);
        }

        /// <summary>
        /// Asks for <paramref name="entry"/> to be played from its preview start: at once when its tracks are
        /// shown, otherwise as soon as they are.
        /// </summary>
        public void RequestPreview(SongEntry entry)
        {
            _previewRequest = entry;
            if (entry == _entry && HasTracks)
                StartRequestedPreview(entry);
        }

        /// <summary>The song asked for with <see cref="RequestPreview"/> isn't wanted any more.</summary>
        public void CancelPreviewRequest() => _previewRequest = null;

        /// <summary>The song asked for with <see cref="RequestPreview"/> that hasn't started yet.</summary>
        public SongEntry? PreviewRequest => _previewRequest;

        /// <summary>Stops playback, closes every file and forgets the song.</summary>
        public void Unload()
        {
            CancelPeaks();
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
            CancelPeaks();
            Close();
        }

        /// <summary>Keeps <paramref name="row"/>'s volume for its song, so it is back when the song is shown again.</summary>
        internal void RememberVolume(TrackRowViewModel row)
        {
            // The row is not yet in Tracks while it is being filled from what was remembered, when _entry is still the old song.
            if (_entry is { } entry && Tracks.Contains(row))
                _trackVolumes[(entry.FullPath, row.Track.Stem)] = row.Volume;
        }

        internal void ApplySwitches(TrackRowViewModel row)
        {
            RebuildWaveform();
            if (_player is null)
                return;

            foreach (var stem in _player.Mixer.Stems.Where(s => s.Name == row.Name))
            {
                stem.IsMuted = row.IsMuted;
                stem.IsSolo = row.IsSolo;
                stem.Volume = (float)row.Volume;
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
        private Task JumpToPreviewAsync() => StartFromPreviewAsync();

        /// <summary>Plays from the preview start, or from the beginning when the song has none or it isn't inside the song.</summary>
        private async Task StartFromPreviewAsync()
        {
            if (!CanPlay() || await EnsureOpenAsync() is not { } player)
                return;

            var start = PreviewStartTime.Resolve(PreviewStart, player.Length);
            player.Seek(start);
            SetPosition(start.TotalSeconds);
            player.Play();
            SetPlaying(player.IsPlaying);
        }

        private void StartRequestedPreview(SongEntry entry)
        {
            if (_previewRequest != entry)
                return;

            _previewRequest = null;
            _ = StartFromPreviewAsync();
        }

        /// <summary>Reads the tracks' audio in the background for the waveform, a little after they are shown so that moving quickly through songs doesn't read each one.</summary>
        private void StartPeaks(SongEntry entry, IReadOnlyList<SongTrack> tracks)
        {
            if (tracks.Count == 0)
                return;

            var cts = new CancellationTokenSource();
            _peaksCts = cts;

            // Taken now: the source is disposed by CancelPeaks, which can happen before the task starts.
            var token = cts.Token;
            _peaksTask = Task.Run(() => ReadPeaks(entry, tracks, token));
        }

        private void ReadPeaks(SongEntry entry, IReadOnlyList<SongTrack> tracks, CancellationToken cancellationToken)
        {
            try
            {
                // Cancelled during the wait: nothing was opened.
                if (cancellationToken.WaitHandle.WaitOne(PeaksDelay))
                    return;

                // Separate from the player's own sources, which are for the audio thread alone.
                using var audio = SongAudio.Open(entry, tracks, cancellationToken);
                foreach (var opened in audio.Tracks)
                {
                    if (opened.Source is not { } source)
                        continue;

                    var peaks = WaveformPeaks.Compute(opened.Track.StoredName, source, cancellationToken);
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (cancellationToken.IsCancellationRequested)
                            return;

                        _peaks[peaks.Name] = peaks;
                        RebuildWaveform();
                    });
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException or UnauthorizedAccessException or InvalidDataException)
            {
                // No waveform; the song still plays, and opening it says what is wrong.
            }
        }

        /// <summary>
        /// Stops reading the waveform and waits for the files to be let go, so that a save straight after
        /// isn't blocked by them. The read checks for cancellation between short chunks, so it is quick.
        /// </summary>
        private void CancelPeaks()
        {
            var cts = _peaksCts;
            var task = _peaksTask;
            _peaksCts = null;
            _peaksTask = null;

            if (cts is not null)
            {
                cts.Cancel();
                try
                {
                    task?.Wait(TimeSpan.FromSeconds(2));
                }
                catch (AggregateException)
                {
                }

                cts.Dispose();
            }

            _peaks.Clear();
            Waveform = Waveform.Empty;
        }

        /// <summary>Mixes the peaks read so far as the tracks are switched, the way the mixer would.</summary>
        private void RebuildWaveform()
        {
            if (_peaks.Count == 0)
            {
                if (Waveform.Count > 0)
                    Waveform = Waveform.Empty;
                return;
            }

            var stems = new List<AudibleStem>(Tracks.Count);
            foreach (var row in Tracks)
            {
                if (_peaks.TryGetValue(row.Name, out var peaks))
                    stems.Add(new AudibleStem(peaks, row.IsMuted, row.IsSolo, (float)row.Volume));
            }

            Waveform = WaveformPeaks.Mix(stems);
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
            _settings.Settings.MasterVolume = value;
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

                // A spot picked on the waveform before the first play.
                if (PositionSeconds > 0)
                    player.Seek(TimeSpan.FromSeconds(PositionSeconds));
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
