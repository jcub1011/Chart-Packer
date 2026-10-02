using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Collections;
using ChartPacker.Core.Charts;
using ChartPacker.Core.Jobs;
using ChartPacker.Core.Library;
using ChartPacker.Core.Merging;
using ChartPacker.Core.Metadata;
using ChartPacker.Core.Scanning;
using ChartPacker.Core.Text;
using ChartPacker.Resources;
using ChartPacker.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChartPacker.ViewModels
{
    /// <summary>One cell of a merge choice: a source, or leaving the thing out.</summary>
    /// <param name="counts">A part's note counts, easiest first, e.g. <c>312 · 640 · 980</c>.</param>
    /// <param name="toolTip">The tooltip, when there's more to it than <see cref="PlainText"/>.</param>
    public sealed partial class MergeOptionViewModel(MergeChoiceViewModel owner, int? source, string text, string? detail, bool isAvailable, bool isShown = true, string? counts = null, string? toolTip = null) : ObservableObject
    {

        /// <summary>The source it takes from; <see langword="null"/> to leave the thing out.</summary>
        public int? Source { get; } = source;

        /// <summary>What the cell shows; a metadata value can carry rich-text tags, which are drawn.</summary>
        public string Text { get; } = text;

        /// <summary><see cref="Text"/> without its tags, for the tooltip and screen readers.</summary>
        public string PlainText => RichText.ToPlainText(Text);

        public string? Detail { get; } = detail;

        public bool HasDetail => !string.IsNullOrEmpty(Detail);

        public string? Counts { get; } = counts;

        public bool HasCounts => !string.IsNullOrEmpty(Counts);

        public string ToolTip => toolTip ?? PlainText;

        /// <summary>The source has the thing, so it can be chosen.</summary>
        public bool IsAvailable { get; } = isAvailable;

        /// <summary>False for a column the choice has no cell in, which keeps the columns lined up.</summary>
        public bool IsShown { get; } = isShown;

        /// <summary>Radio buttons of one choice form one group.</summary>
        public string GroupName => owner.GroupName;

        [ObservableProperty]
        public partial bool IsSelected { get; set; }

        partial void OnIsSelectedChanged(bool value)
        {
            if (value)
                owner.OnOptionSelected(this);
        }
    }

    /// <summary>A row of the merge window: one thing to take from one of the sources.</summary>
    public sealed partial class MergeChoiceViewModel(string label, string? key = null) : ObservableObject
    {
        private static int _nextGroup;
        private bool _selecting;

        public string Label { get; } = label;

        /// <summary>The part id or metadata key the row is for.</summary>
        public string? Key { get; } = key;

        public string GroupName { get; } = "merge-choice-" + Interlocked.Increment(ref _nextGroup).ToString(CultureInfo.InvariantCulture);

        public List<MergeOptionViewModel> Options { get; } = [];

        public MergeOptionViewModel? Selected => Options.FirstOrDefault(o => o.IsSelected);

        /// <summary>Nothing is chosen yet, and something must be.</summary>
        public bool NeedsChoice => Selected is null;

        /// <summary>The user chose, so defaults no longer follow other choices.</summary>
        public bool IsTouched { get; private set; }

        /// <summary>Raised when the selection changes, by the user or by <see cref="Select"/>.</summary>
        public event Action<MergeChoiceViewModel>? Changed;

        /// <summary>Selects the option for <paramref name="source"/>, when the user hasn't chosen yet or <paramref name="force"/> is set.</summary>
        public void Select(int? source, bool force = false)
        {
            if (IsTouched && !force)
                return;

            var option = Options.FirstOrDefault(o => o.Source == source && o.IsAvailable && o.IsShown);
            _selecting = true;
            try
            {
                foreach (var other in Options)
                    other.IsSelected = ReferenceEquals(other, option);
            }
            finally
            {
                _selecting = false;
            }

            Raise();
        }

        internal void OnOptionSelected(MergeOptionViewModel option)
        {
            if (_selecting)
                return;

            IsTouched = true;
            _selecting = true;
            try
            {
                foreach (var other in Options)
                {
                    if (!ReferenceEquals(other, option))
                        other.IsSelected = false;
                }
            }
            finally
            {
                _selecting = false;
            }

            Raise();
        }

        private void Raise()
        {
            OnPropertyChanged(nameof(Selected));
            OnPropertyChanged(nameof(NeedsChoice));
            Changed?.Invoke(this);
        }
    }

    /// <summary>A column header: one of the songs being merged.</summary>
    /// <param name="Charter">The charter as stored, rich-text tags and all, for the header to draw.</param>
    public sealed record MergeSourceViewModel(string Title, string? Artist, string? Charter, string Format, string? Problem)
    {
        public bool HasArtist => !string.IsNullOrEmpty(Artist);

        public bool HasCharter => !string.IsNullOrEmpty(Charter);

        public string? CharterPlainText => Charter is null ? null : RichText.ToPlainText(Charter);

        public bool HasProblem => !string.IsNullOrEmpty(Problem);
    }

    /// <summary>
    /// The merge window: reads the selected songs, offers a choice wherever they differ, and merges
    /// them into a new <c>.sng</c> with <see cref="SongMerger"/>.
    /// </summary>
    /// <remarks>
    /// A part only one song has is taken from it; a part several have must be chosen. Metadata the songs
    /// agree on isn't offered. The audio defaults to the song with the most tracks, and the base chart,
    /// album art and backgrounds follow it until they're chosen.
    /// </remarks>
    /// <param name="entries">The songs, in the order their columns are shown.</param>
    /// <param name="hasDrafts">Whether each song has unsaved changes, which the merge doesn't see.</param>
    /// <param name="outputFolder">Where the merged <c>.sng</c> goes.</param>
    /// <param name="policy">What to do when it already exists.</param>
    /// <param name="video">Reads the background video's length, for <c>song_length</c>.</param>
    /// <param name="preview">
    /// The side panel's inspector, read-only but for its offsets (the note highway's, the lyrics', the
    /// audio's and the video's), with a player of its own; it shows the merged song as chosen.
    /// </param>
    /// <param name="previewDrafts">The preview's drafts: the offsets set on it, which the merge applies.</param>
    public sealed partial class MergeWindowViewModel(
        IReadOnlyList<SongEntry> entries,
        IReadOnlyList<bool> hasDrafts,
        string outputFolder,
        OverwritePolicy policy,
        IVideoDurationProbe video,
        SongDetailViewModel preview,
        MetadataDraftStore previewDrafts) : ViewModelBase
    {

        // The plan the preview shows, which its highway's, audio's and video's offsets are set against: the one it was staged
        // with, or the one it shows lined up live, ahead of staging.
        private SongMergePlan? _previewed;
        private SongMergeAnalysis? _analysis;
        private CancellationTokenSource? _cts;

        // The preview: a song folder the merge is staged into, what's in it, and what it was staged for.
        private readonly Dictionary<string, string> _staged = new(StringComparer.OrdinalIgnoreCase);
        private string? _previewFolder;
        private string? _previewedPlan;
        private bool _previewRunning;
        private bool _previewPending;
        private bool _closed;
        private CancellationTokenSource? _previewCts;

        /// <summary>The merged song as chosen so far, as the inspector shows a song.</summary>
        public SongDetailViewModel Preview { get; } = preview;

        /// <summary>The preview is being put together.</summary>
        [ObservableProperty]
        public partial bool IsPreviewUpdating { get; set; }

        /// <summary>Why there's no preview to show: a choice is still to be made, or putting it together failed.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasPreviewMessage))]
        [NotifyPropertyChangedFor(nameof(ShowPreview))]
        public partial string? PreviewMessage { get; set; } = Strings.MergeLoading;

        public bool HasPreviewMessage => PreviewMessage is not null;

        public bool ShowPreview => PreviewMessage is null;

        /// <summary>Parts of the preview that didn't come out exactly as their source has them.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasPreviewWarnings))]
        public partial IReadOnlyList<string> PreviewWarnings { get; set; } = [];

        public bool HasPreviewWarnings => PreviewWarnings.Count > 0;

        /// <summary>Asks the window to close; true when a song was merged.</summary>
        public event Action<bool>? CloseRequested;

        /// <summary>One column per source, plus one for leaving a thing out.</summary>
        public int ColumnCount => Sources.Count + 1;

        public AvaloniaList<MergeSourceViewModel> Sources { get; } = [];

        public MergeChoiceViewModel? BaseChoice { get; private set; }

        public AvaloniaList<MergeChoiceViewModel> Parts { get; } = [];

        public MergeChoiceViewModel? AudioChoice { get; private set; }

        public AvaloniaList<MergeChoiceViewModel> Assets { get; } = [];

        public AvaloniaList<MergeChoiceViewModel> Metadata { get; } = [];

        public bool HasNoConflicts => Metadata.Count == 0;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsReady))]
        [NotifyCanExecuteChangedFor(nameof(MergeCommand))]
        public partial bool IsLoading { get; set; } = true;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsReady))]
        [NotifyPropertyChangedFor(nameof(CloseLabel))]
        [NotifyCanExecuteChangedFor(nameof(MergeCommand))]
        public partial bool IsMerging { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsReady))]
        [NotifyPropertyChangedFor(nameof(CloseLabel))]
        [NotifyCanExecuteChangedFor(nameof(MergeCommand))]
        public partial bool IsDone { get; set; }

        /// <summary>The choices are loaded and can be changed.</summary>
        public bool IsReady => !IsLoading && !IsMerging && !IsDone && _analysis is not null;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasError))]
        public partial string? ErrorMessage { get; set; }

        public bool HasError => ErrorMessage is not null;

        [ObservableProperty]
        public partial string CharterPreview { get; set; } = "";

        [ObservableProperty]
        public partial string TargetText { get; set; } = "";

        /// <summary>Why the merge can't run as chosen, e.g. the output exists and is to be skipped.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasTargetProblem))]
        public partial string? TargetProblem { get; set; }

        public bool HasTargetProblem => TargetProblem is not null;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasResult))]
        public partial string? ResultText { get; set; }

        public bool HasResult => ResultText is not null;

        /// <summary>The merged <c>.sng</c>, once a song is merged.</summary>
        public string? MergedPath { get; private set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasWarnings))]
        public partial IReadOnlyList<string> Warnings { get; set; } = [];

        public bool HasWarnings => Warnings.Count > 0;

        public string CloseLabel => IsDone ? Strings.CloseButton : Strings.CancelButton;

        /// <summary>Reads the songs and builds the choices.</summary>
        public async Task LoadAsync()
        {
            Preview.Lyrics.PropertyChanged += OnPreviewLyricsChanged;
            _cts = new CancellationTokenSource();
            try
            {
                _analysis = await SongMergeAnalysis.LoadAsync(entries, _cts.Token);
                Build(_analysis);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ErrorMessage = string.Format(CultureInfo.CurrentCulture, Strings.MergeReadFailedFormat, ex.Message);
            }
            finally
            {
                IsLoading = false;
                Refresh();
            }
        }

        private void Build(SongMergeAnalysis analysis)
        {
            var sources = analysis.Sources;
            for (int i = 0; i < sources.Count; i++)
            {
                var s = sources[i];
                string? problem = s.ChartError is { } error ? string.Format(CultureInfo.CurrentCulture, Strings.MergeChartErrorFormat, error)
                    : i < hasDrafts.Count && hasDrafts[i] ? Strings.MergeUnsavedEdits
                    : null;
                Sources.Add(new MergeSourceViewModel(s.Headline.Name ?? s.Entry.DisplayName, s.Headline.Artist, SongHeadline.CharterMarkup(s.Pairs), FormatOf(s.Format), problem));
            }

            BaseChoice = Choice(Strings.MergeBaseLabel, null, i => sources[i].Chart is not null, i => FormatOf(sources[i].Format), null, leaveOut: null);

            foreach (var part in analysis.Parts)
            {
                var row = Choice(ChartLabels.PartName(part), part.Id, i => sources[i].HasPart(part.Id), i => DifficultiesOf(sources[i], part.Id), i => RatingOf(sources[i], part.Id), leaveOut: Strings.MergeLeaveOut,
                    counts: i => sources[i].Parts.First(p => p.Part.Id == part.Id).NoteCounts);
                var with = analysis.SourcesWith(part.Id);
                if (with.Count == 1)
                    row.Select(with[0]);
                Parts.Add(row);
            }

            AudioChoice = Choice(Strings.MergeAudioLabel, null, i => true, i => StemsOf(sources[i]), null, leaveOut: null);
            Assets.Add(Choice(Strings.MergeAlbumArtLabel, "album", i => sources[i].AlbumArt is not null, i => sources[i].AlbumArt ?? Strings.MergeUnavailable, null, leaveOut: Strings.MergeNone));
            Assets.Add(Choice(Strings.MergeBackgroundLabel, "background", i => sources[i].BackgroundImage is not null, i => sources[i].BackgroundImage ?? Strings.MergeUnavailable, null, leaveOut: Strings.MergeNone));
            Assets.Add(Choice(Strings.MergeVideoLabel, "video", i => sources[i].Video is not null, i => sources[i].Video ?? Strings.MergeUnavailable, null, leaveOut: Strings.MergeNone));

            foreach (var conflict in analysis.Conflicts)
                Metadata.Add(Choice(conflict.Key, conflict.Key, i => true, i => conflict.Values[i] ?? Strings.MergeNotSet, null, leaveOut: null));

            BuildAlignment(analysis);

            // The audio with the most tracks; the rest follow it until chosen.
            int audio = Enumerable.Range(0, sources.Count)
                .OrderByDescending(i => sources[i].AudioFiles.Count(f => !AudioStems.StemOf(f)!.Equals(AudioStems.Preview, StringComparison.OrdinalIgnoreCase)))
                .ThenBy(i => i)
                .First();
            AudioChoice.Select(audio);

            OnPropertyChanged(nameof(ColumnCount));
            OnPropertyChanged(nameof(BaseChoice));
            OnPropertyChanged(nameof(AudioChoice));
            OnPropertyChanged(nameof(HasNoConflicts));
        }

        /// <param name="counts">An available cell's note counts, for a part's row.</param>
        private MergeChoiceViewModel Choice(string label, string? key, Func<int, bool> available, Func<int, string> text, Func<int, string?>? detail, string? leaveOut,
            Func<int, IReadOnlyList<NoteCount>>? counts = null)
        {
            var row = new MergeChoiceViewModel(label, key);
            for (int i = 0; i < Sources.Count; i++)
            {
                bool has = available(i);
                string shown = has ? text(i) : Strings.MergeUnavailable;
                var noteCounts = has && counts is not null ? counts(i) : [];
                bool counted = noteCounts.Count > 0;
                row.Options.Add(new MergeOptionViewModel(row, i, shown, detail?.Invoke(i), has,
                    counts: counted ? string.Join(Strings.MergeNoteCountSeparator, noteCounts.Select(c => c.Count.ToString("N0", CultureInfo.CurrentCulture))) : null,
                    toolTip: counted ? string.Join(Environment.NewLine, [RichText.ToPlainText(shown), .. noteCounts.Select(NoteCountLine)]) : null));
            }

            row.Options.Add(leaveOut is null
                ? new MergeOptionViewModel(row, null, "", null, isAvailable: false, isShown: false)
                : new MergeOptionViewModel(row, null, leaveOut, null, isAvailable: true));
            row.Changed += OnChoiceChanged;
            return row;
        }

        private void OnChoiceChanged(MergeChoiceViewModel row)
        {
            if (_analysis is not { } analysis)
                return;

            if (ReferenceEquals(row, AudioChoice) && row.Selected?.Source is { } audio)
            {
                var sources = analysis.Sources;
                int? baseSource = sources[audio].Chart is not null ? audio : Enumerable.Range(0, sources.Count).Cast<int?>().FirstOrDefault(i => sources[i!.Value].Chart is not null);
                BaseChoice?.Select(baseSource);

                Assets[0].Select(Pick(audio, i => sources[i].AlbumArt is not null));
                Assets[1].Select(Pick(audio, i => sources[i].BackgroundImage is not null));
                Assets[2].Select(Pick(audio, i => sources[i].Video is not null));
                AlignTo(audio);
            }

            if (ReferenceEquals(row, BaseChoice) && row.Selected?.Source is { } chosenBase)
            {
                foreach (var conflict in Metadata)
                    conflict.Select(chosenBase);
            }

            Refresh();
        }

        /// <summary><paramref name="preferred"/> when it has the thing, otherwise the first source that does.</summary>
        private int? Pick(int preferred, Func<int, bool> has) =>
            has(preferred) ? preferred : Enumerable.Range(0, Sources.Count).Cast<int?>().FirstOrDefault(i => has(i!.Value));

        /// <summary>The plan as chosen so far; <see langword="null"/> while something still needs choosing.</summary>
        private SongMergePlan? Plan()
        {
            if (_analysis is not { } analysis || BaseChoice?.Selected?.Source is not { } baseSource || AudioChoice?.Selected?.Source is not { } audio)
                return null;
            if (Parts.Any(p => p.NeedsChoice) || !Parts.Any(p => p.Selected?.Source is not null))
                return null;

            var plan = new SongMergePlan(
                analysis,
                baseSource,
                Parts.Where(p => p.Selected?.Source is not null).ToDictionary(p => p.Key!, p => p.Selected!.Source!.Value),
                Metadata.ToDictionary(m => m.Key!, m => (int?)(m.Selected?.Source ?? baseSource)),
                audio,
                Assets[0].Selected?.Source,
                Assets[1].Selected?.Source,
                Assets[2].Selected?.Source,
                SourceOffsets: SourceOffsets());

            // Checked here, not only when the preview is next staged, so a merge straight after a change can't take them.
            var timing = TimingOf(plan);
            if (PreviewDraft() is not { } draft)
                return plan;

            var offsets = draft.ChartOffsets.Where(p => OffsetApplies(p.Key, plan, timing)).ToDictionary(p => p.Key, p => p.Value);
            var (audioOffset, videoOffset) = PreviewTimingOffsets(draft);
            return plan with
            {
                ChartOffsets = offsets.Count > 0 ? offsets : null,
                AudioOffset = AudioOffsetApplies(plan, timing) ? audioOffset : 0,
                VideoOffset = VideoOffsetApplies(plan, timing) ? videoOffset : 0,
                VocalsOffset = VocalsOffsetApplies(plan, timing) ? draft.VocalsOffset : 0,
            };
        }

        /// <summary>
        /// Whether <paramref name="plan"/> stages the <c>delay</c> and the <c>video_start_time</c> as <see cref="_previewed"/>
        /// did: an offset set against one that has since changed, as by choosing another source's value for it, no
        /// longer means what it did. Lining the songs up moves them both, but an offset set on the preview moves things
        /// from where the songs are lined up to, so it still means what it did.
        /// </summary>
        private (bool SameDelay, bool SameVideoStart) TimingOf(SongMergePlan plan)
        {
            if (_previewed is not { } previewed)
                return (false, false);

            var before = SongMerger.BuildMetadata(previewed with { SourceOffsets = null });
            var after = SongMerger.BuildMetadata(plan with { SourceOffsets = null });
            return (TimingOffsets.OffsetOf(TimingOffsets.AudioKey, before, after, TimeSpan.Zero) == 0,
                TimingOffsets.OffsetOf(TimingOffsets.VideoKey, before, after, TimeSpan.Zero) == 0);
        }

        /// <summary>
        /// The preview's draft, which holds the offsets set on it against <see cref="_previewed"/>: the highway's, the lyrics',
        /// the audio's and the video's. <see langword="null"/> while none are set.
        /// </summary>
        private MetadataDraft? PreviewDraft() =>
            _previewFolder is { } folder && previewDrafts.HasDrafts(folder) ? previewDrafts.Get(folder) : null;

        /// <summary>
        /// The audio and video offsets set on the preview, against <see cref="_previewed"/>: how far its <paramref name="draft"/>
        /// moves the <c>delay</c> and <c>video_start_time</c> the merge staged it with.
        /// </summary>
        private (int Audio, int Video) PreviewTimingOffsets(MetadataDraft draft)
        {
            if (_previewed is not { } previewed)
                return (0, 0);

            var staged = SongMerger.BuildMetadata(previewed);
            var drafted = draft.ApplyTo(staged);
            return (TimingOffsets.OffsetOf(TimingOffsets.AudioKey, staged, drafted, TimeSpan.Zero),
                TimingOffsets.OffsetOf(TimingOffsets.VideoKey, staged, drafted, TimeSpan.Zero));
        }

        /// <summary>
        /// Whether the audio offset set on the preview still lines the audio up in <paramref name="plan"/>: it's the same
        /// audio against the same base chart, with the same <c>delay</c>.
        /// </summary>
        private bool AudioOffsetApplies(SongMergePlan plan, (bool SameDelay, bool SameVideoStart) timing) =>
            _previewed is { } previewed && previewed.BaseSource == plan.BaseSource && previewed.AudioSource == plan.AudioSource && timing.SameDelay;

        /// <summary>
        /// Whether the video offset set on the preview still lines the video up in <paramref name="plan"/>: it's the same
        /// video against the same audio, with the same <c>video_start_time</c>.
        /// </summary>
        private bool VideoOffsetApplies(SongMergePlan plan, (bool SameDelay, bool SameVideoStart) timing) =>
            _previewed is { } previewed && plan.VideoSource is not null && previewed.VideoSource == plan.VideoSource && previewed.AudioSource == plan.AudioSource
            && timing.SameVideoStart;

        /// <summary>
        /// The preview's draft holds its audio and video offsets as changes to the keys of the song it was staged as;
        /// staged again as <paramref name="plan"/>, they're made changes to its keys instead, or dropped where they no
        /// longer apply, as <see cref="Plan"/> has worked out.
        /// </summary>
        private void RebaseTimingOffsets(SongMergePlan plan)
        {
            if (PreviewDraft() is not { } draft)
                return;

            var staged = SongMerger.BuildMetadata(plan);
            TimingOffsets.Set(draft, TimingOffsets.AudioKey, staged, TimeSpan.Zero, plan.AudioOffset);
            TimingOffsets.Set(draft, TimingOffsets.VideoKey, staged, TimeSpan.Zero, plan.VideoOffset);
        }

        /// <summary>
        /// Whether an offset set on the preview still lines <paramref name="key"/> up in <paramref name="plan"/>: its part
        /// comes from the same song as when it was set, and the audio offset still applies, as the same base chart's tempo
        /// map and the same <c>delay</c> place the notes against the same audio.
        /// </summary>
        private bool OffsetApplies(ChartTrackKey key, SongMergePlan plan, (bool SameDelay, bool SameVideoStart) timing) =>
            AudioOffsetApplies(plan, timing) && _previewed is { } previewed
            && plan.Analysis.Parts.Where(p => MidiPartCatalog.InstrumentsOf(p).Contains(key.Instrument)).All(p => SamePart(p.Id, previewed, plan));

        /// <summary>
        /// Whether the offset set on the preview's lyrics still lines the vocals up in <paramref name="plan"/>: the vocals
        /// and harmonies come from the same songs as when it was set, and the audio offset still applies.
        /// </summary>
        private bool VocalsOffsetApplies(SongMergePlan plan, (bool SameDelay, bool SameVideoStart) timing) =>
            AudioOffsetApplies(plan, timing) && _previewed is { } previewed
            && SamePart("vocals", previewed, plan) && SamePart("harmonies", previewed, plan);

        /// <summary>Whether <paramref name="partId"/> is taken from the same song in both plans, or left out of both.</summary>
        private static bool SamePart(string partId, SongMergePlan before, SongMergePlan after)
        {
            bool was = before.Parts.TryGetValue(partId, out int from);
            bool now = after.Parts.TryGetValue(partId, out int to);
            return was == now && from == to;
        }

        private void Refresh()
        {
            if (_analysis is { } analysis)
            {
                CharterPreview = CharterList.Merge(analysis.Sources.Select(s => s.Value("charter")));
                if (string.IsNullOrEmpty(CharterPreview))
                    CharterPreview = Strings.MergeNotSet;
            }

            // Once merged, the output exists; that's no longer a problem to show.
            if (IsDone)
            {
                MergeCommand.NotifyCanExecuteChanged();
                return;
            }

            // Lining the songs up moves the preview right away, the way dragging a highway offset does; the staging that
            // follows changes nothing shown.
            PreviewLiveAlignment();
            SchedulePreview();
            RefreshTarget();
        }

        /// <summary>Where the merge will write, and why it can't run as chosen, if it can't.</summary>
        private void RefreshTarget()
        {
            TargetProblem = null;
            if (Plan() is { } plan)
            {
                string preferred = Path.Combine(outputFolder, SongMerger.FileNameFor(SongMerger.BuildMetadata(plan), plan.Analysis.Sources[plan.BaseSource].Entry.DisplayName) + ".sng");
                string? target = SongMerger.TargetFor(plan, outputFolder, policy);
                TargetText = target ?? preferred;
                if (target is null)
                    TargetProblem = string.Format(CultureInfo.CurrentCulture, Strings.MergeTargetExistsFormat, Path.GetFileName(preferred));
                else if (SongMerger.IsSource(plan, target))
                    TargetProblem = string.Format(CultureInfo.CurrentCulture, Strings.MergeTargetIsSourceFormat, Path.GetFileName(target));

                // The lyrics say so only of an offset set on vocals the plan still takes; merging would fail on it.
                else if (plan.VocalsOffset != 0 && Preview.Lyrics.HasProblem)
                    TargetProblem = Strings.MergeLyricsOffsetBeforeStart;
                else
                    TargetProblem = AlignmentProblemOf(plan);
            }
            else
            {
                TargetText = !Parts.Any() ? Strings.MergeNoParts : Parts.Any(p => p.NeedsChoice) ? Strings.MergeChooseParts : Strings.MergeChooseSomething;
            }

            MergeCommand.NotifyCanExecuteChanged();
        }

        /// <summary>
        /// An offset set on the preview's lyrics that moves the vocals before the song starts can't be merged; see <see cref="RefreshTarget"/>.
        /// The preview isn't staged again for it, as staging clears the lyrics' problem and would cancel itself.
        /// </summary>
        private void OnPreviewLyricsChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(LyricsViewModel.HasProblem) && !IsDone && !_closed)
                RefreshTarget();
        }

        private bool CanMerge() => IsReady && TargetProblem is null && Plan() is not null;

        [RelayCommand(CanExecute = nameof(CanMerge))]
        private async Task MergeAsync()
        {
            if (Plan() is not { } plan)
                return;

            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            ErrorMessage = null;
            IsMerging = true;
            try
            {
                var result = await SongMerger.MergeAsync(plan, outputFolder, policy, video, _cts.Token);
                MergedPath = result.OutputPath;
                ResultText = string.Format(CultureInfo.CurrentCulture, Strings.MergeDoneFormat, result.OutputPath);
                Warnings = [.. result.Warnings.Select(Describe), .. result.PackFindings.Select(f => f.Message)];
                IsDone = true;
            }
            catch (OperationCanceledException)
            {
                ErrorMessage = Strings.MergeCanceled;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                ErrorMessage = string.Format(CultureInfo.CurrentCulture, Strings.MergeFailedFormat, ex.Message);
            }
            finally
            {
                IsMerging = false;
                Refresh();
            }
        }

        /// <summary>Cancels a merge that's running; otherwise closes the window.</summary>
        [RelayCommand]
        private void Close()
        {
            if (IsMerging)
            {
                _cts?.Cancel();
                return;
            }

            _cts?.Cancel();
            CloseRequested?.Invoke(IsDone);
        }

        /// <summary>The window is closing some other way; stops anything still running.</summary>
        public void OnClosed()
        {
            _closed = true;
            Preview.Lyrics.PropertyChanged -= OnPreviewLyricsChanged;
            _cts?.Cancel();
            _previewCts?.Cancel();
        }

        /// <summary>
        /// Once the window has closed: stops the preview's playback, lets go of its files and deletes
        /// the folder it was staged in.
        /// </summary>
        public async Task ShutdownAsync()
        {
            OnClosed();
            await Preview.ShowAsync([]);
            await Preview.Player.WhenFilesClosedAsync();
            Preview.Player.Dispose();

            if (_previewFolder is { } folder)
            {
                try
                {
                    Directory.Delete(folder, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A temporary folder that can't be deleted now is the system's to clean up.
                }
            }
        }

        /// <summary>Puts the preview together again soon, once the choices have settled, if they changed what it shows.</summary>
        private void SchedulePreview()
        {
            _previewPending = true;
            if (_previewRunning)
            {
                // The newer choices win; a staging still under way is abandoned.
                _previewCts?.Cancel();
                return;
            }

            _ = RunPreviewAsync();
        }

        private async Task RunPreviewAsync()
        {
            _previewRunning = true;
            try
            {
                while (_previewPending && !_closed)
                {
                    _previewPending = false;

                    // Clicks in quick succession make one preview, not one each.
                    await Task.Delay(PreviewDelay);
                    if (!_previewPending)
                        await UpdatePreviewAsync();
                }
            }
            finally
            {
                _previewRunning = false;
            }
        }

        private static readonly TimeSpan PreviewDelay = TimeSpan.FromMilliseconds(300);

        private async Task UpdatePreviewAsync()
        {
            if (_closed || IsLoading)
                return;

            if (Plan() is not { } plan)
            {
                _previewedPlan = null;
                PreviewWarnings = [];
                PreviewMessage = _analysis is null ? ErrorMessage ?? PreviewMessage : Strings.MergePreviewNeedsChoices;
                await Preview.ShowAsync([]);
                return;
            }

            ForgetStaleOffsets(plan);
            string signature = Signature(plan);
            if (signature == _previewedPlan && PreviewMessage is null)
                return;

            using var cts = new CancellationTokenSource();
            _previewCts = cts;
            IsPreviewUpdating = true;
            try
            {
                // Where playback is, to put it back should reading the song again stop it: the audio, the parts or the
                // names may change what's staged, but that shouldn't move the playhead.
                TimeSpan resumeAt = Preview.Player.CurrentPosition;
                bool wasPlaying = Preview.Player.IsPlaying;
                bool wasScrubbing = Preview.Player.IsScrubbing;

                // Only the songs' lining-up changed since the chart was read, which the preview already shows: staging
                // writes the same, and nothing shown is read again. Otherwise it's read again in place; only what holds
                // a file staging replaces is let go first: playback for other audio, the video for another video.
                bool live = LiveShiftsOf(plan) is not null;
                if (!live)
                {
                    if (_previewed?.AudioSource != plan.AudioSource)
                    {
                        await Preview.ReleaseFilesAsync();
                    }
                    else if (_previewed?.VideoSource != plan.VideoSource)
                    {
                        Preview.Background.Unload();
                        await Preview.Background.WhenClosedAsync();
                    }
                }

                _previewFolder ??= Directory.CreateTempSubdirectory("chartpacker-merge-preview-").FullName;
                var warnings = await SongMerger.StageAsync(plan, _previewFolder, video, _staged, cts.Token);
                if (cts.IsCancellationRequested || _closed)
                    return;

                string name = SongMerger.FileNameFor(SongMerger.BuildMetadata(plan), plan.Analysis.Sources[plan.BaseSource].Entry.DisplayName);
                PreviewWarnings = [.. warnings.Select(Describe)];
                PreviewMessage = null;
                _previewedPlan = signature;
                RebaseTimingOffsets(plan);
                _previewed = plan;

                if (live)
                {
                    // The rows, files and length catch up with what's staged; the rest already shows it.
                    await Preview.SyncPreviewStagingAsync(cts.Token);
                    return;
                }

                await Preview.ShowAsync([new SongEntry(SongEntryKind.SongFolder, _previewFolder, ".", name)], inPlace: true);
                if (_closed)
                    return;

                // What the preview read is what was just staged: the next staging can't start until this one is done.
                _loadedPlan = plan;
                _loadedGeneration = Preview.LoadGeneration;

                // Playback that went on through the reload needs nothing. Otherwise it went with it, for other tracks;
                // it goes back to where it was, playing again if it was, even with another change on its way, which
                // lets go of it again only if it has to.
                if (wasPlaying && !wasScrubbing && !Preview.Player.IsPlaying)
                    await Preview.Player.RestorePlaybackAsync(resumeAt, resume: true);
                else if (!wasPlaying && !wasScrubbing && Preview.Player.PositionSeconds != resumeAt.TotalSeconds)
                    await Preview.Player.RestorePlaybackAsync(resumeAt, resume: false);
            }
            catch (OperationCanceledException)
            {
                _previewedPlan = null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                _previewedPlan = null;
                PreviewWarnings = [];
                PreviewMessage = string.Format(CultureInfo.CurrentCulture, Strings.MergePreviewFailedFormat, ex.Message);
                await Preview.ShowAsync([]);
            }
            finally
            {
                if (ReferenceEquals(_previewCts, cts))
                    _previewCts = null;
                IsPreviewUpdating = false;
            }
        }

        /// <summary>
        /// Drops the highway offsets that no longer apply to <paramref name="plan"/>: those of a part now
        /// taken from another song, or left out, and all of them when the audio, the base chart or the
        /// <c>delay</c> changed. They were set to line the chart as it was up with the audio as it was.
        /// The lyrics' offset goes the same way, with the vocals or harmonies taken from another song. The preview
        /// is told, so it doesn't go on showing an offset that's gone.
        /// </summary>
        private void ForgetStaleOffsets(SongMergePlan plan)
        {
            if (_previewFolder is not { } folder || PreviewDraft() is not { } draft)
                return;

            var timing = TimingOf(plan);
            var stale = draft.ChartOffsets.Keys.Where(k => !OffsetApplies(k, plan, timing)).ToList();
            foreach (var key in stale)
                draft.SetChartOffset(key, 0);

            bool staleVocals = draft.HasVocalsOffset && !VocalsOffsetApplies(plan, timing);
            if (staleVocals)
                draft.SetVocalsOffset(0);

            if (stale.Count > 0 || staleVocals)
                previewDrafts.NotifyChanged(folder);
        }

        /// <summary>
        /// What a plan takes from where, and how far it lines each song up, to tell whether the preview already shows it; the
        /// preview's own offsets aren't part of it, as the preview shows them itself.
        /// </summary>
        private static string Signature(SongMergePlan plan) => string.Join(
            ";",
            [
                plan.BaseSource.ToString(CultureInfo.InvariantCulture),
                plan.AudioSource.ToString(CultureInfo.InvariantCulture),
                $"{plan.AlbumArtSource}/{plan.BackgroundSource}/{plan.VideoSource}",
                .. plan.Parts.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value}"),
                .. plan.Metadata.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value}"),
                .. (plan.SourceOffsets ?? new Dictionary<int, int>()).OrderBy(p => p.Key).Select(p => $"@{p.Key}={p.Value}"),
            ]);

        // How many of a warning's places it lists before saying how many more there are.
        private const int WarningTimesShown = 5;

        private static string Describe(ChartMergeWarning warning)
        {
            string format = warning.Issue switch
            {
                ChartMergeIssue.SustainsChanged => Strings.MergeIssueSustainsFormat,
                ChartMergeIssue.MarkingsChanged => Strings.MergeIssueMarkingsFormat,
                ChartMergeIssue.NotesChanged => Strings.MergeIssueNotesChangedFormat,
                _ => Strings.MergeIssueNotesLostFormat,
            };
            string text = string.Format(CultureInfo.CurrentCulture, format, TrackName(warning.TrackName), ChartLabels.DifficultyName(warning.Difficulty));
            if (warning.Times.Count == 0)
                return text;

            // Where, as song time in the source chart, so they can be found in a chart editor.
            string times = string.Join(", ", warning.Times.Take(WarningTimesShown).Select(t => Controls.AlignmentTimelineView.FormatTime(t.TotalSeconds, 1)));
            if (warning.Times.Count > WarningTimesShown)
                times = string.Format(CultureInfo.CurrentCulture, Strings.MergeIssueMoreTimesFormat, times, warning.Times.Count - WarningTimesShown);
            return string.Format(CultureInfo.CurrentCulture, Strings.MergeIssueTimesFormat, text, times);
        }

        private static string TrackName(string track) => MidiPartCatalog.PartOf(track) is { IsKnown: true } part ? ChartLabels.PartName(part) : track;

        private static string FormatOf(MergeChartFormat format) => format switch
        {
            MergeChartFormat.Midi => Strings.MergeFormatMidi,
            MergeChartFormat.Chart => Strings.MergeFormatChart,
            _ => Strings.MergeFormatNone,
        };

        /// <summary>The difficulties a source's part has notes for, as letters; for a part that isn't read, just that it's there.</summary>
        private static string DifficultiesOf(MergeSource source, string partId)
        {
            var part = source.Parts.First(p => p.Part.Id == partId);
            string letters = string.Join(" ", part.Difficulties.Select(ChartLabels.DifficultyLetter));
            return letters.Length > 0 ? letters : Strings.MergePartPresent;
        }

        /// <summary>A tooltip's line for <paramref name="count"/>, e.g. <c>Expert: 1,234 notes</c>.</summary>
        private static string NoteCountLine(NoteCount count) =>
            string.Format(CultureInfo.CurrentCulture, Strings.NoteCountLineFormat, ChartLabels.NoteCountLabel(count), ChartLabels.NoteCountText(count.Count));

        /// <summary>The source's song.ini rating for the part, which comes along with it.</summary>
        private static string? RatingOf(MergeSource source, string partId) =>
            source.Parts.FirstOrDefault(p => p.Part.Id == partId) is { Difficulty: { } rating } part && MetadataDifficulty.IsDifficultyKey(part.Part.Keys[0])
                ? string.Format(CultureInfo.CurrentCulture, Strings.MergeDifficultyFormat, rating)
                : null;

        private static string StemsOf(MergeSource source) =>
            source.AudioFiles.Count == 0 ? Strings.MergeStemsNone : string.Join(", ", source.AudioFiles);
    }
}
