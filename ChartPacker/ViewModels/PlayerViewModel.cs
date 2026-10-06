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
using ChartPacker.Audio.Spectrum;
using ChartPacker.Audio.Waveforms;
using ChartPacker.Core.Metadata;
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
            IsMuted = track.Stem == AudioStems.Preview;
        }

        public SongTrack Track { get; }

        public string Name => Track.StoredName;

        /// <summary><c>+</c> for a track a staged change adds, like an added file.</summary>
        public string? Marker => Track.IsStaged ? "+" : null;

        /// <summary>The track's file size, formatted; set by whoever knows the song's files.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasSize))]
        public partial string? Size { get; set; }

        public bool HasSize => Size is not null;

        /// <summary>Saves a copy of the track's file, from its right-click menu; set by whoever knows where to save it. None, no menu.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanDownload))]
        public partial IAsyncRelayCommand? DownloadCommand { get; set; }

        public bool CanDownload => DownloadCommand is not null;

        public string DownloadHeader => SongFileSource.DownloadHeaderFor(Name);

        [ObservableProperty]
        public partial bool IsMuted { get; set; }

        /// <summary>While any track is soloed, only soloed tracks are heard. A track is muted or soloed, never both.</summary>
        [ObservableProperty]
        public partial bool IsSolo { get; set; }

        /// <summary>How loud the track is in the mix, from 0 to 1. Remembered for the song until the app closes.</summary>
        [ObservableProperty]
        public partial double Volume { get; set; } = 1;

        /// <summary>Why the track can't be played, once that's known.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasError))]
        public partial string? Error { get; set; }

        public bool HasError => Error is not null;

        /// <summary>The track's own waveform, at full level; empty until its audio has been read.</summary>
        [ObservableProperty]
        public partial Waveform Waveform { get; set; } = Waveform.Empty;

        /// <summary>Whether the track is in the mix as it's switched now: soloed, or unmuted with nothing soloed, and not turned all the way down.</summary>
        [ObservableProperty]
        public partial bool IsHeard { get; set; } = true;

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

    /// <summary>What <see cref="PlayerViewModel.TrackWaveformsChanged"/> is about.</summary>
    public sealed class TrackWaveformsChangedEventArgs(IReadOnlySet<string>? stems) : EventArgs
    {
        /// <summary>The tracks shown changed, so every waveform may have.</summary>
        public static readonly TrackWaveformsChangedEventArgs Tracks = new(null);

        /// <summary>
        /// The stems whose waveforms more has been read of, or which were dropped, with the tracks as they were;
        /// <see langword="null"/> when the tracks shown changed.
        /// </summary>
        public IReadOnlySet<string>? Stems { get; } = stems;

        /// <summary>Whether any of <paramref name="stems"/> may have a different waveform now.</summary>
        public bool Touches(IEnumerable<string> stems) => Stems is not { } changed || stems.Any(changed.Contains);
    }

    /// <summary>
    /// Plays the selected song's audio tracks together, in step, with each track muted or soloed
    /// as asked. Nothing is opened until the first play; <see cref="Unload"/> closes every file.
    /// </summary>
    public sealed partial class PlayerViewModel : ViewModelBase, IDisposable, ISpectrumSource
    {
        private static readonly TimeSpan PeaksDelay = TimeSpan.FromMilliseconds(300);
        private static readonly TimeSpan FilesClosedTimeout = TimeSpan.FromSeconds(2);

        // How often a track being read shows what has been read of it.
        private static readonly TimeSpan PeaksProgressInterval = TimeSpan.FromMilliseconds(250);

        private readonly Func<int, IAudioOutput> _openOutput;
        private readonly DispatcherTimer _timer;
        private SongEntry? _entry;
        private SongAudio? _audio;
        private StemPlayer? _player;

        // Bumped by every load and unload, so a slow open for an earlier song is dropped.
        private int _generation;

        // Set while the position is being updated from playback rather than by the user.
        private bool _isUpdatingPosition;

        // The waveform's per-track peaks, read on a background thread as the tracks are loaded. By track, so a
        // staged file replacing one of the same name is read afresh.
        private readonly Dictionary<SongTrack, StemPeaks> _peaks = [];

        // The tracks whose peaks cover only what had been read when their read stopped, to be read again.
        private readonly HashSet<SongTrack> _partialPeaks = [];
        private CancellationTokenSource? _peaksCts;
        private Task? _peaksTask;

        // Reads that were stopped but may not have let go of their files yet; see WhenFilesClosedAsync.
        private readonly List<Task> _closingReads = [];

        // A waveform rebuild has been posted and not run yet.
        private bool _isWaveformRebuildPending;

        // A waveform is being mixed in the background, and another was asked for while it was.
        private bool _isWaveformMixing;
        private bool _isWaveformRemixNeeded;

        // Moved on whenever the waveform is cleared, so that a mix begun before then is dropped rather than shown.
        private int _waveformGeneration;

        // The stems whose waveforms have changed since TrackWaveformsChanged was last raised, and the timer that raises it for them.
        private readonly HashSet<string> _changedWaveformStems = new(StringComparer.OrdinalIgnoreCase);
        private IDisposable? _waveformsChangedTimer;

        // The waveforms of several stems drawn together behind one lane, by the stems, with the peaks each was mixed from.
        private readonly Dictionary<string, (StemPeaks[] From, Waveform Mix)> _laneMixes = new(StringComparer.OrdinalIgnoreCase);

        // The lanes' mixes being made in the background, by the stems, so that each is only mixed once at a time.
        private readonly HashSet<string> _laneMixesMixing = new(StringComparer.OrdinalIgnoreCase);

        // A song the library asked to play, to be started when the detail view gives it its tracks.
        private SongEntry? _previewRequest;

        // Each song's track volumes, by song path and stem, for as long as the app runs. Nothing is saved to disk.
        private readonly Dictionary<(string Path, string Stem), double> _trackVolumes = [];

        private readonly SettingsService _settings;

        // For TryReadSpectrum, made when it's first asked for.
        private SpectrumAnalyzer? _analyzer;
        private readonly float[] _heard = new float[SpectrumAnalyzer.FftSize];

        /// <param name="settings">
        /// Where the master volume and which visualisers are shown are kept between runs, and the playback speed for this run.
        /// </param>
        /// <param name="openOutput">Opens the audio device at a sample rate.</param>
        public PlayerViewModel(SettingsService settings, Func<int, IAudioOutput> openOutput)
        {
            _settings = settings;
            Volume = settings.Settings.MasterVolume;
            PlaybackSpeed = settings.PlaybackSpeed;
            PitchCorrection = settings.Settings.PitchCorrection;
            ShowWaveforms = settings.Settings.ShowWaveforms;
            ShowLaneWaveforms = settings.Settings.ShowLaneWaveforms;
            ShowSpectrum = settings.Settings.ShowSpectrum;
            WaveformFidelity = settings.Settings.WaveformFidelity;
            ScrubStep = settings.Settings.ScrubStep;
            FineScrubMilliseconds = settings.Settings.FineScrubMilliseconds;
            LaneFineScrubFactor = settings.Settings.LaneFineScrubFactor;
            _openOutput = openOutput;
            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(33) };
            _timer.Tick += (_, _) => RefreshPosition();
        }

        public AvaloniaList<TrackRowViewModel> Tracks { get; } = [];

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(PlayPauseToolTip))]
        [NotifyPropertyChangedFor(nameof(ActiveEntry))]
        public partial bool IsPlaying { get; set; }

        /// <summary>What is heard from the selected tracks, as a waveform; empty until their audio has been read.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(LengthText))]
        [NotifyPropertyChangedFor(nameof(IsLengthKnown))]
        [NotifyPropertyChangedFor(nameof(LengthToolTip))]
        [NotifyPropertyChangedFor(nameof(TimelineSeconds))]
        public partial Waveform Waveform { get; set; } = Waveform.Empty;

        /// <summary>The audio is being opened for the first play.</summary>
        [ObservableProperty]
        public partial bool IsOpening { get; set; }

        /// <summary>Why playback isn't possible or stopped.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasError))]
        public partial string? Error { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(PositionText))]
        public partial double PositionSeconds { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(LengthText))]
        [NotifyPropertyChangedFor(nameof(IsLengthKnown))]
        [NotifyPropertyChangedFor(nameof(LengthToolTip))]
        [NotifyPropertyChangedFor(nameof(TimelineSeconds))]
        public partial double LengthSeconds { get; set; }

        /// <summary>
        /// How long the song plays for in a game, in seconds, which can run on past its audio: its <c>song_length</c>, or else
        /// worked out from its chart, audio and video; 0 while that isn't known. Set by the details panel. Playback plays on
        /// in silence past the audio to here, so what follows the clock does too.
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(LengthText))]
        [NotifyPropertyChangedFor(nameof(IsLengthKnown))]
        [NotifyPropertyChangedFor(nameof(LengthToolTip))]
        [NotifyPropertyChangedFor(nameof(TimelineSeconds))]
        public partial double SongLengthSeconds { get; set; }

        /// <summary>The master volume, from 0 to 1. Kept in the settings, so it carries over to the next run.</summary>
        [ObservableProperty]
        public partial double Volume { get; set; }

        /// <summary>
        /// How fast the song plays, as a fraction from 0.01 to 10: 1 is its own speed. The pitch moves with it unless
        /// <see cref="PitchCorrection"/> is on. Shared by every song and player for this run, but not saved.
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(PlaybackSpeedText))]
        public partial double PlaybackSpeed { get; set; } = 1;

        /// <summary>
        /// Whether a <see cref="PlaybackSpeed"/> other than 100% keeps the song's pitch, rather than raising or lowering it
        /// with the speed. Kept in the settings.
        /// </summary>
        [ObservableProperty]
        public partial bool PitchCorrection { get; set; }

        /// <summary>The speeds the playback speed box offers, as it shows them; any whole percent in range can be typed too.</summary>
        public static IReadOnlyList<string> PlaybackSpeedPresets { get; } =
            [.. new[] { 1, 10, 25, 50, 75, 90, 100, 110, 125, 150, 200, 300, 500, 1000 }.Select(FormatPercent)];

        /// <summary><see cref="PlaybackSpeed"/> as the playback speed box shows it: a whole percent.</summary>
        public string PlaybackSpeedText => FormatPercent((int)Math.Round(PlaybackSpeed * 100));

        /// <summary>
        /// Sets <see cref="PlaybackSpeed"/> from what was typed or picked in its box: a number of percent, with or without the
        /// <c>%</c>, rounded to a whole one and kept from 1% to 1000%. Either way the box is told to show the speed again,
        /// so what can't be read goes back to what it was.
        /// </summary>
        /// <returns>Whether <paramref name="text"/> could be read as a speed.</returns>
        public bool TrySetPlaybackSpeed(string? text)
        {
            string trimmed = (text ?? "").Trim();
            if (trimmed.EndsWith('%'))
                trimmed = trimmed[..^1].TrimEnd();

            bool isRead = double.TryParse(trimmed, NumberStyles.Float, CultureInfo.CurrentCulture, out double percent) && double.IsFinite(percent);
            if (isRead)
            {
                percent = Math.Clamp(Math.Round(percent), SettingsService.MinPlaybackSpeed * 100, SettingsService.MaxPlaybackSpeed * 100);
                PlaybackSpeed = percent / 100;
            }

            OnPropertyChanged(nameof(PlaybackSpeedText));
            return isRead;
        }

        private static string FormatPercent(int percent) => string.Format(CultureInfo.CurrentCulture, "{0}%", percent);

        /// <summary>
        /// Whether the tracks' waveforms are read and shown. Off, no track is read ahead of playback, so the song's
        /// length isn't known until it's played. Kept in the settings.
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(AreLaneWaveformsShown))]
        [NotifyPropertyChangedFor(nameof(LaneWaveformsToolTip))]
        public partial bool ShowWaveforms { get; set; }

        /// <summary>
        /// Whether the highways and the lyrics lane draw their tracks' waveforms behind their notes. Only while
        /// <see cref="ShowWaveforms"/> is on, as nothing is read otherwise. Kept in the settings.
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(AreLaneWaveformsShown))]
        public partial bool ShowLaneWaveforms { get; set; }

        /// <summary>The lanes draw their tracks' waveforms; see <see cref="ShowLaneWaveforms"/>.</summary>
        public bool AreLaneWaveformsShown => ShowWaveforms && ShowLaneWaveforms;

        /// <summary>What the lanes' waveform switch does, or, while waveforms are off, why it does nothing.</summary>
        public string LaneWaveformsToolTip => ShowWaveforms ? Strings.LaneWaveformsToolTip : Strings.LaneWaveformsNeedWaveformsToolTip;

        /// <summary>Whether the spectrum of what's heard is worked out and shown. Kept in the settings.</summary>
        [ObservableProperty]
        public partial bool ShowSpectrum { get; set; }

        /// <summary>How finely the tracks' waveforms are read; changed, they're read again. Kept in the settings.</summary>
        [ObservableProperty]
        public partial WaveformFidelity WaveformFidelity { get; set; }

        private WaveformResolution Resolution => WaveformResolution.Of(WaveformFidelity);

        /// <summary>How far Ctrl and a notch of the wheel move the audio over the seek bar or a lane. Kept in the settings.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(SeekBarToolTip))]
        [NotifyPropertyChangedFor(nameof(HighwayScrubToolTip))]
        [NotifyPropertyChangedFor(nameof(LyricsScrubToolTip))]
        public partial ScrubStep ScrubStep { get; set; }

        /// <summary>What the seek bar does with the pointer, and with Ctrl held, a line each.</summary>
        public string SeekBarToolTip => ScrubToolTip(Strings.SeekBarScrubToolTip, Strings.SeekBarFineScrubToolTip);

        /// <summary>What a highway does with the pointer, and with Shift and with Ctrl held, a line each.</summary>
        public string HighwayScrubToolTip => ScrubToolTip(Strings.HighwayScrubToolTip, Strings.HighwayFineScrubToolTip, Strings.HighwayOverviewToolTip);

        /// <summary>What the lyrics lane does with the pointer, and with Shift and with Ctrl held, a line each.</summary>
        public string LyricsScrubToolTip => ScrubToolTip(Strings.LyricsScrubToolTip, Strings.LyricsFineScrubToolTip, Strings.LyricsOverviewToolTip);

        /// <summary>How far each pixel dragged with Ctrl held on the seek bar moves the audio, in milliseconds. Kept in the settings.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(FineScrubSecondsPerPixel))]
        public partial double FineScrubMilliseconds { get; set; }

        /// <summary><see cref="FineScrubMilliseconds"/> in seconds, as the seek bar takes it.</summary>
        public double FineScrubSecondsPerPixel => FineScrubMilliseconds / 1000;

        /// <summary>How many times less far a drag with Ctrl held moves a highway's or the lyrics' notes than a plain drag. Kept in the settings.</summary>
        [ObservableProperty]
        public partial double LaneFineScrubFactor { get; set; }

        /// <summary>Where the song's <c>preview_start_time</c> puts the preview, if it sets one.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasPreviewStart))]
        [NotifyCanExecuteChangedFor(nameof(JumpToPreviewCommand))]
        public partial TimeSpan? PreviewStart { get; set; }

        public bool HasTracks => Tracks.Count > 0;

        /// <summary>The song whose tracks are shown, playing or not.</summary>
        public SongEntry? LoadedEntry => _entry;

        /// <summary>
        /// Raised at once when the tracks shown change, and while their waveforms are read, at most every
        /// <see cref="PeaksProgressInterval"/> for the stems read further since; see <see cref="TrackWaveformsChangedEventArgs"/>.
        /// </summary>
        public event EventHandler<TrackWaveformsChangedEventArgs>? TrackWaveformsChanged;

        /// <summary>The waveform of the first track shown of <paramref name="stem"/>, or <see langword="null"/> when there's no such track.</summary>
        public Waveform? WaveformOfStem(string stem) =>
            RowOfStem(stem)?.Waveform;

        /// <summary>
        /// The waveform of <paramref name="stems"/> heard together at full level, as far as they've been read: one's own,
        /// or the mix of several, as of a split drum kit. <see langword="null"/> when none of them is shown.
        /// </summary>
        public Waveform? LaneWaveformOf(IReadOnlyList<string> stems)
        {
            ArgumentNullException.ThrowIfNull(stems);
            if (stems.Count <= 1)
                return stems.Count == 0 ? null : WaveformOfStem(stems[0]);

            var peaks = new List<StemPeaks>(stems.Count);
            foreach (var stem in stems)
            {
                if (RowOfStem(stem) is { } row && _peaks.TryGetValue(row.Track, out var read))
                    peaks.Add(read);
            }

            if (peaks.Count == 0)
                return RowOfStem(stems[0])?.Waveform;

            // Mixed again only once one of them has been read further, and in the background, as a mix of several long
            // stems takes a while: meanwhile the lane keeps the mix it had, or the first stem's own waveform.
            string key = string.Join('|', stems);
            bool isKnown = _laneMixes.TryGetValue(key, out var known);
            if (isKnown && known.From.SequenceEqual(peaks))
                return known.Mix;

            _ = MixLaneAsync(key, stems, [.. peaks]);
            return isKnown ? known.Mix : RowOfStem(stems[0])?.Waveform;
        }

        /// <summary>
        /// Mixes <paramref name="peaks"/> for the lane of <paramref name="stems"/> on a background thread, unless it's being
        /// mixed already, then tells the lanes to ask for it again: the mix, if nothing was cleared meanwhile, or a newer
        /// one if they've been read further since. On the UI thread.
        /// </summary>
        private async Task MixLaneAsync(string key, IReadOnlyList<string> stems, StemPeaks[] peaks)
        {
            if (!_laneMixesMixing.Add(key))
                return;

            int generation = _waveformGeneration;
            try
            {
                var mix = await Task.Run(() => WaveformPeaks.Mix([.. peaks.Select(p => new AudibleStem(p, IsMuted: false, IsSolo: false))]));
                if (generation == _waveformGeneration)
                    _laneMixes[key] = (peaks, mix);
            }
            finally
            {
                _laneMixesMixing.Remove(key);
            }

            foreach (var stem in stems)
                ScheduleWaveformChanged(stem);
        }

        private TrackRowViewModel? RowOfStem(string stem) =>
            Tracks.FirstOrDefault(t => t.Track.Stem.Equals(stem, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Takes up the settings again, as after another player sharing them changed them: the master volume, the
        /// playback speed and pitch correction, and which visualisers are shown.
        /// </summary>
        public void ReloadSettings()
        {
            Volume = _settings.Settings.MasterVolume;
            PlaybackSpeed = _settings.PlaybackSpeed;
            PitchCorrection = _settings.Settings.PitchCorrection;
            ShowWaveforms = _settings.Settings.ShowWaveforms;
            ShowLaneWaveforms = _settings.Settings.ShowLaneWaveforms;
            ShowSpectrum = _settings.Settings.ShowSpectrum;
            WaveformFidelity = _settings.Settings.WaveformFidelity;
            ScrubStep = _settings.Settings.ScrubStep;
            FineScrubMilliseconds = _settings.Settings.FineScrubMilliseconds;
            LaneFineScrubFactor = _settings.Settings.LaneFineScrubFactor;
        }

        /// <summary>The song that is playing, or <see langword="null"/> when nothing is.</summary>
        public SongEntry? ActiveEntry => IsPlaying ? _entry : null;

        /// <summary>The seek slider is held; see <see cref="BeginScrub"/>.</summary>
        public bool IsScrubbing { get; private set; }

        /// <summary>
        /// Goes up each time the tracks are let go, for another song or other tracks, which stops playback and sends it back
        /// to the start; how a reload of the shown song tells it took playback down, rather than the user stopping it.
        /// </summary>
        public int TracksGeneration { get; private set; }

        /// <summary>
        /// Where playback is right now, read from the player rather than <see cref="PositionSeconds"/>, which
        /// only moves on a timer; or, while scrubbing or before the first play, where the seek bar is.
        /// </summary>
        public TimeSpan CurrentPosition => _player is { } player && !IsScrubbing ? player.Position : TimeSpan.FromSeconds(Math.Max(0, PositionSeconds));

        public string TracksHeader => string.Format(CultureInfo.CurrentCulture, Strings.TracksHeaderFormat, Tracks.Count);

        public bool HasError => Error is not null;

        public bool HasPreviewStart => PreviewStart is not null;

        public string PositionText => MetadataTimes.FormatDuration((long)(Math.Max(0, PositionSeconds) * 1000));

        /// <summary>
        /// The song's length is known: from <see cref="SongLengthSeconds"/>, from the player once the audio is open, and
        /// until then from how far the waveform reaches, the way the waveform itself is laid out before the first play.
        /// </summary>
        public bool IsLengthKnown => SongLengthSeconds > 0 || LengthSeconds > 0 || Waveform.Count > 0;

        /// <summary>
        /// How long the song is, in the same way as <see cref="LengthText"/>, or 0 while that isn't known: the longer of
        /// <see cref="SongLengthSeconds"/> and the audio, so neither is cut off. Every track's waveform is laid out across
        /// it, so they share one time axis.
        /// </summary>
        public double TimelineSeconds => Math.Max(Math.Max(0, SongLengthSeconds), LengthSeconds > 0 ? LengthSeconds : Waveform.Seconds);

        /// <summary>How long the song is, or a placeholder while that isn't known yet; see <see cref="IsLengthKnown"/>.</summary>
        public string LengthText => IsLengthKnown
            ? MetadataTimes.FormatDuration((long)(TimelineSeconds * 1000))
            : Strings.LengthPending;

        /// <summary>Why there's no length yet, while there isn't; otherwise no tool tip.</summary>
        public string? LengthToolTip => IsLengthKnown ? null : Strings.LengthPendingToolTip;

        /// <summary>What the play button does now, with Space, which does the same from anywhere in the window.</summary>
        public string PlayPauseToolTip => IsPlaying ? Strings.PauseShortcutToolTip : Strings.PlayShortcutToolTip;

        /// <summary>
        /// Shows <paramref name="tracks"/> of <paramref name="entry"/>. When they're the tracks
        /// already shown, nothing changes and playback carries on; otherwise playback stops, and
        /// each track keeps the switches its stem had, and its waveform if it was shown already.
        /// </summary>
        public void Load(SongEntry entry, IReadOnlyList<SongTrack> tracks, TimeSpan? previewStart)
        {
            PreviewStart = previewStart;
            bool isSameSong = entry == _entry;
            if (isSameSong && tracks.SequenceEqual(Tracks.Select(t => t.Track)))
            {
                StartRequestedPreview(entry);
                return;
            }

            var switches = isSameSong
                ? Tracks.GroupBy(t => t.Track.Stem).ToDictionary(g => g.Key, g => (g.First().IsMuted, g.First().IsSolo))
                : [];

            // A request for another song is of no use once this one is shown.
            if (entry != _previewRequest)
                _previewRequest = null;

            CancelPeaks();
            if (!isSameSong)
                ClearPeaks();
            TracksGeneration++;
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

                if (_peaks.TryGetValue(track, out var known))
                    row.Waveform = WaveformPeaks.Of(known);

                Tracks.Add(row);
            }

            RefreshHeard();
            OnPropertyChanged(nameof(HasTracks));
            OnPropertyChanged(nameof(TracksHeader));
            RefreshCommands();

            // Only the tracks that weren't shown before, or weren't read to the end, are read; the others keep their peaks.
            foreach (var gone in _peaks.Keys.Except(tracks).ToList())
            {
                _peaks.Remove(gone);
                _partialPeaks.Remove(gone);
            }

            RebuildWaveform();
            RaiseTracksChanged();
            if (ShowWaveforms)
                StartPeaks(entry, [.. tracks.Where(t => !_peaks.ContainsKey(t) || _partialPeaks.Contains(t))]);
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

        /// <summary>
        /// Completes once the waveform reads stopped by <see cref="Load"/> or <see cref="Unload"/> have let go of
        /// their files, or after a couple of seconds if one is stuck. Await it after <see cref="Unload"/> before
        /// replacing or deleting the song's files.
        /// </summary>
        public async Task WhenFilesClosedAsync()
        {
            _closingReads.RemoveAll(t => t.IsCompleted);
            if (_closingReads.Count > 0)
                await Task.WhenAny(Task.WhenAll(_closingReads), Task.Delay(FilesClosedTimeout));
        }

        /// <summary>Stops playback, closes every file and forgets the song.</summary>
        /// <remarks>A waveform read under way is stopped, but may hold its files a moment longer; see <see cref="WhenFilesClosedAsync"/>.</remarks>
        public void Unload()
        {
            CancelPeaks();
            ClearPeaks();
            TracksGeneration++;
            Close();
            _entry = null;
            Tracks.Clear();
            PreviewStart = null;
            OnPropertyChanged(nameof(HasTracks));
            OnPropertyChanged(nameof(TracksHeader));
            RefreshCommands();
            RaiseTracksChanged();
        }

        /// <summary>The seek slider is held: the position stops following playback until <see cref="EndScrub"/>.</summary>
        public void BeginScrub()
        {
            IsScrubbing = true;

            // From where the audio is, rather than where the timer last left the position, so a press that isn't
            // moved leaves playback where it is instead of a tick back.
            if (_player is { } player)
                SetPosition(player.Position.TotalSeconds);
        }

        /// <summary>The seek slider was let go: playback moves to where it was left.</summary>
        public void EndScrub()
        {
            IsScrubbing = false;
            _player?.Seek(TimeSpan.FromSeconds(PositionSeconds));
        }

        /// <summary>The spectrum of what's being heard, for the visualiser behind the details panel. On the UI thread.</summary>
        public bool TryReadSpectrum(Span<float> levels)
        {
            if (!ShowSpectrum || _player is not { } player || !player.TryReadHeard(_heard, out double rate))
                return false;

            _analyzer ??= new SpectrumAnalyzer();
            // Without pitch correction, what's heard is played faster or slower than it was mixed, and higher or lower with it.
            int heardRate = Math.Max(1, (int)Math.Round(rate));
            _analyzer.Analyze(_heard, heardRate, levels);
            return true;
        }

        public void Dispose()
        {
            _timer.Stop();
            _waveformsChangedTimer?.Dispose();
            _waveformsChangedTimer = null;
            CancelPeaks();
            Close();
            _waveformGeneration++;
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
            ScheduleWaveformRebuild();
            RefreshHeard();
            if (_player is null)
                return;

            foreach (var stem in _player.Mixer.Stems.Where(s => s.Name == row.Name))
            {
                stem.IsMuted = row.IsMuted;
                stem.IsSolo = row.IsSolo;
                stem.Volume = (float)row.Volume;
            }
        }

        /// <summary>Marks each track heard or not, the way the mixer and <see cref="WaveformPeaks.Mix"/> decide it.</summary>
        private void RefreshHeard()
        {
            bool anySolo = Tracks.Any(t => t.IsSolo);
            foreach (var row in Tracks)
                row.IsHeard = (anySolo ? row.IsSolo : !row.IsMuted) && row.Volume > 0;
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

        /// <summary>
        /// Puts playback back at <paramref name="position"/> after the shown song was reloaded, e.g. the merge
        /// preview was staged again: the seek bar moves there in any case, and playing resumes when
        /// <paramref name="resume"/> is set. Nothing happens when there's nothing playable to go back to.
        /// </summary>
        public async Task RestorePlaybackAsync(TimeSpan position, bool resume)
        {
            double seconds = Math.Max(0, position.TotalSeconds);
            double timeline = TimelineSeconds;
            if (timeline > 0)
                seconds = Math.Min(seconds, timeline);

            // While scrubbing the slider owns the position; a reload must not fight it.
            if (IsScrubbing)
                return;

            SetPosition(seconds);

            if (!resume || IsScrubbing)
                return;

            if (await EnsureOpenAsync() is not { } player)
                return;

            // The length may only be known now that the audio is open.
            double playable = Math.Max(TimelineSeconds, player.Length.TotalSeconds);
            if (playable > 0 && seconds > playable)
            {
                seconds = playable;
                SetPosition(seconds);
            }

            player.Seek(TimeSpan.FromSeconds(seconds));
            player.Play();
            SetPlaying(player.IsPlaying);
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

            var start = PreviewStartTime.Resolve(PreviewStart, TimeSpan.FromSeconds(Math.Max(TimelineSeconds, player.Length.TotalSeconds)));
            player.Seek(start);
            SetPosition(start.TotalSeconds);
            player.Play();
            SetPlaying(player.IsPlaying);
        }

        private void StartRequestedPreview(SongEntry entry)
        {
            // While the audio is opening the request waits; EnsureOpenAsync starts it once the audio is open.
            if (_previewRequest != entry || IsOpening)
                return;

            _previewRequest = null;
            _ = StartFromPreviewAsync();
        }

        /// <summary>
        /// Finds the tracks' peaks for the waveform in the background: from <see cref="StemPeaksCache.Shared"/> at once,
        /// or else by reading their audio, a little after they are shown so that moving quickly through songs doesn't
        /// read each one.
        /// </summary>
        private void StartPeaks(SongEntry entry, IReadOnlyList<SongTrack> tracks)
        {
            if (tracks.Count == 0)
                return;

            var cts = new CancellationTokenSource();
            _peaksCts = cts;

            // Taken now, so the task never reads the token source itself, which CancelPeaks disposes.
            var token = cts.Token;
            var resolution = Resolution;
            _peaksTask = Task.Run(() => ReadPeaks(entry, tracks, resolution, token));
        }

        private void ReadPeaks(SongEntry entry, IReadOnlyList<SongTrack> tracks, WaveformResolution resolution, CancellationToken cancellationToken)
        {
            try
            {
                // Each key is taken before its file is read, so what's kept under it is never older than the file it names.
                var keys = new Dictionary<SongTrack, StemPeaksKey?>();
                foreach (var track in tracks)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var key = StemPeaksCache.KeyOf(entry, track);
                    if (key is { } found && StemPeaksCache.Shared.TryGet(found, resolution, out var cached))
                        ShowPeaks(track, cached, isComplete: true, cancellationToken);
                    else
                        keys[track] = key;
                }

                // Cancelled during the wait: nothing was opened.
                if (keys.Count == 0 || cancellationToken.WaitHandle.WaitOne(PeaksDelay))
                    return;

                // Separate from the player's own sources, which are for the audio thread alone.
                using var audio = SongAudio.Open(entry, [.. keys.Keys], cancellationToken);

                // Each track fills in from its start at the same pace, so the mix keeps up. One that can't be read has no
                // waveform, and what was read of it goes, so the mix isn't left waiting on it.
                ChunkedPeaksRead.Read(
                    audio,
                    resolution,
                    completed: (opened, peaks) =>
                    {
                        ShowPeaks(opened.Track, peaks, isComplete: true, cancellationToken);
                        if (keys[opened.Track] is { } key)
                            StemPeaksCache.Shared.Add(key, peaks);
                    },
                    failed: opened => ForgetPeaks(opened.Track, cancellationToken),
                    progress: (opened, partial) => ShowPeaks(opened.Track, partial, isComplete: false, cancellationToken),
                    PeaksProgressInterval,
                    cancellationToken);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException or UnauthorizedAccessException or InvalidDataException)
            {
                // No waveform; the song still plays, and opening it says what is wrong.
            }
        }

        /// <summary>Shows <paramref name="peaks"/> for <paramref name="track"/> unless the read has been stopped; from any thread.</summary>
        /// <param name="isComplete">Whether they're all of the track, rather than as much as has been read.</param>
        private void ShowPeaks(SongTrack track, StemPeaks peaks, bool isComplete, CancellationToken cancellationToken) =>
            Dispatcher.UIThread.Post(() =>
            {
                if (cancellationToken.IsCancellationRequested)
                    return;

                _peaks[track] = peaks;
                if (isComplete)
                    _partialPeaks.Remove(track);
                else
                    _partialPeaks.Add(track);

                foreach (var row in Tracks.Where(r => Equals(r.Track, track)))
                    row.Waveform = WaveformPeaks.Of(peaks);
                ScheduleWaveformRebuild();
                ScheduleWaveformChanged(track.Stem);
            });

        /// <summary>Drops what was read of <paramref name="track"/>, whose read failed, unless the read has been stopped; from any thread.</summary>
        private void ForgetPeaks(SongTrack track, CancellationToken cancellationToken) =>
            Dispatcher.UIThread.Post(() =>
            {
                if (cancellationToken.IsCancellationRequested || !_peaks.Remove(track))
                    return;

                _partialPeaks.Remove(track);
                foreach (var row in Tracks.Where(r => Equals(r.Track, track)))
                    row.Waveform = Waveform.Empty;
                ScheduleWaveformRebuild();
                ScheduleWaveformChanged(track.Stem);
            });

        /// <summary>Tells the lanes the tracks shown changed, now; any waveform changes pending are part of that.</summary>
        private void RaiseTracksChanged()
        {
            _waveformsChangedTimer?.Dispose();
            _waveformsChangedTimer = null;
            _changedWaveformStems.Clear();
            _laneMixes.Clear();
            TrackWaveformsChanged?.Invoke(this, TrackWaveformsChangedEventArgs.Tracks);
        }

        /// <summary>
        /// Tells the lanes <paramref name="stem"/>'s waveform changed, along with any others that do meanwhile, a moment
        /// from now: with several tracks being read, their snapshots would otherwise redraw the lanes many times a second.
        /// </summary>
        private void ScheduleWaveformChanged(string stem)
        {
            _changedWaveformStems.Add(stem);
            _waveformsChangedTimer ??= DispatcherTimer.RunOnce(() =>
            {
                _waveformsChangedTimer = null;
                var stems = new HashSet<string>(_changedWaveformStems, StringComparer.OrdinalIgnoreCase);
                _changedWaveformStems.Clear();
                TrackWaveformsChanged?.Invoke(this, new TrackWaveformsChangedEventArgs(stems));
            }, PeaksProgressInterval, DispatcherPriority.Background);
        }

        /// <summary>
        /// Stops reading the waveform, without waiting: opening a file can't be interrupted, so the read may hold
        /// its files a little longer. It is kept until it ends, for <see cref="WhenFilesClosedAsync"/>.
        /// </summary>
        private void CancelPeaks()
        {
            var cts = _peaksCts;
            var task = _peaksTask;
            _peaksCts = null;
            _peaksTask = null;
            if (cts is null)
                return;

            cts.Cancel();
            _closingReads.RemoveAll(t => t.IsCompleted);
            if (task is null)
            {
                cts.Dispose();
                return;
            }

            // Disposed once the read is done with its token.
            _closingReads.Add(task);
            task.ContinueWith(_ => cts.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        /// <summary>Forgets every track's peaks, so the waveform is flat until they are read again.</summary>
        private void ClearPeaks()
        {
            _peaks.Clear();
            _partialPeaks.Clear();
            _laneMixes.Clear();
            _waveformGeneration++;
            Waveform = Waveform.Empty;
        }

        /// <summary>Rebuilds the waveform once the changes made meanwhile are in, so dragging a slider doesn't rebuild it for every step.</summary>
        private void ScheduleWaveformRebuild()
        {
            if (_isWaveformRebuildPending)
                return;

            _isWaveformRebuildPending = true;
            Dispatcher.UIThread.Post(() =>
            {
                if (_isWaveformRebuildPending)
                    RebuildWaveform();
            }, DispatcherPriority.Background);
        }

        /// <summary>Mixes the peaks read so far as the tracks are switched, the way the mixer would.</summary>
        private void RebuildWaveform()
        {
            _isWaveformRebuildPending = false;
            if (_peaks.Count == 0)
            {
                _waveformGeneration++;
                if (Waveform.Count > 0)
                    Waveform = Waveform.Empty;
                return;
            }

            // One mix at a time: one asked for meanwhile is made when it's done, from the switches as they are then.
            if (_isWaveformMixing)
            {
                _isWaveformRemixNeeded = true;
                return;
            }

            var stems = new List<AudibleStem>(Tracks.Count);
            foreach (var row in Tracks)
            {
                if (_peaks.TryGetValue(row.Track, out var peaks))
                    stems.Add(new AudibleStem(peaks, row.IsMuted, row.IsSolo, (float)row.Volume));
            }

            _ = MixWaveformAsync(stems);
        }

        /// <summary>
        /// Mixes <paramref name="stems"/> on a background thread, since several long ones take a while, and shows
        /// the waveform unless it has been cleared meanwhile. On the UI thread.
        /// </summary>
        private async Task MixWaveformAsync(List<AudibleStem> stems)
        {
            _isWaveformMixing = true;
            int generation = _waveformGeneration;
            try
            {
                var waveform = await Task.Run(() => WaveformPeaks.Mix(stems));
                if (generation == _waveformGeneration)
                    Waveform = waveform;
            }
            finally
            {
                _isWaveformMixing = false;
            }

            if (_isWaveformRemixNeeded)
            {
                _isWaveformRemixNeeded = false;
                RebuildWaveform();
            }
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

        partial void OnPlaybackSpeedChanged(double value)
        {
            double speed = double.IsFinite(value) ? Math.Clamp(value, SettingsService.MinPlaybackSpeed, SettingsService.MaxPlaybackSpeed) : 1;
            if (speed != value)
            {
                PlaybackSpeed = speed;
                return;
            }

            _settings.PlaybackSpeed = speed;
            if (_player is not null)
                _player.Speed = (float)speed;
        }

        partial void OnPitchCorrectionChanged(bool value)
        {
            _settings.Settings.PitchCorrection = value;
            if (_player is not null)
                _player.PitchCorrection = value;
        }

        partial void OnShowWaveformsChanged(bool value)
        {
            _settings.Settings.ShowWaveforms = value;
            if (!value)
            {
                // What was read goes, and nothing more is read or mixed until they're shown again.
                CancelPeaks();
                ClearPeaks();
                foreach (var row in Tracks)
                    row.Waveform = Waveform.Empty;
                RaiseTracksChanged();
            }
            else if (_entry is { } entry && HasTracks)
            {
                // Tracks read before come back from StemPeaksCache.Shared at once.
                StartPeaks(entry, [.. Tracks.Select(t => t.Track)]);
            }
        }

        partial void OnShowLaneWaveformsChanged(bool value) => _settings.Settings.ShowLaneWaveforms = value;

        partial void OnScrubStepChanged(ScrubStep value) => _settings.Settings.ScrubStep = value;

        /// <summary>
        /// A scrubbed surface's tooltip: what dragging does, what Shift does if <paramref name="overview"/> is given, what Ctrl
        /// does, and the step Ctrl and the wheel take.
        /// </summary>
        private string ScrubToolTip(string drag, string fine, string? overview = null)
        {
            string step = string.Format(CultureInfo.CurrentCulture, Strings.ScrubStepToolTipFormat, ScrubSteps.NameOf(ScrubStep));
            return overview is null ? string.Join('\n', drag, fine, step) : string.Join('\n', drag, overview, fine, step);
        }

        partial void OnFineScrubMillisecondsChanged(double value) => _settings.Settings.FineScrubMilliseconds = value;

        partial void OnLaneFineScrubFactorChanged(double value) => _settings.Settings.LaneFineScrubFactor = value;

        partial void OnWaveformFidelityChanged(WaveformFidelity value)
        {
            _settings.Settings.WaveformFidelity = value;
            if (!ShowWaveforms || _entry is not { } entry || !HasTracks)
            {
                // Read again at the new fidelity when they're next shown.
                ClearPeaks();
                return;
            }

            // Every track is read again, or found in StemPeaksCache.Shared if it was read at this fidelity before.
            CancelPeaks();
            ClearPeaks();
            foreach (var row in Tracks)
                row.Waveform = Waveform.Empty;
            RaiseTracksChanged();
            StartPeaks(entry, [.. Tracks.Select(t => t.Track)]);
        }

        partial void OnShowSpectrumChanged(bool value)
        {
            _settings.Settings.ShowSpectrum = value;
            if (_player is not null)
                _player.CapturesHeard = value;
        }

        partial void OnSongLengthSecondsChanged(double value)
        {
            if (_player is not null)
                _player.MinimumLength = TimeSpan.FromSeconds(Math.Max(0, value));
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
                player.PitchCorrection = PitchCorrection;
                player.Speed = (float)PlaybackSpeed;
                player.CapturesHeard = ShowSpectrum;
                foreach (var row in Tracks)
                    ApplySwitches(row);
                player.Stopped += OnPlayerStopped;
                player.MinimumLength = TimeSpan.FromSeconds(Math.Max(0, SongLengthSeconds));
                LengthSeconds = player.Length.TotalSeconds;

                // A spot picked on the waveform before the first play.
                if (PositionSeconds > 0)
                    player.Seek(TimeSpan.FromSeconds(PositionSeconds));
                return player;
            }
            finally
            {
                if (generation == _generation)
                {
                    IsOpening = false;

                    // The song's art was clicked while it opened: play it from its preview now. If it
                    // couldn't be opened, the request is dropped rather than tried again later.
                    if (_entry is { } shown && _previewRequest == shown)
                    {
                        if (_player is not null)
                            StartRequestedPreview(shown);
                        else
                            _previewRequest = null;
                    }
                }
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
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                return (null, null, string.Format(CultureInfo.CurrentCulture, Strings.SongAudioUnreadableFormat, ex.Message));
            }

            try
            {
                var sources = audio.Sources;
                if (sources.Count == 0)
                    return (audio, null, Strings.NoPlayableTracks);

                var mixer = new StemMixer(sources);
                IAudioOutput output;
                try
                {
                    output = _openOutput(mixer.OutputRate);
                }
                catch (AudioDeviceException ex)
                {
                    return (audio, null, string.Format(CultureInfo.CurrentCulture, Strings.AudioDeviceFailedFormat, ex.Message));
                }

                try
                {
                    return (audio, new StemPlayer(mixer, output), null);
                }
                catch
                {
                    output.Dispose();
                    throw;
                }
            }
            catch
            {
                // Whatever went wrong, the song's files aren't left open.
                audio.Dispose();
                throw;
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

            // A stem found to be damaged while playing goes quiet; its row says why. Only those stems are looked for among the rows.
            var stems = _player.Mixer.Stems;
            for (int i = 0; i < stems.Count; i++)
            {
                if (stems[i].Error is not { } error)
                    continue;

                foreach (var row in Tracks)
                {
                    if (row.Error is null && row.Name == stems[i].Name)
                        row.Error = string.Format(CultureInfo.CurrentCulture, Strings.TrackUnplayableFormat, error);
                }
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
    }
}
