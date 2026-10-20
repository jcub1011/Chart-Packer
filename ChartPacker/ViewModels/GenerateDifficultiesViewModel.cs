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
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charts;
using ChartShared.Core.Scanning;
using ChartShared.Core.Settings;
using ChartShared.Core.Validation;
using ChartShared.UI.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace ChartPacker.ViewModels
{
    /// <summary>A song in the Generate difficulties dialog: its name, and each part's missing difficulties.</summary>
    public sealed record GenerateDifficultiesSongRow(GenerateDifficultiesSong Song, string Title, string Missing);

    /// <summary>
    /// A part of the song picked in the Generate difficulties dialog, as it would be generated: its difficulties on the
    /// estimator's ladder, the new ones marked, and how many lint findings the new ones have.
    /// </summary>
    public sealed record GenerateDifficultiesPartPreview(ChartInstrument Part, string Name, DifficultyLadder Ladder, string Findings);

    /// <summary>
    /// The Generate difficulties dialog (charter-spec C16 item 5): Fix selected in the Validator opens it for the songs whose
    /// missing difficulties (CHR087) are ticked. It shows the default reduction rules to change for this run, by family
    /// and target, and the song picked as its difficulties would be generated, each change worked out again: each part on
    /// the estimator's ladder, and one part's drafts in the reduction preview, as highways played with the song, as Chart
    /// Builder's Reduce shows them. Generate hands the rules back; Save as defaults writes them to the rules both apps share.
    /// </summary>
    public sealed partial class GenerateDifficultiesViewModel : ViewModelBase, IDisposable
    {
        // How long after a change of the rules the preview is worked out again, so typing a number does it once.
        private static readonly TimeSpan PreviewDelay = TimeSpan.FromMilliseconds(200);

        // What the preview remembers, for as long as Chart Packer runs: whether it's shown, its look-ahead and its filters.
        private static readonly ReductionPreviewMemory PreviewSettings = new();

        private readonly ReductionRulesFile _rulesFile;
        private readonly PlayerViewModel _player;
        private readonly PlayerPreviewPlayback _playback;
        private readonly DispatcherTimer _redraw;
        private readonly CancellationTokenSource _closing = new();

        // Each song's chart and tracks, read once, as they'd be read for generating.
        private readonly Dictionary<GenerateDifficultiesSong, Task<SongRead>> _reads = [];

        // The song picked as last worked out, for the preview to show another of its parts without working it out again.
        private (GenerateDifficultiesSong Song, SongRead Read, SongGeneration Generation, IReadOnlyDictionary<ChartInstrument, ReductionCheck> Checks)? _shown;
        private int _previews;
        private bool _loading;

        // Set while a redraw picks the part again, which it then shows itself.
        private bool _pickingPart;
        private bool _disposed;

        /// <param name="songs">The songs to generate.</param>
        /// <param name="rules">The default rules, which it starts from and can save.</param>
        /// <param name="player">Plays the song picked, for the preview; the dialog's own, which the caller disposes.</param>
        public GenerateDifficultiesViewModel(IReadOnlyList<GenerateDifficultiesSong> songs, ReductionRulesFile rules, PlayerViewModel player)
        {
            ArgumentNullException.ThrowIfNull(songs);
            ArgumentNullException.ThrowIfNull(rules);
            ArgumentNullException.ThrowIfNull(player);
            _rulesFile = rules;
            _player = player;
            _playback = new PlayerPreviewPlayback(player);
            Preview = new ReductionPreviewViewModel(_playback, PreviewSettings, wheelUpIsLater: true);
            Songs = [.. songs.Select(s => new GenerateDifficultiesSongRow(s, s.Title, MissingText(s)))];

            // The rules first shown are those of the first part to generate.
            var first = songs.SelectMany(static s => s.Targets).FirstOrDefault();
            var kind = first.Value is null ? ChartInstrumentKind.FiveFret : ChartInstruments.KindOf(first.Key);
            var target = first.Value?.FirstOrDefault() ?? ChartDifficulty.Hard;
            Rules = new ReductionRulesViewModel(rules.Load(), kind, target);
            Rules.Changed += (_, _) => ScheduleRedraw();
            _loading = true;
            SelectedFamily = FamilyChoices.First(c => c.Kind == kind);
            SelectedTarget = TargetChoices.First(c => c.Difficulty == target);
            _loading = false;

            _redraw = new DispatcherTimer { Interval = PreviewDelay };
            _redraw.Tick += (_, _) => _ = RedrawAsync();
            SelectedSong = Songs[0];
        }

        /// <summary>The rules to generate by once Generate is pressed; null until then, and when it's cancelled.</summary>
        public ReductionDefaults? Result { get; private set; }

        /// <summary>Raised when the dialog's work is done: generated or cancelled.</summary>
        public event EventHandler? Done;

        public IReadOnlyList<GenerateDifficultiesSongRow> Songs { get; }

        /// <summary>The rules for the family and target picked, as fields; changes are this run's until saved as defaults.</summary>
        public ReductionRulesViewModel Rules { get; }

        /// <summary>The picked part's drafts against what they're made from, as highways played with the song.</summary>
        public ReductionPreviewViewModel Preview { get; }

        public IReadOnlyList<FamilyChoice> FamilyChoices => ReductionRulesViewModel.FamilyChoices;

        public IReadOnlyList<DifficultyChoice> TargetChoices => ReductionRulesViewModel.TargetChoices;

        /// <summary>The family whose rules are shown.</summary>
        [ObservableProperty]
        public partial FamilyChoice? SelectedFamily { get; set; }

        /// <summary>The target difficulty whose rules are shown.</summary>
        [ObservableProperty]
        public partial DifficultyChoice? SelectedTarget { get; set; }

        /// <summary>The song whose difficulties are shown, and played.</summary>
        [ObservableProperty]
        public partial GenerateDifficultiesSongRow? SelectedSong { get; set; }

        /// <summary>Each part of the song picked, as it'd be generated.</summary>
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

        /// <summary>Whether the default rules were saved, or couldn't be; empty before Save as defaults.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasSaveStatus))]
        public partial string SaveStatus { get; private set; } = "";

        public bool HasSaveStatus => SaveStatus.Length > 0;

        partial void OnSelectedFamilyChanged(FamilyChoice? value) => ShowRules();

        partial void OnSelectedTargetChanged(DifficultyChoice? value) => ShowRules();

        partial void OnSelectedSongChanged(GenerateDifficultiesSongRow? value)
        {
            _shown = null;
            Ladders = [];
            SelectedPart = null;
            _playback.SetChart(null, TimeSpan.Zero);
            _player.Unload();
            _ = RedrawAsync();
        }

        partial void OnSelectedPartChanged(GenerateDifficultiesPartPreview? value)
        {
            if (!_pickingPart)
                ShowPart();
        }

        /// <summary>Hands back the rules as shown, to generate every song by.</summary>
        [RelayCommand]
        private void Generate()
        {
            Result = Rules.Defaults;
            Finish();
        }

        /// <summary>Generates nothing.</summary>
        [RelayCommand]
        public void Cancel()
        {
            Result = null;
            Finish();
        }

        /// <summary>The shipped rules for the family and target shown, in place of what's shown.</summary>
        [RelayCommand]
        private void Reset() => Rules.Reset();

        /// <summary>Writes the rules as shown as the defaults both apps start from.</summary>
        [RelayCommand]
        private void SaveDefaults() =>
            SaveStatus = _rulesFile.Save(Rules.Defaults) ? Strings.GenerateDifficultiesSavedDefaults : Strings.GenerateDifficultiesSaveDefaultsFailed;

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

        private void ShowRules()
        {
            if (_loading || SelectedFamily is not { } family || SelectedTarget is not { } target)
                return;
            Rules.Show(family.Kind, target.Difficulty);
        }

        private void ScheduleRedraw()
        {
            SaveStatus = "";
            _redraw.Stop();
            _redraw.Start();
        }

        // The song picked as it'd be generated by the rules as they are, worked out off the UI thread; a newer one replaces
        // it. The song is read, and given to the player, the first time it's picked.
        private async Task RedrawAsync()
        {
            _redraw.Stop();
            int redraw = ++_previews;
            if (SelectedSong is not { Song: var song })
            {
                Ladders = [];
                LaddersStatus = Strings.GenerateDifficultiesPickSong;
                Preview.Clear(Strings.GenerateDifficultiesPickSong);
                return;
            }

            LaddersStatus = Strings.GenerateDifficultiesWorking;
            var rules = Rules.Defaults;
            var token = _closing.Token;
            try
            {
                var read = await ReadOf(song);
                if (redraw != _previews || token.IsCancellationRequested)
                    return;
                LoadPlayer(song, read);

                var (generation, checks, ladders) = await Task.Run(() => Work(song, read, rules), token);
                if (redraw != _previews || token.IsCancellationRequested)
                    return;
                _shown = (song, read, generation, checks);
                var part = SelectedPart?.Part;
                Ladders = ladders;
                LaddersStatus = ladders.Count == 0 ? Strings.GenerateDifficultiesNothing : "";
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
                Log.ForContext<GenerateDifficultiesViewModel>().Warning(ex, "Previewing generated difficulties of {Song} failed", song.Entry.FullPath);
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
                    Preview.Clear(Strings.GenerateDifficultiesNothing);
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

        // The song's tracks, into the player, and its chart's timing, for the change strip, once it's picked.
        private void LoadPlayer(GenerateDifficultiesSong song, SongRead read)
        {
            if (_player.LoadedEntry != song.Entry)
            {
                _player.Load(song.Entry, read.Tracks, null);
                _player.SongLengthSeconds = read.LengthSeconds;
            }

            _playback.SetChart(read.Chart.Load.Chart.TempoMap.ToTempoMap(), read.Chart.Delay);
        }

        private Task<SongRead> ReadOf(GenerateDifficultiesSong song)
        {
            if (!_reads.TryGetValue(song, out var read))
                _reads[song] = read = Task.Run(() => SongRead.Of(song, _closing.Token));
            return read;
        }

        private static (SongGeneration Generation, IReadOnlyDictionary<ChartInstrument, ReductionCheck> Checks, IReadOnlyList<GenerateDifficultiesPartPreview> Ladders) Work(
            GenerateDifficultiesSong song,
            SongRead read,
            ReductionDefaults rules)
        {
            var generation = SongDifficultyGeneration.Prepare(read.Chart, song.Pairs, rules);
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

        private void Finish()
        {
            _redraw.Stop();
            _closing.Cancel();
            Done?.Invoke(this, EventArgs.Empty);
        }

        // Each part's missing difficulties, e.g. "Guitar Hard, Medium; Drums Easy".
        private static string MissingText(GenerateDifficultiesSong song) =>
            string.Join(
                Strings.ValidatorMissingPartSeparator,
                song.Targets.Select(t => string.Format(
                    CultureInfo.CurrentCulture,
                    Strings.ValidatorMissingPartFormat,
                    ChartLabels.InstrumentName(t.Key),
                    string.Join(", ", t.Value.Select(ChartLabels.DifficultyName)))));

        /// <summary>A song as the dialog reads it once: its chart, its tracks as drafted, and how long it plays.</summary>
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
