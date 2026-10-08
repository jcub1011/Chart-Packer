using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Collections;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ChartPacker.Audio.Decoding;
using ChartPacker.Core.Charts;
using ChartPacker.Core.Ini;
using ChartPacker.Core.Library;
using ChartPacker.Core.Merging;
using ChartPacker.Core.Metadata;
using ChartPacker.Core.Scanning;
using ChartPacker.Core.Sng;
using ChartPacker.Core.Validation;
using ChartPacker.Core.Validation.QuickFixes;
using ChartPacker.Resources;
using ChartPacker.Services;
using ChartPacker.UI.Formatting;
using ChartPacker.UI.Services;
using ChartPacker.UI.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChartPacker.ViewModels
{
    /// <summary>A file in the song, with its size already formatted.</summary>
    /// <param name="Marker"><c>+</c> for a file a staged change adds.</param>
    /// <param name="IsRemoved">A staged change removes or replaces the file.</param>
    /// <param name="CanRemove">The file can be removed, or, when it's added, the add dropped.</param>
    /// <param name="CanRevert">The staged change that removes or replaces the file can be dropped.</param>
    /// <param name="DownloadCommand">Saves a copy of the file, from its right-click menu; none where nothing is saved.</param>
    public sealed record FileRow(string Path, string Size, string? Marker = null, bool IsRemoved = false, bool CanRemove = false, bool CanRevert = false, IAsyncRelayCommand? DownloadCommand = null)
    {
        public bool IsAdded => Marker is not null;

        public string DownloadHeader => SongFileSource.DownloadHeaderFor(Path);
    }

    /// <summary>A chart file of the song, with its size already formatted.</summary>
    /// <param name="Marker"><c>+</c> for a chart a staged change adds.</param>
    /// <param name="IsRemoved">A staged change removes or replaces the file.</param>
    /// <param name="DownloadCommand">Saves a copy of the chart, from its right-click menu; none where nothing is saved.</param>
    public sealed record ChartRow(string Name, string Size, string? Marker = null, bool IsRemoved = false, IAsyncRelayCommand? DownloadCommand = null)
    {
        public bool IsKept => !IsRemoved;

        public string DownloadHeader => SongFileSource.DownloadHeaderFor(Name);
    }

    /// <summary>A track a staged change removes, shown struck through so that its removal can be undone.</summary>
    public sealed record RemovedTrackRow(string Name, string Size);

    /// <summary>A structural problem found in a <c>.sng</c>.</summary>
    public sealed record FindingRow(string RuleId, string Message, bool IsError);

    /// <summary>Why a key that has no metadata row, such as a difficulty, fails validation.</summary>
    /// <param name="QuickFixDescription">What the quick fix does, when that's one thing.</param>
    /// <param name="QuickFixCommand">Applies the key's quick fixes from its right-click menu; disabled when it has none.</param>
    public sealed record MetadataIssueRow(string Key, string Message, string? QuickFixDescription, IRelayCommand QuickFixCommand)
    {
        /// <summary>The right-click menu's Quick Fix item, saying what it does when that's one thing.</summary>
        public string QuickFixHeader => QuickFixDescription is { } description
            ? string.Format(CultureInfo.CurrentCulture, Strings.QuickFixMenuFormat, description)
            : Strings.QuickFixMenu;
    }

    /// <summary>An entry of the Add track menu: a stem, labelled to say whether choosing it replaces the song's track for it.</summary>
    public sealed record StemOption(string Stem, string Label, IAsyncRelayCommand Command);

    /// <summary>
    /// The right panel: the album art, background and metadata of the songs selected in the
    /// library, editable for one song or for all of them at once, plus the tracks, the chart and
    /// the other files when one song is selected. For a <c>.sng</c> all of it comes from the
    /// container; for a folder, from its <c>song.ini</c> and a listing.
    /// </summary>
    /// <remarks>
    /// Edits go into each song's <see cref="MetadataDraft"/> in the shared
    /// <see cref="MetadataDraftStore"/>, so they survive a change of selection. Nothing is written
    /// until <see cref="SaveCommand"/>, which re-reads each file and changes only the edited keys,
    /// the album art if it was changed, and the files added or removed.
    /// <para>The one selected song's audio tracks, with the draft's added and removed tracks
    /// applied, play in <see cref="Player"/>. It holds the files open while it plays, so it's
    /// unloaded before saving.</para>
    /// </remarks>
    public partial class SongDetailViewModel : ViewModelBase
    {
        private const int BulkLoadParallelism = 4;

        internal const string SongLengthKey = "song_length";

        // A square tile at its 512px largest, at 2x scaling.
        private const int ArtDecodeWidth = 1024;

        private readonly MetadataDraftStore _drafts;
        private readonly JobViewModel _job;
        private readonly IImagePickerService _imagePicker;
        private readonly IFilePickerService _filePicker;
        private readonly IVideoDurationProbe _videoProbe;
        private readonly IDownloadService? _downloads;
        private readonly IFileRevealService? _reveal;
        private readonly SongFileGate _songFiles;
        private readonly IConfirmationService? _confirmation;

        // Whether Chart Builder has the song at a path open, so saving asks first (charter-spec §4.8).
        private readonly Func<string, bool>? _isOpenInChartBuilder;
        private readonly DraftSaver _saver;
        private CancellationTokenSource? _loadCts;
        private CancellationTokenSource? _chartLintCts;

        // This panel is telling the draft store about its own change, which it needn't hear back.
        private bool _notifyingDrafts;

        // Another panel sharing the drafts, such as the Validator window's, changed a song shown here: what's shown is
        // brought up to date once it's been still a moment, so a drag of an offset there updates it once. Only key
        // changes are rebuilt from the shared drafts; anything else, such as a save, is read again from disk.
        private readonly DispatcherTimer _externalChangeTimer;
        private static readonly TimeSpan ExternalChangeDelay = TimeSpan.FromMilliseconds(250);
        private bool _externalNeedsReload;

        // Holds none of its songs' files open and doesn't follow other panels' changes, until it's resumed.
        private bool _suspended;

        // The download command of each of the one selected song's files, by file, so a row rebuilt for the same file is equal to it.
        private readonly Dictionary<SongFileSource, IAsyncRelayCommand> _downloadCommands = [];

        // Cancels Auto Set song_length when the selection changes under it.
        private CancellationTokenSource? _autoLengthCts;

        // The one selected song's length worked out for the player's timeline when it has no song_length: what the latest
        // estimate was of, what it found, and the one running, if one is.
        private string? _songLengthKey;
        private double _estimatedSongLength;
        private CancellationTokenSource? _songLengthCts;
        private Task _songLengthTask = Task.CompletedTask;

        // How long the drafts must be still before the length is worked out again, so a burst of edits reads the files once.
        private static readonly TimeSpan SongLengthDelay = TimeSpan.FromMilliseconds(300);

        // The metadata the length estimate reads: the chart's delay and drums, and the video's timeline.
        private static readonly string[] SongLengthKeys = ["delay", "pro_drums", "pro_drum", "video_start_time", "video_end_time", "video_loop"];

        // What's shown: the selection, and the songs among it whose metadata could be read.
        private IReadOnlyList<SongEntry> _entries = [];
        private IReadOnlyList<EditableSong> _songs = [];

        // Why there's no metadata, from loading, e.g. the folder has no song.ini.
        private string? _loadNote;

        // The one selected song's files as they are on disk; the tracks, charts and other files add the staged changes to them.
        private IReadOnlyList<DiskFile> _diskFiles = [];

        // The one selected song's total size on disk.
        private ulong _totalSize;

        // How far the merge preview draws the chart and the vocals it read moved, lining its songs up before that's staged;
        // see ShowPreviewAlignment. Let go whenever the chart is read again.
        private (IReadOnlyDictionary<ChartTrackKey, int> Chart, int Vocals)? _previewShifts;

        // Decoded album art by image hash: the first song's art, and every staged replacement.
        private Dictionary<string, Bitmap> _artImages = [];

        // Staged replacement images by source path.
        private Dictionary<string, StagedArt> _stagedArt = new(StringComparer.Ordinal);

        // One command per stem for the Add track menu, whose labels change with the song's tracks.
        private readonly Dictionary<string, IAsyncRelayCommand> _addTrackCommands = [];

        // The Quick Fix commands of keys without a row, and the keys some selected song has a quick fix for.
        private readonly Dictionary<string, IRelayCommand> _quickFixKeyCommands = new(StringComparer.OrdinalIgnoreCase);
        private HashSet<string> _keysWithQuickFix = new(StringComparer.OrdinalIgnoreCase);

        /// <param name="downloads">Saves copies of the song's files from their right-click menus; none for a panel that only previews.</param>
        /// <param name="reveal">Shows the selected songs in the file manager; none for a panel that only previews.</param>
        /// <param name="songFiles">
        /// Keeps saving a song apart from copies of it being downloaded; the one <paramref name="downloads"/> uses.
        /// None for a panel nothing else downloads from.
        /// </param>
        /// <param name="confirmation">Asks before saving metadata that fails validation; none for a panel that never saves.</param>
        /// <param name="saver">Writes the drafts; one of its own over <paramref name="songFiles"/> when none is given.</param>
        public SongDetailViewModel(
            MetadataDraftStore drafts,
            JobViewModel job,
            IImagePickerService imagePicker,
            IFilePickerService filePicker,
            PlayerViewModel player,
            HighwayViewModel highway,
            IVideoDurationProbe videoProbe,
            IDownloadService? downloads = null,
            IFileRevealService? reveal = null,
            SongFileGate? songFiles = null,
            IConfirmationService? confirmation = null,
            DraftSaver? saver = null,
            Func<string, bool>? isOpenInChartBuilder = null)
        {
            _isOpenInChartBuilder = isOpenInChartBuilder;
            _drafts = drafts;
            _downloads = downloads;
            _reveal = reveal;
            _songFiles = songFiles ?? new SongFileGate();
            _confirmation = confirmation;
            _saver = saver ?? new DraftSaver(_songFiles);
            _externalChangeTimer = new DispatcherTimer(ExternalChangeDelay, DispatcherPriority.Background, (_, _) => OnExternalChangeSettled());
            _externalChangeTimer.Stop();
            _drafts.DraftsChanged += OnDraftsChangedElsewhere;
            _job = job;
            _imagePicker = imagePicker;
            _filePicker = filePicker;
            _videoProbe = videoProbe;
            Player = player;
            Background = new BackgroundViewModel(() => Player.DrawnPosition);
            Background.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(BackgroundViewModel.ShownKind))
                    RemoveBackgroundCommand.NotifyCanExecuteChanged();
                else if (e.PropertyName == nameof(BackgroundViewModel.ShownFile))
                    DownloadBackgroundCommand.NotifyCanExecuteChanged();
                else if (e.PropertyName == nameof(BackgroundViewModel.HasVideoKind))
                {
                    OnPropertyChanged(nameof(ShowVideoOffset));
                    RefreshTimingOffsetsEditable();
                }
            };
            Highway = highway;
            Highway.OffsetsChanged += (_, _) => OnOffsetsChanged();
            Highway.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(HighwayViewModel.ChartParts))
                    RefreshInstruments();
                else if (e.PropertyName == nameof(HighwayViewModel.ChartOffset))
                {
                    // The audio offset is worked out against the chart's own, which is only known once the chart is read.
                    SyncTimingOffsets();
                    RefreshTimingOffsetsEditable();
                }
            };
            AudioOffset.OffsetChanged += (_, _) => OnTimingChanged();
            VideoOffset.OffsetChanged += (_, _) => OnTimingChanged();
            Instruments = [.. DifficultySlots.All.Select(s => new InstrumentDifficultyViewModel(this, s))];
            Lyrics = new LyricsViewModel(Player);
            Lyrics.OffsetChanged += (_, _) => OnOffsetsChanged();

            // What the lyrics commands can do depends on what the lyrics row has read, which comes later.
            Lyrics.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(LyricsViewModel.HasLyrics) or nameof(LyricsViewModel.HasChart) or nameof(LyricsViewModel.IsChanged) or nameof(LyricsViewModel.CanExport))
                    RefreshLyricsCommands();
            };
            foreach (string stem in AudioStems.Stems)
                _addTrackCommands[stem] = new AsyncRelayCommand(() => AddTrackAsync(stem), CanChangeFiles);
            _job.PropertyChanged += OnJobPropertyChanged;
        }

        private void OnJobPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(JobViewModel.IsRunning))
                RefreshCommands();
        }

        public AvaloniaList<MetadataRowViewModel> Metadata { get; } = [];

        /// <summary>An icon per song.ini difficulty, above the metadata rows, which leave the difficulties out.</summary>
        public IReadOnlyList<InstrumentDifficultyViewModel> Instruments { get; }

        /// <summary>Whether the icons of instruments that aren't charted and have no difficulty are shown too.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(InstrumentsToggleText))]
        public partial bool ShowAllInstruments { get; set; }

        /// <summary>The instruments' icons are shown: some selected song's metadata was read.</summary>
        public bool ShowInstruments => _songs.Count > 0;

        /// <summary>Some icons are hidden until Show all.</summary>
        public bool HasHiddenInstruments => Instruments.Any(i => i.IsHidden);

        public string InstrumentsToggleText => ShowAllInstruments
            ? Strings.InstrumentsShowFewer
            : string.Format(CultureInfo.CurrentCulture, Strings.InstrumentsShowAllFormat, Instruments.Count(i => i.IsHidden));

        /// <summary>The one selected song's files that aren't its album art, background, tracks, chart or song.ini.</summary>
        public AvaloniaList<FileRow> Files { get; } = [];

        /// <summary>The one selected song's chart files: normally one, <c>notes.chart</c> or <c>notes.mid</c>.</summary>
        public AvaloniaList<ChartRow> Charts { get; } = [];

        /// <summary>The one selected song's tracks that a staged change removes, and nothing replaces.</summary>
        public AvaloniaList<RemovedTrackRow> RemovedTracks { get; } = [];

        public AvaloniaList<FindingRow> Findings { get; } = [];

        /// <summary>Validation failures of keys that have no row, such as a difficulty, shown under their icons.</summary>
        public AvaloniaList<MetadataIssueRow> HiddenIssues { get; } = [];

        public bool HasHiddenIssues => HiddenIssues.Count > 0;

        /// <summary>How many keys, shown as rows or not, fail validation as drafted.</summary>
        public int FailingFieldCount => Metadata.Count(r => r.HasIssue) + HiddenIssues.Count;

        /// <summary>Some selected song's metadata, as drafted, fails validation; saving asks first.</summary>
        public bool HasValidationIssues => FailingFieldCount > 0;

        /// <summary>Plays the one selected song's audio tracks.</summary>
        public PlayerViewModel Player { get; }

        /// <summary>The selected songs' background image or video, beside the album art.</summary>
        public BackgroundViewModel Background { get; }

        /// <summary>The note highway, over the background or in its own row, for the one selected song's chart.</summary>
        public HighwayViewModel Highway { get; }

        /// <summary>The lyrics row under the album art, for the one selected song's vocals.</summary>
        public LyricsViewModel Lyrics { get; }

        /// <summary>How far the one selected song's draft moves its audio against the chart, as its <c>delay</c>; beside the master volume.</summary>
        public TimingOffsetViewModel AudioOffset { get; } = new(TimingOffsets.AudioKey);

        /// <summary>How far the one selected song's draft moves its background video, as its <c>video_start_time</c>; in the background's caption.</summary>
        public TimingOffsetViewModel VideoOffset { get; } = new(TimingOffsets.VideoKey);

        /// <summary>The Add track menu: every stem, labelled to replace the ones the song already has.</summary>
        [ObservableProperty]
        public partial IReadOnlyList<StemOption> AddTrackOptions { get; set; } = [];

        /// <summary>The song's name and artist, as drafted, or how many items are selected.</summary>
        [ObservableProperty]
        public partial string? Title { get; set; }

        /// <summary>The one selected song's charter as drafted, with its rich-text tags left in for the view to render.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasCharter))]
        public partial string? Charter { get; set; }

        /// <summary>The full path, or how many of each kind are selected.</summary>
        [ObservableProperty]
        public partial string? Subtitle { get; set; }

        /// <summary>The one selected song's size on disk, formatted.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasSizeText))]
        public partial string? SizeText { get; set; }

        [ObservableProperty]
        public partial bool IsLoading { get; set; }

        [ObservableProperty]
        public partial string? ErrorMessage { get; set; }

        /// <summary>Why there is no metadata table, e.g. the folder has no song.ini.</summary>
        [ObservableProperty]
        public partial string? MetadataNote { get; set; }

        /// <summary>Why the metadata can't be edited, e.g. a selected .sng has structural errors.</summary>
        [ObservableProperty]
        public partial string? ReadOnlyNote { get; set; }

        /// <summary>
        /// Set for a panel that only shows songs, such as the Merge window's preview: every song is
        /// read-only, with this as the reason.
        /// </summary>
        public string? PreviewNote { get; init; }

        /// <summary>
        /// How many times songs have been shown: it changes whenever the chart is read again, so whatever was worked out
        /// against the chart read before, as <see cref="ShowPreviewAlignment"/>'s shifts are, no longer applies.
        /// </summary>
        public int LoadGeneration { get; private set; }

        /// <summary>More than one item is selected: only the merged metadata is shown.</summary>
        [ObservableProperty]
        public partial bool IsBulk { get; set; }

        [ObservableProperty]
        public partial bool IsSaving { get; set; }

        [ObservableProperty]
        public partial string? SaveError { get; set; }

        /// <summary>The new-row editor is open.</summary>
        [ObservableProperty]
        public partial bool IsAdding { get; set; }

        [ObservableProperty]
        public partial string NewKey { get; set; } = "";

        [ObservableProperty]
        public partial string NewValue { get; set; } = "";

        [ObservableProperty]
        public partial string? NewRowError { get; set; }

        /// <summary>Known keys to offer for a new row: every one the selection doesn't already use.</summary>
        [ObservableProperty]
        public partial IReadOnlyList<KnownKey> AvailableKeys { get; set; } = [];

        /// <summary>The album art every selected song has once its draft is applied, or <see langword="null"/> when there's none to show.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasArtImage))]
        [NotifyPropertyChangedFor(nameof(ArtAspect))]
        public partial Bitmap? ArtImage { get; set; }

        /// <summary>Shown in the tile instead of an image, e.g. "Album art not set" or "Album art differs".</summary>
        [ObservableProperty]
        public partial string? ArtNote { get; set; }

        /// <summary>The shown album art's stored name, e.g. <c>album.png</c>, when it's known.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ArtDownloadHeader))]
        public partial string? ArtFileName { get; set; }

        /// <summary>The shown album art's size, formatted, when it's known.</summary>
        [ObservableProperty]
        public partial string? ArtFileSize { get; set; }

        /// <summary>Why the chosen chart can't be used.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasChartError))]
        public partial string? ChartError { get; set; }

        /// <summary>Why the chosen replacement image can't be used.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasArtError))]
        public partial string? ArtError { get; set; }

        /// <summary>Why the chosen background file can't be used.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasBackgroundError))]
        public partial string? BackgroundError { get; set; }

        /// <summary>Why the chosen chart's lyrics can't be used.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasLyricsError))]
        public partial string? LyricsError { get; set; }

        /// <summary>Why some of the chosen files couldn't be added.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasFilesError))]
        public partial string? FilesError { get; set; }

        /// <summary>Why the chosen file couldn't be added as a track.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasTracksError))]
        public partial string? TracksError { get; set; }

        public bool HasArtImage => ArtImage is not null;

        /// <summary>The art's right-click menu item, naming the file it saves: <c>Download "album.png"</c>.</summary>
        public string ArtDownloadHeader => ArtFileName is { } name ? SongFileSource.DownloadHeaderFor(name) : Strings.MenuDownloadArt;

        /// <summary>The art tile's width over its height: the shown image's, or square when there's none.</summary>
        public double ArtAspect => ArtImage is { Size: { Width: > 0, Height: > 0 } size } ? size.Width / size.Height : 1;

        public bool HasCharter => Charter is not null;

        public bool HasSizeText => SizeText is not null;

        public bool HasChartError => ChartError is not null;

        public bool HasArtError => ArtError is not null;

        public bool HasBackgroundError => BackgroundError is not null;

        public bool HasLyricsError => LyricsError is not null;

        public bool HasFilesError => FilesError is not null;

        public bool HasTracksError => TracksError is not null;

        /// <summary>A selected song has a staged album-art change.</summary>
        public bool IsArtChanged => _songs.Any(s => s.Item.Draft.AlbumArt is not null);

        /// <summary><c>*</c> beside the tile while the art has a staged change, like an edited row.</summary>
        public string? ArtMarker => IsArtChanged ? "*" : null;

        public bool ShowPlaceholder => _entries.Count == 0;

        public bool HasError => ErrorMessage is not null;

        public bool ShowDetails => _entries.Count > 0 && !IsLoading && !HasError;

        public bool HasFindings => Findings.Count > 0;

        public bool HasMetadataNote => MetadataNote is not null;

        public bool HasReadOnlyNote => ReadOnlyNote is not null;

        public bool HasSaveError => SaveError is not null;

        public bool HasNewRowError => NewRowError is not null;

        /// <summary>One song is selected, so its tracks, chart and other files are shown.</summary>
        public bool ShowSongFiles => !IsBulk;

        /// <summary>
        /// The background's offset button is shown: one song is selected, and it has a video. It needn't be the one
        /// shown, or be able to play, as the offset only sets a song.ini key.
        /// </summary>
        public bool ShowVideoOffset => ShowSongFiles && Background.HasVideoKind;

        public bool ShowNoOtherFiles => !IsBulk && Files.Count == 0;

        public bool HasRemovedTracks => RemovedTracks.Count > 0;

        /// <summary>The song will have no chart once its draft is saved.</summary>
        public bool ShowChartMissing => !IsBulk && Charts.All(c => c.IsRemoved);

        /// <summary>Why there's no chart: there's none, or a staged change removes it.</summary>
        public string ChartMissingNote => Charts.Count > 0 ? Strings.ChartWillBeRemoved : Strings.NoChart;

        /// <summary>The one selected song's chart has a staged change.</summary>
        public bool IsChartChanged => Charts.Any(c => c.IsRemoved || c.Marker is not null) || (_songs.Count == 1 && _songs[0].Item.Draft.HasChartOffsets);

        /// <summary>Every selected song was read and can be written back.</summary>
        public bool IsEditable => _songs.Count > 0 && _songs.Count == _entries.Count && ReadOnlyNote is null;

        public bool CanEdit => IsEditable && !IsSaving;

        public bool ShowAddRow => CanEdit && !IsAdding;

        /// <summary>No selected song has a <c>song_length</c> row, so Auto Set is offered beside Add row instead.</summary>
        public bool ShowAutoSetLengthFallback => CanEdit && !Metadata.Any(r => r.IsSongLength);

        /// <summary>The songs' lengths are being worked out.</summary>
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(AutoSetSongLengthCommand))]
        public partial bool IsAutoSettingLength { get; set; }

        /// <summary>How many songs Auto Set couldn't find a length for.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasAutoLengthNote))]
        public partial string? AutoLengthNote { get; set; }

        public bool HasAutoLengthNote => AutoLengthNote is not null;

        /// <summary>A selected song has unsaved changes.</summary>
        public bool HasDrafts => _songs.Any(s => !s.Item.Draft.IsEmpty);

        public string MetadataHeader => string.Format(CultureInfo.CurrentCulture, Strings.MetadataHeaderFormat, Metadata.Count);

        // Counts the other files there will be once the staged changes are saved.
        public string OtherFilesHeader => string.Format(CultureInfo.CurrentCulture, Strings.OtherFilesHeaderFormat, Files.Count(f => !f.IsRemoved));

        /// <summary>Why <paramref name="value"/> can't be stored, or <see langword="null"/> when it can.</summary>
        public static string? ValidateValue(string value) =>
            value.Length == 0 ? Strings.ValueEmpty
            : MetadataRules.ValueHasForbiddenCharacter(value) ? Strings.ValueForbidden
            : null;

        /// <summary>Why <paramref name="key"/> can't be stored, or <see langword="null"/> when it can.</summary>
        public static string? ValidateKey(string key) =>
            key.Length == 0 ? Strings.KeyEmpty
            : MetadataRules.KeyHasForbiddenCharacter(key) ? Strings.KeyForbidden
            : null;

        /// <summary>Loads <paramref name="entries"/>, replacing whatever is shown. A newer call cancels an older one.</summary>
        /// <param name="inPlace">
        /// For the one song shown, read again where it is, as the merge preview is once it's staged again: what's shown stays,
        /// playback and the background video going on, until what's read replaces it, rather than the panel emptying while
        /// it's read. Playback stops only when its tracks changed. For anything else the songs are loaded as usual.
        /// </param>
        public async Task ShowAsync(IReadOnlyList<SongEntry> entries, bool inPlace = false)
        {
            // This reads what's shown afresh, so a reload another panel's change was waiting on is done by it.
            _externalChangeTimer.Stop();
            _externalNeedsReload = false;
            _loadCts?.Cancel();
            _autoLengthCts?.Cancel();
            using var cts = new CancellationTokenSource();
            _loadCts = cts;

            // The chart is read again, so the shifts drawn on it, made against the one read before, are let go.
            LoadGeneration++;
            _previewShifts = null;

            inPlace = inPlace && !IsBulk && !IsSaving && ErrorMessage is null
                && entries is [var entry] && _entries is [var shown] && _songs.Count == 1
                && entry.FullPath.Equals(shown.FullPath, StringComparison.Ordinal);
            if (inPlace)
            {
                // The chart and lyrics are read again, still drawn until they are.
                Highway.ReloadChart();
                Lyrics.ReloadChart();
                _entries = entries;
                Title = entries[0].DisplayName;
            }
            else
            {
                ClearShown(entries);
            }

            if (entries.Count == 0)
            {
                _loadCts = null;
                return;
            }

            // Drafts live on the UI thread, so the staged images to show are picked out here.
            var stagedSources = entries
                .Select(e => _drafts.Get(e.FullPath).AlbumArt?.SourcePath)
                .OfType<string>()
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var stagedBackgrounds = entries
                .SelectMany(e => SongBackgrounds.Kinds.Select(k => _drafts.Get(e.FullPath).GetBackground(k)?.SourcePath))
                .OfType<string>()
                .Distinct(StringComparer.Ordinal)
                .ToList();

            try
            {
                var loaded = await Task.Run(
                    async () =>
                    {
                        var selection = entries.Count == 1 ? await LoadSingleAsync(entries[0], cts.Token) : await LoadBulkAsync(entries, cts.Token);
                        var art = await LoadArtAsync(selection.Songs, stagedSources, cts.Token);
                        try
                        {
                            var backgrounds = await BackgroundViewModel.LoadAsync(
                                [.. selection.Songs.Select(s => s.Pairs is null ? null : s.Entry)], stagedBackgrounds, cts.Token);
                            return selection with { Art = art, Backgrounds = backgrounds };
                        }
                        catch
                        {
                            DisposeLater(art.Images.Values);
                            throw;
                        }
                    },
                    cts.Token);

                if (_loadCts == cts)
                {
                    Apply(loaded, inPlace);
                }
                else
                {
                    DisposeLater(loaded.Art.Images.Values);
                    DisposeLater(loaded.Backgrounds.Images.Values);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (_loadCts == cts)
                    ErrorMessage = string.Format(CultureInfo.CurrentCulture, Strings.DetailUnreadableFormat, ex.Message);
            }
            finally
            {
                if (_loadCts == cts)
                {
                    _loadCts = null;
                    IsLoading = false;
                    RefreshState();
                }
            }
        }

        /// <summary>Empties the panel for <paramref name="entries"/>, about to be loaded, letting go of everything shown before.</summary>
        private void ClearShown(IReadOnlyList<SongEntry> entries)
        {
            // Playback is of what's shown, and holds its files open; so does the background video.
            Player.Unload();
            ForgetSongLength();
            Background.Unload();
            Highway.Unload();
            Lyrics.Unload();

            _entries = entries;
            _songs = [];
            SyncTimingOffsets();
            _downloadCommands.Clear();
            RevealCommand.NotifyCanExecuteChanged();
            _loadNote = null;
            _diskFiles = [];
            _totalSize = 0;
            ReleaseArt();
            Metadata.Clear();
            Files.Clear();
            Charts.Clear();
            RemovedTracks.Clear();
            Findings.Clear();
            HiddenIssues.Clear();
            OnPropertyChanged(nameof(HasHiddenIssues));
            OnPropertyChanged(nameof(HasValidationIssues));
            Title = entries.Count switch
            {
                0 => Strings.DetailPlaceholderTitle,
                1 => entries[0].DisplayName,
                _ => string.Format(CultureInfo.CurrentCulture, Strings.MultipleSelectedFormat, entries.Count),
            };
            IsBulk = entries.Count > 1;
            Charter = null;
            Subtitle = null;
            SizeText = null;
            MetadataNote = null;
            ReadOnlyNote = null;
            ErrorMessage = null;
            SaveError = null;
            ArtError = null;
            BackgroundError = null;
            LyricsError = null;
            FilesError = null;
            TracksError = null;
            ChartError = null;
            AutoLengthNote = null;
            CancelAdd();
            IsLoading = entries.Count > 0;
            RefreshState();
        }

        /// <summary>Ends editing on every row but <paramref name="row"/>, dropping text that wasn't committed.</summary>
        internal void CancelOtherEdits(MetadataRowViewModel? row)
        {
            foreach (var other in Metadata)
            {
                if (other != row && other.IsEditing)
                    other.CancelCommand.Execute(null);
            }
        }

        internal void SetValue(string key, string value)
        {
            BulkMetadata.Set(Items(), key, value);
            OnDraftsChanged();
        }

        internal void RemoveKey(string key)
        {
            BulkMetadata.Remove(Items(), key);
            OnDraftsChanged();
        }

        internal void RevertKey(string key)
        {
            BulkMetadata.Revert(Items(), key);
            OnDraftsChanged();
        }

        /// <summary>
        /// Applies every quick fix for <paramref name="key"/> to each selected song's draft, each worked out from that
        /// song's own value. Nothing is saved.
        /// </summary>
        internal void ApplyQuickFixes(string key)
        {
            bool applied = false;
            foreach (var song in _songs)
                applied |= QuickFixRegistry.FixKey(song.Item, key) > 0;
            if (applied)
                OnDraftsChanged();
        }

        /// <summary>
        /// The Quick Fix command of a key without a row, kept per key so a listed failure keeps its command, and the
        /// list its rows, while the key's validation stays the same.
        /// </summary>
        private IRelayCommand QuickFixCommandFor(string key)
        {
            if (!_quickFixKeyCommands.TryGetValue(key, out var command))
            {
                command = new RelayCommand(() => ApplyQuickFixes(key), () => CanEdit && _keysWithQuickFix.Contains(key));
                _quickFixKeyCommands[key] = command;
            }

            return command;
        }

        private void RefreshQuickFixKeyCommands()
        {
            foreach (var command in _quickFixKeyCommands.Values)
                command.NotifyCanExecuteChanged();
        }

        /// <summary>Unsets the difficulty <paramref name="key"/>: <c>-1</c> where one is stored, and no change elsewhere.</summary>
        internal void UnsetDifficulty(string key)
        {
            MetadataDifficulty.Unset(Items(), key);
            OnDraftsChanged();
        }

        [RelayCommand]
        private void ToggleShowAllInstruments() => ShowAllInstruments = !ShowAllInstruments;

        partial void OnShowAllInstrumentsChanged(bool value)
        {
            foreach (var instrument in Instruments)
                instrument.RefreshShown();
        }

        private bool CanAutoSetLength() => CanEdit && !IsAutoSettingLength;

        /// <summary>
        /// Sets each selected song's <c>song_length</c> to its own length: the longest of its chart's
        /// last note, its audio and its background video, plus a second. Songs whose length can't be
        /// found are left as they are.
        /// </summary>
        [RelayCommand(CanExecute = nameof(CanAutoSetLength))]
        private async Task AutoSetSongLengthAsync()
        {
            CancelOtherEdits(null);
            CancelAdd();
            AutoLengthNote = null;

            // Drafts live on the UI thread and can change while the files are read, so each is copied here.
            var songs = _songs;
            var inputs = songs.Select(s => (s.Entry, Draft: s.Item.Draft.Clone(), Pairs: s.Item.Draft.ApplyTo(s.Item.Original))).ToList();
            var lengths = new long?[inputs.Count];

            using var cts = new CancellationTokenSource();
            _autoLengthCts = cts;
            IsAutoSettingLength = true;
            try
            {
                var options = new ParallelOptions { MaxDegreeOfParallelism = BulkLoadParallelism, CancellationToken = cts.Token };
                await Task.Run(() => Parallel.For(0, inputs.Count, options, i =>
                {
                    var (entry, draft, pairs) = inputs[i];
                    try
                    {
                        lengths[i] = SongLengthEstimator.Estimate(entry, draft, pairs, _videoProbe, cts.Token).SongLengthMs;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // The song can't be listed: it's counted as not found below.
                    }
                    catch (OperationCanceledException)
                    {
                        // Thrown here it would come out wrapped; the token is checked below instead.
                    }
                }));
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                if (ReferenceEquals(_autoLengthCts, cts))
                    _autoLengthCts = null;
                IsAutoSettingLength = false;
            }

            // The selection may have moved on while the files were read.
            if (cts.IsCancellationRequested || !ReferenceEquals(songs, _songs) || !CanEdit)
                return;

            // Each song gets its own length, unlike an edit typed into the row.
            for (int i = 0; i < songs.Count; i++)
            {
                if (lengths[i] is { } ms)
                    BulkMetadata.Set([songs[i].Item], SongLengthKey, ms.ToString(CultureInfo.InvariantCulture));
            }

            int missed = lengths.Count(l => l is null);
            if (missed > 0)
                AutoLengthNote = string.Format(CultureInfo.CurrentCulture, Strings.AutoSetLengthFailedFormat, missed);
            OnDraftsChanged();
        }

        [RelayCommand(CanExecute = nameof(CanEdit))]
        private void BeginAdd()
        {
            NewKey = "";
            NewValue = "";
            NewRowError = null;
            RefreshAvailableKeys();
            IsAdding = true;
        }

        [RelayCommand]
        private void CommitAdd()
        {
            if (!IsAdding)
                return;

            string key = NewKey.Trim();
            string value = NewValue.Trim();

            // A hidden unset difficulty isn't a row, so adding it edits the stored value.
            string? error = ValidateKey(key)
                ?? (Metadata.Any(r => !r.IsRemoved && r.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) ? Strings.KeyExists : null)
                ?? ValidateValue(value);
            if (error is not null)
            {
                NewRowError = error;
                return;
            }

            CancelAdd();
            SetValue(key, value);
        }

        [RelayCommand]
        private void CancelAdd()
        {
            IsAdding = false;
            NewRowError = null;
        }

        /// <summary>Asks for an image and stages it as every selected song's album art.</summary>
        [RelayCommand(CanExecute = nameof(CanEdit))]
        private async Task ReplaceArtAsync()
        {
            var songs = _songs;
            string? path = await _imagePicker.PickImageAsync(Strings.PickArtTitle);
            if (path is null || !ReferenceEquals(songs, _songs) || !CanEdit)
                return;

            await StageArtAsync(path);
        }

        /// <summary>Reads the image at <paramref name="path"/> and, if it can be used, stages it as every selected song's album art.</summary>
        private async Task StageArtAsync(string path)
        {
            var songs = _songs;
            ArtError = null;
            if (AlbumArt.StoredNameFor(path) is null)
            {
                ArtError = SharedStrings.ArtNotAnImage;
                return;
            }

            var (art, bitmap) = await Task.Run(() => LoadStagedAsync(path, CancellationToken.None));

            // The selection may have moved on while the image was read.
            if (!ReferenceEquals(songs, _songs) || !CanEdit)
            {
                DisposeLater(bitmap is null ? [] : [bitmap]);
                return;
            }

            if (art.Error is not null)
            {
                ArtError = art.Error;
                return;
            }

            AddImage(art.Hash!, bitmap!);
            _stagedArt[path] = art;
            foreach (var song in _songs)
                song.Item.Draft.SetAlbumArt(path);
            OnDraftsChanged();
        }

        private bool CanRemoveArt() => CanEdit && _songs.Any(s => EffectiveArtKey(s) is not null);

        /// <summary>Stages removing every selected song's album art.</summary>
        [RelayCommand(CanExecute = nameof(CanRemoveArt))]
        private void RemoveArt()
        {
            ArtError = null;
            foreach (var song in _songs)
                song.Item.Draft.RemoveAlbumArt();
            OnDraftsChanged();
        }

        private bool CanRevertArt() => IsArtChanged && CanEdit;

        /// <summary>Drops the staged album-art change of every selected song.</summary>
        [RelayCommand(CanExecute = nameof(CanRevertArt))]
        private void RevertArt()
        {
            ArtError = null;
            foreach (var song in _songs)
                song.Item.Draft.RevertAlbumArt();
            OnDraftsChanged();
        }

        /// <summary>Asks for an image or video and stages it as every selected song's background of its kind.</summary>
        [RelayCommand(CanExecute = nameof(CanEdit))]
        private async Task ReplaceBackgroundAsync()
        {
            var songs = _songs;
            string? path = await _filePicker.PickBackgroundAsync(Strings.PickBackgroundTitle);
            if (path is null || !ReferenceEquals(songs, _songs) || !CanEdit)
                return;

            await StageBackgroundAsync(path);
        }

        /// <summary>
        /// Reads the file at <paramref name="path"/> and, if it can be used, stages it as every
        /// selected song's background image or video, as its extension says, and shows it.
        /// </summary>
        private async Task StageBackgroundAsync(string path)
        {
            var songs = _songs;
            BackgroundError = null;
            if (SongBackgrounds.KindOf(path) is not { } kind)
            {
                BackgroundError = Strings.BackgroundNotSupported;
                return;
            }

            var (staged, bitmap) = await Task.Run(() => BackgroundViewModel.LoadStagedAsync(path, CancellationToken.None));

            // The selection may have moved on while the file was read.
            if (!ReferenceEquals(songs, _songs) || !CanEdit)
            {
                DisposeLater(bitmap is null ? [] : [bitmap]);
                return;
            }

            if (staged.Error is not null)
            {
                DisposeLater(bitmap is null ? [] : [bitmap]);
                BackgroundError = staged.Error;
                return;
            }

            Background.AddStaged(path, staged, bitmap);
            foreach (var song in _songs)
                song.Item.Draft.SetBackground(kind, path);
            Background.Show(kind);
            OnDraftsChanged();
        }

        private bool CanRemoveBackground()
        {
            var kind = Background.ShownKind;
            return CanEdit && _songs.Any(s => Background.EffectiveKey(BackgroundSongOf(s), kind) is not null);
        }

        /// <summary>Stages removing the shown kind of background, image or video, from every selected song.</summary>
        [RelayCommand(CanExecute = nameof(CanRemoveBackground))]
        private void RemoveBackground()
        {
            var kind = Background.ShownKind;
            BackgroundError = null;
            foreach (var song in _songs)
                song.Item.Draft.RemoveBackground(kind);
            OnDraftsChanged();
        }

        private bool CanRevertBackground() => IsBackgroundChanged && CanEdit;

        /// <summary>A selected song has a staged background change.</summary>
        public bool IsBackgroundChanged => _songs.Any(s => s.Item.Draft.HasBackgroundChanges);

        /// <summary>Drops the staged background changes, of both kinds, of every selected song.</summary>
        [RelayCommand(CanExecute = nameof(CanRevertBackground))]
        private void RevertBackground()
        {
            BackgroundError = null;
            foreach (var song in _songs)
            {
                foreach (var kind in SongBackgrounds.Kinds)
                    song.Item.Draft.RevertBackground(kind);
            }

            OnDraftsChanged();
        }

        // Lyrics go in the chart, so like the chart they're changed only when one song is selected, and it has one.
        private bool CanReplaceLyrics() => CanChangeFiles() && Lyrics.HasChart;

        /// <summary>
        /// Asks for a chart or <c>.sng</c> and stages its vocals, <c>PART VOCALS</c> and the harmonies,
        /// as the selected song's; adding them when it has none.
        /// </summary>
        [RelayCommand(CanExecute = nameof(CanReplaceLyrics))]
        private async Task ReplaceLyricsAsync()
        {
            var songs = _songs;
            string? path = await _filePicker.PickLyricsSourceAsync(Strings.PickLyricsTitle);
            if (path is null || !ReferenceEquals(songs, _songs) || !CanReplaceLyrics())
                return;

            string fileName = Path.GetFileName(path);
            LyricsError = null;
            VocalsSource source;
            try
            {
                source = await Task.Run(() => VocalsSource.LoadAsync(path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // ChartFormatException is an IOException: the chart is malformed.
                if (ReferenceEquals(songs, _songs))
                    LyricsError = string.Format(CultureInfo.CurrentCulture, Strings.LyricsSourceUnreadableFormat, fileName, ex.Message);
                return;
            }

            // The selection may have moved on while the chart was read.
            if (!ReferenceEquals(songs, _songs) || !CanReplaceLyrics())
                return;
            if (!source.Vocals.HasVocals)
            {
                LyricsError = string.Format(CultureInfo.CurrentCulture, Strings.LyricsSourceHasNoneFormat, fileName);
                return;
            }

            // The draft keeps the source as read, so saving writes the vocals shown. An offset was set against the vocals being replaced.
            songs[0].Item.Draft.SetVocals(source);
            songs[0].Item.Draft.SetVocalsOffset(0);
            OnDraftsChanged();
        }

        private bool CanRemoveLyrics() => CanChangeFiles() && Lyrics.HasLyrics;

        /// <summary>Stages removing the selected song's vocals; or, when they're ones a change adds to a chart without any, drops that change.</summary>
        [RelayCommand(CanExecute = nameof(CanRemoveLyrics))]
        private void RemoveLyrics()
        {
            var draft = _songs[0].Item.Draft;
            LyricsError = null;
            if (Lyrics.ChartHasVocals)
                draft.RemoveVocals();
            else
                draft.RevertVocals();
            draft.SetVocalsOffset(0);
            OnDraftsChanged();
        }

        private bool CanRevertLyrics() => CanChangeFiles() && Lyrics.IsChanged;

        /// <summary>Drops the staged changes to the selected song's vocals: others in their place, and their offset.</summary>
        [RelayCommand(CanExecute = nameof(CanRevertLyrics))]
        private void RevertLyrics()
        {
            LyricsError = null;
            _songs[0].Item.Draft.RevertVocals();
            _songs[0].Item.Draft.SetVocalsOffset(0);
            OnDraftsChanged();
        }

        // Files are shown, and so changed, only when one song is selected.
        private bool CanChangeFiles() => CanEdit && !IsBulk && _songs.Count == 1;

        /// <summary>
        /// Asks for files and stages adding them to the selected song, each under its own name. A
        /// file named like album art, a background image or a background video is staged as that
        /// instead, so it replaces the song's other files of the kind.
        /// </summary>
        [RelayCommand(CanExecute = nameof(CanChangeFiles))]
        private async Task AddFilesAsync()
        {
            var songs = _songs;
            var paths = await _filePicker.PickFilesAsync(Strings.PickFilesTitle);
            if (paths.Count == 0 || !ReferenceEquals(songs, _songs) || !CanChangeFiles())
                return;

            var song = songs[0];
            bool isSng = song.Entry.Kind == SongEntryKind.SngFile;
            var errors = new List<string>();
            string? art = null;
            var backgrounds = new List<string>();
            foreach (string path in paths)
            {
                string name = Path.GetFileName(path);
                if (AlbumArt.IsAlbumArtName(name))
                {
                    art = path;
                    continue;
                }

                if (SongBackgrounds.KindOfName(name) is not null)
                {
                    backgrounds.Add(path);
                    continue;
                }

                if (name.Equals(SongIni.FileName, StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add(string.Format(CultureInfo.CurrentCulture, Strings.FileSongIniReservedFormat, name));
                    continue;
                }

                if (isSng)
                {
                    if (FilenameRules.FindProblem(name) is { } problem)
                    {
                        errors.Add(string.Format(CultureInfo.CurrentCulture, Strings.FileNameInvalidFormat, name, problem.Reason));
                        continue;
                    }

                    name = FilenameRules.NormaliseStoredName(name);
                }

                long length;
                try
                {
                    length = new FileInfo(path).Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    errors.Add(string.Format(CultureInfo.CurrentCulture, Strings.FileUnreadableFormat, name, ex.Message));
                    continue;
                }

                // Offsets and a vocals change were set against the chart being replaced.
                if (ChartFiles.IsChartName(name))
                {
                    song.Item.Draft.ClearChartOffsets();
                    song.Item.Draft.RevertVocals();
                }
                song.Item.Draft.AddFile(new AddedFile(name, path, length));
            }

            FilesError = errors.Count > 0 ? string.Join(Environment.NewLine, errors) : null;
            OnDraftsChanged();
            if (art is not null)
                await StageArtAsync(art);

            // One of each kind: the last chosen, as when choosing a background twice.
            foreach (string background in backgrounds.GroupBy(p => SongBackgrounds.KindOf(p)).Select(g => g.Last()))
                await StageBackgroundAsync(background);
        }

        /// <summary>
        /// Stages removing <paramref name="row"/>'s file, or drops it when it's one the draft adds.
        /// Only other files are listed, so a file with an album-art or background name here is one
        /// beside the file used, such as an <c>album.jpg</c> beside the <c>album.png</c> shown, and
        /// is removed on its own.
        /// </summary>
        [RelayCommand(CanExecute = nameof(CanChangeFiles))]
        private void RemoveFile(FileRow? row)
        {
            if (row is null || !row.CanRemove || !CanChangeFiles())
                return;

            var draft = _songs[0].Item.Draft;
            if (row.IsAdded)
                draft.RevertFile(row.Path);
            else
                draft.RemoveFile(row.Path);

            OnDraftsChanged();
        }

        /// <summary>Drops the staged change that removes or replaces <paramref name="row"/>'s file.</summary>
        [RelayCommand(CanExecute = nameof(CanChangeFiles))]
        private void RevertFile(FileRow? row)
        {
            if (row is null || !row.CanRevert || !CanChangeFiles())
                return;

            // A file removed on its own is put back on its own, leaving any art or background change.
            var draft = _songs[0].Item.Draft;
            if (draft.IsFileRemoved(row.Path))
            {
                draft.RevertFile(row.Path);
            }
            else if (AlbumArt.IsAlbumArtName(row.Path))
            {
                draft.RevertAlbumArt();
                ArtError = null;
            }
            else if (SongBackgrounds.KindOfName(row.Path) is { } kind)
            {
                draft.RevertBackground(kind);
                BackgroundError = null;
            }
            else
            {
                draft.RevertFile(row.Path);
            }

            OnDraftsChanged();
        }

        /// <summary>
        /// Asks for an audio file and stages it as the selected song's <paramref name="stem"/>
        /// track, stored as the stem with the file's extension. Every other file of the stem, in
        /// any format, is staged for removal, so the song ends up with one track for it.
        /// </summary>
        private async Task AddTrackAsync(string stem)
        {
            var songs = _songs;
            string? path = await _filePicker.PickAudioAsync(string.Format(CultureInfo.CurrentCulture, Strings.PickTrackTitleFormat, stem));
            if (path is null || !ReferenceEquals(songs, _songs) || !CanChangeFiles())
                return;

            string fileName = Path.GetFileName(path);
            string extension = Path.GetExtension(path).ToLowerInvariant();
            TracksError = null;
            if (!AudioStems.IsAudioExtension(extension))
            {
                TracksError = string.Format(CultureInfo.CurrentCulture, Strings.TrackNotAudioFormat, fileName);
                return;
            }

            // Decoding a little of it now catches a file that isn't what its extension says.
            var (length, problem) = await Task.Run(() =>
            {
                try
                {
                    AudioDecoder.Check(path);
                    return (new FileInfo(path).Length, (string?)null);
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
                {
                    return (0L, ex.Message);
                }
            });

            if (!ReferenceEquals(songs, _songs) || !CanChangeFiles())
                return;
            if (problem is not null)
            {
                TracksError = string.Format(CultureInfo.CurrentCulture, Strings.TrackUnusableFormat, fileName, problem);
                return;
            }

            string storedName = stem + extension;
            var draft = songs[0].Item.Draft;
            foreach (string added in SongTracks.FilesOfStem(draft.AddedFiles.Select(f => f.StoredName), stem))
                draft.RevertFile(added);

            // A stored file that differs only in case is still replaced, on a case-sensitive file system too.
            // Recorded as replaced by the add, so removing the added track brings them back.
            var replaced = SongTracks.FilesOfStem(_diskFiles.Select(f => f.Path), stem)
                .Where(stored => !stored.Equals(storedName, StringComparison.Ordinal))
                .ToList();

            draft.AddFile(new AddedFile(storedName, path, length), replaced);
            OnDraftsChanged();
        }

        /// <summary>Stages removing <paramref name="row"/>'s track, or drops it when it's one the draft adds.</summary>
        [RelayCommand(CanExecute = nameof(CanChangeFiles))]
        private void RemoveTrack(TrackRowViewModel? row)
        {
            if (row is null || !CanChangeFiles())
                return;

            var draft = _songs[0].Item.Draft;
            if (row.Track.IsStaged)
                draft.RevertFile(row.Track.StoredName);
            else
                draft.RemoveFile(row.Track.StoredName);
            TracksError = null;
            OnDraftsChanged();
        }

        /// <summary>Asks for an audio file to replace <paramref name="row"/>'s track with, as Add track does for its stem.</summary>
        [RelayCommand(CanExecute = nameof(CanChangeFiles))]
        private Task ReplaceTrackAsync(TrackRowViewModel? row) =>
            row is null || !CanChangeFiles() ? Task.CompletedTask : AddTrackAsync(row.Track.Stem);

        /// <summary>Keeps <paramref name="row"/>'s track after all, dropping its staged removal.</summary>
        [RelayCommand(CanExecute = nameof(CanChangeFiles))]
        private void UndoRemoveTrack(RemovedTrackRow? row)
        {
            if (row is null || !CanChangeFiles())
                return;

            _songs[0].Item.Draft.RevertFile(row.Name);
            TracksError = null;
            OnDraftsChanged();
        }

        /// <summary>
        /// Asks for a <c>.chart</c> or MIDI file and stages it as the selected song's chart, stored as
        /// <c>notes.chart</c> or <c>notes.mid</c>. Every other chart file is staged for removal, so the
        /// song ends up with the one chosen.
        /// </summary>
        [RelayCommand(CanExecute = nameof(CanChangeFiles))]
        private async Task ChooseChartAsync()
        {
            var songs = _songs;
            string? path = await _filePicker.PickChartAsync(Strings.PickChartTitle);
            if (path is null || !ReferenceEquals(songs, _songs) || !CanChangeFiles())
                return;

            string fileName = Path.GetFileName(path);
            ChartError = null;
            if (ChartFiles.StoredNameFor(path) is not { } storedName)
            {
                ChartError = string.Format(CultureInfo.CurrentCulture, Strings.ChartNotSupportedFormat, fileName);
                return;
            }

            var (length, problem) = await Task.Run(() =>
            {
                try
                {
                    return (new FileInfo(path).Length, (string?)null);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return (0L, ex.Message);
                }
            });

            if (!ReferenceEquals(songs, _songs) || !CanChangeFiles())
                return;
            if (problem is not null)
            {
                ChartError = string.Format(CultureInfo.CurrentCulture, Strings.ChartUnreadableFormat, fileName, problem);
                return;
            }

            var draft = songs[0].Item.Draft;
            RevertAddedCharts(draft);

            // Offsets and a vocals change were set against the chart being replaced.
            draft.ClearChartOffsets();
            draft.RevertVocals();

            // Recorded as replaced by the add, so removing it (or reverting the chart) brings them back.
            var replaced = ChartFiles.FilesOf(_diskFiles.Select(f => f.Path))
                .Where(stored => !stored.Equals(storedName, StringComparison.Ordinal))
                .ToList();

            draft.AddFile(new AddedFile(storedName, path, length), replaced);
            OnDraftsChanged();
        }

        private bool CanRemoveChart() => CanChangeFiles() && Charts.Any(c => c.IsKept);

        /// <summary>Stages removing the selected song's chart: every chart file it has, and any chart a draft adds.</summary>
        [RelayCommand(CanExecute = nameof(CanRemoveChart))]
        private void RemoveChart()
        {
            var draft = _songs[0].Item.Draft;
            RevertAddedCharts(draft);
            draft.ClearChartOffsets();
            draft.RevertVocals();
            foreach (string stored in ChartFiles.FilesOf(_diskFiles.Select(f => f.Path)))
                draft.RemoveFile(stored);
            ChartError = null;
            OnDraftsChanged();
        }

        private bool CanRevertChart() => CanChangeFiles() && IsChartChanged;

        /// <summary>Drops every staged change to the selected song's chart, its offsets and vocals too.</summary>
        [RelayCommand(CanExecute = nameof(CanRevertChart))]
        private void RevertChart()
        {
            var draft = _songs[0].Item.Draft;
            RevertAddedCharts(draft);
            draft.ClearChartOffsets();
            draft.RevertVocals();
            foreach (string stored in ChartFiles.FilesOf(_diskFiles.Select(f => f.Path)))
                draft.RevertFile(stored);
            ChartError = null;
            OnDraftsChanged();
        }

        /// <summary>Drops the charts <paramref name="draft"/> adds, which brings back the ones they replaced.</summary>
        private static void RevertAddedCharts(MetadataDraft draft)
        {
            foreach (string added in ChartFiles.FilesOf(draft.AddedFiles.Select(f => f.StoredName)))
                draft.RevertFile(added);
        }

        // Only committed changes count: text still in an edit box isn't a change yet.
        private bool CanSave() => CanEdit && HasDrafts && !_job.IsRunning;

        /// <summary>
        /// Writes the selected songs' drafts. Songs that fail keep their drafts. Metadata that fails
        /// validation is only saved once the user confirms it.
        /// </summary>
        [RelayCommand(CanExecute = nameof(CanSave))]
        private async Task SaveAsync()
        {
            var entries = _entries;
            var dirty = _songs.Where(s => !s.Item.Draft.IsEmpty).ToList();
            IReadOnlyList<string> failures = [];

            // Only the songs being saved count: a selected song without a draft isn't written.
            int failingKeys = FailingKeyCount(dirty);
            if (failingKeys > 0 && _confirmation is not null)
            {
                string message = string.Format(CultureInfo.CurrentCulture, Strings.SaveInvalidMessageFormat, failingKeys);
                if (!await _confirmation.ConfirmAsync(Strings.SaveInvalidTitle, message, Strings.SaveAnywayButton))
                    return;

                // The selection, or the drafts, may have moved on while the dialog was open.
                if (!ReferenceEquals(entries, _entries) || !CanSave())
                    return;
                dirty = [.. _songs.Where(s => !s.Item.Draft.IsEmpty)];
            }

            if (!await ChartBuilderSongs.ConfirmWriteAsync(
                    _confirmation, _isOpenInChartBuilder, [.. dirty.Select(s => s.Entry)],
                    Strings.ChartBuilderOpenSaveOneFormat, Strings.ChartBuilderOpenSaveManyFormat, Strings.SaveAnywayButton))
            {
                return;
            }

            if (!ReferenceEquals(entries, _entries) || !CanSave())
                return;
            dirty = [.. _songs.Where(s => !s.Item.Draft.IsEmpty)];

            CancelAdd();
            IsSaving = true;
            SaveError = null;
            try
            {
                await ReleaseFilesAsync();
                var bySong = dirty.ToDictionary(s => s.Entry.FullPath, StringComparer.Ordinal);
                failures = await _saver.SaveAsync(
                    [.. dirty.Select(s => new SaveTarget(s.Entry, s.SongIniPath, s.Item.Draft))],
                    entry => NotifyDrafts([bySong[entry.FullPath]]));
            }
            finally
            {
                IsSaving = false;
            }

            // Show what's on disk now, unless the selection moved on while saving.
            if (!ReferenceEquals(entries, _entries))
                return;

            await ShowAsync(entries);
            if (failures.Count > 0 && ReferenceEquals(entries, _entries))
                SaveError = string.Format(CultureInfo.CurrentCulture, Strings.SaveFailedFormat, string.Join(Environment.NewLine, failures));
        }

        /// <summary>How many keys fail validation in <paramref name="songs"/>, as drafted: the rows saving them would leave marked.</summary>
        private static int FailingKeyCount(IEnumerable<EditableSong> songs)
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var song in songs)
            {
                var pairs = song.Item.Draft.ApplyTo(song.Item.Original);
                foreach (var finding in MetadataValidator.Validate(pairs, song.Item.RemainingDuplicateKeys()))
                {
                    if (MetadataValidator.IsFailing(finding))
                        keys.Add(finding.Key ?? "");
                }
            }

            return keys.Count;
        }

        private bool CanDiscard() => HasDrafts && !IsSaving;

        [RelayCommand(CanExecute = nameof(CanDiscard))]
        private void Discard()
        {
            CancelAdd();
            BackgroundError = null;
            LyricsError = null;
            FilesError = null;
            TracksError = null;
            ChartError = null;
            foreach (var song in _songs)
                song.Item.Draft.Clear();
            OnDraftsChanged();
        }

        /// <summary>
        /// Lets go of every file of the shown songs this panel holds open, which would stop them being replaced or
        /// deleted: playback's, and the background video's. They're opened again when the songs are next shown.
        /// </summary>
        internal async Task ReleaseFilesAsync()
        {
            Player.Unload();
            Background.Unload();
            ForgetSongLength();
            await Player.WhenFilesClosedAsync();
            await Background.WhenClosedAsync();

            // A failed estimate only costs the timeline its length; it mustn't stop the save.
            await _songLengthTask.ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
        }

        partial void OnIsLoadingChanged(bool value) => RefreshState();

        partial void OnErrorMessageChanged(string? value) => RefreshState();

        partial void OnMetadataNoteChanged(string? value) => RefreshState();

        partial void OnReadOnlyNoteChanged(string? value) => RefreshState();

        partial void OnSaveErrorChanged(string? value) => OnPropertyChanged(nameof(HasSaveError));

        partial void OnNewRowErrorChanged(string? value) => OnPropertyChanged(nameof(HasNewRowError));

        partial void OnIsAddingChanged(bool value) => OnPropertyChanged(nameof(ShowAddRow));

        partial void OnIsSavingChanged(bool value) => RefreshState();

        private List<MetadataItem> Items() => [.. _songs.Select(s => s.Item)];

        private void OnDraftsChanged()
        {
            NotifyDrafts(_songs);
            RebuildRows();
            RefreshArt();
            if (!IsBulk && _songs.Count == 1)
                _ = LoadChartFindingsAsync(_songs[0]);
        }

        /// <summary>Tells the draft store that <paramref name="songs"/>' drafts changed, without this panel hearing it back.</summary>
        private void NotifyDrafts(IEnumerable<EditableSong> songs)
        {
            _notifyingDrafts = true;
            try
            {
                foreach (var song in songs)
                    _drafts.NotifyChanged(song.Entry.FullPath);
            }
            finally
            {
                _notifyingDrafts = false;
            }
        }

        /// <summary>
        /// Another panel sharing the drafts changed one: if it's a song shown here, what's shown is brought up to date
        /// shortly. A change to its keys alone is rebuilt from the shared draft; anything else is read again, as the
        /// song may have been saved, or have staged files this panel hasn't read.
        /// </summary>
        private void OnDraftsChangedElsewhere(string path, DraftChange change)
        {
            if (_notifyingDrafts || _suspended || !_entries.Any(e => e.FullPath.Equals(path, StringComparison.Ordinal)))
                return;

            _externalNeedsReload |= change != DraftChange.Keys;
            _externalChangeTimer.Stop();
            _externalChangeTimer.Start();
        }

        private void OnExternalChangeSettled()
        {
            _externalChangeTimer.Stop();
            if (_suspended)
                return;

            // Never under an edit in progress here: try again once it's done.
            if (IsSaving || IsAdding || Metadata.Any(r => r.IsEditing))
            {
                _externalChangeTimer.Start();
                return;
            }

            if (_externalNeedsReload)
            {
                _ = ShowAsync(_entries);
                return;
            }

            // The drafts are shared, so a key change is rebuilt from them as an edit made here is, and playback carries on.
            RebuildRows();
            RefreshArt();
        }

        /// <summary>
        /// Lets go of the shown songs' files and stops following other panels' changes, as while another window that
        /// shares the drafts may save them. <see cref="ResumeAsync"/> shows the songs again.
        /// </summary>
        internal async Task SuspendAsync()
        {
            _suspended = true;
            _externalChangeTimer.Stop();
            _externalNeedsReload = false;
            _loadCts?.Cancel();
            _autoLengthCts?.Cancel();
            await ReleaseFilesAsync();
        }

        /// <summary>Follows other panels' changes again, and shows <paramref name="entries"/>, or what was shown, read afresh.</summary>
        internal Task ResumeAsync(IReadOnlyList<SongEntry>? entries = null)
        {
            _suspended = false;
            return ShowAsync(entries ?? _entries);
        }

        /// <summary>Stops following the shared drafts and job, for a panel whose window has closed.</summary>
        public void Detach()
        {
            _drafts.DraftsChanged -= OnDraftsChangedElsewhere;
            _job.PropertyChanged -= OnJobPropertyChanged;
            _externalChangeTimer.Stop();
            _loadCts?.Cancel();
        }

        /// <summary>
        /// The highway or the lyrics set an offset, and redraw themselves. Nothing else shown depends on the offsets,
        /// so this is lighter than <see cref="OnDraftsChanged"/>, as it runs for every step of a drag.
        /// </summary>
        private void OnOffsetsChanged()
        {
            NotifyDrafts(_songs);
            OnPropertyChanged(nameof(IsChartChanged));
            RefreshState();

            // Moving the chart can move its last note.
            RefreshSongLength();
        }

        /// <summary>
        /// The audio or video offset set its key. The key's row, the highway, the lyrics and the video follow it, but
        /// no file does, so this is lighter than <see cref="OnDraftsChanged"/>, as it runs for every step of a drag.
        /// </summary>
        private void OnTimingChanged()
        {
            NotifyDrafts(_songs);
            RebuildRows();
            RefreshPlayback();

            // The delay moves the chart's last note, and the video's start moves its end.
            RefreshSongLength();
        }

        /// <summary>The background, the highway and the lyrics, which play the selected songs as they're drafted, follow the drafts.</summary>
        private void RefreshPlayback()
        {
            Background.Refresh([.. _songs.Select(BackgroundSongOf)]);
            Highway.Refresh(HighwaySongOf());
            Lyrics.Refresh(HighwaySongOf(), HasVocalStem());
        }

        /// <summary>
        /// Shows the merge preview's songs lined up as chosen, before that's staged, while an alignment offset is dragged:
        /// the chart and lyrics already read drawn moved by <paramref name="chartShifts"/> and <paramref name="vocalsShift"/>,
        /// against the <c>delay</c> and <c>video_start_time</c> of <paramref name="metadata"/>, the song.ini that staging
        /// writes for it. Nothing is read; playback and the video go on, the video along its new timeline. The shifts
        /// last until the song is next shown, which reads the chart afresh.
        /// </summary>
        /// <returns>Whether it's shown: there's one song, as the preview has.</returns>
        public bool ShowPreviewAlignment(IReadOnlyDictionary<ChartTrackKey, int> chartShifts, int vocalsShift, IReadOnlyList<KeyValuePair<string, string>> metadata)
        {
            if (_songs is not [var song] || IsBulk || IsSaving)
                return false;

            // Lining up moves only these keys; the rest of what's staged, song_length and all, stays as it is until it's staged.
            var original = song.Item.Original.ToList();
            foreach (string key in (string[])[TimingOffsets.AudioKey, TimingOffsets.VideoKey])
            {
                string? value = metadata.FirstOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) is { Key: not null } pair ? pair.Value : null;
                int at = original.FindIndex(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
                if (value is null)
                    original.RemoveAll(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
                else if (at >= 0)
                    original[at] = KeyValuePair.Create(original[at].Key, value);
                else
                    original.Add(KeyValuePair.Create(key, value));
            }

            _songs = [song with { Item = song.Item with { Original = original } }];
            _previewShifts = (chartShifts, vocalsShift);
            RebuildRows();
            SyncTimingOffsets();
            RefreshPlayback();
            return true;
        }

        /// <summary>
        /// Brings what's shown up to date with the files the merge preview just staged again, as <see cref="ShowPreviewAlignment"/>
        /// already shows them: the song's listing, metadata rows, offsets, files and length are read afresh. The chart, lyrics,
        /// background video and playback are left alone, as lining the songs up changes none of what they read.
        /// </summary>
        public async Task SyncPreviewStagingAsync(CancellationToken cancellationToken)
        {
            if (_songs is not [var song] || IsBulk || _entries.Count != 1 || IsSaving)
                return;

            LoadedSelection selection;
            try
            {
                selection = await Task.Run(() => LoadSingleAsync(song.Entry, cancellationToken), cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                return;
            }

            if (cancellationToken.IsCancellationRequested)
                return;

            // Another load took over meanwhile; its truth wins.
            if (_songs is not [var current] || !ReferenceEquals(current, song))
                return;

            var loaded = selection.Songs.Single();
            if (loaded.Pairs is not { } pairs)
                return;

            _diskFiles = selection.Files;
            _totalSize = selection.TotalSize;
            _songs = [song with { Item = song.Item with { Original = pairs, DuplicateKeys = loaded.DuplicateKeys } }];
            _loadNote = selection.MetadataNote;
            SizeText = ByteSizeFormatter.Format(_totalSize);
            RebuildRows();
            SyncTimingOffsets();
            RebuildFiles();
        }

        /// <summary>Shows the one selected song's audio and video offsets, as its draft has its timing keys.</summary>
        private void SyncTimingOffsets()
        {
            if (_songs is [var song] && !IsBulk)
            {
                var drafted = song.Item.Draft.ApplyTo(song.Item.Original);

                // Until the chart is read, its own offset, which an unset delay stands for, isn't known.
                AudioOffset.Sync(Highway.ChartOffset is null ? null : song.Item.Draft, song.Item.Original, drafted, Highway.ChartOffset ?? TimeSpan.Zero);
                VideoOffset.Sync(song.Item.Draft, song.Item.Original, drafted, TimeSpan.Zero);
            }
            else
            {
                AudioOffset.Sync(null, [], [], TimeSpan.Zero);
                VideoOffset.Sync(null, [], [], TimeSpan.Zero);
            }
        }

        /// <summary>
        /// The one selected song's highway, audio and video offsets can be changed: it can be edited, or it's a preview,
        /// whose offsets stay editable, as lining a merge's parts up is what it's for. The merge takes them.
        /// </summary>
        private bool CanEditOffsets() => _songs.Count == 1 && !IsBulk && !IsSaving && (ReadOnlyNote is null || ReadOnlyNote == PreviewNote);

        /// <summary>
        /// The audio and video offsets can be changed, as the highway's can. The delay only moves a chart, so the
        /// audio's needs one read; the video's needs the song to have a video, shown or not.
        /// </summary>
        private void RefreshTimingOffsetsEditable()
        {
            AudioOffset.CanEdit = CanEditOffsets() && Highway.ChartOffset is not null;
            VideoOffset.CanEdit = CanEditOffsets() && Background.HasVideoKind;
        }

        /// <param name="inPlace">The song shown read again in place; see <see cref="ShowAsync"/>.</param>
        private void Apply(LoadedSelection loaded, bool inPlace = false)
        {
            // In place, what's shown is replaced, rather than having been let go before reading.
            var oldArt = inPlace ? _artImages : null;

            _songs = [.. loaded.Songs
                .Select((s, i) => (Song: s, ArtHash: loaded.Art.DiskHashes[i], Backgrounds: loaded.Backgrounds.Stored.ElementAtOrDefault(i) ?? StoredBackgrounds.None))
                .Where(x => x.Song.Pairs is not null)
                .Select(x => new EditableSong(
                    x.Song.Entry, new MetadataItem(x.Song.Pairs!, _drafts.Get(x.Song.Entry.FullPath), x.Song.DuplicateKeys), x.Song.SongIniPath, x.ArtHash, x.Backgrounds))];
            _artImages = loaded.Art.Images;
            _stagedArt = loaded.Art.Staged;
            _diskFiles = loaded.Files;
            _totalSize = loaded.TotalSize;
            Background.Apply(loaded.Backgrounds, keepShown: inPlace);
            if (oldArt is not null)
                DisposeLater(oldArt.Values);

            // A staged image that can no longer be read (moved, say) is reported like a bad pick.
            ArtError = _songs
                .Select(s => s.Item.Draft.AlbumArt?.SourcePath)
                .OfType<string>()
                .Select(p => _stagedArt.GetValueOrDefault(p)?.Error)
                .FirstOrDefault(e => e is not null);
            BackgroundError = _songs
                .SelectMany(s => SongBackgrounds.Kinds.Select(k => s.Item.Draft.GetBackground(k)?.SourcePath))
                .OfType<string>()
                .Select(p => Background.StagedAt(p)?.Error)
                .FirstOrDefault(e => e is not null);
            ReadOnlyNote = PreviewNote ?? loaded.Songs.Select(s => s.ReadOnlyReason).FirstOrDefault(r => r is not null);
            _loadNote = loaded.MetadataNote;

            if (IsBulk)
            {
                int sngCount = _entries.Count(e => e.Kind == SongEntryKind.SngFile);
                Subtitle = string.Format(CultureInfo.CurrentCulture, Strings.BulkSubtitleFormat, sngCount, _entries.Count - sngCount);
            }
            else
            {
                Subtitle = _entries[0].FullPath;
                SizeText = ByteSizeFormatter.Format(_totalSize);
            }

            if (inPlace)
                Findings.Clear();
            Findings.AddRange(loaded.Findings);
            RebuildRows();
            RefreshArt();
            if (!IsBulk && _songs.Count == 1)
                _ = LoadChartFindingsAsync(_songs[0]);
        }

        /// <summary>
        /// Lints the song's chart with its <c>song.ini</c> as drafted (charter-spec C16 item 5), off the UI thread, and lists
        /// what it finds after the song's own findings. A newer load or lint takes its place.
        /// </summary>
        private async Task LoadChartFindingsAsync(EditableSong song)
        {
            _chartLintCts?.Cancel();
            using var cts = new CancellationTokenSource();
            _chartLintCts = cts;
            try
            {
                var pairs = song.Item.Draft.ApplyTo(song.Item.Original);
                var entry = song.Entry;
                var report = await Task.Run(() => SongChartLint.LintAsync(entry, pairs, null, LibraryChartLint.Cache, cts.Token), cts.Token);
                if (_chartLintCts != cts || _songs.Count != 1 || !ReferenceEquals(_songs[0], song))
                    return;

                for (int i = Findings.Count - 1; i >= 0; i--)
                {
                    if (ChrRules.IsChartRule(Findings[i].RuleId))
                        Findings.RemoveAt(i);
                }

                Findings.AddRange(report.Findings.Select(f => new FindingRow(
                    f.Finding.RuleId,
                    LibraryChartLint.Where(f) is { } where ? string.Format(CultureInfo.CurrentCulture, Strings.ChartFindingWhereFormat, f.Finding.Message, where) : f.Finding.Message,
                    f.Finding.Severity == Severity.Error)));
                OnPropertyChanged(nameof(HasFindings));
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                if (_chartLintCts == cts)
                    _chartLintCts = null;
            }
        }

        /// <summary>
        /// Merges the songs' metadata and drafts into rows, updating the existing row view models in
        /// place so that a row being clicked isn't replaced under the pointer.
        /// </summary>
        private void RebuildRows()
        {
            var byKey = Metadata.ToDictionary(r => r.Key, StringComparer.OrdinalIgnoreCase);
            var next = BulkMetadata.BuildRows(Items(), DifficultySlots.Contains)
                .Select(r => byKey.TryGetValue(r.Key, out var existing) ? existing.Update(r) : new MetadataRowViewModel(this, r))
                .ToList();

            for (int i = 0; i < next.Count; i++)
            {
                if (i < Metadata.Count && ReferenceEquals(Metadata[i], next[i]))
                    continue;

                int from = Metadata.IndexOf(next[i]);
                if (from >= 0)
                    Metadata.Move(from, i);
                else
                    Metadata.Insert(i, next[i]);
            }

            while (Metadata.Count > next.Count)
                Metadata.RemoveAt(Metadata.Count - 1);

            RefreshInstruments();
            MetadataNote = _loadNote ?? (Metadata.Count == 0 && Instruments.All(i => i.IsAllUnset) ? Strings.NoMetadata : null);
            OnPropertyChanged(nameof(ShowAutoSetLengthFallback));
            RefreshAvailableKeys();
            RefreshHeadline();
            RefreshValidation();
            RefreshState();
        }

        /// <summary>
        /// Checks each selected song's metadata as drafted, so what's marked is what saving would write: each row
        /// gets its key's failures and hints, and the failures of keys without a row are listed apart.
        /// </summary>
        private void RefreshValidation()
        {
            // Per key, every failing finding with the song it's in, the first hint, and what its quick fixes do.
            var failing = new Dictionary<string, List<(int Song, Finding Finding)>>(StringComparer.OrdinalIgnoreCase);
            var hints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var fixes = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < _songs.Count; i++)
            {
                var song = _songs[i];
                var pairs = song.Item.Draft.ApplyTo(song.Item.Original);
                foreach (var finding in MetadataValidator.Validate(pairs, song.Item.RemainingDuplicateKeys()))
                {
                    string key = finding.Key ?? "";
                    if (QuickFixRegistry.Propose(finding, pairs) is { } fix)
                    {
                        if (!fixes.TryGetValue(key, out var descriptions))
                            fixes[key] = descriptions = new HashSet<string>(StringComparer.Ordinal);
                        descriptions.Add(fix.Description);
                    }

                    if (MetadataValidator.IsFailing(finding))
                    {
                        if (!failing.TryGetValue(key, out var list))
                            failing[key] = list = [];
                        list.Add((i, finding));
                    }
                    else
                    {
                        hints.TryAdd(key, finding.Message);
                    }
                }
            }

            _keysWithQuickFix = new HashSet<string>(fixes.Keys, StringComparer.OrdinalIgnoreCase);

            // A key's fixes are named when they're one; several, e.g. different values across songs, are just "Quick Fix".
            (bool Available, string? Description) QuickFixOf(string key) => fixes.TryGetValue(key, out var descriptions)
                ? (true, descriptions.Count == 1 ? descriptions.First() : null)
                : (false, null);

            foreach (var row in Metadata)
                row.SetValidation(DescribeIssue(failing.GetValueOrDefault(row.Key)), hints.GetValueOrDefault(row.Key), QuickFixOf(row.Key));

            var rowKeys = Metadata.Select(r => r.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var hidden = failing
                .Where(f => !rowKeys.Contains(f.Key))
                .Select(f => new MetadataIssueRow(f.Key, DescribeIssue(f.Value)!, QuickFixOf(f.Key).Description, QuickFixCommandFor(f.Key)))
                .ToList();
            if (!hidden.SequenceEqual(HiddenIssues))
            {
                HiddenIssues.Clear();
                HiddenIssues.AddRange(hidden);
            }

            OnPropertyChanged(nameof(HasHiddenIssues));
            OnPropertyChanged(nameof(FailingFieldCount));
            OnPropertyChanged(nameof(HasValidationIssues));
            RefreshQuickFixKeyCommands();
        }

        /// <summary>A key's failures as one message: each distinct one on its own line, and for several songs, how many fail.</summary>
        private string? DescribeIssue(List<(int Song, Finding Finding)>? failures)
        {
            if (failures is null or [])
                return null;

            string message = string.Join(Environment.NewLine, failures.Select(f => f.Finding.Message).Distinct(StringComparer.Ordinal));
            int songs = failures.Select(f => f.Song).Distinct().Count();
            return IsBulk ? string.Format(CultureInfo.CurrentCulture, Strings.IssueSongCountFormat, message, songs, _songs.Count) : message;
        }

        /// <summary>
        /// Brings the instruments' icons up to date with the drafts and, for one song, with what its
        /// chart has, as the highway read it.
        /// </summary>
        private void RefreshInstruments()
        {
            var items = Items();
            var parts = IsBulk ? null : Highway.ChartParts;
            foreach (var instrument in Instruments)
                instrument.Update(BulkMetadata.RowOf(items, instrument.Key), MetadataDifficulty.IsUnsetIn(items, instrument.Key), parts);
            OnPropertyChanged(nameof(HasHiddenInstruments));
            OnPropertyChanged(nameof(InstrumentsToggleText));
        }

        /// <summary>
        /// Titles the one selected song "name - artist", from its metadata as drafted, and picks out
        /// its charter; without a name, the title is the file or folder name. A bulk selection keeps
        /// its count as the title, and shows no charter.
        /// </summary>
        private void RefreshHeadline()
        {
            if (IsBulk || _songs.Count != 1 || _entries.Count != 1)
            {
                Charter = null;
                return;
            }

            var song = _songs[0];
            var pairs = song.Item.Draft.ApplyTo(song.Item.Original);
            Title = SongHeadline.From(pairs) switch
            {
                { Name: { } name, Artist: { } artist } => string.Format(CultureInfo.CurrentCulture, Strings.TitleFormat, name, artist),
                { Name: { } name } => name,
                _ => _entries[0].DisplayName,
            };
            Charter = SongHeadline.CharterMarkup(pairs);
        }

        /// <summary>
        /// Offers the known keys that aren't a row yet. A removed row's key is offered again, since
        /// adding it back is allowed. The difficulties are left out, as their icons set them.
        /// Apart from that, this is the same test <see cref="CommitAdd"/> uses to reject a key.
        /// </summary>
        private void RefreshAvailableKeys()
        {
            var used = Metadata.Where(r => !r.IsRemoved).Select(r => r.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var available = KnownKeys.All.Where(k => !used.Contains(k.Key) && !DifficultySlots.Contains(k.Key)).ToList();
            if (!available.SequenceEqual(AvailableKeys))
                AvailableKeys = available;
        }

        private static async Task<LoadedSelection> LoadSingleAsync(SongEntry entry, CancellationToken cancellationToken)
        {
            if (entry.Kind == SongEntryKind.SngFile)
            {
                var result = await SngReader.ReadAsync(entry.FullPath, cancellationToken).ConfigureAwait(false);
                ulong size = (ulong)new FileInfo(entry.FullPath).Length;

                return new LoadedSelection(
                    [LoadedSongOf(SongMetadataCheck.FromSng(entry, result)).Song],
                    size,
                    [.. result.Files.Select(f => new DiskFile(f.Name, f.Length))],
                    [.. result.Findings.Select(f => new FindingRow(f.RuleId, f.Message, f.Severity == Severity.Error))],
                    MetadataNote: null);
            }

            var files = await SongFolderListing.ListAsync(entry.FullPath, cancellationToken).ConfigureAwait(false);

            // Only a song.ini directly in the folder counts; one in a subfolder belongs to something else.
            var songIni = files.FirstOrDefault(f => f.RelativePath.Equals(SongIni.FileName, StringComparison.OrdinalIgnoreCase));
            var (song, note) = LoadedSongOf(await SongMetadataCheck.ReadFolderAsync(
                entry, songIni is null ? null : Path.Combine(entry.FullPath, songIni.RelativePath), cancellationToken).ConfigureAwait(false));

            return new LoadedSelection(
                [song],
                (ulong)files.Sum(f => f.Length),
                [.. files.Select(f => new DiskFile(f.RelativePath, (ulong)f.Length))],
                [],
                note);
        }

        /// <summary>Reads only the metadata of each entry. An entry that can't be read turns editing off rather than failing the rest.</summary>
        private static async Task<LoadedSelection> LoadBulkAsync(IReadOnlyList<SongEntry> entries, CancellationToken cancellationToken)
        {
            var songs = new LoadedSong[entries.Count];
            var options = new ParallelOptions { MaxDegreeOfParallelism = BulkLoadParallelism, CancellationToken = cancellationToken };

            await Parallel.ForEachAsync(Enumerable.Range(0, entries.Count), options, async (i, token) =>
            {
                var entry = entries[i];
                try
                {
                    var report = entry.Kind == SongEntryKind.SngFile
                        ? SongMetadataCheck.FromSng(entry, await SngReader.ReadAsync(entry.FullPath, token).ConfigureAwait(false))
                        : await SongMetadataCheck.ReadFolderAsync(entry, SongIni.FindIn(entry.FullPath), token).ConfigureAwait(false);
                    songs[i] = LoadedSongOf(report).Song;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    songs[i] = new LoadedSong(entry, null, null,
                        string.Format(CultureInfo.CurrentCulture, Strings.ItemUnreadableFormat, entry.DisplayName, ex.Message));
                }
            }).ConfigureAwait(false);

            return new LoadedSelection(songs, 0, [], [], MetadataNote: null);
        }

        /// <summary>
        /// A song's metadata as read, with why it can't be edited, if it can't, and why it has none, if it hasn't. A
        /// folder without a song.ini, or whose song.ini has no [song] section, has empty metadata, which saving adds to.
        /// </summary>
        private static (LoadedSong Song, string? Note) LoadedSongOf(SongMetadataReport report)
        {
            var entry = report.Entry;
            return report.Status switch
            {
                SongMetadataStatus.NoSongIni => (new LoadedSong(entry, [], report.SongIniPath, null), Strings.NoSongIni),
                SongMetadataStatus.NoSongSection => (new LoadedSong(entry, [], report.SongIniPath, null), Strings.SongIniNoSongSection),
                SongMetadataStatus.SongIniTooLarge => (
                    new LoadedSong(entry, [], report.SongIniPath, string.Format(CultureInfo.CurrentCulture, Strings.SongIniTooLargeFormat, entry.DisplayName)),
                    Strings.SongIniTooLarge),
                SongMetadataStatus.SngHasErrors => (
                    new LoadedSong(entry, report.Pairs, null, string.Format(CultureInfo.CurrentCulture, Strings.SngHasErrorsFormat, entry.DisplayName)),
                    null),
                _ => (new LoadedSong(entry, report.Pairs, report.SongIniPath, null, report.DuplicateKeys), null),
            };
        }

        /// <summary>
        /// The key of the art <paramref name="song"/> will have once its draft is saved: an image
        /// hash, <see langword="null"/> for none, or, for a staged image that can't be read, a key
        /// no other image shares.
        /// </summary>
        private string? EffectiveArtKey(EditableSong song) => song.Item.Draft.AlbumArt switch
        {
            null => song.DiskArtHash,
            { SourcePath: null } => null,
            { SourcePath: { } source } => _stagedArt.GetValueOrDefault(source)?.Hash ?? "unreadable:" + source,
        };

        /// <summary>Shows the art the selected songs share, or why there's none to show, and marks the files a staged change touches.</summary>
        private void RefreshArt()
        {
            var keys = _songs.Select(EffectiveArtKey).Distinct(StringComparer.Ordinal).ToList();
            if (keys.Count > 1)
            {
                ArtImage = null;
                ArtNote = Strings.AlbumArtDiffers;
            }
            else if (keys.Count == 1 && keys[0] is { } key)
            {
                ArtImage = _artImages.GetValueOrDefault(key);
                ArtNote = ArtImage is null ? Strings.AlbumArtUnreadable : null;
            }
            else
            {
                ArtImage = null;
                ArtNote = _songs.Any(s => s.Item.Draft.AlbumArt is { IsRemoval: true }) ? Strings.AlbumArtWillBeRemoved : Strings.NoAlbumArt;
            }

            (ArtFileName, ArtFileSize) = keys is [not null] ? SharedArtFile() : (null, null);
            RefreshPlayback();
            SyncTimingOffsets();
            RebuildFiles();
            OnPropertyChanged(nameof(IsArtChanged));
            OnPropertyChanged(nameof(ArtMarker));
            OnPropertyChanged(nameof(IsBackgroundChanged));
            RefreshCommands();
        }

        /// <summary>
        /// The stored name and size of the album art every selected song shares: a staged image they
        /// all have, or the one selected song's own file. Songs sharing stored art aren't listed, so
        /// in a bulk selection only a staged image is named.
        /// </summary>
        private (string? Name, string? Size) SharedArtFile()
        {
            var sources = _songs.Select(s => s.Item.Draft.AlbumArt?.SourcePath).Distinct(StringComparer.Ordinal).ToList();
            if (sources is [{ } source])
            {
                string? size = _stagedArt.TryGetValue(source, out var staged) && staged.Error is null ? ByteSizeFormatter.Format((ulong)staged.Length) : null;
                return (AlbumArt.StoredNameFor(source), size);
            }

            if (sources is [null] && _songs.Count == 1 && AlbumArt.Pick(_diskFiles, f => f.Path) is { } file)
                return (file.Path, ByteSizeFormatter.Format(file.Length));

            return (null, null);
        }

        /// <summary><paramref name="song"/> as the background tile needs it, with its video timeline as drafted.</summary>
        private static BackgroundSong BackgroundSongOf(EditableSong song) =>
            new(song.Entry, song.Item.Draft, song.Backgrounds, VideoTimeline.From(song.Item.Draft.ApplyTo(song.Item.Original)));

        /// <summary>The one selected song as the note highway needs it, or <see langword="null"/> when there isn't one.</summary>
        private HighwaySong? HighwaySongOf() =>
            _songs is [var song] && !IsBulk
                ? new HighwaySong(song.Entry, song.Item.Draft, song.Item.Draft.ApplyTo(song.Item.Original), [.. _diskFiles.Select(f => f.Path)],
                    _previewShifts?.Chart, _previewShifts?.Vocals ?? 0)
                : null;

        /// <summary>Whether the one selected song will have a vocals audio track once its draft is saved.</summary>
        private bool HasVocalStem() =>
            _songs is [var song] && !IsBulk
            && SongTracks.Resolve(_diskFiles.Select(f => f.Path), song.Item.Draft).Any(t => LibraryContentFilters.StemGroupOf(t.Stem) == LibraryStems.Vocals);

        /// <summary>
        /// The one selected song's chart files and other files, with the ones a staged change removes
        /// or replaces struck through and the ones it adds marked. The album art, backgrounds and
        /// tracks are shown in their own places, and a folder's song.ini is edited as metadata, so
        /// none of them are other files; a second album-art or background file, not the one used, is.
        /// </summary>
        private void RebuildFiles()
        {
            var song = _songs.Count == 1 ? _songs[0] : null;
            var draft = song?.Item.Draft;
            var change = draft?.AlbumArt;
            bool isFolder = song is not null && song.Entry.Kind != SongEntryKind.SngFile;
            var entry = !IsBulk && _entries.Count == 1 ? _entries[0] : null;

            bool IsRemoved(string path) =>
                (change is not null && AlbumArt.IsAlbumArtName(path))
                || (draft is not null && SongBackgrounds.KindOfName(path) is { } kind && draft.GetBackground(kind) is not null)
                || (draft is not null && (draft.IsFileRemoved(path) || draft.IsFileAdded(path)));

            // The files the art and background tiles show; any others of their names are other files.
            string? art = AlbumArt.Pick(_diskFiles, f => f.Path)?.Path;
            var backgrounds = SongBackgrounds.Kinds
                .Select(k => SongBackgrounds.Pick(_diskFiles, f => f.Path, k)?.Path)
                .OfType<string>()
                .ToHashSet(StringComparer.Ordinal);
            bool IsShownElsewhere(string path) =>
                AudioStems.StemOf(path) is not null
                || ChartFiles.IsChartName(path)
                || path == art
                || backgrounds.Contains(path)
                || (isFolder && path.Equals(SongIni.FileName, StringComparison.OrdinalIgnoreCase));

            var charts = _diskFiles
                .Where(f => ChartFiles.IsChartName(f.Path))
                .Select(f => new ChartRow(f.Path, ByteSizeFormatter.Format(f.Length), IsRemoved: IsRemoved(f.Path), DownloadCommand: DownloadCommandFor(entry, f.Path)))
                .ToList();
            if (draft is not null)
            {
                charts.InsertRange(0, draft.AddedFiles
                    .Where(f => ChartFiles.IsChartName(f.StoredName))
                    .Select(f => new ChartRow(f.StoredName, ByteSizeFormatter.Format((ulong)f.Length), Marker: "+", DownloadCommand: DownloadCommandFor(entry, f.StoredName, f.SourcePath))));
            }

            // What's kept first, then what's going.
            charts = [.. charts.OrderBy(c => c.IsRemoved)];
            if (!charts.SequenceEqual(Charts))
            {
                Charts.Clear();
                Charts.AddRange(charts);
            }

            var rows = _diskFiles
                .Where(f => !IsShownElsewhere(f.Path))
                .Select(f =>
                {
                    bool removed = IsRemoved(f.Path);
                    return new FileRow(f.Path, ByteSizeFormatter.Format(f.Length),
                        IsRemoved: removed, CanRemove: draft is not null && !removed, CanRevert: draft is not null && removed,
                        DownloadCommand: DownloadCommandFor(entry, f.Path));
                })
                .ToList();

            // Added tracks and charts are shown with the tracks and the chart; staged art and backgrounds on their tiles.
            if (draft is not null)
            {
                rows.AddRange(draft.AddedFiles
                    .Where(f => AudioStems.StemOf(f.StoredName) is null && !ChartFiles.IsChartName(f.StoredName))
                    .Select(f => new FileRow(f.StoredName, ByteSizeFormatter.Format((ulong)f.Length), Marker: "+", CanRemove: true,
                        DownloadCommand: DownloadCommandFor(entry, f.StoredName, f.SourcePath))));
            }

            if (!rows.SequenceEqual(Files))
            {
                Files.Clear();
                Files.AddRange(rows);
            }

            OnPropertyChanged(nameof(ShowNoOtherFiles));
            OnPropertyChanged(nameof(OtherFilesHeader));
            OnPropertyChanged(nameof(ShowChartMissing));
            OnPropertyChanged(nameof(ChartMissingNote));
            OnPropertyChanged(nameof(IsChartChanged));
            RefreshTracks();
        }

        /// <summary>
        /// Gives the player the one selected song's tracks once its draft is applied, each with its
        /// size; lists the tracks the draft removes without replacing; and labels the Add track menu to match.
        /// </summary>
        private void RefreshTracks()
        {
            if (IsBulk || _entries.Count != 1)
            {
                Player.Unload();
                ForgetSongLength();
                RemovedTracks.Clear();
                OnPropertyChanged(nameof(HasRemovedTracks));
                AddTrackOptions = [];
                return;
            }

            var song = _songs.Count == 1 ? _songs[0] : null;
            var draft = song?.Item.Draft;
            var tracks = SongTracks.Resolve(_diskFiles.Select(f => f.Path), draft);
            Player.Load(_entries[0], tracks, song is null ? null : PreviewStartOf(song));
            RefreshSongLength();
            foreach (var row in Player.Tracks)
            {
                row.Size = TrackSize(row.Track, draft);
                row.DownloadCommand = DownloadCommandFor(_entries[0], row.Track.StoredName, row.Track.StagedSourcePath);
            }

            // A stem with a track staged to replace it isn't missing a track, so what it replaces isn't listed.
            var stagedStems = draft?.AddedFiles.Select(f => AudioStems.StemOf(f.StoredName)).OfType<string>().ToHashSet(StringComparer.Ordinal) ?? [];
            var removed = draft is null
                ? []
                : _diskFiles
                    .Select(f => (File: f, Stem: AudioStems.StemOf(f.Path)))
                    .Where(x => x.Stem is { } stem && draft.IsFileRemoved(x.File.Path) && !stagedStems.Contains(stem))
                    .OrderBy(x => AudioStems.OrderOf(x.Stem!))
                    .ThenBy(x => x.File.Path, StringComparer.OrdinalIgnoreCase)
                    .Select(x => new RemovedTrackRow(x.File.Path, ByteSizeFormatter.Format(x.File.Length)))
                    .ToList();
            if (!removed.SequenceEqual(RemovedTracks))
            {
                RemovedTracks.Clear();
                RemovedTracks.AddRange(removed);
            }

            OnPropertyChanged(nameof(HasRemovedTracks));

            var stems = tracks.Select(t => t.Stem).ToHashSet(StringComparer.Ordinal);
            AddTrackOptions = [.. AudioStems.Stems
                .Select(stem => new StemOption(
                    stem,
                    stems.Contains(stem) ? string.Format(CultureInfo.CurrentCulture, Strings.ReplaceTrackFormat, stem) : stem,
                    _addTrackCommands[stem]))];
        }

        /// <summary><paramref name="track"/>'s file size, formatted: the file to be added, or the one stored.</summary>
        private string? TrackSize(SongTrack track, MetadataDraft? draft)
        {
            if (track.IsStaged)
            {
                foreach (var added in draft?.AddedFiles ?? [])
                {
                    if (added.StoredName.Equals(track.StoredName, StringComparison.OrdinalIgnoreCase))
                        return ByteSizeFormatter.Format((ulong)added.Length);
                }

                return null;
            }

            return _diskFiles.FirstOrDefault(f => f.Path == track.StoredName) is { } file ? ByteSizeFormatter.Format(file.Length) : null;
        }

        /// <summary>Where the song's <c>preview_start_time</c>, drafted or stored, starts the preview.</summary>
        private static TimeSpan? PreviewStartOf(EditableSong song) =>
            PreviewStart.From(song.Item.Draft.ApplyTo(song.Item.Original));

        /// <summary>
        /// Gives the player how long the one selected song plays for in a game, which can run on past its audio: its
        /// <c>song_length</c> as drafted, or else what <see cref="SongLengthEstimator"/> works out, as Auto Set does. The
        /// estimate reads the song's files, so it's made in the background once the drafts are still, and only again when
        /// something it reads has changed.
        /// </summary>
        private void RefreshSongLength()
        {
            if (IsBulk || IsSaving || _entries.Count != 1 || _songs is not [var song])
            {
                ForgetSongLength();
                return;
            }

            var pairs = song.Item.Draft.ApplyTo(song.Item.Original);
            if (SongHeadline.From(pairs).LengthMs is { } ms)
            {
                // An estimate still running would be of no use; one already made is kept, should the row go again.
                if (_songLengthCts is not null)
                {
                    CancelSongLength();
                    _songLengthKey = null;
                }

                Player.SongLengthSeconds = ms / 1000.0;
                return;
            }

            string key = SongLengthKeyOf(song, pairs);
            if (key == _songLengthKey)
            {
                // The estimate of this is made, or on its way.
                if (_songLengthCts is null)
                    Player.SongLengthSeconds = _estimatedSongLength;
                return;
            }

            // Until the new estimate is in, the timeline keeps the length it has.
            CancelSongLength();
            _songLengthKey = key;
            _songLengthTask = EstimateSongLengthAsync(song.Entry, song.Item.Draft.Clone(), pairs, key);
        }

        /// <summary>Works out the song's length off the UI thread, and gives it to the player unless something newer is wanted by then.</summary>
        private async Task EstimateSongLengthAsync(SongEntry entry, MetadataDraft draft, IReadOnlyList<KeyValuePair<string, string>> pairs, string key)
        {
            using var cts = new CancellationTokenSource();
            _songLengthCts = cts;
            long? ms = null;
            try
            {
                await Task.Delay(SongLengthDelay, cts.Token);
                ms = await Task.Run(() => SongLengthEstimator.Estimate(entry, draft, pairs, _videoProbe, cts.Token).SongLengthMs, cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The song can't be listed: the timeline is the audio's length.
            }
            finally
            {
                if (ReferenceEquals(_songLengthCts, cts))
                    _songLengthCts = null;
            }

            if (cts.IsCancellationRequested || key != _songLengthKey)
                return;

            _estimatedSongLength = ms is { } length ? length / 1000.0 : 0;
            Player.SongLengthSeconds = _estimatedSongLength;
        }

        /// <summary>What the song's length estimate reads, as one string: when it's unchanged, so is the estimate.</summary>
        private string SongLengthKeyOf(EditableSong song, IReadOnlyList<KeyValuePair<string, string>> pairs)
        {
            var draft = song.Item.Draft;
            var stored = _diskFiles.Select(f => f.Path).ToList();
            var parts = new List<string?> { song.Entry.FullPath };
            parts.AddRange(SongTracks.Resolve(stored, draft).Select(t => $"{t.StoredName}<{t.StagedSourcePath}"));
            parts.Add(DraftChart.Of(draft, stored)?.ToString());
            parts.Add(draft.BackgroundVideo is { } video ? $"video<{video.SourcePath}" : null);
            parts.AddRange(draft.RemovedFiles.Order(StringComparer.Ordinal).Select(f => $"-{f}"));
            parts.AddRange(draft.ChartOffsets.Select(o => $"{o.Key}={o.Value}").Order(StringComparer.Ordinal));
            parts.AddRange(SongLengthKeys.Select(k => $"{k}={pairs.FirstOrDefault(p => p.Key.Equals(k, StringComparison.OrdinalIgnoreCase)).Value}"));
            return string.Join('\n', parts);
        }

        private void CancelSongLength()
        {
            _songLengthCts?.Cancel();
            _songLengthCts = null;
        }

        /// <summary>Stops any length estimate and takes the length from the player, as when the song shown changes.</summary>
        private void ForgetSongLength()
        {
            CancelSongLength();
            _songLengthKey = null;
            _estimatedSongLength = 0;
            Player.SongLengthSeconds = 0;
        }

        private void AddImage(string hash, Bitmap bitmap)
        {
            if (!_artImages.TryAdd(hash, bitmap))
                DisposeLater([bitmap]);
        }

        /// <summary>Stops showing the current art and lets go of every decoded image.</summary>
        private void ReleaseArt()
        {
            ArtImage = null;
            ArtNote = null;
            DisposeLater(_artImages.Values);
            _artImages = [];
            _stagedArt = new(StringComparer.Ordinal);
        }

        /// <summary>Disposes <paramref name="bitmaps"/> once the UI has rendered without them.</summary>
        private static void DisposeLater(IEnumerable<Bitmap> bitmaps)
        {
            var old = bitmaps.ToArray();
            if (old.Length == 0)
                return;

            Dispatcher.UIThread.Post(() =>
            {
                foreach (var bitmap in old)
                    bitmap.Dispose();
            }, DispatcherPriority.Background);
        }

        /// <summary>
        /// Hashes each readable song's album art, decodes the first one's (the only one that can be
        /// what every song shares), and reads the staged replacement images.
        /// </summary>
        private static async Task<LoadedArt> LoadArtAsync(IReadOnlyList<LoadedSong> songs, IReadOnlyList<string> stagedSources, CancellationToken cancellationToken)
        {
            var hashes = new string?[songs.Count];
            var images = new Dictionary<string, Bitmap>(StringComparer.Ordinal);
            var staged = new Dictionary<string, StagedArt>(StringComparer.Ordinal);
            int first = songs.ToList().FindIndex(s => s.Pairs is not null);
            var options = new ParallelOptions { MaxDegreeOfParallelism = BulkLoadParallelism, CancellationToken = cancellationToken };

            try
            {
                await Parallel.ForEachAsync(Enumerable.Range(0, songs.Count), options, async (i, token) =>
                {
                    if (songs[i].Pairs is null)
                        return;

                    byte[]? bytes;
                    try
                    {
                        bytes = await AlbumArt.ReadAsync(songs[i].Entry, cancellationToken: token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        return;
                    }

                    if (bytes is null)
                        return;

                    hashes[i] = Hash(bytes);
                    if (i == first && Decode(bytes) is { } bitmap)
                    {
                        lock (images)
                            images[hashes[i]!] = bitmap;
                    }
                }).ConfigureAwait(false);

                foreach (string source in stagedSources)
                {
                    var (art, bitmap) = await LoadStagedAsync(source, cancellationToken).ConfigureAwait(false);
                    staged[source] = art;
                    if (bitmap is not null && !images.TryAdd(art.Hash!, bitmap))
                        bitmap.Dispose();
                }
            }
            catch
            {
                foreach (var bitmap in images.Values)
                    bitmap.Dispose();
                throw;
            }

            return new LoadedArt(hashes, images, staged);
        }

        /// <summary>Reads, hashes and decodes an image chosen as album art.</summary>
        /// <returns>The image, with its decoded bitmap; or, when it can't be used, why not and no bitmap.</returns>
        private static async Task<(StagedArt Art, Bitmap? Bitmap)> LoadStagedAsync(string path, CancellationToken cancellationToken)
        {
            byte[] bytes;
            try
            {
                long length = new FileInfo(path).Length;
                if (length > AlbumArt.DefaultMaxBytes)
                {
                    string limit = ByteSizeFormatter.Format((ulong)AlbumArt.DefaultMaxBytes);
                    return (new StagedArt(null, length, string.Format(CultureInfo.CurrentCulture, SharedStrings.ArtTooLargeFormat, limit)), null);
                }

                bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return (new StagedArt(null, 0, string.Format(CultureInfo.CurrentCulture, SharedStrings.ArtUnreadableFormat, ex.Message)), null);
            }

            return Decode(bytes) is { } bitmap
                ? (new StagedArt(Hash(bytes), bytes.Length, null), bitmap)
                : (new StagedArt(null, bytes.Length, SharedStrings.ArtNotAnImage), null);
        }

        private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

        /// <summary>The image decoded at tile size, or <see langword="null"/> when it can't be decoded.</summary>
        private static Bitmap? Decode(byte[] bytes)
        {
            try
            {
                using var stream = new MemoryStream(bytes, writable: false);
                return Bitmap.DecodeToWidth(stream, ArtDecodeWidth, BitmapInterpolationMode.HighQuality);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Corrupt or unsupported images surface as assorted decoder exceptions.
                return null;
            }
        }

        /// <summary>Raises change notifications for every derived display state.</summary>
        private void RefreshState()
        {
            OnPropertyChanged(nameof(ShowPlaceholder));
            OnPropertyChanged(nameof(ShowInstruments));
            OnPropertyChanged(nameof(HasError));
            OnPropertyChanged(nameof(ShowDetails));
            OnPropertyChanged(nameof(HasFindings));
            OnPropertyChanged(nameof(HasMetadataNote));
            OnPropertyChanged(nameof(HasReadOnlyNote));
            OnPropertyChanged(nameof(ShowSongFiles));
            OnPropertyChanged(nameof(ShowVideoOffset));
            OnPropertyChanged(nameof(ShowNoOtherFiles));
            OnPropertyChanged(nameof(ShowChartMissing));
            OnPropertyChanged(nameof(IsEditable));
            OnPropertyChanged(nameof(CanEdit));
            OnPropertyChanged(nameof(ShowAddRow));
            OnPropertyChanged(nameof(ShowAutoSetLengthFallback));
            OnPropertyChanged(nameof(HasDrafts));
            OnPropertyChanged(nameof(MetadataHeader));
            OnPropertyChanged(nameof(OtherFilesHeader));
            OnPropertyChanged(nameof(IsArtChanged));
            OnPropertyChanged(nameof(ArtMarker));
            OnPropertyChanged(nameof(IsBackgroundChanged));

            Highway.CanEditOffsets = CanEditOffsets();

            // In a preview too, as the highway's: the merge takes the lyrics' offset.
            Lyrics.CanEditOffset = CanEditOffsets();
            RefreshTimingOffsetsEditable();
            RefreshCommands();
        }

        private void RefreshCommands()
        {
            SaveCommand.NotifyCanExecuteChanged();
            DiscardCommand.NotifyCanExecuteChanged();
            BeginAddCommand.NotifyCanExecuteChanged();
            AutoSetSongLengthCommand.NotifyCanExecuteChanged();
            ReplaceArtCommand.NotifyCanExecuteChanged();
            RemoveArtCommand.NotifyCanExecuteChanged();
            RevertArtCommand.NotifyCanExecuteChanged();
            ReplaceBackgroundCommand.NotifyCanExecuteChanged();
            RemoveBackgroundCommand.NotifyCanExecuteChanged();
            RevertBackgroundCommand.NotifyCanExecuteChanged();
            AddFilesCommand.NotifyCanExecuteChanged();
            RemoveFileCommand.NotifyCanExecuteChanged();
            RevertFileCommand.NotifyCanExecuteChanged();
            RemoveTrackCommand.NotifyCanExecuteChanged();
            ReplaceTrackCommand.NotifyCanExecuteChanged();
            UndoRemoveTrackCommand.NotifyCanExecuteChanged();
            ChooseChartCommand.NotifyCanExecuteChanged();
            RemoveChartCommand.NotifyCanExecuteChanged();
            RevertChartCommand.NotifyCanExecuteChanged();
            DownloadArtCommand.NotifyCanExecuteChanged();
            DownloadBackgroundCommand.NotifyCanExecuteChanged();
            RefreshQuickFixKeyCommands();
            RefreshLyricsCommands();
            foreach (var command in _addTrackCommands.Values)
                command.NotifyCanExecuteChanged();
            foreach (var row in Metadata)
                row.RefreshCommands();
            foreach (var instrument in Instruments)
                instrument.RefreshCommands();
        }

        private void RefreshLyricsCommands()
        {
            ReplaceLyricsCommand.NotifyCanExecuteChanged();
            RemoveLyricsCommand.NotifyCanExecuteChanged();
            RevertLyricsCommand.NotifyCanExecuteChanged();
            ExportLyricsCommand.NotifyCanExecuteChanged();
        }

        /// <summary>The right-click menu's name for <see cref="RevealCommand"/>, after the platform's file manager.</summary>
        public static string RevealLabel => FileRevealService.RevealLabel;

        private bool CanReveal() => _reveal is not null && _entries.Count > 0;

        /// <summary>Shows the selected songs' files and folders in the file manager.</summary>
        [RelayCommand(CanExecute = nameof(CanReveal))]
        private void Reveal() => _reveal?.Reveal([.. _entries.Select(e => e.FullPath)]);

        private bool CanDownloadArt() => _downloads is not null && ArtSource() is not null;

        /// <summary>Saves a copy of the album art shown: the staged image, or the song's own.</summary>
        [RelayCommand(CanExecute = nameof(CanDownloadArt))]
        private Task DownloadArtAsync() => ArtSource() is { } source ? DownloadAsync(source) : Task.CompletedTask;

        private bool CanDownloadBackground() => _downloads is not null && !IsBulk && Background.ShownFile is not null;

        /// <summary>Saves a copy of the background shown, the image or the video, staged or the song's own.</summary>
        [RelayCommand(CanExecute = nameof(CanDownloadBackground))]
        private Task DownloadBackgroundAsync() => Background.ShownFile is { } source ? DownloadAsync(source) : Task.CompletedTask;

        private bool CanExportLyrics() => _downloads is not null && !IsBulk && Lyrics.CanExport;

        /// <summary>Saves the lyrics drawn, with the draft's change and offset, as a <c>notes.mid</c> of their own.</summary>
        [RelayCommand(CanExecute = nameof(CanExportLyrics))]
        private Task ExportLyricsAsync()
        {
            if (_downloads is null || Lyrics.ExportSource() is not { } export)
                return Task.CompletedTask;

            // The lyrics were read when shown: this reads no file of the song.
            return _downloads.SaveFileAsync(null, DownloadPrefix(), ChartFiles.MidiName, _ => Task.Run(() => (Stream)new MemoryStream(export())));
        }

        /// <summary>
        /// The album art shown, as a file to save: a staged image, or the one selected song's own.
        /// <see langword="null"/> for none, or for several songs.
        /// </summary>
        private SongFileSource? ArtSource()
        {
            if (IsBulk || _songs is not [var song] || ArtImage is null)
                return null;

            if (song.Item.Draft.AlbumArt is { } change)
            {
                return change.SourcePath is { } source && AlbumArt.StoredNameFor(source) is { } name
                    ? new SongFileSource(song.Entry, name, source)
                    : null;
            }

            return AlbumArt.Pick(_diskFiles, f => f.Path) is { } file ? new SongFileSource(song.Entry, file.Path) : null;
        }

        /// <summary>
        /// The command that saves the file <paramref name="storedName"/> of <paramref name="entry"/>, the
        /// same one each time; none without a song, or for a panel that only previews.
        /// </summary>
        private IAsyncRelayCommand? DownloadCommandFor(SongEntry? entry, string storedName, string? stagedPath = null)
        {
            if (_downloads is null || entry is null)
                return null;

            var source = new SongFileSource(entry, storedName, stagedPath);
            if (!_downloadCommands.TryGetValue(source, out var command))
                _downloadCommands[source] = command = new AsyncRelayCommand(() => DownloadAsync(source));
            return command;
        }

        private Task DownloadAsync(SongFileSource source) =>
            _downloads?.SaveFileAsync(source.Entry, DownloadPrefix(), source.StoredName, _ => Task.Run(source.Open)) ?? Task.CompletedTask;

        /// <summary>What names the one selected song's saved files: <c>Artist - Title</c> as drafted, or its file or folder name.</summary>
        private string DownloadPrefix()
        {
            if (_songs is [var song])
            {
                var headline = SongHeadline.From(song.Item.Draft.ApplyTo(song.Item.Original));
                return DownloadNames.SongPrefix(headline.Artist, headline.Name, song.Entry.DisplayName);
            }

            return _entries is [var entry] ? DownloadNames.SafeName(entry.DisplayName) : string.Empty;
        }

        /// <summary>A selected song whose metadata was read, with its draft.</summary>
        /// <param name="SongIniPath">For a folder, the song.ini to edit (which may not exist yet).</param>
        /// <param name="DiskArtHash">The hash of its album art as stored, or <see langword="null"/> when it has none that could be read.</param>
        /// <param name="Item">Its metadata as stored, with the keys its song.ini repeats, and its draft.</param>
        /// <param name="Backgrounds">Its background image and video as stored.</param>
        private sealed record EditableSong(SongEntry Entry, MetadataItem Item, string? SongIniPath, string? DiskArtHash, StoredBackgrounds Backgrounds);

        /// <summary>A file of the one selected song, as stored.</summary>
        private sealed record DiskFile(string Path, ulong Length);

        /// <summary>An image chosen as album art.</summary>
        /// <param name="Hash">Its hash, or <see langword="null"/> when it can't be used.</param>
        /// <param name="Error">Why it can't be used, if it can't.</param>
        private sealed record StagedArt(string? Hash, long Length, string? Error);

        /// <summary>The selection's album art as loaded off the UI thread.</summary>
        /// <param name="DiskHashes">Each loaded song's art hash, by index.</param>
        /// <param name="Images">Decoded images by hash.</param>
        /// <param name="Staged">Staged replacement images by source path.</param>
        private sealed record LoadedArt(IReadOnlyList<string?> DiskHashes, Dictionary<string, Bitmap> Images, Dictionary<string, StagedArt> Staged);

        /// <summary>One song's metadata as loaded off the UI thread.</summary>
        /// <param name="Pairs">The stored pairs, or <see langword="null"/> when the song couldn't be read.</param>
        /// <param name="ReadOnlyReason">Why the song's metadata can't be edited, if it can't.</param>
        /// <param name="DuplicateKeys">The keys a song.ini repeats, which reading it merged.</param>
        private sealed record LoadedSong(
            SongEntry Entry,
            IReadOnlyList<KeyValuePair<string, string>>? Pairs,
            string? SongIniPath,
            string? ReadOnlyReason,
            IReadOnlyList<string>? DuplicateKeys = null);

        /// <summary>Everything loaded off the UI thread, applied in one go. Files and findings are only loaded for one song.</summary>
        private sealed record LoadedSelection(
            IReadOnlyList<LoadedSong> Songs,
            ulong TotalSize,
            IReadOnlyList<DiskFile> Files,
            IReadOnlyList<FindingRow> Findings,
            string? MetadataNote)
        {
            /// <summary>Filled in after the metadata by <see cref="LoadArtAsync"/>.</summary>
            public LoadedArt Art { get; init; } = new([], [], []);

            /// <summary>Filled in after the metadata by <see cref="BackgroundViewModel.LoadAsync"/>.</summary>
            public LoadedBackgrounds Backgrounds { get; init; } = LoadedBackgrounds.Empty;
        }
    }
}
