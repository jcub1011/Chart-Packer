using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using ChartBuilder.Controls;
using ChartBuilder.Resources;
using ChartPacker.Audio.Mixing;
using ChartPacker.Audio.Playback;
using ChartPacker.Audio.Synthesis;
using ChartPacker.Audio.Waveforms;
using ChartPacker.Core.Charting;
using ChartPacker.Core.Scanning;
using ChartPacker.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChartBuilder.ViewModels
{
    /// <summary>
    /// Plays the open song's stems (charter-spec C10): play and pause, speed with pitch correction, and a metronome laid
    /// over the stems after the time-stretcher. Gives the timeline the song's length, the mix's waveform, the chart's
    /// beat lines and a smooth clock.
    /// </summary>
    /// <remarks>
    /// Everything is in audio time: the beats are the chart's, moved by the song's delay (charter-spec §4.4.1). What's
    /// drawn isn't moved by the audio/visual latency yet; that comes with calibration (C11).
    /// </remarks>
    public sealed partial class PlaybackViewModel : ViewModelBase, IDisposable
    {
        /// <summary>How long the timeline is when there's no audio to say.</summary>
        private const double PlaceholderLengthSeconds = 180;

        // How often the position shown as text follows the playhead while playing.
        private static readonly TimeSpan PositionInterval = TimeSpan.FromMilliseconds(50);

        // How often the waveform is drawn again as its peaks are read.
        private static readonly TimeSpan PeaksProgressInterval = TimeSpan.FromMilliseconds(250);

        private static readonly WaveformResolution PeaksResolution = WaveformResolution.Medium;

        private readonly OpenedSong? _song;
        private readonly Func<int, IAudioOutput>? _openOutput;
        private readonly IReadOnlyList<SongTrack> _tracks;
        private readonly DispatcherTimer _timer;
        private readonly CancellationTokenSource _cancel = new();

        // The peaks read so far, by track, from the reading threads; under the lock.
        private readonly Dictionary<SongTrack, StemPeaks> _peaks = [];
        private readonly Lock _peaksLock = new();

        private SongAudio? _audio;
        private StemPlayer? _player;
        private MetronomeSchedule? _metronome;
        private bool _settingPosition;
        private bool _disposed;

        /// <summary>No song: an empty timeline, with nothing to play.</summary>
        public PlaybackViewModel()
            : this(null, null)
        {
        }

        /// <param name="song">The song to play; none for an empty timeline.</param>
        /// <param name="openOutput">Opens the audio output at a rate.</param>
        public PlaybackViewModel(OpenedSong? song, Func<int, IAudioOutput>? openOutput)
        {
            _song = song;
            _openOutput = openOutput;
            _tracks = song is null ? [] : SongTracks.Resolve(song.StoredNames, null);
            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = PositionInterval };
            _timer.Tick += (_, _) => RefreshPosition();
            LengthSeconds = PlaceholderLengthSeconds;
        }

        public static IReadOnlyList<int> SpeedPercents { get; } = [25, 50, 75, 100, 125, 150, 200];

        /// <summary>Whether there's audio to play.</summary>
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(PlayPauseCommand))]
        public partial bool IsReady { get; private set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(PlayPauseToolTip))]
        public partial bool IsPlaying { get; private set; }

        /// <summary>Where the song is, in seconds of audio: followed while playing, and moved by scrubbing.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(PositionText))]
        public partial double PositionSeconds { get; set; }

        /// <summary>How long the audio is, in seconds: what the timeline spans.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(PositionText))]
        public partial double LengthSeconds { get; private set; }

        /// <summary>The mix's waveform, as much as has been read.</summary>
        [ObservableProperty]
        public partial Waveform? Waveform { get; private set; }

        /// <summary>The chart's beat and measure lines, in audio time.</summary>
        [ObservableProperty]
        public partial IReadOnlyList<TimelineBeat> Beats { get; private set; } = [];

        /// <summary>The speed, as a percentage of the song's own.</summary>
        [ObservableProperty]
        public partial int SpeedPercent { get; set; } = 100;

        /// <summary>Whether a speed other than 100% keeps the audio's pitch. On by default, as in Chart Packer's inspector.</summary>
        [ObservableProperty]
        public partial bool PitchCorrection { get; set; } = true;

        /// <summary>Whether the metronome clicks each beat of the chart's tempo map.</summary>
        [ObservableProperty]
        public partial bool IsMetronomeOn { get; set; }

        /// <summary>Why the audio can't be played, or what of it can't; null when it all can.</summary>
        [ObservableProperty]
        public partial string? Message { get; private set; }

        /// <summary>The playhead and the song's length, e.g. "0:42.0 / 3:00.0".</summary>
        public string PositionText => string.Format(
            CultureInfo.CurrentCulture, Strings.TimelinePositionFormat, FormatTime(PositionSeconds), FormatTime(LengthSeconds));

        public string PlayPauseToolTip => IsPlaying ? Strings.PauseToolTip : Strings.PlayToolTip;

        /// <summary>Where the audio is heard to be now, moving smoothly, for drawing against it every frame.</summary>
        public Func<TimeSpan> Clock => () => _player?.SmoothPosition ?? TimeSpan.FromSeconds(PositionSeconds);

        /// <summary>
        /// Opens the song's audio, then reads its waveform in the background. Never throws: what can't be played is said
        /// in <see cref="Message"/>.
        /// </summary>
        public async Task OpenAsync()
        {
            if (_song is null || _openOutput is null)
                return;

            Beats = BeatsOf(_song, LengthOf(_song, null));
            if (_tracks.Count == 0)
            {
                Message = Strings.AudioNoStems;
                return;
            }

            try
            {
                var token = _cancel.Token;
                var (audio, player) = await Task.Run(() => Open(_song.Entry, _tracks, _openOutput, token), token);
                if (_disposed)
                {
                    player.Dispose();
                    audio.Dispose();
                    return;
                }

                _audio = audio;
                _player = player;
                player.PitchCorrection = PitchCorrection;
                player.Speed = SpeedPercent / 100f;
                player.Stopped += (_, _) => Dispatcher.UIThread.Post(OnStopped);

                LengthSeconds = LengthOf(_song, player);
                Beats = BeatsOf(_song, LengthSeconds);
                _metronome = new MetronomeSchedule(_song.Chart.TempoMap.ToTempoMap(), _song.Delay, TimeSpan.FromSeconds(LengthSeconds), player.Mixer.OutputRate);
                player.Sounds = IsMetronomeOn ? _metronome : null;
                IsReady = true;

                if (audio.Tracks.FirstOrDefault(t => t.Error is not null) is { } failed)
                    Message = string.Format(CultureInfo.CurrentCulture, Strings.AudioTrackFailedFormat, failed.Track.StoredName, failed.Error);

                StartPeaks(_song.Entry, [.. audio.Tracks.Where(t => t.Source is not null).Select(t => t.Track)], token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                // Nothing awaits this, so anything not caught here would be lost; the chart still opens without audio.
                if (ex is not (AudioDeviceException or IOException or UnauthorizedAccessException or InvalidDataException))
                    Trace.TraceError($"Opening the audio failed: {ex}");
                Message = string.Format(CultureInfo.CurrentCulture, Strings.AudioUnavailableFormat, ex.Message);
            }
        }

        [RelayCommand(CanExecute = nameof(IsReady))]
        private void PlayPause()
        {
            if (_player is not { } player)
                return;

            if (player.IsPlaying)
            {
                player.Pause();
                SetPlaying(false);
            }
            else
            {
                player.Play();
                SetPlaying(true);
            }
        }

        partial void OnPositionSecondsChanged(double value)
        {
            // Scrubbing moves the song; following it as it plays doesn't.
            if (!_settingPosition && _player is { } player)
                player.Seek(TimeSpan.FromSeconds(Math.Max(0, value)));
        }

        partial void OnSpeedPercentChanged(int value)
        {
            if (_player is { } player)
                player.Speed = value / 100f;
        }

        partial void OnPitchCorrectionChanged(bool value)
        {
            if (_player is { } player)
                player.PitchCorrection = value;
        }

        partial void OnIsMetronomeOnChanged(bool value)
        {
            if (_player is { } player)
                player.Sounds = value ? _metronome : null;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _timer.Stop();
            _cancel.Cancel();

            // The player before the sources it reads.
            _player?.Dispose();
            _audio?.Dispose();
            _player = null;
            _audio = null;
        }

        private void OnStopped()
        {
            if (_disposed || _player is not { } player)
                return;

            SetPlaying(false);
            if (player.Error is { } error)
                Message = string.Format(CultureInfo.CurrentCulture, Strings.AudioUnavailableFormat, error);
        }

        private void SetPlaying(bool playing)
        {
            IsPlaying = playing;
            if (playing)
                _timer.Start();
            else
                _timer.Stop();
            RefreshPosition();
        }

        private void RefreshPosition()
        {
            if (_player is not { } player)
                return;

            _settingPosition = true;
            try
            {
                PositionSeconds = player.Position.TotalSeconds;
            }
            finally
            {
                _settingPosition = false;
            }
        }

        /// <summary>Opens the tracks and a player for them, the preview muted, as in Chart Packer's inspector.</summary>
        private static (SongAudio Audio, StemPlayer Player) Open(SongEntry entry, IReadOnlyList<SongTrack> tracks, Func<int, IAudioOutput> openOutput, CancellationToken cancellationToken)
        {
            var audio = SongAudio.Open(entry, tracks, cancellationToken);
            try
            {
                if (audio.Sources.Count == 0)
                    throw new InvalidDataException(audio.Tracks.FirstOrDefault(t => t.Error is not null)?.Error ?? Strings.AudioNoStems);

                var mixer = new StemMixer(audio.Sources);
                foreach (var stem in mixer.Stems)
                    stem.IsMuted = tracks.Any(t => t.StoredName == stem.Name && t.Stem == AudioStems.Preview);

                return (audio, new StemPlayer(mixer, openOutput(mixer.OutputRate)));
            }
            catch
            {
                audio.Dispose();
                throw;
            }
        }

        /// <summary>The audio's length; without audio, song.ini's <c>song_length</c>, or else a placeholder.</summary>
        private static double LengthOf(OpenedSong song, StemPlayer? player)
        {
            if (player is { Length.TotalSeconds: > 0 } )
                return player.Length.TotalSeconds;
            if (long.TryParse(song.Value("song_length"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long ms) && ms > 0)
                return ms / 1000.0;
            return PlaceholderLengthSeconds;
        }

        /// <summary>The chart's beat lines from 0 to <paramref name="length"/> seconds of audio.</summary>
        private static IReadOnlyList<TimelineBeat> BeatsOf(OpenedSong song, double length)
        {
            var tempoMap = song.Chart.TempoMap.ToTempoMap();
            double delay = song.Delay.TotalSeconds;
            long lastTick = (long)Math.Ceiling(tempoMap.TickAt(length - delay));
            if (lastTick < 0)
                return [];

            var beats = new List<TimelineBeat>();
            foreach (var line in tempoMap.BeatLines(0, lastTick))
            {
                double seconds = tempoMap.SecondsAt(line.Tick) + delay;
                if (seconds >= 0 && seconds <= length)
                    beats.Add(new TimelineBeat(seconds, line.IsMeasure));
            }

            return beats;
        }

        /// <summary>Reads the tracks' peaks in the background, from the cache where it has them, and draws the mix as they come.</summary>
        private void StartPeaks(SongEntry entry, IReadOnlyList<SongTrack> tracks, CancellationToken cancellationToken) =>
            _ = Task.Run(() => ReadPeaks(entry, tracks, cancellationToken), cancellationToken);

        private void ReadPeaks(SongEntry entry, IReadOnlyList<SongTrack> tracks, CancellationToken cancellationToken)
        {
            try
            {
                // Each key is taken before its file is read, so what's kept under it is never older than the file it names.
                var keys = new Dictionary<SongTrack, StemPeaksKey?>();
                foreach (var track in tracks)
                {
                    var key = StemPeaksCache.KeyOf(entry, track);
                    if (key is { } found && StemPeaksCache.Shared.TryGet(found, PeaksResolution, out var cached))
                        ShowPeaks(track, cached, cancellationToken);
                    else
                        keys[track] = key;
                }

                if (keys.Count == 0)
                    return;

                // Separate from the player's own sources, which are for the audio thread alone.
                using var audio = SongAudio.Open(entry, [.. keys.Keys], cancellationToken);
                ChunkedPeaksRead.Read(
                    audio,
                    PeaksResolution,
                    completed: (opened, peaks) =>
                    {
                        ShowPeaks(opened.Track, peaks, cancellationToken);
                        if (keys[opened.Track] is { } key)
                            StemPeaksCache.Shared.Add(key, peaks);
                    },
                    failed: opened => ForgetPeaks(opened.Track, cancellationToken),
                    progress: (opened, partial) => ShowPeaks(opened.Track, partial, cancellationToken),
                    PeaksProgressInterval,
                    cancellationToken);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException or UnauthorizedAccessException or InvalidDataException)
            {
                // No waveform; the song still plays.
                if (ex is not OperationCanceledException)
                    Trace.TraceWarning($"Reading the waveform failed: {ex.Message}");
            }
        }

        private void ShowPeaks(SongTrack track, StemPeaks peaks, CancellationToken cancellationToken)
        {
            lock (_peaksLock)
                _peaks[track] = peaks;
            PostWaveform(cancellationToken);
        }

        private void ForgetPeaks(SongTrack track, CancellationToken cancellationToken)
        {
            lock (_peaksLock)
                _peaks.Remove(track);
            PostWaveform(cancellationToken);
        }

        /// <summary>Draws the mix of the peaks read so far, the preview left out as it's muted.</summary>
        private void PostWaveform(CancellationToken cancellationToken)
        {
            List<AudibleStem> stems;
            lock (_peaksLock)
                stems = [.. _peaks.Select(p => new AudibleStem(p.Value, IsMuted: p.Key.Stem == AudioStems.Preview, IsSolo: false))];

            var waveform = WaveformPeaks.Mix(stems);
            Dispatcher.UIThread.Post(() =>
            {
                if (!cancellationToken.IsCancellationRequested)
                    Waveform = waveform;
            });
        }

        /// <summary>A time as m:ss.f, or h:mm:ss.f from an hour, e.g. 3:05.2.</summary>
        private static string FormatTime(double seconds) =>
            SongTime.Format(TimeSpan.FromSeconds(Math.Max(0, seconds)), decimals: 1);
    }
}
