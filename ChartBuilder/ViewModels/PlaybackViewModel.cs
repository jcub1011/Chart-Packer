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
using ChartBuilder.Services;
using ChartShared.Audio.Mixing;
using ChartShared.Audio.Playback;
using ChartShared.Audio.Synthesis;
using ChartShared.Audio.Waveforms;
using ChartShared.Core.Charting;
using ChartShared.Core.Scanning;
using ChartShared.UI.Services;
using ChartShared.UI.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChartBuilder.ViewModels
{
    /// <summary>
    /// Plays the open song's stems (charter-spec C10): play and pause, speed with pitch correction, a region played once
    /// or looped, each stem's mute, solo and volume, and a metronome and hit sounds laid over the stems after the
    /// time-stretcher. Gives the timeline the song's length, the mix's waveform, the chart's beat lines and a smooth
    /// clock.
    /// </summary>
    /// <remarks>
    /// Everything is in audio time: the beats are the chart's, moved by the song's delay (charter-spec §4.4.1). What's
    /// drawn against the audio while it plays is drawn the calibrated latency earlier, times the speed (C10 item 5),
    /// for the device the audio is on (C11).
    /// </remarks>
    public sealed partial class PlaybackViewModel : ViewModelBase, IDisposable, ITrackMixer
    {
        /// <summary>How long the timeline is when there's no audio to say.</summary>
        private const double PlaceholderLengthSeconds = 180;

        // How often the position shown as text follows the playhead while playing.
        private static readonly TimeSpan PositionInterval = TimeSpan.FromMilliseconds(50);

        private readonly EditorSong? _song;
        private readonly Func<int, IAudioOutput>? _openOutput;
        private readonly AudioLatencyService? _latency;
        private readonly Func<(int Metronome, int Hits)> _volumes;
        private readonly Action? _calibrate;
        private readonly DispatcherTimer _timer;
        private readonly CancellationTokenSource _cancel = new();
        private readonly EditorAudio? _editorAudio;

        private SongAudio? _audio;
        private StemPlayer? _player;
        private MetronomeSchedule? _metronome;
        private HitSoundSchedule? _hits;
        private IReadOnlyList<NoteHit> _noteHits = [];
        private IDisposable? _followingDevice;
        private IReadOnlyList<SongTrack> _tracks;
        private bool _settingPosition;
        private bool _seeking;
        private bool _disposed;

        // Counts the opens, so an open that finishes after a later one began is let go of.
        private int _openings;

        // Each stem's mute, solo and volume as last set, kept as the tracks are opened again; a stem not set has its default.
        private readonly Dictionary<string, (bool IsMuted, bool IsSolo, double Volume)> _switches = new(StringComparer.Ordinal);

        // Clock, made once: it's read every frame.
        private Func<TimeSpan>? _clock;

        /// <summary>No song: an empty timeline, with nothing to play.</summary>
        public PlaybackViewModel()
            : this(null, null, null)
        {
        }

        /// <param name="song">The song or new chart to play; none for an empty timeline.</param>
        /// <param name="openOutput">Opens the audio output at a rate.</param>
        /// <param name="latency">The audio/visual latency, which follows the output's device.</param>
        /// <param name="volumes">The metronome's and the hit sounds' volumes, in percent, as Settings has them now.</param>
        /// <param name="calibrate">Opens the calibration tool, from the transport bar's latency (charter-spec C11).</param>
        /// <param name="editorAudio">Where the mix's waveform comes from, as its stems' peaks are read.</param>
        public PlaybackViewModel(EditorSong? song, Func<int, IAudioOutput>? openOutput, AudioLatencyService? latency, Func<(int Metronome, int Hits)>? volumes = null, Action? calibrate = null, EditorAudio? editorAudio = null)
        {
            _song = song;
            _openOutput = openOutput;
            _latency = latency;
            _volumes = volumes ?? (() => (50, 50));
            _calibrate = calibrate;
            _tracks = song?.Tracks ?? [];
            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = PositionInterval };
            _timer.Tick += (_, _) => RefreshPosition();
            LengthSeconds = PlaceholderLengthSeconds;
            if (latency is not null)
                latency.Changed += OnLatencyChanged;
            _editorAudio = editorAudio;
            if (editorAudio is not null)
                editorAudio.Changed += OnWaveformsChanged;
        }

        /// <summary>Raised when the playhead is moved by hand, on the timeline, rather than by playing or by <see cref="SeekTo"/>.</summary>
        public event EventHandler? Scrubbed;

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

        /// <summary>Whether each note shown sounds as it reaches the strike line (charter-spec C10 item 4).</summary>
        [ObservableProperty]
        public partial bool IsHitSoundsOn { get; set; }

        /// <summary>Whether playing repeats the loop region (charter-spec C10 item 2).</summary>
        [ObservableProperty]
        public partial bool IsLooping { get; set; }

        /// <summary>The loop region, in seconds of audio, or null for none.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasLoop), nameof(LoopToolTip))]
        public partial LoopRegion? Loop { get; private set; }

        public bool HasLoop => Loop is not null;

        /// <summary>The loop region, in words, for the loop button.</summary>
        public string LoopToolTip => Loop is { } loop
            ? string.Format(CultureInfo.CurrentCulture, Strings.LoopToolTipFormat, FormatTime(loop.Start), FormatTime(loop.End))
            : Strings.LoopNoneToolTip;

        /// <summary>
        /// Each track, with its mute, solo and volume and its waveform, for Chart settings' Tracks. The switches are for
        /// listening: not undone, and not saved.
        /// </summary>
        [ObservableProperty]
        public partial IReadOnlyList<TrackRowViewModel> Tracks { get; private set; } = [];

        /// <summary>The calibrated latency, as the transport bar shows it, e.g. "42 ms".</summary>
        public string LatencyText => string.Format(CultureInfo.CurrentCulture, Strings.LatencyFormat, _latency?.Milliseconds ?? 0);

        /// <summary>Why the audio can't be played, or what of it can't; null when it all can.</summary>
        [ObservableProperty]
        public partial string? Message { get; private set; }

        /// <summary>The playhead and the song's length, e.g. "0:42.0 / 3:00.0".</summary>
        public string PositionText => string.Format(
            CultureInfo.CurrentCulture, Strings.TimelinePositionFormat, FormatTime(PositionSeconds), FormatTime(LengthSeconds));

        public string PlayPauseToolTip => IsPlaying ? Strings.PauseToolTip : Strings.PlayToolTip;

        /// <summary>
        /// Where to draw what's drawn against the audio now, moving smoothly, for drawing every frame: where the audio
        /// is, less the calibrated latency times the speed (charter-spec C10 item 5).
        /// </summary>
        /// <remarks>While paused, nothing heard lags behind, so it's the audio's position.</remarks>
        public Func<TimeSpan> Clock => _clock ??= () => _player is { } player
            ? player.IsPlaying ? LatencyCompensation.Drawn(player.SmoothPosition, _latency?.Milliseconds ?? 0, player.Speed) : player.Position
            : TimeSpan.FromSeconds(PositionSeconds);

        /// <summary>Moves the playhead to <paramref name="seconds"/> as the editor's cursor moves, without raising <see cref="Scrubbed"/>.</summary>
        public void SeekTo(double seconds)
        {
            _seeking = true;
            try
            {
                PositionSeconds = Math.Max(0, seconds);
            }
            finally
            {
                _seeking = false;
            }
        }

        /// <summary>Plays from <paramref name="start"/> to <paramref name="end"/>, in seconds of audio, then pauses (Shift+Space).</summary>
        public void PlayRegion(double start, double end)
        {
            if (_player is not { } player || end <= start)
                return;
            UpdateSounds();
            player.PlayRegion(TimeSpan.FromSeconds(Math.Max(0, start)), TimeSpan.FromSeconds(end), loops: false);
            SetPlaying(true);
        }

        /// <summary>Sets the loop region, in seconds of audio, and turns looping on (L).</summary>
        public void SetLoop(double start, double end)
        {
            if (end <= start)
                return;
            Loop = new LoopRegion(Math.Max(0, start), end);
            if (IsLooping && _player is { IsPlaying: true } player)
                player.PlayRegion(TimeSpan.FromSeconds(Loop.Start), TimeSpan.FromSeconds(Loop.End), loops: true);
            IsLooping = true;
        }

        /// <summary>Takes the notes' hit sounds, in audio time, which play while <see cref="IsHitSoundsOn"/>.</summary>
        public void SetHits(IReadOnlyList<NoteHit> hits)
        {
            _noteHits = hits;
            _hits = _player is { } player ? new HitSoundSchedule(hits, player.Mixer.OutputRate) : null;
            UpdateSounds();
        }

        /// <summary>Pauses playback, as when the calibration tool opens over the Editor.</summary>
        public void Pause()
        {
            if (_player is { IsPlaying: true } player)
            {
                player.Pause();
                SetPlaying(false);
            }
        }

        /// <summary>Redraws the beats, and moves the metronome onto them, once the chart's tempo map has changed.</summary>
        public void RefreshTempoMap()
        {
            if (_song is null)
                return;

            Beats = BeatsOf(_song, LengthSeconds);
            if (_player is { } player)
            {
                _metronome = new MetronomeSchedule(_song.Chart.TempoMap.ToTempoMap(), _song.Delay, TimeSpan.FromSeconds(LengthSeconds), player.Mixer.OutputRate);
                UpdateSounds();
            }
        }

        /// <summary>
        /// Opens the song's audio, then reads its waveform in the background. Never throws: what can't be played is said
        /// in <see cref="Message"/>.
        /// </summary>
        public async Task OpenAsync()
        {
            if (_song is null || _openOutput is null)
                return;

            Beats = BeatsOf(_song, LengthOf(_song, null));
            int opening = ++_openings;
            Tracks = [.. _tracks.Select(RowOf)];
            RefreshTrackWaveforms();
            if (_tracks.Count == 0)
            {
                // A new chart may start with no audio, against a silent timeline (charter-spec C1).
                if (!_song.IsNew)
                    Message = Strings.AudioNoStems;
                return;
            }

            try
            {
                var token = _cancel.Token;
                var tracks = _tracks;
                var switches = Tracks.ToDictionary(r => r.Name, r => (r.IsMuted, r.IsSolo, r.Volume), StringComparer.Ordinal);
                var (audio, player, output) = await Task.Run(() => Open(_song.Entry, tracks, switches, _openOutput, token), token);
                if (_disposed || opening != _openings)
                {
                    player.Dispose();
                    audio.Dispose();
                    return;
                }

                _audio = audio;
                _player = player;
                _followingDevice = _latency?.Follow(output);
                player.PitchCorrection = PitchCorrection;
                player.Speed = SpeedPercent / 100f;
                player.Stopped += (_, _) => Dispatcher.UIThread.Post(OnStopped);

                LengthSeconds = LengthOf(_song, player);
                Beats = BeatsOf(_song, LengthSeconds);
                _metronome = new MetronomeSchedule(_song.Chart.TempoMap.ToTempoMap(), _song.Delay, TimeSpan.FromSeconds(LengthSeconds), player.Mixer.OutputRate);
                _hits = new HitSoundSchedule(_noteHits, player.Mixer.OutputRate);
                UpdateSounds();
                IsReady = true;
                foreach (var opened in audio.Tracks)
                {
                    if (opened.Error is { } trackError && Tracks.FirstOrDefault(r => r.Track == opened.Track) is { } row)
                        row.Error = trackError;
                }

                if (audio.Tracks.FirstOrDefault(t => t.Error is not null) is { } failed)
                    Message = string.Format(CultureInfo.CurrentCulture, Strings.AudioTrackFailedFormat, failed.Track.StoredName, failed.Error);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                // Nothing awaits this, so anything not caught here would be lost; the chart still opens without audio.
                if (ex is not (AudioDeviceException or IOException or UnauthorizedAccessException or InvalidDataException))
                    Trace.TraceError($"Opening the audio failed: {ex}");
                if (opening == _openings)
                    Message = string.Format(CultureInfo.CurrentCulture, Strings.AudioUnavailableFormat, ex.Message);
            }
        }

        /// <summary>
        /// Lets go of the audio and its files, as a save that replaces or removes a track needs; <see cref="ReloadAsync"/>
        /// opens it again. Playback stops.
        /// </summary>
        public void Release()
        {
            ++_openings;
            if (_player is { IsPlaying: true } playing)
            {
                playing.Pause();
                SetPlaying(false);
            }

            IsReady = false;
            _followingDevice?.Dispose();
            _followingDevice = null;
            _player?.Dispose();
            _audio?.Dispose();
            _player = null;
            _audio = null;
        }

        /// <summary>
        /// Plays <paramref name="tracks"/> from now on, after a track was added, replaced or removed, or the song saved:
        /// opens them at the same place, playing on if it was, with each stem's mute, solo and volume as they were.
        /// </summary>
        public async Task ReloadAsync(IReadOnlyList<SongTrack> tracks)
        {
            ArgumentNullException.ThrowIfNull(tracks);
            if (_disposed)
                return;

            bool wasPlaying = IsPlaying;
            double position = PositionSeconds;
            Release();
            _tracks = tracks;
            Message = null;
            await OpenAsync();
            if (_disposed || _player is null)
                return;

            SeekTo(Math.Min(position, LengthSeconds));
            if (wasPlaying)
                PlayPauseCommand.Execute(null);
        }

        /// <inheritdoc/>
        public void ApplySwitches(TrackRowViewModel row)
        {
            ArgumentNullException.ThrowIfNull(row);
            _switches[row.Track.Stem] = (row.IsMuted, row.IsSolo, row.Volume);
            TrackRowViewModel.RefreshHeard(Tracks);
            if (_player is null)
                return;

            foreach (var stem in _player.Mixer.Stems.Where(s => s.Name == row.Name))
            {
                stem.IsMuted = row.IsMuted;
                stem.IsSolo = row.IsSolo;
                stem.Volume = (float)row.Volume;
            }
        }

        /// <inheritdoc/>
        public void RememberVolume(TrackRowViewModel row)
        {
            // ApplySwitches keeps it, with the switches, for as long as the chart is open.
        }

        // A track's row, switched as its stem last was; the preview starts muted, as in Chart Packer's inspector.
        private TrackRowViewModel RowOf(SongTrack track)
        {
            var row = new TrackRowViewModel(this, track);
            if (_switches.TryGetValue(track.Stem, out var kept))
            {
                row.IsMuted = kept.IsMuted;
                row.IsSolo = kept.IsSolo;
                row.Volume = kept.Volume;
            }

            return row;
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
                return;
            }

            // The volumes may have changed in Settings since.
            UpdateSounds();
            if (IsLooping && Loop is { } loop)
                player.PlayRegion(TimeSpan.FromSeconds(loop.Start), TimeSpan.FromSeconds(loop.End), loops: true);
            else
                player.Play();
            SetPlaying(true);
        }

        [RelayCommand]
        private void OpenCalibration() => _calibrate?.Invoke();

        partial void OnPositionSecondsChanged(double value)
        {
            // Scrubbing moves the song; following it as it plays doesn't.
            if (_settingPosition)
                return;
            _player?.Seek(TimeSpan.FromSeconds(Math.Max(0, value)));
            if (!_seeking)
                Scrubbed?.Invoke(this, EventArgs.Empty);
        }

        partial void OnIsLoopingChanged(bool value)
        {
            if (_player is not { IsPlaying: true } player)
                return;
            if (!value)
                player.ClearRegion();
            else if (Loop is { } loop)
                player.PlayRegion(TimeSpan.FromSeconds(loop.Start), TimeSpan.FromSeconds(loop.End), loops: true);
        }

        partial void OnIsHitSoundsOnChanged(bool value) => UpdateSounds();

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

        partial void OnIsMetronomeOnChanged(bool value) => UpdateSounds();

        /// <summary>Lays the metronome and the hit sounds that are on over the stems, each at its volume.</summary>
        private void UpdateSounds()
        {
            if (_player is not { } player)
                return;

            // At 50% each is at the level it's made at, which leaves room above it.
            var (metronome, hits) = _volumes();
            var parts = new List<(ISoundSchedule, float)>();
            if (IsMetronomeOn && _metronome is not null)
                parts.Add((_metronome, Math.Clamp(metronome, 0, 100) / 50f));
            if (IsHitSoundsOn && _hits is not null)
                parts.Add((_hits, Math.Clamp(hits, 0, 100) / 50f));
            var combined = new CombinedSchedule([.. parts]);
            player.Sounds = combined.IsEmpty ? null : combined;
        }

        private void OnLatencyChanged(object? sender, EventArgs e) =>
            Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(LatencyText)));

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _timer.Stop();
            _cancel.Cancel();
            if (_latency is not null)
                _latency.Changed -= OnLatencyChanged;
            if (_editorAudio is not null)
                _editorAudio.Changed -= OnWaveformsChanged;

            // The player before the sources it reads.
            _followingDevice?.Dispose();
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

        /// <summary>Opens the tracks and a player for them, each stem switched as its row is.</summary>
        private static (SongAudio Audio, StemPlayer Player, IAudioOutput Output) Open(SongEntry? entry, IReadOnlyList<SongTrack> tracks, IReadOnlyDictionary<string, (bool IsMuted, bool IsSolo, double Volume)> switches, Func<int, IAudioOutput> openOutput, CancellationToken cancellationToken)
        {
            var audio = SongAudio.Open(entry, tracks, cancellationToken);
            try
            {
                if (audio.Sources.Count == 0)
                    throw new InvalidDataException(audio.Tracks.FirstOrDefault(t => t.Error is not null)?.Error ?? Strings.AudioNoStems);

                var mixer = new StemMixer(audio.Sources);
                foreach (var stem in mixer.Stems)
                {
                    if (switches.TryGetValue(stem.Name, out var set))
                        (stem.IsMuted, stem.IsSolo, stem.Volume) = (set.IsMuted, set.IsSolo, (float)set.Volume);
                }

                var output = openOutput(mixer.OutputRate);
                return (audio, new StemPlayer(mixer, output), output);
            }
            catch
            {
                audio.Dispose();
                throw;
            }
        }

        /// <summary>The audio's length; without audio, song.ini's <c>song_length</c>, or else a placeholder.</summary>
        private static double LengthOf(EditorSong song, StemPlayer? player)
        {
            if (player is { Length.TotalSeconds: > 0 } )
                return player.Length.TotalSeconds;
            if (long.TryParse(song.Value("song_length"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long ms) && ms > 0)
                return ms / 1000.0;
            return PlaceholderLengthSeconds;
        }

        /// <summary>The chart's beat lines from 0 to <paramref name="length"/> seconds of audio.</summary>
        private static IReadOnlyList<TimelineBeat> BeatsOf(EditorSong song, double length)
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

        /// <summary>Draws the mix of the peaks read so far, the preview left out as it's muted, and each track's own.</summary>
        private void OnWaveformsChanged(object? sender, EventArgs e)
        {
            if (_disposed || _editorAudio is not { } audio)
                return;
            Waveform = audio.Mix is { } mix ? audio.Waveforms.Mono(mix) : null;
            RefreshTrackWaveforms();
        }

        private void RefreshTrackWaveforms()
        {
            if (_editorAudio is not { } audio)
                return;
            foreach (var row in Tracks)
            {
                // A row whose track the editor's audio doesn't have yet keeps what it shows until it does.
                if (audio.Tracks.Contains(row.Track) && audio.Waveforms.Mono(AudioSource.Single(row.Track)) is { } waveform)
                    row.Waveform = waveform;
            }
        }

        /// <summary>A time as m:ss.f, or h:mm:ss.f from an hour, e.g. 3:05.2.</summary>
        private static string FormatTime(double seconds) =>
            SongTime.Format(TimeSpan.FromSeconds(Math.Max(0, seconds)), decimals: 1);
    }
}
