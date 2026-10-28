using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Collections;
using ChartPacker.Resources;
using ChartPacker.Services;
using ChartShared.Core.Charts;
using ChartShared.Core.Jobs;
using ChartShared.Core.Library;
using ChartShared.Core.Merging;
using ChartShared.Core.Metadata;
using ChartShared.Core.Scanning;
using ChartShared.Core.Text;
using ChartShared.UI.Resources;
using ChartShared.UI.Services;
using ChartShared.UI.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChartPacker.ViewModels
{
    /// <summary>A charted difficulty's letter in a merge cell, colored like the inspector's.</summary>
    public sealed record MergeDifficultyItem(ChartDifficulty Difficulty, string Letter);

    /// <summary>A part's note count in a merge cell, colored by its difficulty like the inspector's.</summary>
    public sealed record MergeNoteCountItem(ChartDifficulty? Difficulty, string Text);

    /// <summary>
    /// A part's song.ini difficulty in a merge cell, as a badge in its tier's colour like the inspector's instrument
    /// badges: the tier, a value set that isn't one as it is, or <c>?</c> when the source doesn't set one.
    /// </summary>
    /// <param name="Tier">The tier, 0 and up; <see langword="null"/> when it's missing, unset (negative) or not a whole number.</param>
    /// <param name="Value">
    /// A value set that isn't a tier, e.g. <c>4.5</c>, which the merge carries as it is; <see langword="null"/> when there's a
    /// <paramref name="Tier"/>, or nothing is set.
    /// </param>
    public sealed record MergeRating(int? Tier, string? Value = null)
    {
        /// <summary>Without a tier's colour: grey, for a difficulty that isn't set or isn't a tier.</summary>
        public bool IsPlain => Tier is null;

        public string Text => Tier?.ToString(CultureInfo.CurrentCulture) ?? Value ?? SharedStrings.DifficultyUnsetBadge;

        /// <summary>The cell tooltip's line for it, e.g. <c>Difficulty: 4</c>.</summary>
        public string ToolTipLine => IsPlain && Value is null
            ? Strings.MergeDifficultyUnset
            : string.Format(CultureInfo.CurrentCulture, Strings.MergeDifficultyFormat, Text);
    }

    /// <summary>One cell of a merge choice: a source, or leaving the thing out.</summary>
    /// <param name="toolTip">The tooltip, when there's more to it than <see cref="PlainText"/>.</param>
    /// <param name="summary">Compact text shown instead of <paramref name="text"/>, e.g. <c>3 tracks</c> for audio; the full text stays in the tooltip.</param>
    /// <param name="difficultyItems">The cell's difficulty letters, colored like the inspector's; empty outside part rows.</param>
    /// <param name="noteCountItems">The cell's note counts, colored by difficulty like the inspector's; empty outside part rows.</param>
    /// <param name="warning">What to know about the source, shown as a glyph in the cell and in its tooltip, e.g. that its chart can't be read.</param>
    /// <param name="rating">A part's song.ini difficulty, as a badge at the end of the cell's top line; <see langword="null"/> outside part rows.</param>
    public sealed partial class MergeOptionViewModel(MergeChoiceViewModel owner, int? source, string text, bool isAvailable, bool isShown = true, string? toolTip = null, string? summary = null,
        IReadOnlyList<MergeDifficultyItem>? difficultyItems = null, IReadOnlyList<MergeNoteCountItem>? noteCountItems = null, string? warning = null, MergeRating? rating = null) : ObservableObject
    {

        /// <summary>The source it takes from; <see langword="null"/> to leave the thing out.</summary>
        public int? Source { get; } = source;

        /// <summary>Compact text shown instead of <see cref="Text"/>, when the full text only fits the tooltip.</summary>
        public string? Summary { get; } = summary;

        public bool HasSummary => !string.IsNullOrEmpty(Summary);

        public IReadOnlyList<MergeDifficultyItem> DifficultyItems { get; } = difficultyItems ?? [];

        public bool HasDifficulties => DifficultyItems.Count > 0;

        public IReadOnlyList<MergeNoteCountItem> NoteCountItems { get; } = noteCountItems ?? [];

        public bool HasNoteCounts => NoteCountItems.Count > 0;

        /// <summary>Whether the cell shows <see cref="Text"/>: only when it has neither colored letters nor a summary.</summary>
        public bool ShowText => !HasDifficulties && !HasSummary;

        /// <summary>Whether the cell shows <see cref="Summary"/>: only when it has no colored letters.</summary>
        public bool ShowSummary => !HasDifficulties && HasSummary;

        /// <summary>What the cell shows; a metadata value can carry rich-text tags, which are drawn.</summary>
        public string Text { get; } = text;

        /// <summary><see cref="Text"/> without its tags, for the tooltip and screen readers.</summary>
        public string PlainText => RichText.ToPlainText(Text);

        public MergeRating? Rating { get; } = rating;

        public bool HasRating => Rating is not null;

        public string? Warning { get; } = warning;

        public bool HasWarning => !string.IsNullOrEmpty(Warning);

        public string ToolTip => HasWarning ? (toolTip ?? PlainText) + Environment.NewLine + Warning : toolTip ?? PlainText;

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

    /// <summary>A merge row's fixed icon.</summary>
    public enum MergeRowIcon
    {
        None,
        Instrument,
        Audio,
        AlbumArt,
        Background,
        Video,
        Midi,
    }

    /// <summary>A row of the merge window: one thing to take from one of the sources.</summary>
    /// <remarks>
    /// Rows that can be left out show an Include checkbox in the gutter instead of a leave-out
    /// column; unchecking it selects the leave-out option, checking it again restores the source.
    /// </remarks>
    public sealed partial class MergeChoiceViewModel(string label, string? key = null, DifficultyGlyph? glyph = null, MergeRowIcon icon = MergeRowIcon.None) : ObservableObject
    {
        private static int _nextGroup;
        private bool _selecting;
        private bool _syncingInclude;
        private int? _lastSource;
        private List<MergeOptionViewModel>? _sourceOptions;

        public string Label { get; } = label;

        /// <summary>The part id or metadata key the row is for.</summary>
        public string? Key { get; } = key;

        /// <summary>The row's instrument icon, drawn like the inspector's; <see langword="null"/> outside part rows.</summary>
        public DifficultyGlyph? Glyph { get; } = glyph;

        public bool HasGlyph => Glyph is not null;

        /// <summary>The row's fixed icon: the audio waveform, an art placeholder, or MIDI.</summary>
        public MergeRowIcon IconKind { get; } = icon;

        public bool HasStaticIcon => IconKind is not MergeRowIcon.None and not MergeRowIcon.Instrument;

        /// <summary>Whether the gutter shows an icon for the row.</summary>
        public bool HasRowIcon => HasGlyph || HasStaticIcon;

        public string GroupName { get; } = "merge-choice-" + Interlocked.Increment(ref _nextGroup).ToString(CultureInfo.InvariantCulture);

        public List<MergeOptionViewModel> Options { get; } = [];

        /// <summary>The per-source cells, without the leave-out option the Include checkbox stands for.</summary>
        /// <remarks>Worked out once, when first shown: the options are all added before then, and a fresh list would regenerate the cells.</remarks>
        public IReadOnlyList<MergeOptionViewModel> SourceOptions => _sourceOptions ??= [.. Options.Where(o => o.Source is not null)];

        /// <summary>The leave-out option the Include checkbox stands for, if the row has one.</summary>
        public MergeOptionViewModel? LeaveOutOption => Options.FirstOrDefault(o => o.Source is null);

        /// <summary>Whether the row can be left out, and so shows the Include checkbox.</summary>
        /// <remarks>Not when no source has the thing: with nothing to include, it's left out without asking.</remarks>
        public bool CanExclude => LeaveOutOption is { IsAvailable: true, IsShown: true } && Options.Any(o => o.Source is not null && o.IsAvailable);

        /// <summary>
        /// Whether the gutter keeps room for the Include checkbox: on every row with an icon, so the icons line up whether
        /// or not the row can be left out, and on any row that can be, e.g. a part the catalog doesn't know, which has no icon.
        /// </summary>
        public bool HasIncludeSlot => HasRowIcon || CanExclude;

        /// <summary>Whether the thing is taken from one of the sources; false leaves it out. Defaults to included.</summary>
        [ObservableProperty]
        public partial bool IsIncluded { get; set; } = true;

        public bool IsExcluded => CanExclude && !IsIncluded;

        public static string IncludeToolTip => Strings.MergeIncludeToolTip;

        public string IncludeName => string.Format(CultureInfo.CurrentCulture, Strings.MergeIncludeNameFormat, Label);

        public MergeOptionViewModel? Selected => Options.FirstOrDefault(o => o.IsSelected);

        /// <summary>Nothing is chosen yet, and something must be.</summary>
        public bool NeedsChoice => Selected is null;

        /// <summary>Nothing is chosen yet, and the row is included, so it still has to be made.</summary>
        public bool ShowNeedsChoice => IsIncluded && NeedsChoice;

        /// <summary>
        /// The label's tooltip, and the icon's while the label is hidden: the label, and that the row still needs a choice
        /// while it does, which otherwise only its amber bar and label say, taking no line of their own.
        /// </summary>
        public string LabelToolTip => ShowNeedsChoice ? Label + Environment.NewLine + Strings.MergeNeedsChoice : Label;

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

            SyncInclude(option);
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

            if (option.Source is { } s)
                _lastSource = s;
            SyncInclude(option);
            Raise();
        }

        partial void OnIsIncludedChanged(bool value)
        {
            if (_syncingInclude)
                return;

            // The user's choice, so defaults no longer follow other choices, as for clicking a cell.
            IsTouched = true;
            _syncingInclude = true;
            try
            {
                if (!value)
                {
                    _lastSource = Selected?.Source;
                    Select(null, force: true);
                }
                else if (_lastSource is { } s)
                {
                    Select(s, force: true);
                }
                else
                {
                    // Included again with nothing to restore: force an explicit choice.
                    _selecting = true;
                    try
                    {
                        foreach (var other in Options)
                            other.IsSelected = false;
                    }
                    finally
                    {
                        _selecting = false;
                    }

                    Raise();
                }
            }
            finally
            {
                _syncingInclude = false;
            }

            OnPropertyChanged(nameof(IsExcluded));
            OnPropertyChanged(nameof(ShowNeedsChoice));
            OnPropertyChanged(nameof(LabelToolTip));
        }

        /// <summary>Keeps the Include checkbox with the selection, without feeding back into it.</summary>
        private void SyncInclude(MergeOptionViewModel? option)
        {
            if (!CanExclude || _syncingInclude)
                return;

            _syncingInclude = true;
            try
            {
                IsIncluded = option?.Source is not null;
            }
            finally
            {
                _syncingInclude = false;
            }
        }

        private void Raise()
        {
            OnPropertyChanged(nameof(Selected));
            OnPropertyChanged(nameof(NeedsChoice));
            OnPropertyChanged(nameof(ShowNeedsChoice));
            OnPropertyChanged(nameof(LabelToolTip));
            OnPropertyChanged(nameof(IsExcluded));
            Changed?.Invoke(this);
        }
    }

    /// <summary>One of the songs being merged: a column of every row.</summary>
    /// <param name="Title">What the song is called where it's named, e.g. in an alignment link.</param>
    /// <param name="Problem">What to know about the song, shown in its header and its Base chart cell, e.g. that its unsaved edits aren't included.</param>
    /// <param name="IsSng">The song is a <c>.sng</c>, not a folder; its header's chip says which, as the library's does.</param>
    /// <param name="Charter">Who charted it, as plain text, under its title in the header; <see langword="null"/> when unset.</param>
    /// <param name="FullPath">Where it is, in its header's tooltip.</param>
    public sealed record MergeSourceViewModel(string Title, string? Problem, bool IsSng = false, string? Charter = null, string FullPath = "")
    {
        public bool HasProblem => !string.IsNullOrEmpty(Problem);

        public string KindText => IsSng ? Strings.KindSng : Strings.KindFolder;
    }

    /// <summary>A song's column header: what the song is, and how far it moves to line up with the kept audio.</summary>
    public sealed record MergeSongHeaderViewModel(MergeSourceViewModel Source, MergeAlignmentCellViewModel Alignment);

    /// <summary>What the Merge window's footer says, which sets how it's drawn.</summary>
    public enum MergeStatusKind
    {
        None,

        /// <summary>What still needs choosing before the merge can run.</summary>
        Hint,

        /// <summary>Why the merge can't run as chosen, e.g. the output exists.</summary>
        Problem,

        Busy,
        Done,
        Error,
    }

    /// <summary>
    /// The merge window: reads the selected songs, offers a choice wherever they differ, and merges
    /// them into a new <c>.sng</c> with <see cref="SongMerger"/>.
    /// </summary>
    /// <remarks>
    /// A part only one song has is taken from it; a part several have must be chosen. Metadata the songs
    /// agree on isn't offered. The audio defaults to the song with the most tracks, and the base chart,
    /// album art, backgrounds and metadata follow it until they're chosen: metadata the audio's song
    /// doesn't set is taken from the first song that does.
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
    /// <param name="settings">Persisted window state: whether the window was maximized and the split ratio.</param>
    /// <param name="locks">
    /// Holds the merged song while it's written, so one another Chart Builder or Chart Packer has open isn't written over
    /// (charter-spec §4.8).
    /// </param>
    public sealed partial class MergeWindowViewModel(
        IReadOnlyList<SongEntry> entries,
        IReadOnlyList<bool> hasDrafts,
        string outputFolder,
        OverwritePolicy policy,
        IVideoDurationProbe video,
        SongDetailViewModel preview,
        MetadataDraftStore previewDrafts,
        SettingsService settings,
        SongLocks? locks = null) : ViewModelBase
    {
        private readonly SettingsService _settings = settings;

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

        /// <summary>Share of the window's width given to the preview panel. The view applies it on open and writes it back on close.</summary>
        public double MergePreviewFraction
        {
            get => _settings.Settings.MergePreviewFraction;
            set => _settings.Settings.MergePreviewFraction = value;
        }

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
        [NotifyPropertyChangedFor(nameof(PreviewWarningsText))]
        public partial IReadOnlyList<string> PreviewWarnings { get; set; } = [];

        public bool HasPreviewWarnings => PreviewWarnings.Count > 0;

        /// <summary>The preview header's warnings button, e.g. <c>2 Warnings</c>.</summary>
        public string PreviewWarningsText => WarningsCount(PreviewWarnings.Count);

        /// <summary>Asks the window to close; true when a song was merged.</summary>
        public event Action<bool>? CloseRequested;

        /// <summary>One column per source; rows that can be left out use an Include checkbox instead of an extra column.</summary>
        public int ColumnCount => Sources.Count;

        public AvaloniaList<MergeSourceViewModel> Sources { get; } = [];

        /// <summary>One header per source, over its column: the song, and its alignment.</summary>
        public AvaloniaList<MergeSongHeaderViewModel> Headers { get; } = [];

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
        [NotifyPropertyChangedFor(nameof(StatusKind))]
        [NotifyPropertyChangedFor(nameof(StatusText))]
        [NotifyCanExecuteChangedFor(nameof(MergeCommand))]
        public partial bool IsMerging { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsReady))]
        [NotifyPropertyChangedFor(nameof(CloseLabel))]
        [NotifyPropertyChangedFor(nameof(MergeLabel))]
        [NotifyCanExecuteChangedFor(nameof(MergeCommand))]
        public partial bool IsDone { get; set; }

        /// <summary>The choices are loaded and can be changed.</summary>
        public bool IsReady => !IsLoading && !IsMerging && !IsDone && _analysis is not null;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasError))]
        [NotifyPropertyChangedFor(nameof(StatusKind))]
        [NotifyPropertyChangedFor(nameof(StatusText))]
        public partial string? ErrorMessage { get; set; }

        public bool HasError => ErrorMessage is not null;

        [ObservableProperty]
        public partial string CharterPreview { get; set; } = "";

        /// <summary>Where the merge will write; a dash while something still needs choosing.</summary>
        [ObservableProperty]
        public partial string TargetText { get; set; } = Strings.MergeUnavailable;

        /// <summary>What still needs choosing before the merge can run, e.g. a part's song.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(StatusKind))]
        [NotifyPropertyChangedFor(nameof(StatusText))]
        public partial string? ChoiceHint { get; set; }

        /// <summary>Why the merge can't run as chosen, e.g. the output exists and is to be skipped.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasTargetProblem))]
        [NotifyPropertyChangedFor(nameof(StatusKind))]
        [NotifyPropertyChangedFor(nameof(StatusText))]
        public partial string? TargetProblem { get; set; }

        public bool HasTargetProblem => TargetProblem is not null;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasResult))]
        [NotifyPropertyChangedFor(nameof(StatusKind))]
        [NotifyPropertyChangedFor(nameof(StatusText))]
        public partial string? ResultText { get; set; }

        public bool HasResult => ResultText is not null;

        /// <summary>
        /// What the footer's one line says, most pressing first: the merge failed, it's running, it's done, it can't run as
        /// chosen, or what's still to choose. One line whatever it says, so nothing above it moves.
        /// </summary>
        public MergeStatusKind StatusKind =>
            HasError ? MergeStatusKind.Error
            : IsMerging ? MergeStatusKind.Busy
            : HasResult ? MergeStatusKind.Done
            : HasTargetProblem ? MergeStatusKind.Problem
            : ChoiceHint is not null ? MergeStatusKind.Hint
            : MergeStatusKind.None;

        public string? StatusText => StatusKind switch
        {
            MergeStatusKind.Error => ErrorMessage,
            MergeStatusKind.Busy => Strings.MergeMerging,
            MergeStatusKind.Done => ResultText,
            MergeStatusKind.Problem => TargetProblem,
            MergeStatusKind.Hint => ChoiceHint,
            _ => null,
        };

        /// <summary>The merged <c>.sng</c>, once a song is merged.</summary>
        public string? MergedPath { get; private set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasWarnings))]
        [NotifyPropertyChangedFor(nameof(WarningsText))]
        public partial IReadOnlyList<string> Warnings { get; set; } = [];

        public bool HasWarnings => Warnings.Count > 0;

        /// <summary>The footer's warnings button, e.g. <c>3 Warnings</c>.</summary>
        public string WarningsText => WarningsCount(Warnings.Count);

        public string CloseLabel => IsDone ? Strings.CloseButton : SharedStrings.CancelButton;

        /// <summary>Merge, until it's done; then the button stays where it was, disabled, as Merged.</summary>
        public string MergeLabel => IsDone ? Strings.MergedButton : Strings.MergeButton;

        private static string WarningsCount(int count) =>
            count == 1 ? Strings.MergeWarningsOne : string.Format(CultureInfo.CurrentCulture, Strings.MergeWarningsFormat, count);

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
                string? charter = s.Value("charter") is { Length: > 0 } c ? RichText.ToPlainText(c) : null;
                Sources.Add(new MergeSourceViewModel(s.Headline.Name ?? s.Entry.DisplayName, problem,
                    s.Entry.Kind == SongEntryKind.SngFile, charter, s.Entry.FullPath));
            }

            BaseChoice = Choice(Strings.MergeBaseLabel, null, i => sources[i].Chart is not null, i => FormatOf(sources[i].Format), leaveOut: null,
                icon: MergeRowIcon.Midi, warning: i => Sources[i].Problem);

            foreach (var part in analysis.Parts)
            {
                var row = Choice(ChartLabels.PartName(part), part.Id, i => sources[i].HasPart(part.Id), i => DifficultiesOf(sources[i], part.Id), leaveOut: Strings.MergeLeaveOut,
                    counts: i => sources[i].Parts.First(p => p.Part.Id == part.Id).NoteCounts,
                    rating: i => RatingOf(sources[i], part.Id),
                    difficulties: i => sources[i].Parts.First(p => p.Part.Id == part.Id).Difficulties,
                    glyph: PartGlyphOf(part.Id));
                var with = analysis.SourcesWith(part.Id);
                if (with.Count == 1)
                    row.Select(with[0]);
                Parts.Add(row);
            }

            AudioChoice = Choice(Strings.MergeAudioLabel, null, i => true, i => StemsOf(sources[i]), leaveOut: null,
                summary: i => StemsSummaryOf(sources[i]),
                icon: MergeRowIcon.Audio);
            Assets.Add(Choice(Strings.MergeAlbumArtLabel, "album", i => sources[i].AlbumArt is not null, i => sources[i].AlbumArt ?? Strings.MergeUnavailable, leaveOut: Strings.MergeNone,
                icon: MergeRowIcon.AlbumArt));
            Assets.Add(Choice(Strings.MergeBackgroundLabel, "background", i => sources[i].BackgroundImage is not null, i => sources[i].BackgroundImage ?? Strings.MergeUnavailable, leaveOut: Strings.MergeNone,
                icon: MergeRowIcon.Background));
            Assets.Add(Choice(Strings.MergeVideoLabel, "video", i => sources[i].Video is not null, i => sources[i].Video ?? Strings.MergeUnavailable, leaveOut: Strings.MergeNone,
                icon: MergeRowIcon.Video));

            foreach (var conflict in analysis.Conflicts)
                Metadata.Add(Choice(conflict.Key, conflict.Key, i => true, i => conflict.Values[i] ?? Strings.MergeNotSet, leaveOut: null));

            BuildAlignment(analysis);
            for (int i = 0; i < Sources.Count; i++)
                Headers.Add(new MergeSongHeaderViewModel(Sources[i], Alignment[i]));

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
        /// <param name="summary">Compact text shown instead of <paramref name="text"/>, e.g. a stem count for audio.</param>
        /// <param name="difficulties">An available cell's charted difficulties, for a part's colored letters.</param>
        /// <param name="glyph">The row's instrument icon, drawn like the inspector's.</param>
        /// <param name="icon">The row's fixed icon: the audio waveform, an art placeholder, or MIDI.</param>
        /// <param name="warning">What to know about a source, in its cell whether it's available or not.</param>
        /// <param name="rating">An available cell's song.ini difficulty, for a part's badge.</param>
        private MergeChoiceViewModel Choice(string label, string? key, Func<int, bool> available, Func<int, string> text, string? leaveOut,
            Func<int, IReadOnlyList<NoteCount>>? counts = null, Func<int, string?>? summary = null, Func<int, IReadOnlyList<ChartDifficulty>>? difficulties = null,
            DifficultyGlyph? glyph = null, MergeRowIcon icon = MergeRowIcon.None, Func<int, string?>? warning = null, Func<int, MergeRating?>? rating = null)
        {
            var row = new MergeChoiceViewModel(label, key, glyph, icon);
            for (int i = 0; i < Sources.Count; i++)
            {
                bool has = available(i);
                string shown = has ? text(i) : Strings.MergeUnavailable;
                var noteCounts = has && counts is not null ? counts(i) : [];
                var rated = has ? rating?.Invoke(i) : null;
                bool counted = noteCounts.Count > 0;
                row.Options.Add(new MergeOptionViewModel(row, i, shown, has,
                    toolTip: counted || rated is not null
                        ? string.Join(Environment.NewLine, [RichText.ToPlainText(shown), .. rated is null ? [] : (string[])[rated.ToolTipLine], .. noteCounts.Select(NoteCountLine)])
                        : null,
                    summary: has ? summary?.Invoke(i) : null,
                    difficultyItems: has && difficulties is not null
                        ? difficulties(i).Select(d => new MergeDifficultyItem(d, ChartLabels.DifficultyLetter(d))).ToList()
                        : [],
                    noteCountItems: [.. noteCounts.Select(c => new MergeNoteCountItem(c.Difficulty, c.Count.ToString("N0", CultureInfo.CurrentCulture)))],
                    warning: warning?.Invoke(i),
                    rating: rated));
            }

            row.Options.Add(leaveOut is null
                ? new MergeOptionViewModel(row, null, "", isAvailable: false, isShown: false)
                : new MergeOptionViewModel(row, null, leaveOut, isAvailable: true));
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
                foreach (var conflict in Metadata)
                    conflict.Select(MetadataSourceFor(analysis, conflict.Key!, audio));
                AlignTo(audio);
            }

            OnChoicesChanged();
        }

        /// <summary>
        /// The user changed what's merged. A merge that failed did so as it was chosen before, so what it said gives way to
        /// what there is to know now: what's still to choose, or why it can't run.
        /// </summary>
        private void OnChoicesChanged()
        {
            ErrorMessage = null;
            Refresh();
        }

        /// <summary><paramref name="preferred"/> when it has the thing, otherwise the first source that does.</summary>
        private int? Pick(int preferred, Func<int, bool> has) =>
            has(preferred) ? preferred : Enumerable.Range(0, Sources.Count).Cast<int?>().FirstOrDefault(i => has(i!.Value));

        /// <summary>
        /// Whose value of <paramref name="key"/> is kept until it's chosen: the kept <paramref name="audio"/>'s song when it
        /// sets the key, otherwise the first song that does, otherwise the audio's song. A blank value isn't set.
        /// </summary>
        private int MetadataSourceFor(SongMergeAnalysis analysis, string key, int audio)
        {
            var values = analysis.Conflicts.First(c => c.Key == key).Values;
            return Pick(audio, i => !string.IsNullOrEmpty(values[i])) ?? audio;
        }

        /// <summary>The plan as chosen so far; <see langword="null"/> while something still needs choosing.</summary>
        private SongMergePlan? Plan()
        {
            if (_analysis is not { } analysis || BaseChoice?.Selected?.Source is not { } baseSource || AudioChoice?.Selected?.Source is not { } audio)
                return null;
            if (Parts.Any(p => p.NeedsChoice) || !Parts.Any(p => p.Selected?.Source is not null))
                return null;

            // An included asset with nothing chosen would otherwise be left out without a word.
            if (Assets.Any(a => a.ShowNeedsChoice))
                return null;

            var plan = new SongMergePlan(
                analysis,
                baseSource,
                Parts.Where(p => p.Selected?.Source is not null).ToDictionary(p => p.Key!, p => p.Selected!.Source!.Value),
                Metadata.ToDictionary(m => m.Key!, m => (int?)(m.Selected?.Source ?? audio)),
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
                // The songs whose base chart or parts are taken, as the merge credits them; while something still needs
                // choosing, as chosen so far.
                CharterPreview = SongMerger.ChartersOf(analysis, BaseChoice?.Selected?.Source, [.. Parts.Select(row => row.Selected?.Source).OfType<int>()]);
                if (string.IsNullOrEmpty(CharterPreview))
                    CharterPreview = Strings.MergeNotSet;
            }

            // Once merged, the output exists; that's no longer a problem to show.
            if (IsDone)
            {
                MergeCommand.NotifyCanExecuteChanged();
                return;
            }

            // Built once for all that follows, as dragging an offset refreshes at every step.
            var plan = Plan();

            // Lining the songs up moves the preview right away, the way dragging a highway offset does; the staging that
            // follows changes nothing shown.
            PreviewLiveAlignment(plan);
            SchedulePreview();
            RefreshTarget(plan);
        }

        /// <summary>Where the merge will write as <paramref name="plan"/>, the plan as chosen, and why it can't run, if it can't.</summary>
        private void RefreshTarget(SongMergePlan? plan)
        {
            TargetProblem = null;
            ChoiceHint = null;
            _hasPlan = plan is not null;
            if (plan is not null)
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
                TargetText = Strings.MergeUnavailable;
                ChoiceHint = !Parts.Any() ? Strings.MergeNoParts : Parts.Any(p => p.NeedsChoice) ? Strings.MergeChooseParts : Strings.MergeChooseSomething;
            }

            MergeCommand.NotifyCanExecuteChanged();
        }

        /// <summary>
        /// An offset set on the preview's lyrics that moves the vocals before the song starts can't be merged; see <see cref="RefreshTarget"/>.
        /// The preview isn't staged again for it, as staging clears the lyrics' problem and would cancel itself.
        /// </summary>
        private void OnPreviewLyricsChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(LyricsViewModel.HasProblem) || IsDone || _closed || _analysis is null)
                return;

            // The offset is the user's change to what's merged, as a choice is; see OnChoicesChanged.
            ErrorMessage = null;
            RefreshTarget(Plan());
        }

        // Whether there was a plan when RefreshTarget last looked: every change to the choices refreshes it, so CanMerge
        // needn't build the plan again.
        private bool _hasPlan;

        private bool CanMerge() => IsReady && TargetProblem is null && _hasPlan;

        [RelayCommand(CanExecute = nameof(CanMerge))]
        private async Task MergeAsync()
        {
            if (Plan() is not { } plan)
                return;

            // Another Chart Builder or Chart Packer would save over the merge with the song it has open.
            IDisposable? held = null;
            if (locks is not null && SongMerger.TargetFor(plan, outputFolder, policy) is { } target)
            {
                IReadOnlyList<(string Path, SongLockHolder Holder)> refused;
                try
                {
                    held = locks.TryHold([target], out refused);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    ErrorMessage = string.Format(CultureInfo.CurrentCulture, Strings.NotMergedInUseFormat, ex.Message);
                    return;
                }

                if (held is null)
                {
                    ErrorMessage = string.Format(CultureInfo.CurrentCulture, Strings.NotMergedInUseFormat, SongLocks.Describe(refused, Path.GetFileName));
                    return;
                }
            }

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
                held?.Dispose();
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
                int tracksGeneration = Preview.Player.TracksGeneration;

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

                // Only playback the reload took down, by letting go of its tracks for other ones, goes back to where it
                // was, playing again if it was, even with another change on its way, which lets go of it again only if it
                // has to. Otherwise the player was left alone, so a pause or seek meanwhile was the user's, and it stays.
                if (!wasScrubbing && Preview.Player.TracksGeneration != tracksGeneration && !Preview.Player.IsPlaying)
                    await Preview.Player.RestorePlaybackAsync(resumeAt, resume: wasPlaying);
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

        /// <summary>The part's instrument icon, drawn like the inspector's; <see langword="null"/> for a part that isn't known.</summary>
        private static DifficultyGlyph? PartGlyphOf(string partId) =>
            DifficultySlots.All.FirstOrDefault(s => s.PartId == partId)?.Glyph;

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

        /// <summary>
        /// The source's song.ini difficulty for the part, which comes along with it; unset when the source doesn't set one.
        /// A value that isn't a tier, e.g. <c>4.5</c>, is set, as <see cref="MetadataDifficulty.IsUnsetIn"/> has it, and shown
        /// as it is. <see langword="null"/>, and no badge, for a part whose key isn't a difficulty.
        /// </summary>
        /// <remarks>A track the catalog doesn't know, e.g. <c>PART DJ</c>, is a part with no keys, so it has no badge.</remarks>
        private static MergeRating? RatingOf(MergeSource source, string partId)
        {
            if (source.Parts.FirstOrDefault(p => p.Part.Id == partId) is not { Part.Keys: [var key, ..] } part || !MetadataDifficulty.IsDifficultyKey(key))
                return null;

            string? value = part.Difficulty;
            int? tier = MetadataDifficulty.TierOf(value);
            bool other = tier is null && !string.IsNullOrWhiteSpace(value) && !MetadataDifficulty.IsUnset(key, value);
            return new MergeRating(tier, other ? value : null);
        }

        private static string StemsOf(MergeSource source) =>
            source.AudioFiles.Count == 0 ? Strings.MergeStemsNone : string.Join(", ", source.AudioFiles);

        /// <summary>Compact audio cell text, e.g. <c>3 tracks</c>; <see langword="null"/> shows the full text instead.</summary>
        private static string? StemsSummaryOf(MergeSource source) => source.AudioFiles.Count switch
        {
            0 => null,
            1 => Strings.MergeStemsOne,
            var n => string.Format(CultureInfo.CurrentCulture, Strings.MergeStemsFormat, n),
        };
    }
}
