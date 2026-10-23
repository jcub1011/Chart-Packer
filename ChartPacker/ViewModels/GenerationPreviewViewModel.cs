using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using ChartPacker.Resources;
using ChartPacker.Services;
using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Authoring;
using ChartShared.Core.Charting.Authoring.Generation;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charts;
using ChartShared.Core.Scanning;
using ChartShared.Core.Validation;
using ChartShared.UI.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using Serilog;

namespace ChartPacker.ViewModels
{
    /// <summary>
    /// A part of the song shown in a <see cref="GenerationPreviewViewModel"/>, as it would be generated: its difficulties on
    /// the estimator's ladder, the new ones marked, and how many lint findings the new ones have.
    /// </summary>
    public sealed record GenerateDifficultiesPartPreview(ChartInstrument Part, string Name, DifficultyLadder Ladder, string Findings);

    /// <summary>
    /// One song as its difficulties would be generated, worked out again each time the rules change: each part on the
    /// estimator's ladder, and one part's drafts in the reduction preview, as highways played with the song, as Chart
    /// Builder's Reduce shows them, in the Generate Lower Difficulties window.
    /// </summary>
    public sealed partial class GenerationPreviewViewModel : ViewModelBase, IDisposable
    {
        // How long after a change of the rules the preview is worked out again, so typing a number does it once.
        private static readonly TimeSpan PreviewDelay = TimeSpan.FromMilliseconds(200);

        // What the preview remembers, for as long as Chart Packer runs: whether it's shown, its look-ahead and its filters.
        private static readonly ReductionPreviewMemory PreviewSettings = new();

        private readonly Func<ReductionDefaults> _rules;
        private readonly Func<IDifficultyGenerator> _generator;
        private readonly PlayerViewModel _player;
        private readonly PlayerPreviewPlayback _playback;
        private readonly DispatcherTimer _redraw;
        private readonly CancellationTokenSource _closing = new();

        // What it says with nothing to generate, and with no song shown.
        private readonly string _nothing;
        private readonly string _pickSong;
        private string? _reason;

        // The song shown, read once, as it'd be read for generating; only the one, so a long list picked through doesn't
        // keep every chart it showed.
        private (GenerateDifficultiesSong Song, Task<SongRead> Read)? _read;

        // The song shown as last worked out, for the preview to show another of its parts without working it out again.
        private (GenerateDifficultiesSong Song, SongRead Read, SongGeneration Generation, IReadOnlyDictionary<ChartInstrument, ReductionCheck> Checks)? _shown;
        private int _previews;

        // Set while a redraw picks the part again, which it then shows itself.
        private bool _pickingPart;
        private bool _disposed;

        /// <param name="player">Plays the song shown, for the preview; the host's own, which the host disposes.</param>
        /// <param name="rules">The rules to generate by, as they are now.</param>
        /// <param name="generator">The difficulty generator to draft with, as it is now.</param>
        /// <param name="nothing">What it says when the song has nothing to generate.</param>
        /// <param name="pickSong">What it says with no song shown.</param>
        public GenerationPreviewViewModel(PlayerViewModel player, Func<ReductionDefaults> rules, Func<IDifficultyGenerator> generator, string nothing, string pickSong)
        {
            ArgumentNullException.ThrowIfNull(player);
            ArgumentNullException.ThrowIfNull(rules);
            ArgumentNullException.ThrowIfNull(generator);
            _player = player;
            _rules = rules;
            _generator = generator;
            _nothing = nothing;
            _pickSong = pickSong;
            _playback = new PlayerPreviewPlayback(player);
            Preview = new ReductionPreviewViewModel(_playback, PreviewSettings, wheelUpIsLater: true);
            _redraw = new DispatcherTimer { Interval = PreviewDelay };
            _redraw.Tick += (_, _) => _ = RedrawAsync();
        }

        /// <summary>The song shown; null for none.</summary>
        public GenerateDifficultiesSong? Song { get; private set; }

        /// <summary>The picked part's drafts against what they're made from, as highways played with the song.</summary>
        public ReductionPreviewViewModel Preview { get; }

        /// <summary>Each part of the song shown, as it'd be generated.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasLadders))]
        public partial IReadOnlyList<GenerateDifficultiesPartPreview> Ladders { get; private set; } = [];

        public bool HasLadders => Ladders.Count > 0;

        /// <summary>The part whose drafts the preview shows.</summary>
        [ObservableProperty]
        public partial GenerateDifficultiesPartPreview? SelectedPart { get; set; }

        /// <summary>Why there's nothing to show, or that it's being worked out; empty when it's shown.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasLaddersStatus))]
        public partial string LaddersStatus { get; private set; } = "";

        public bool HasLaddersStatus => LaddersStatus.Length > 0;

        /// <summary>Shows <paramref name="song"/>, worked out off the UI thread, and gives it to the player; null for none.</summary>
        /// <param name="reason">With no song, what's said in its place; null to ask for one to be picked.</param>
        public void Show(GenerateDifficultiesSong? song, string? reason = null)
        {
            Song = song;
            _reason = reason;
            _shown = null;
            Ladders = [];
            SelectedPart = null;
            _playback.SetChart(null, TimeSpan.Zero);
            if (_player.LoadedEntry != song?.Entry)
                _player.Unload();
            _ = RedrawAsync();
        }

        /// <summary>Works the song shown out again shortly, by the rules as they'll be then.</summary>
        public void ScheduleRedraw()
        {
            _redraw.Stop();
            _redraw.Start();
        }

        /// <summary>Stops working the song out, for good: the host is done.</summary>
        public void Stop()
        {
            _redraw.Stop();
            _closing.Cancel();
        }

        /// <summary>Lets go of the song's files, as before they're written; showing a song again opens them again.</summary>
        public async Task ReleaseFilesAsync()
        {
            _player.Unload();
            await _player.WhenFilesClosedAsync();
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _redraw.Stop();
            _closing.Cancel();
            _closing.Dispose();
            Preview.Dispose();
            _playback.Dispose();
        }

        partial void OnSelectedPartChanged(GenerateDifficultiesPartPreview? value)
        {
            if (!_pickingPart)
                ShowPart();
        }

        // The song shown as it'd be generated by the rules as they are, worked out off the UI thread; a newer one replaces
        // it. The song is read, and given to the player, the first time it's shown.
        private async Task RedrawAsync()
        {
            _redraw.Stop();
            int redraw = ++_previews;
            if (Song is not { } song)
            {
                Ladders = [];
                LaddersStatus = _reason ?? _pickSong;
                Preview.Clear(LaddersStatus);
                return;
            }

            LaddersStatus = Strings.GenerateDifficultiesWorking;
            var rules = _rules();
            var generator = _generator();
            var token = _closing.Token;
            try
            {
                var read = await ReadOf(song, token);
                if (redraw != _previews || token.IsCancellationRequested)
                    return;
                LoadPlayer(song, read);

                var (generation, checks, ladders) = await Task.Run(() => Work(song, read, rules, generator), token);
                if (redraw != _previews || token.IsCancellationRequested)
                    return;
                _shown = (song, read, generation, checks);
                var part = SelectedPart?.Part;
                Ladders = ladders;
                LaddersStatus = ladders.Count == 0 ? _nothing : "";
                _pickingPart = true;
                try
                {
                    SelectedPart = ladders.FirstOrDefault(l => l.Part == part) ?? ladders.FirstOrDefault();
                }
                finally
                {
                    _pickingPart = false;
                }

                ShowPart();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                if (redraw != _previews || token.IsCancellationRequested)
                    return;
                Log.ForContext<GenerationPreviewViewModel>().Warning(ex, "Previewing generated difficulties of {Song} failed", song.Entry.FullPath);
                _shown = null;
                Ladders = [];
                LaddersStatus = string.Format(CultureInfo.CurrentCulture, Strings.GenerateDifficultiesPreviewFailedFormat, ex.Message);
                Preview.Clear(LaddersStatus);
            }
        }

        // The picked part's drafts, into the preview, with their findings.
        private void ShowPart()
        {
            if (_shown is not { } shown || SelectedPart is not { Part: var part } || !shown.Checks.TryGetValue(part, out var check))
            {
                if (_shown is not null)
                    Preview.Clear(_nothing);
                return;
            }

            var generation = shown.Generation;
            var chart = shown.Read.Chart;
            bool fiveLane = DifficultyGenerator.DrumsVariantOf(chart.Load.Chart, chart.Options) == DrumsVariant.FiveLane;
            Preview.OnDrafted(new ReductionPreviewRun(
                part,
                null,
                generation.Drafted.TempoMap.ToTempoMap(),
                chart.Delay,
                fiveLane,
                () => Task.Run(() => generation.Diff(part))));
            Preview.OnFindings(check.Findings);
        }

        // The song's tracks, into the player, and its chart's timing, for the change strip, once it's shown.
        private void LoadPlayer(GenerateDifficultiesSong song, SongRead read)
        {
            if (_player.LoadedEntry != song.Entry)
            {
                _player.Load(song.Entry, read.Tracks, null);
                _player.SongLengthSeconds = read.LengthSeconds;
            }

            _playback.SetChart(read.Chart.Load.Chart.TempoMap.ToTempoMap(), read.Chart.Delay);
        }

        // The song as read once, for as long as it's shown as drafted the same, whatever's generated of it; read again when
        // that failed or was cancelled, so showing it again tries again.
        private Task<SongRead> ReadOf(GenerateDifficultiesSong song, CancellationToken token)
        {
            if (_read is { } known && SameRead(known.Song, song) && !known.Read.IsFaulted && !known.Read.IsCanceled)
                return known.Read;
            var read = Task.Run(() => SongRead.Of(song, token), token);
            _read = (song, read);
            return read;
        }

        private static bool SameRead(GenerateDifficultiesSong read, GenerateDifficultiesSong song) =>
            read.Entry == song.Entry && ReferenceEquals(read.Pairs, song.Pairs) && ReferenceEquals(read.Draft, song.Draft);

        private static (SongGeneration Generation, IReadOnlyDictionary<ChartInstrument, ReductionCheck> Checks, IReadOnlyList<GenerateDifficultiesPartPreview> Ladders) Work(
            GenerateDifficultiesSong song,
            SongRead read,
            ReductionDefaults rules,
            IDifficultyGenerator generator)
        {
            var generation = SongDifficultyGeneration.Prepare(read.Chart, song.Pairs, rules, generator, song.Work);
            var checks = new Dictionary<ChartInstrument, ReductionCheck>();
            var ladders = new List<GenerateDifficultiesPartPreview>();
            foreach (var (part, generated) in generation.Parts.OrderBy(static p => p.Key))
            {
                if (generated.Drafts.Count == 0)
                    continue;
                var check = generation.Check(part);
                checks[part] = check;
                var findings = check.Findings.Count == 0
                    ? Strings.GenerateDifficultiesNoFindings
                    : string.Format(CultureInfo.CurrentCulture, Strings.GenerateDifficultiesFindingsFormat, check.Findings.Count);
                var drafted = generated.Drafts.Select(static d => d.Target.Difficulty).ToList();
                ladders.Add(new GenerateDifficultiesPartPreview(part, ChartLabels.InstrumentName(part), DifficultyLadder.Of(check, drafted), findings));
            }

            return (generation, checks, ladders);
        }

        /// <summary>A song as the preview reads it once: its chart, its tracks as drafted, and how long it plays.</summary>
        private sealed record SongRead(SongChartRead Chart, IReadOnlyList<SongTrack> Tracks, double LengthSeconds)
        {
            // How far past the last note a song with no song_length is taken to play, in seconds.
            private const double TailSeconds = 2;

            public static async Task<SongRead> Of(GenerateDifficultiesSong song, CancellationToken cancellationToken)
            {
                var chart = await SongDifficultyGeneration.ReadAsync(song.Entry, song.Pairs, song.Draft, cancellationToken).ConfigureAwait(false);
                var tracks = SongTracks.Resolve(SongFile.ListTopLevel(song.Entry, cancellationToken).Select(static f => f.Name), song.Draft);
                string? songLength = song.Pairs.LastOrDefault(static p => p.Key.Equals("song_length", StringComparison.OrdinalIgnoreCase)).Value;
                double length = long.TryParse(songLength, NumberStyles.Integer, CultureInfo.InvariantCulture, out long ms) && ms > 0
                    ? ms / 1000.0
                    : LastNoteSeconds(chart) + TailSeconds;
                return new SongRead(chart, tracks, length);
            }

            private static double LastNoteSeconds(SongChartRead chart)
            {
                var load = chart.Load.Chart;
                long last = load.Parts.SelectMany(static p => p.Tracks).Where(static t => t.Notes.Count > 0).Select(static t => t.Notes[^1].Tick + t.Notes[^1].Length).DefaultIfEmpty(0).Max();
                return load.TempoMap.ToTempoMap().SecondsAt(last) + chart.Delay.TotalSeconds;
            }
        }
    }
}
