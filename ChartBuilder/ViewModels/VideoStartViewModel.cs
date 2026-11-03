using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using ChartBuilder.Resources;
using ChartShared.Audio.Alignment;
using ChartShared.Audio.Video;
using ChartShared.Audio.Waveforms;
using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Commands;
using ChartShared.Core.Merging;
using ChartShared.Core.Metadata;
using ChartShared.Core.Scanning;
using ChartShared.Ffmpeg;
using ChartShared.UI.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChartBuilder.ViewModels
{
    /// <summary>
    /// The Art tab's video start box (charter-spec C1): the song's <c>video_start_time</c> in milliseconds, where in the
    /// background video the song starts. The video shows each value as it's typed or dragged, and once it's left alone a
    /// moment it's set as one undo step, as the delay box in Chart settings is.
    /// <para>Below it, the chart's and the video's waveforms line the video up by ear, and <see cref="AutoAlignCommand"/>
    /// lines it up by the video's audio track, using the Merge window's <see cref="AudioAligner"/>.</para>
    /// </summary>
    public sealed partial class VideoStartViewModel : ViewModelBase, IDisposable
    {
        private static readonly TimeSpan TypingDelay = TimeSpan.FromMilliseconds(600);

        private readonly EditorSong _song;
        private readonly EditorAudio _audio;
        private readonly Action<IChartCommand> _execute;
        private readonly DispatcherTimer _typingTimer;
        private CancellationTokenSource? _videoCts;
        private Func<Stream>? _openVideo;
        private string? _videoKey;
        private StemPeaks? _videoPeaks;
        private bool _videoAudioAttempted;
        private bool _loading;
        private bool _typed;
        private bool _isShown;
        private bool _syncingOffset;

        /// <param name="song">The song open.</param>
        /// <param name="audio">The song's audio, for the chart's waveform and mix.</param>
        /// <param name="execute">Makes an edit, as one undo step.</param>
        public VideoStartViewModel(EditorSong song, EditorAudio audio, Action<IChartCommand> execute)
        {
            ArgumentNullException.ThrowIfNull(song);
            ArgumentNullException.ThrowIfNull(audio);
            ArgumentNullException.ThrowIfNull(execute);
            _song = song;
            _audio = audio;
            _execute = execute;
            _audio.Changed += OnAudioChanged;
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
        [NotifyCanExecuteChangedFor(nameof(AutoAlignCommand))]
        public partial bool IsSaving { get; set; }

        public bool CanEdit => !IsSaving;

        /// <summary><c>*</c> beside the box while the start has a pending change, as an edited row has.</summary>
        public string? Marker => IsChanged ? "*" : null;

        /// <summary>The video has an audio track that could be read.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(AutoAlignToolTip))]
        [NotifyCanExecuteChangedFor(nameof(AutoAlignCommand))]
        public partial bool HasVideoAudio { get; private set; }

        /// <summary>The video's audio is being read.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(AutoAlignToolTip))]
        [NotifyCanExecuteChangedFor(nameof(AutoAlignCommand))]
        public partial bool IsLoadingVideoAudio { get; private set; }

        /// <summary>Why the video's audio isn't shown or can't be aligned, or null while all's well.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasVideoAudioNote), nameof(AutoAlignToolTip))]
        public partial string? VideoAudioNote { get; private set; }

        public bool HasVideoAudioNote => VideoAudioNote is not null;

        /// <summary>The chart mix's waveform, drawn above the video's.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(AutoAlignToolTip))]
        [NotifyCanExecuteChangedFor(nameof(AutoAlignCommand))]
        public partial Waveform? ChartWaveform { get; private set; }

        /// <summary>The video's waveform, drawn below the chart's.</summary>
        [ObservableProperty]
        public partial Waveform? VideoWaveform { get; private set; }

        /// <summary>
        /// How far the video's waveform is drawn later than the chart's, in seconds; dragging it changes the start.
        /// </summary>
        [ObservableProperty]
        public partial double WaveformOffsetSeconds { get; set; }

        /// <summary>An auto alignment is being worked out.</summary>
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(AutoAlignCommand))]
        public partial bool IsAligning { get; private set; }

        /// <summary>Why auto alignment can't run, or what the button does.</summary>
        public string AutoAlignToolTip
        {
            get
            {
                if (IsLoadingVideoAudio)
                    return Strings.ArtVideoAutoAlignLoading;
                if (VideoAudioNote is { } note)
                    return note;
                if (!HasVideoAudio)
                    return Strings.ArtVideoAutoAlignNoAudio;
                if (ChartWaveform is null)
                    return Strings.ArtVideoAutoAlignNoChartAudio;
                if (!ChartComplete())
                    return Strings.ArtVideoAutoAlignReadingChart;
                return Strings.ArtVideoAutoAlignToolTip;
            }
        }

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
                decimal start = (long)Math.Round(VideoTimeline.From(_song.EffectivePairs()).Start.TotalMilliseconds, MidpointRounding.AwayFromZero);
                StartMs = start;
                SyncWaveformOffset(start);
            }
            finally
            {
                _loading = false;
            }
        }

        /// <summary>
        /// Tells the view where the background video is now: <paramref name="open"/> opens it a fresh time, and
        /// <paramref name="key"/> says which file it is, so the same one isn't read again.
        /// </summary>
        public void SetVideo(Func<Stream>? open, string? key)
        {
            if (string.Equals(_videoKey, key, StringComparison.Ordinal))
            {
                _openVideo = open;
                if (open is not null && _isShown && !_videoAudioAttempted && !IsLoadingVideoAudio)
                    _ = LoadVideoAudioAsync();
                return;
            }

            _videoKey = key;
            _openVideo = open;
            _videoCts?.Cancel();
            _videoCts = null;
            _videoPeaks = null;
            _videoAudioAttempted = false;
            HasVideoAudio = false;
            VideoWaveform = null;
            VideoAudioNote = null;
            IsLoadingVideoAudio = false;
            if (open is not null && _isShown)
                _ = LoadVideoAudioAsync();
        }

        /// <summary>The Art tab is shown or hidden: its waveforms are read only while it is.</summary>
        public void SetShown(bool shown)
        {
            if (_isShown == shown)
                return;

            _isShown = shown;
            if (shown)
            {
                RefreshChartWaveform();
                if (_openVideo is not null && !_videoAudioAttempted && !IsLoadingVideoAudio)
                    _ = LoadVideoAudioAsync();
            }
            else
            {
                _videoCts?.Cancel();
                _videoCts = null;
                IsLoadingVideoAudio = false;
            }
        }

        public void Dispose()
        {
            _audio.Changed -= OnAudioChanged;
            _videoCts?.Cancel();
            _videoCts = null;
            _typingTimer.Stop();
        }

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

        /// <summary>Whether the chart's waveform has been read to its end, so aligning against it is meaningful.</summary>
        private bool ChartComplete() => ChartSource() is { } source && _audio.Waveforms.IsComplete(source);

        /// <summary>Lines the video up with the chart by the audio they share, as the Merge window does.</summary>
        [RelayCommand(CanExecute = nameof(CanAutoAlign))]
        private async Task AutoAlignAsync()
        {
            if (_videoPeaks is not { } videoPeaks || ChartSource() is not { } source)
                return;

            if (!_audio.Waveforms.IsComplete(source))
            {
                VideoAudioNote = Strings.ArtVideoAutoAlignReadingChart;
                return;
            }

            var chartPeaks = _audio.Waveforms.Peaks(source);
            if (chartPeaks.Count == 0)
            {
                VideoAudioNote = Strings.ArtVideoAutoAlignNoChartAudio;
                return;
            }

            IsAligning = true;
            try
            {
                var estimate = await Task.Run(() =>
                {
                    var chart = AlignmentAudio.Of(chartPeaks);
                    var video = AlignmentAudio.Of([videoPeaks]);
                    return chart is null || video is null ? null : AudioAligner.Align(chart, video);
                });

                if (estimate is null)
                {
                    VideoAudioNote = Strings.ArtVideoAutoAlignFailed;
                    return;
                }

                // A weak, ambiguous or drifting match would move the song to the wrong place; say why rather than apply it.
                if (!estimate.IsReliable || estimate.HasDrift)
                {
                    VideoAudioNote = UnreliableNote(estimate);
                    return;
                }

                VideoAudioNote = null;
                StartMs = Math.Clamp(StartMsFor(estimate), MinStartMs, MaxStartMs);
                ApplyTyped();
            }
            finally
            {
                IsAligning = false;
            }
        }

        private bool CanAutoAlign() =>
            CanEdit && HasVideoAudio && !IsLoadingVideoAudio && !IsAligning && ChartComplete();

        /// <summary>Why an alignment estimate was too poor to apply, as a note to the user.</summary>
        private static string UnreliableNote(AlignmentEstimate estimate)
        {
            if (estimate.IsAmbiguous)
                return Strings.ArtVideoAutoAlignAmbiguous;
            if (estimate.HasDrift)
                return Strings.ArtVideoAutoAlignDrift;
            return string.Format(CultureInfo.CurrentCulture, Strings.ArtVideoAutoAlignWeakFormat, estimate.Match);
        }

        /// <summary>The start that lines the video up for <paramref name="estimate"/>: positive skips that much video.</summary>
        internal static decimal StartMsFor(AlignmentEstimate estimate) => -estimate.OffsetMs;

        partial void OnStartMsChanged(decimal? value)
        {
            if (_loading)
                return;
            _typed = true;
            SyncWaveformOffset(value);
            PreviewChanged?.Invoke(this, EventArgs.Empty);
            _typingTimer.Stop();
            _typingTimer.Start();
        }

        partial void OnWaveformOffsetSecondsChanged(double value)
        {
            if (_syncingOffset || _loading)
                return;

            double milliseconds = Math.Clamp(-value * 1000, (double)MinStartMs, (double)MaxStartMs);
            decimal start = (decimal)Math.Round(milliseconds, MidpointRounding.AwayFromZero);
            if (StartMs != start)
            {
                StartMs = start;
                return;
            }

            // Past a limit, the waveform is put back where the start really is.
            if (Math.Abs(WaveformOffsetSeconds - (-(double)start / 1000)) > 0.0000005)
                SyncWaveformOffset(start);
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

        private void SyncWaveformOffset(decimal? start)
        {
            _syncingOffset = true;
            try
            {
                WaveformOffsetSeconds = start is { } ms ? -(double)ms / 1000 : 0;
            }
            finally
            {
                _syncingOffset = false;
            }
        }

        /// <summary>The chart mix's stems, the preview and crowd left out as the Merge window leaves them out.</summary>
        private AudioSource? ChartSource()
        {
            var tracks = _audio.Tracks
                .Where(t => !t.Stem.Equals(AudioStems.Crowd, StringComparison.OrdinalIgnoreCase))
                .ToList();
            return AudioSource.MixOf(tracks);
        }

        private void RefreshChartWaveform()
        {
            if (!_isShown)
                return;
            ChartWaveform = ChartSource() is { } source ? _audio.Waveforms.Mono(source) : null;
        }

        private void OnAudioChanged(object? sender, EventArgs e)
        {
            RefreshChartWaveform();
            AutoAlignCommand.NotifyCanExecuteChanged();
        }

        private async Task LoadVideoAudioAsync()
        {
            if (_openVideo is not { } open || !_isShown)
                return;

            _videoCts?.Cancel();
            var cts = new CancellationTokenSource();
            _videoCts = cts;
            IsLoadingVideoAudio = true;
            VideoAudioNote = null;
            try
            {
                if (!FfmpegLibrary.IsAvailable)
                {
                    VideoAudioNote = Strings.ArtVideoAutoAlignNoFfmpeg;
                    return;
                }

                var resolution = _audio.Waveforms.Resolution;
                var read = await Task.Run(() => VideoAudio.Read(open(), resolution, cts.Token), cts.Token);
                if (cts.IsCancellationRequested)
                    return;

                if (read.Peaks is not { } peaks)
                {
                    VideoAudioNote = Strings.ArtVideoAutoAlignNoAudio;
                    return;
                }

                _videoPeaks = peaks;
                VideoWaveform = WaveformPeaks.Of(peaks);
                HasVideoAudio = true;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
            {
                VideoAudioNote = string.Format(CultureInfo.CurrentCulture, Strings.ArtVideoAudioUnreadableFormat, ex.Message);
            }
            finally
            {
                if (ReferenceEquals(_videoCts, cts))
                {
                    // A read that was cancelled, as by hiding the tab or changing the video, is tried again when shown.
                    _videoAudioAttempted = !cts.IsCancellationRequested;
                    IsLoadingVideoAudio = false;
                    _videoCts = null;
                }

                cts.Dispose();
            }
        }
    }
}
