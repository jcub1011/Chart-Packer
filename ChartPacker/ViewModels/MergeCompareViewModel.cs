using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ChartPacker.Controls;
using ChartPacker.Resources;
using ChartShared.Audio.Alignment;
using ChartShared.Audio.Waveforms;
using ChartShared.Core.Charts;
using ChartShared.Core.Merging;
using ChartShared.Core.Scanning;
using ChartShared.UI.Controls.Highway;
using ChartShared.UI.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChartPacker.ViewModels
{
    /// <summary>A part of a song that can be compared, as its picker offers it; or none, to hide its lane.</summary>
    /// <param name="PartId">The part, by <see cref="MidiPart.Id"/>; <see langword="null"/> for none.</param>
    public sealed record ComparePartOption(string? PartId, string Label, TimelineNotes? Notes);

    /// <summary>An offset worked out for the song, to jump to: by a link, or where it was when the window opened.</summary>
    public sealed record CompareEstimate(string Label, int OffsetMs);

    /// <summary>
    /// The window that lines one of the songs being merged up with the kept audio's song by eye: both songs' audio and a part
    /// of each, on one time axis, the one moved by an offset that's dragged or typed. The offset is only taken when it's applied.
    /// </summary>
    /// <param name="apply">Takes the offset, when the user applies it.</param>
    public sealed partial class MergeCompareViewModel(
        string referenceName,
        string songName,
        int startOffset,
        IReadOnlyList<ComparePartOption> referenceParts,
        IReadOnlyList<ComparePartOption> songParts,
        string? preferredPart,
        IReadOnlyList<CompareEstimate> estimates,
        Func<CancellationToken, (Waveform? Reference, Waveform? Song)> readAudio,
        Action<int> apply) : ViewModelBase
    {
        private readonly CancellationTokenSource _cts = new();
        private bool _syncing;

        public string ReferenceName { get; } = referenceName;

        public string SongName { get; } = songName;

        public string Title { get; } = string.Format(CultureInfo.CurrentCulture, Strings.MergeCompareTitleFormat, songName);

        public string Heading { get; } = string.Format(CultureInfo.CurrentCulture, Strings.MergeCompareHeadingFormat, songName, referenceName);

        public string Description { get; } = string.Format(CultureInfo.CurrentCulture, Strings.MergeCompareDescriptionFormat, songName);

        public IReadOnlyList<ComparePartOption> ReferenceParts { get; } = referenceParts;

        public IReadOnlyList<ComparePartOption> SongParts { get; } = songParts;

        /// <summary>The offsets worked out for the song, the one it had when the window opened first.</summary>
        public IReadOnlyList<CompareEstimate> Estimates { get; } = estimates;

        public bool HasEstimates => Estimates.Count > 0;

        [ObservableProperty]
        public partial ComparePartOption? SelectedReferencePart { get; set; } =
            referenceParts.FirstOrDefault(p => p.PartId is { } id && id == preferredPart) ?? referenceParts.FirstOrDefault(p => p.Notes is not null) ?? referenceParts.FirstOrDefault();

        [ObservableProperty]
        public partial ComparePartOption? SelectedSongPart { get; set; } =
            songParts.FirstOrDefault(p => p.PartId is { } id && id == preferredPart) ?? songParts.FirstOrDefault(p => p.Notes is not null) ?? songParts.FirstOrDefault();

        [ObservableProperty]
        public partial Waveform? ReferenceWaveform { get; set; }

        [ObservableProperty]
        public partial Waveform? SongWaveform { get; set; }

        /// <summary>The songs' audio is being read.</summary>
        [ObservableProperty]
        public partial bool IsLoadingAudio { get; set; }

        /// <summary>Why a song's audio isn't shown.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasAudioMessage))]
        public partial string? AudioMessage { get; set; }

        public bool HasAudioMessage => AudioMessage is not null;

        /// <summary>How far the song moves, in milliseconds, later when positive; the offset box.</summary>
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(RevertCommand))]
        public partial decimal? Offset { get; set; } = startOffset;

        /// <summary>The same, in seconds, for the timeline, which drags it.</summary>
        [ObservableProperty]
        public partial double OffsetSeconds { get; set; } = startOffset / 1000.0;

        /// <summary>Asks the window to close.</summary>
        public event Action? CloseRequested;

        /// <summary>The offset, to the nearest millisecond.</summary>
        public int OffsetMs => (int)Math.Clamp(Math.Round(Offset ?? 0), int.MinValue, int.MaxValue);

        /// <summary>Reads the songs' audio, mixed, for their waveforms.</summary>
        public async Task LoadAsync()
        {
            IsLoadingAudio = true;
            try
            {
                var token = _cts.Token;
                var (reference, song) = await Task.Run(() => readAudio(token), token);
                ReferenceWaveform = reference;
                SongWaveform = song;
                if (reference is null || song is null)
                    AudioMessage = string.Format(CultureInfo.CurrentCulture, Strings.MergeCompareNoAudioFormat, reference is null ? ReferenceName : SongName);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                IsLoadingAudio = false;
            }
        }

        /// <summary>The window has closed: stops reading the audio.</summary>
        public void OnClosed() => _cts.Cancel();

        partial void OnOffsetChanged(decimal? value)
        {
            if (_syncing || value is null)
                return;
            _syncing = true;
            OffsetSeconds = (double)value.Value / 1000;
            _syncing = false;
        }

        partial void OnOffsetSecondsChanged(double value)
        {
            if (_syncing)
                return;
            _syncing = true;
            Offset = (decimal)Math.Round(value * 1000);
            _syncing = false;
        }

        [RelayCommand]
        private void Jump(CompareEstimate estimate) => Offset = estimate.OffsetMs;

        private bool CanRevert() => OffsetMs != startOffset;

        /// <summary>Back to the offset the song had when the window opened.</summary>
        [RelayCommand(CanExecute = nameof(CanRevert))]
        private void Revert() => Offset = startOffset;

        [RelayCommand]
        private void Apply()
        {
            apply(OffsetMs);
            CloseRequested?.Invoke();
        }

        [RelayCommand]
        private void Cancel() => CloseRequested?.Invoke();

        /// <summary>
        /// The parts of <paramref name="source"/>'s chart to compare, each at its hardest difficulty, after one to show none:
        /// each instrument the chart reader models, and the vocals.
        /// </summary>
        internal static IReadOnlyList<ComparePartOption> PartsOf(MergeSource source)
        {
            var parts = new List<ComparePartOption> { new(null, Strings.MergeNone, null) };
            if (source.Chart is not { } chart)
                return parts;

            try
            {
                var doc = ChartReader.Read(chart.Midi, ChartFiles.MidiName, chart.Options);
                foreach (var instrument in doc.Instruments)
                {
                    var difficulty = doc.DifficultiesOf(instrument).Max();
                    var key = new ChartTrackKey(instrument, difficulty);
                    var kind = ChartInstruments.KindOf(instrument);
                    int laneCount = kind switch
                    {
                        ChartInstrumentKind.FiveFret => 5,
                        ChartInstrumentKind.Drums => doc.IsFiveLaneDrums ? 5 : 4,
                        _ => 3,
                    };
                    string label = string.Format(CultureInfo.CurrentCulture, Strings.HighwayTrackFormat, ChartLabels.DifficultyName(difficulty), ChartLabels.InstrumentName(instrument));
                    var lane = new HighwayLane(kind, laneCount, label, doc.Tracks[key].Notes, doc.Tempo, chart.Delay);
                    string? partId = MidiPartCatalog.Known.FirstOrDefault(p => MidiPartCatalog.InstrumentsOf(p) is [var only] && only == instrument)?.Id;
                    parts.Add(new ComparePartOption(partId, label, TimelineNotes.Of(lane)));
                }

                var vocals = VocalReader.Read(chart.Midi, ChartFiles.MidiName, doc.Tempo);
                if (vocals.Parts.FirstOrDefault(p => p.TrackName.Equals(VocalReader.VocalsTrackName, StringComparison.OrdinalIgnoreCase) && p.Notes.Count > 0) is { } main)
                    parts.Add(new ComparePartOption("vocals", Strings.MergeCompareVocals, TimelineNotes.OfVocals(Strings.MergeCompareVocals, main, doc.Tempo, chart.Delay)));
            }
            catch (ChartFormatException)
            {
                // Nothing to compare but the audio.
            }

            return parts;
        }

        /// <summary>
        /// <paramref name="source"/>'s audio, mixed, for its waveform; <see langword="null"/> when it has none that can be read.
        /// Each track's peaks come from <see cref="StemPeaksCache.Shared"/> when they're there at <paramref name="resolution"/>.
        /// </summary>
        internal static Waveform? WaveformOf(MergeSource source, WaveformResolution resolution, CancellationToken cancellationToken)
        {
            try
            {
                var peaks = AudioAligner.ReadPeaks(source.Entry, source.AudioFiles, resolution, StemPeaksCache.Shared, anyResolution: false, cancellationToken);
                return peaks.Count > 0 ? WaveformPeaks.Mix([.. peaks.Select(p => new AudibleStem(p, IsMuted: false, IsSolo: false))]) : null;
            }
            catch (Exception ex) when (IsUnreadableAudio(ex))
            {
                return null;
            }
        }

        /// <summary>
        /// Whether <paramref name="ex"/> is how reading a song's audio says it can't be read: the audio, or its file, is bad or
        /// gone. Thrown by a track read on another thread, it comes wrapped, and counts only when all that's wrapped does.
        /// </summary>
        internal static bool IsUnreadableAudio(Exception ex) => ex is AggregateException aggregate
            ? aggregate.InnerExceptions.Count > 0 && aggregate.InnerExceptions.All(IsUnreadableAudio)
            : ex is System.IO.IOException or UnauthorizedAccessException or System.IO.InvalidDataException or NotSupportedException or ObjectDisposedException;
    }
}
