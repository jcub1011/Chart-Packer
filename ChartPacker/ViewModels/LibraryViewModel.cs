using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Collections;
using Avalonia.Controls.Selection;
using Avalonia.Threading;
using ChartPacker.Resources;
using ChartPacker.Services;
using ChartShared.Core.Dedup;
using ChartShared.Core.Jobs;
using ChartShared.Core.Launching;
using ChartShared.Core.Metadata;
using ChartShared.Core.Scanning;
using ChartShared.UI.Services;
using ChartShared.UI.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChartPacker.ViewModels
{
    /// <summary>An overwrite policy as offered in the Pack menu's "If the output exists".</summary>
    public sealed partial class OverwriteOption(OverwritePolicy value, string label) : ObservableObject
    {
        public OverwritePolicy Value { get; } = value;

        public string Label { get; } = label;

        /// <summary>It's the policy in use (<see cref="LibraryViewModel.SelectedOverwriteOption"/>): checked in the menu.</summary>
        [ObservableProperty]
        public partial bool IsSelected { get; set; }
    }

    /// <summary>The left panel: a picked folder, the song sources found under it, and Pack/Unpack/Merge/Validate/Deduplicate.</summary>
    /// <remarks>
    /// Scanning only happens on an explicit <see cref="RescanCommand"/>, or as the first step of
    /// <see cref="DeduplicateCommand"/>. Picking a folder, changing
    /// the depth or launching the app never scans; the list is flagged as out of date instead.
    /// The same goes for a pack or unpack job, which changes what's on disk.
    /// <para>
    /// <see cref="Entries"/> is what the list shows: the scanned songs that pass <see cref="Filter"/>'s
    /// search and filters, in its sort order.
    /// </para>
    /// </remarks>
    public partial class LibraryViewModel : ViewModelBase
    {
        // Songs a scan has read are put in the list a chunk at a time, for at most this long per UI
        // turn, so drawing and input keep up however fast they arrive.
        private const int ListChunkSize = 64;
        private static readonly TimeSpan ListBudget = TimeSpan.FromMilliseconds(8);

        // Edits change a song's facts a keystroke at a time; re-sorting for each would redo the list over and over.
        private static readonly TimeSpan MetadataApplyDelay = TimeSpan.FromMilliseconds(250);

        private static readonly IComparer<LibraryItemViewModel> ByPosition =
            Comparer<LibraryItemViewModel>.Create((a, b) => a.Position.CompareTo(b.Position));

        private readonly IFolderPickerService _folderPicker;
        private readonly SettingsService _settings;
        private readonly MetadataDraftStore _drafts;
        private readonly IMergeWindowService _mergeWindow;
        private readonly IDedupWindowService _dedupWindow;
        private readonly IValidatorWindowService _validatorWindow;
        private readonly IFileRevealService _reveal;
        private readonly IDownloadService _downloads;
        private readonly IChartBuilderLauncher _chartBuilder;
        private readonly NoticeViewModel _notice;
        private readonly Dictionary<string, LibraryItemViewModel> _itemsByPath = new(StringComparer.Ordinal);

        // The songs opened in Chart Builder, and what each was on disk when last read, to re-read them once it has
        // changed them (charter-spec §4.8). A background poller re-reads them a save or two after Chart Builder
        // writes, so the library shows the latest version without waiting for the window to be activated.
        private readonly Dictionary<string, (SongEntry Entry, EntryStamp? Stamp)> _editedSongs = new(StringComparer.Ordinal);

        // A changed stamp seen once by the poller but not yet re-read, waiting for a second sighting so a
        // folder save caught between its files isn't read half-written. The activation path re-reads at once.
        private readonly Dictionary<string, EntryStamp?> _pendingEditedStamps = new(StringComparer.Ordinal);

        // How often songs opened in Chart Builder are checked for saves while this app runs.
        private static readonly TimeSpan EditedSongsPollInterval = TimeSpan.FromSeconds(2);

        private readonly DispatcherTimer _editedSongsTimer;

        // A poller tick is already re-reading; activation may call in at the same time.
        private bool _refreshingEditedSongs;

        // Every scanned song, in scan order (by Position), whether or not it passes the filters.
        private readonly List<LibraryItemViewModel> _allItems = [];

        // Re-applies the query once metadata has stopped arriving for a moment.
        private readonly DispatcherTimer _applyTimer;

        // An ApplyQuery is posted to run as soon as the UI is idle.
        private bool _applyPosted;

        // Entries is being rebuilt; the selection is put back together afterwards.
        private bool _syncingSelection;
        private CancellationTokenSource? _scanCts;

        // Where the running scan's read songs wait to be listed; null when no scan is running.
        private ScanSink? _scanSink;

        private readonly AlbumArtLoader _albumArt;

        // Per-item background loads (metadata re-reads and album art) for the current list.
        private CancellationTokenSource _itemsCts = new();

        // What the list currently reflects: the folder and depth of the most recent scan.
        private string? _scannedFolder;
        private int _scannedDepth;

        // A job has written to disk since the last scan.
        private bool _outputsChanged;

        // The item whose art shows a stop button, because it's the song being played.
        private LibraryItemViewModel? _previewingItem;

        /// <param name="player">Plays a song from the list's art buttons; the detail panel shows the same one.</param>
        /// <param name="mergeWindow">Shows the Merge window for the selected songs.</param>
        /// <param name="dedupWindow">Shows the Deduplicate window for the whole library.</param>
        /// <param name="validatorWindow">Shows the Validator window for the selected songs.</param>
        /// <param name="reveal">Shows the selected songs in the file manager.</param>
        /// <param name="downloads">Saves copies of the selected songs as song folders.</param>
        /// <param name="chartBuilder">Starts Chart Builder to edit the selected song's chart.</param>
        /// <param name="notice">Says why Chart Builder couldn't be started.</param>
        public LibraryViewModel(
            IFolderPickerService folderPicker,
            SettingsService settings,
            MetadataDraftStore drafts,
            JobViewModel job,
            PlayerViewModel player,
            IMergeWindowService mergeWindow,
            IDedupWindowService dedupWindow,
            IValidatorWindowService validatorWindow,
            IFileRevealService reveal,
            IDownloadService downloads,
            IChartBuilderLauncher chartBuilder,
            NoticeViewModel notice)
        {
            _folderPicker = folderPicker;
            _settings = settings;
            _drafts = drafts;
            _mergeWindow = mergeWindow;
            _dedupWindow = dedupWindow;
            _validatorWindow = validatorWindow;
            _reveal = reveal;
            _downloads = downloads;
            _chartBuilder = chartBuilder;
            _notice = notice;
            Player = player;
            Player.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(PlayerViewModel.ActiveEntry))
                    RefreshPreviewing();
            };
            _albumArt = new AlbumArtLoader(path => _drafts.HasDrafts(path) ? _drafts.Get(path).AlbumArt : null);
            Job = job;
            ScanDepth = settings.Settings.ScanMaxDepth;
            FolderPath = settings.Settings.LastFolder;

            OverwriteOptions =
            [
                new(OverwritePolicy.Skip, Strings.OverwriteSkip),
                new(OverwritePolicy.Overwrite, Strings.OverwriteOverwrite),
                new(OverwritePolicy.Rename, Strings.OverwriteRename),
            ];
            SelectedOverwriteOption = OverwriteOptions.First(o => o.Value == settings.Settings.OverwritePolicy);
            FlatPackOutput = settings.Settings.FlatPackOutput;

            Filter = new LibraryFilterViewModel(settings);
            Filter.QueryChanged += () => ScheduleApply(immediate: true);
            _applyTimer = new DispatcherTimer(MetadataApplyDelay, DispatcherPriority.Background, (_, _) => ApplyQuery());
            _applyTimer.Stop();

            _editedSongsTimer = new DispatcherTimer(EditedSongsPollInterval, DispatcherPriority.Background, (_, _) => _ = PollEditedSongsAsync());
            _editedSongsTimer.Stop();

            Selection = new SelectionModel<LibraryItemViewModel> { SingleSelect = false, Source = Entries };
            Selection.PropertyChanged += OnSelectionPropertyChanged;

            _drafts.DraftsChanged += (path, change) =>
            {
                if (!_itemsByPath.TryGetValue(path, out var item))
                    return;

                bool hadDrafts = item.HasDrafts;
                item.HasDrafts = _drafts.HasDrafts(path);

                // Saved or discarded: what's on disk may no longer match what was read, and showing
                // the old read first would flash the pre-save values.
                if (hadDrafts && !item.HasDrafts)
                    _ = ReloadSummaryAsync(item);
                else
                    item.Refresh(DraftFor(path));

                // Shows a staged image or removal; after a save or discard, what's on disk.
                _albumArt.DraftChanged(item);
            };

            Job.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(JobViewModel.IsRunning))
                {
                    RefreshCommands();
                    OnPropertyChanged(nameof(SongsFolderToolTip));
                    OnPropertyChanged(nameof(OutputFolderToolTip));
                }
            };
        }

        public JobViewModel Job { get; }

        public PlayerViewModel Player { get; }

        /// <summary>The search box and the sort and filter flyout.</summary>
        public LibraryFilterViewModel Filter { get; }

        /// <summary>The songs the list shows: those that pass the filters, sorted.</summary>
        public AvaloniaList<LibraryItemViewModel> Entries { get; } = [];

        /// <summary>The list's selection. Several items can be selected for Pack, Unpack and bulk metadata editing.</summary>
        public SelectionModel<LibraryItemViewModel> Selection { get; }

        public IReadOnlyList<OverwriteOption> OverwriteOptions { get; }

        [ObservableProperty]
        public partial OverwriteOption SelectedOverwriteOption { get; set; }

        /// <summary>Pack writes every .sng directly in the output folder. Unpack always mirrors.</summary>
        [ObservableProperty]
        public partial bool FlatPackOutput { get; set; }

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(RescanCommand))]
        public partial string? FolderPath { get; set; }

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(PackCommand))]
        [NotifyCanExecuteChangedFor(nameof(UnpackCommand))]
        [NotifyCanExecuteChangedFor(nameof(MergeCommand))]
        [NotifyCanExecuteChangedFor(nameof(ValidateCommand))]
        public partial bool IsScanning { get; set; }

        /// <summary>How many songs the scan has found so far.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsScanProgressIndeterminate))]
        public partial int ScanFound { get; set; }

        /// <summary>How many of the songs found have been read and listed.</summary>
        [ObservableProperty]
        public partial int ScanRead { get; set; }

        /// <summary>The scan is still searching folders, so <see cref="ScanFound"/> may grow.</summary>
        [ObservableProperty]
        public partial bool IsSearching { get; set; }

        /// <summary>Nothing has been found yet, so the progress bar can't show how far along the scan is.</summary>
        public bool IsScanProgressIndeterminate => ScanFound == 0;

        [ObservableProperty]
        public partial string? ErrorMessage { get; set; }

        /// <summary>The selected items, in list order rather than the order they were clicked.</summary>
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(RevealCommand))]
        [NotifyCanExecuteChangedFor(nameof(DownloadAllCommand))]
        public partial IReadOnlyList<SongEntry> SelectedEntries { get; set; } = [];

        /// <summary>The right-click menu's name for <see cref="RevealCommand"/>, after the platform's file manager.</summary>
        public static string RevealLabel => FileRevealService.RevealLabel;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(PackCommand))]
        [NotifyCanExecuteChangedFor(nameof(UnpackCommand))]
        [NotifyCanExecuteChangedFor(nameof(MergeCommand))]
        [NotifyCanExecuteChangedFor(nameof(ValidateCommand))]
        [NotifyCanExecuteChangedFor(nameof(EditChartCommand))]
        [NotifyPropertyChangedFor(nameof(IsSingleSelection))]
        public partial int SelectedCount { get; set; }

        /// <summary>One song is selected: the right-click menu offers Edit Chart.</summary>
        public bool IsSingleSelection => SelectedCount == 1;

        /// <summary>Max scan depth, set from the Settings window.</summary>
        [ObservableProperty]
        public partial decimal? ScanDepth { get; set; }

        private bool HasScanned => _scannedFolder is not null;

        private bool ScannedFolderIsSelected => string.Equals(FolderPath, _scannedFolder, StringComparison.Ordinal);

        public bool ShowPrompt => FolderPath is null;

        /// <summary>A folder is chosen (e.g. restored at launch) but hasn't been scanned yet.</summary>
        public bool ShowNotScanned => FolderPath is not null && !HasScanned && !IsScanning;

        /// <summary>The list was scanned with a different folder or depth than is now selected, or a job has changed the disk since.</summary>
        public bool IsStale => HasScanned && !IsScanning &&
            (_outputsChanged || !ScannedFolderIsSelected || _settings.Settings.ScanMaxDepth != _scannedDepth);

        public bool HasError => ErrorMessage is not null;

        /// <summary>Only when the empty result is for the folder shown; after switching folders the stale banner speaks instead.</summary>
        public bool ShowEmpty => HasScanned && ScannedFolderIsSelected && !IsScanning && !HasError && _allItems.Count == 0;

        public bool ShowCenteredSpinner => IsScanning && _allItems.Count == 0;

        /// <summary>Songs were found, but the search or filters hide every one.</summary>
        public bool ShowNoMatches => HasScanned && !HasError && _allItems.Count > 0 && Entries.Count == 0;

        /// <summary>The search box and filter button, once there's a list to narrow down.</summary>
        public bool ShowFilterBar => HasScanned;

        /// <summary>
        /// The bottom status line, with the progress bar while scanning; hidden while a centered state
        /// (empty, error) says it instead.
        /// </summary>
        public bool ShowStatus => HasScanned && !HasError && (IsScanning || _allItems.Count > 0);

        /// <summary>
        /// While scanning, how many of the songs found have been read, and how many show while the search or a filter
        /// may hide some; then the song count, or "shown/total".
        /// </summary>
        public string StatusText
        {
            get
            {
                if (IsScanning)
                {
                    string format = (IsSearching, Filter.Query.IsFiltering) switch
                    {
                        (true, true) => Strings.ScanSearchingFilteredProgressFormat,
                        (true, false) => Strings.ScanSearchingProgressFormat,
                        (false, true) => Strings.ScanReadingFilteredProgressFormat,
                        (false, false) => Strings.ScanReadingProgressFormat,
                    };
                    return string.Format(CultureInfo.CurrentCulture, format, ScanRead, ScanFound, Entries.Count);
                }

                if (Filter.Query.IsFiltering)
                    return string.Format(CultureInfo.CurrentCulture, Strings.FilteredSongCountFormat, Entries.Count, _allItems.Count);

                return _allItems.Count == 1
                    ? Strings.SongCountOne
                    : string.Format(CultureInfo.CurrentCulture, Strings.SongCountFormat, _allItems.Count);
            }
        }

        /// <summary>Where Pack, Unpack and Merge write: the chosen output folder, or else the scanned folder.</summary>
        public string? EffectiveOutputFolder => OutputFolder ?? _scannedFolder ?? FolderPath;

        /// <summary>The chosen output folder, or <see langword="null"/> to write next to the scanned songs.</summary>
        public string? OutputFolder
        {
            get => _settings.Settings.OutputFolder;
            set
            {
                _settings.Settings.OutputFolder = value;
                RefreshOutput();
            }
        }

        public bool HasCustomOutputFolder => OutputFolder is not null;

        /// <summary>
        /// How many folders a scan searches, and how many songs it reads, at once; also how many files
        /// Deduplicate reads at once. Taken when a scan or a Deduplicate starts.
        /// </summary>
        public int ScanParallelism
        {
            get => _settings.Settings.ScanParallelism;
            set => _settings.Settings.ScanParallelism = Math.Max(value, AppSettings.MinScanParallelism);
        }

        /// <summary>Which copy the Deduplicate window keeps by default when a song is both a folder and a .sng.</summary>
        public DedupKindPreference DedupKeepPreference
        {
            get => _settings.Settings.DedupKeepPreference;
            set => _settings.Settings.DedupKeepPreference = value;
        }

        private bool CanBrowse() => !Job.IsRunning;

        /// <summary>The title bar's songs folder tooltip: what it does, or while a job runs, why it can't.</summary>
        public string SongsFolderToolTip => Job.IsRunning ? Strings.BrowseBusyToolTip : Strings.BrowseToolTip;

        /// <summary>The title bar's output folder tooltip: what it does, or while a job runs, why it can't.</summary>
        public string OutputFolderToolTip => Job.IsRunning ? Strings.BrowseBusyToolTip : Strings.OutputToolTip;

        [RelayCommand(CanExecute = nameof(CanBrowse))]
        private async Task BrowseAsync()
        {
            var path = await _folderPicker.PickFolderAsync(Strings.BrowseDialogTitle, FolderPath);
            if (path is null)
                return;

            FolderPath = path;
            _settings.Settings.LastFolder = path;
        }

        private bool CanRescan() => FolderPath is not null && !Job.IsRunning;

        /// <summary>Scans the chosen folder. Re-running it mid-scan cancels and restarts.</summary>
        [RelayCommand(CanExecute = nameof(CanRescan))]
        private void Rescan() => _ = StartScan(FolderPath!, _settings.Settings.ScanMaxDepth);

        [RelayCommand(CanExecute = nameof(CanBrowse))]
        private async Task BrowseOutputAsync()
        {
            var path = await _folderPicker.PickFolderAsync(Strings.BrowseOutputDialogTitle, EffectiveOutputFolder);
            if (path is null)
                return;

            OutputFolder = path;
        }

        private bool CanResetOutput() => HasCustomOutputFolder && !Job.IsRunning;

        /// <summary>Goes back to writing next to the scanned songs.</summary>
        [RelayCommand(CanExecute = nameof(CanResetOutput))]
        private void ResetOutput() => OutputFolder = null;

        /// <summary>
        /// The play button on a song's art: stops the song if it is the one playing; otherwise shows the
        /// song in the detail panel, which loads it into the player, and plays it from its preview start.
        /// </summary>
        [RelayCommand]
        private void TogglePreview(LibraryItemViewModel? item)
        {
            if (item is null)
                return;

            if (Player.ActiveEntry == item.Entry)
            {
                Player.StopCommand.Execute(null);
                return;
            }

            Player.RequestPreview(item.Entry);
            int index = Entries.IndexOf(item);
            if (index >= 0)
                Selection.SelectedIndex = index;
        }

        /// <summary>Marks the item whose song is playing, and clears the one that was.</summary>
        private void RefreshPreviewing()
        {
            var playing = Player.ActiveEntry is { } entry && _itemsByPath.TryGetValue(entry.FullPath, out var item) ? item : null;
            if (ReferenceEquals(playing, _previewingItem))
                return;

            if (_previewingItem is not null)
                _previewingItem.IsPreviewing = false;
            _previewingItem = playing;
            if (playing is not null)
                playing.IsPreviewing = true;
        }

        private bool CanRunJob() => SelectedCount > 0 && HasScanned && !IsScanning && !Job.IsRunning;

        [RelayCommand(CanExecute = nameof(CanRunJob))]
        private Task PackAsync() => RunJobAsync(JobKind.Pack);

        [RelayCommand(CanExecute = nameof(CanRunJob))]
        private Task UnpackAsync() => RunJobAsync(JobKind.Unpack);

        private async Task RunJobAsync(JobKind kind)
        {
            if (_scannedFolder is not { } scanRoot || EffectiveOutputFolder is not { } destination)
                return;

            var entries = SelectedEntries;
            var layout = kind == JobKind.Pack && FlatPackOutput ? OutputLayout.Flat : OutputLayout.Mirror;
            await Job.RunAsync(kind, entries, scanRoot, destination, SelectedOverwriteOption.Value, layout);

            _outputsChanged = true;
            RefreshState();
        }

        private bool CanMerge() => SelectedCount > 1 && HasScanned && !IsScanning && !Job.IsRunning;

        /// <summary>Opens the Merge window, which combines the selected songs into one new .sng in the output folder.</summary>
        [RelayCommand(CanExecute = nameof(CanMerge))]
        private async Task MergeAsync()
        {
            if (EffectiveOutputFolder is not { } destination)
                return;

            if (await _mergeWindow.ShowAsync(SelectedEntries, destination, SelectedOverwriteOption.Value) is not null)
            {
                _outputsChanged = true;
                RefreshState();
            }
        }

        private bool CanValidate() => SelectedCount > 0 && HasScanned && !IsScanning && !Job.IsRunning;

        /// <summary>Opens the Validator window, which lists the selected songs whose metadata fails validation, to fix them.</summary>
        [RelayCommand(CanExecute = nameof(CanValidate))]
        private Task ValidateAsync() => _validatorWindow.ShowAsync(SelectedEntries);

        // One song at a time, and not while a job may be writing it.
        private bool CanEditChart() => SelectedCount == 1 && !Job.IsRunning;

        public static string EditChartToolTip => Strings.EditChartToolTip;

        /// <summary>
        /// Asked before a song opens in Chart Builder, which can't see this app's drafts: whatever has to be done
        /// first, such as saving or discarding them (charter-spec C2 item 4). False stops it.
        /// </summary>
        public Func<SongEntry, Task<bool>>? PrepareEditChart { get; set; }

        /// <summary>Opens the selected song in Chart Builder (charter-spec C2). Only one song is edited at a time.</summary>
        [RelayCommand(CanExecute = nameof(CanEditChart))]
        private async Task EditChartAsync()
        {
            if (SelectedEntries is not [var song])
                return;
            if (PrepareEditChart is { } prepare && !await prepare(song))
                return;

            var stamp = await StampAsync(song);
            if (_chartBuilder.Start(BuilderArgs.ForSong(song.FullPath)) is { } problem)
            {
                _notice.ShowError(problem);
                return;
            }

            _editedSongs[song.FullPath] = (song, stamp);
            _pendingEditedStamps.Remove(song.FullPath);
            if (!_editedSongsTimer.IsEnabled)
                _editedSongsTimer.Start();
        }

        /// <summary>Raised on the UI thread when the background poller re-reads songs Chart Builder saved.</summary>
        public event EventHandler<IReadOnlyList<SongEntry>>? EditedSongsChanged;

        /// <summary>
        /// The poller's tick: re-reads the songs Chart Builder has saved since they were last read, and tells the
        /// window so the inspector can follow. Never throws; a tick already running is skipped.
        /// </summary>
        private async Task PollEditedSongsAsync()
        {
            IReadOnlyList<SongEntry> changed = [];
            try
            {
                changed = await RefreshEditedSongsAsync(requireStable: true);
            }
            catch (Exception ex)
            {
                // Nothing awaits this, so anything not caught here would be lost.
                System.Diagnostics.Trace.TraceError($"Re-reading the songs Chart Builder changed failed: {ex}");
                return;
            }

            if (changed.Count > 0)
                EditedSongsChanged?.Invoke(this, changed);
        }

        /// <summary>
        /// Re-reads the songs opened in Chart Builder that have changed since they were last read, as when the user comes
        /// back from saving there (charter-spec §4.8). Chart Builder's processes aren't kept, so every song opened in it
        /// is checked, for as long as this app runs.
        /// </summary>
        /// <param name="requireStable">
        /// For the background poller: only re-read a change seen on two checks in a row, so a folder save caught
        /// between writing <c>notes.mid</c> and <c>song.ini</c> isn't read half-written. The activation path passes
        /// <see langword="false"/> and re-reads at once.
        /// </param>
        /// <returns>The songs that had changed.</returns>
        public async Task<IReadOnlyList<SongEntry>> RefreshEditedSongsAsync(bool requireStable = false)
        {
            if (_refreshingEditedSongs)
                return [];
            _refreshingEditedSongs = true;
            try
            {
                var changed = new List<SongEntry>();
                foreach (var (path, (entry, stamp)) in _editedSongs.ToList())
                {
                    var now = await StampAsync(entry);
                    if (now == stamp)
                    {
                        _pendingEditedStamps.Remove(path);
                        continue;
                    }

                    if (requireStable)
                    {
                        if (!_pendingEditedStamps.TryGetValue(path, out var pending) || pending != now)
                        {
                            _pendingEditedStamps[path] = now;
                            continue;
                        }
                    }

                    _pendingEditedStamps.Remove(path);
                    _editedSongs[path] = (entry, now);
                    changed.Add(entry);
                    if (_itemsByPath.TryGetValue(path, out var item))
                    {
                        await ReloadSummaryAsync(item);
                        _albumArt.Reload(item);
                    }
                }

                return changed;
            }
            finally
            {
                _refreshingEditedSongs = false;
            }
        }

        /// <summary>What the song is on disk now, or null when it's gone or can't be read.</summary>
        private static Task<EntryStamp?> StampAsync(SongEntry entry) => Task.Run(() =>
        {
            try
            {
                return EntryStamps.Capture(entry.FullPath, entry.Kind);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        });

        private bool CanDeduplicate() => FolderPath is not null && !Job.IsRunning;

        /// <summary>
        /// Rescans the library and opens the Deduplicate window, which compares the songs once the scan
        /// is done, finds duplicates across the whole library and deletes the copies marked.
        /// </summary>
        /// <remarks>
        /// Always on a fresh scan: after a pack, the new .sng files aren't listed until a rescan, and
        /// deciding what to delete from an old list is riskier.
        /// </remarks>
        [RelayCommand(CanExecute = nameof(CanDeduplicate))]
        private async Task DeduplicateAsync()
        {
            // Also clears the selection, so the detail panel lets go of the song it shows and its files can be deleted.
            var scan = StartScan(FolderPath!, _settings.Settings.ScanMaxDepth);
            if (EffectiveOutputFolder is not { } destination)
                return;

            var request = new DedupRequest(
                (progress, ct) => WaitForScanAsync(scan, progress, ct),
                destination,
                SelectedOverwriteOption.Value,
                ScanParallelism);
            var result = await _dedupWindow.ShowAsync(request);
            RemoveEntries(result.RemovedPaths);
            if (result.OutputsChanged)
                _outputsChanged = true;
            RefreshState();
        }

        /// <summary>Reports how far <paramref name="scan"/> has got until it ends; then, if it finished, every song it found.</summary>
        /// <returns>The songs, or <see langword="null"/> when the scan stopped short.</returns>
        /// <remarks>Cancelling stops only the wait; the scan carries on.</remarks>
        private async Task<IReadOnlyList<SongEntry>?> WaitForScanAsync(Task<bool> scan, IProgress<DedupScanProgress> progress, CancellationToken cancellationToken)
        {
            void Report() => progress.Report(new DedupScanProgress(ScanRead, ScanFound, IsSearching));

            void OnChanged(object? sender, PropertyChangedEventArgs e)
            {
                if (e.PropertyName is nameof(ScanRead) or nameof(ScanFound) or nameof(IsSearching))
                    Report();
            }

            PropertyChanged += OnChanged;
            try
            {
                Report();
                if (!await scan.WaitAsync(cancellationToken))
                    return null;
            }
            finally
            {
                PropertyChanged -= OnChanged;
            }

            return [.. _allItems.Select(i => i.Entry)];
        }

        /// <summary>Takes songs that no longer exist out of the list, without a rescan.</summary>
        private void RemoveEntries(IReadOnlyCollection<string> paths)
        {
            if (paths.Count == 0)
                return;

            foreach (string path in paths)
            {
                if (!_itemsByPath.Remove(path, out var item))
                    continue;

                item.FactsChanged -= OnItemFactsChanged;
                _allItems.Remove(item);
                Entries.Remove(item);
                if (ReferenceEquals(_previewingItem, item))
                    _previewingItem = null;
            }

            _drafts.DiscardMissing();
            SyncSelection();
        }

        partial void OnSelectedOverwriteOptionChanged(OverwriteOption value)
        {
            _settings.Settings.OverwritePolicy = value.Value;
            foreach (var option in OverwriteOptions)
                option.IsSelected = option == value;
        }

        /// <summary>Picks a policy from the Pack menu.</summary>
        [RelayCommand]
        private void SelectOverwriteOption(OverwriteOption option) => SelectedOverwriteOption = option;

        partial void OnFlatPackOutputChanged(bool value) => _settings.Settings.FlatPackOutput = value;

        partial void OnFolderPathChanged(string? value) => RefreshState();

        partial void OnIsScanningChanged(bool value) => RefreshState();

        partial void OnErrorMessageChanged(string? value) => RefreshState();

        partial void OnScanDepthChanged(decimal? value)
        {
            if (value is null)
            {
                // The box was cleared; put the last good value back.
                ScanDepth = _settings.Settings.ScanMaxDepth;
                return;
            }

            _settings.Settings.ScanMaxDepth = Math.Clamp((int)Math.Round(value.Value), 0, AppSettings.MaxScanMaxDepth);
            RefreshState();
        }

        /// <summary>Scans <paramref name="folder"/>, replacing any scan that's running.</summary>
        /// <returns>
        /// Completes with <see langword="true"/> once every song found is listed, or with
        /// <see langword="false"/> if the scan stops short: it failed, or another scan replaced it.
        /// </returns>
        private Task<bool> StartScan(string folder, int depth)
        {
            _scanCts?.Cancel();
            var cts = new CancellationTokenSource();
            _scanCts = cts;

            // A replaced scan never ends on its own, since only the current one is listed.
            _scanSink?.Ended.TrySetResult(false);
            var sink = new ScanSink(this);
            _scanSink = sink;

            _itemsCts.Cancel();
            _itemsCts.Dispose();
            _itemsCts = new CancellationTokenSource();

            _scannedFolder = folder;
            _scannedDepth = depth;
            _outputsChanged = false;
            Selection.Clear();
            Entries.Clear();
            _allItems.Clear();
            _itemsByPath.Clear();
            _previewingItem = null;
            _albumArt.Reset(_itemsCts.Token);
            ErrorMessage = null;
            ScanFound = 0;
            ScanRead = 0;
            IsSearching = true;
            IsScanning = true;
            RefreshState();

            _ = LoadAsync(folder, depth, sink, cts);
            return sink.Ended.Task;
        }

        private async Task LoadAsync(string folder, int depth, ScanSink sink, CancellationTokenSource cts)
        {
            using var _ = cts;
            try
            {
                await SongLibraryLoader.LoadAsync(folder, depth, ScanParallelism, sink, cts.Token);

                // Every song read is queued by now; the scan ends once the last of them is listed.
                if (_scanSink == sink)
                {
                    sink.IsLoaded = true;
                    sink.RequestList();
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (_scanSink == sink)
                    ErrorMessage = string.Format(CultureInfo.CurrentCulture, Strings.FolderUnreadableFormat, ex.Message);
            }
            finally
            {
                if (_scanCts == cts)
                    _scanCts = null;
                if (_scanSink == sink && !sink.IsLoaded)
                    EndScan(sink, completed: false);
            }
        }

        /// <summary>
        /// Lists the songs the scan has read since last time, a chunk at a time for as long as
        /// <see cref="ListBudget"/> allows, and asks to come back for the rest.
        /// </summary>
        private void ListArrived(ScanSink sink)
        {
            if (sink != _scanSink)
                return;

            var watch = Stopwatch.StartNew();
            var chunk = new List<LibraryItemViewModel>(ListChunkSize);
            do
            {
                chunk.Clear();
                while (chunk.Count < ListChunkSize && sink.Songs.TryDequeue(out var song))
                    chunk.Add(CreateItem(song));
                if (chunk.Count > 0)
                    AddItems(chunk);
            }
            while (chunk.Count == ListChunkSize && watch.Elapsed < ListBudget);

            ScanFound = sink.Found;
            ScanRead = _allItems.Count;
            IsSearching = !sink.SearchComplete;

            if (!sink.Songs.IsEmpty)
                sink.RequestList();
            else if (sink.IsLoaded)
                EndScan(sink, completed: true);

            RefreshState();
        }

        private void EndScan(ScanSink sink, bool completed)
        {
            _scanSink = null;

            // Drafts for songs that were deleted or moved can never be saved.
            if (completed)
                _drafts.DiscardMissing();

            IsSearching = false;
            IsScanning = false;
            sink.Ended.TrySetResult(completed);
        }

        /// <summary>A list item for a song the scan read, showing what was read.</summary>
        private LibraryItemViewModel CreateItem(LoadedSong song)
        {
            string path = song.Entry.FullPath;
            var item = new LibraryItemViewModel(song.Entry, song.Position, _drafts.HasDrafts(path));
            item.SetSummary(song.Summary, DraftFor(path));
            return item;
        }

        /// <summary>Puts newly read songs in the list where the sort and filters place them. Songs already listed don't move.</summary>
        private void AddItems(List<LibraryItemViewModel> items)
        {
            foreach (var item in items)
            {
                _itemsByPath[item.Entry.FullPath] = item;
                item.FactsChanged += OnItemFactsChanged;
            }

            MergeByPosition(items);

            // A re-apply on its way lists them along with everything else.
            if (!IsApplyPending)
            {
                var query = Filter.Query;
                var matches = query.CreateMatcher();
                var order = query.CreateComparer(item => item.Facts, ByPosition);
                var shown = items.Where(item => matches(item.Facts)).ToList();
                shown.Sort(order);
                InsertSorted(shown, order);
            }

            RefreshPreviewing();
        }

        /// <summary>
        /// Puts <paramref name="items"/> into <see cref="_allItems"/> in scan order, in one pass from the back rather than an
        /// insert apiece, each of which would move every song after it.
        /// </summary>
        private void MergeByPosition(List<LibraryItemViewModel> items)
        {
            var added = items.ToArray();
            Array.Sort(added, ByPosition);

            int old = _allItems.Count - 1;
            _allItems.AddRange(added);
            for (int to = _allItems.Count - 1, from = added.Length - 1; from >= 0; to--)
            {
                _allItems[to] = old >= 0 && ByPosition.Compare(_allItems[old], added[from]) > 0
                    ? _allItems[old--]
                    : added[from--];
            }
        }

        /// <summary>Inserts <paramref name="sorted"/> into <see cref="Entries"/>, which <paramref name="order"/> already sorts.</summary>
        private void InsertSorted(List<LibraryItemViewModel> sorted, IComparer<LibraryItemViewModel> order)
        {
            // Each song's place is found in the list as it is now, then the songs go in from the last
            // place back so the earlier places stay right. Songs sharing a place go in together.
            var places = new int[sorted.Count];
            int low = 0;
            for (int i = 0; i < sorted.Count; i++)
                low = places[i] = LowerBound(Entries, sorted[i], order, low);

            for (int end = sorted.Count; end > 0;)
            {
                int start = end - 1;
                while (start > 0 && places[start - 1] == places[start])
                    start--;
                Entries.InsertRange(places[start], sorted.GetRange(start, end - start));
                end = start;
            }
        }

        /// <summary>The first index at or after <paramref name="low"/> whose item doesn't sort before <paramref name="item"/>.</summary>
        private static int LowerBound(AvaloniaList<LibraryItemViewModel> list, LibraryItemViewModel item, IComparer<LibraryItemViewModel> order, int low)
        {
            int high = list.Count;
            while (low < high)
            {
                int mid = low + (high - low) / 2;
                if (order.Compare(list[mid], item) < 0)
                    low = mid + 1;
                else
                    high = mid;
            }

            return low;
        }

        /// <summary>Re-reads one item's metadata, e.g. after its changes were saved.</summary>
        private async Task ReloadSummaryAsync(LibraryItemViewModel item)
        {
            var token = _itemsCts.Token;
            try
            {
                var summary = await SongSummaryReader.TryReadAsync(item.Entry, token);
                if (!token.IsCancellationRequested)
                    item.SetSummary(summary, DraftFor(item.Entry.FullPath));
            }
            catch (OperationCanceledException)
            {
            }
        }

        /// <summary>The list created a row for <paramref name="item"/>; its album art is loaded if it hasn't been.</summary>
        public void OnItemRealized(LibraryItemViewModel item) => _albumArt.Realized(item);

        /// <summary>The list let go of <paramref name="item"/>'s row.</summary>
        public void OnItemUnrealized(LibraryItemViewModel item) => AlbumArtLoader.Unrealized(item);

        /// <summary>The song's unsaved changes, without creating an empty draft for every listed song.</summary>
        private MetadataDraft? DraftFor(string path) => _drafts.HasDrafts(path) ? _drafts.Get(path) : null;

        private bool IsApplyPending => _applyPosted || _applyTimer.IsEnabled;

        /// <summary>
        /// Re-applies the query soon: when the UI is next idle, or after <see cref="MetadataApplyDelay"/>
        /// for changes that come in bursts.
        /// </summary>
        private void ScheduleApply(bool immediate)
        {
            if (_applyPosted)
                return;

            if (immediate)
            {
                _applyTimer.Stop();
                _applyPosted = true;
                Dispatcher.UIThread.Post(ApplyQuery, DispatcherPriority.Background);
            }
            else if (!_applyTimer.IsEnabled)
            {
                _applyTimer.Start();
            }
        }

        private void OnItemFactsChanged(LibraryItemViewModel item)
        {
            // Nothing can move or hide while every song shows in scan order. Items from an earlier scan don't count.
            if (!Filter.Query.KeepsScanOrder && _itemsByPath.TryGetValue(item.Entry.FullPath, out var current) && ReferenceEquals(current, item))
                ScheduleApply(immediate: false);
        }

        /// <summary>Rebuilds <see cref="Entries"/> from the query, keeping the selected songs that still show selected.</summary>
        /// <remarks>Selected songs the filters now hide are deselected, so Pack and Unpack only act on songs that can be seen.</remarks>
        private void ApplyQuery()
        {
            _applyPosted = false;
            _applyTimer.Stop();

            var visible = Filter.Query.Apply(_allItems, item => item.Facts);
            if (!visible.SequenceEqual(Entries))
            {
                var selected = Selection.SelectedItems.OfType<LibraryItemViewModel>().ToHashSet();
                _syncingSelection = true;
                try
                {
                    Entries.Clear();
                    Entries.AddRange(visible);
                    for (int i = 0; i < visible.Count; i++)
                    {
                        if (selected.Contains(visible[i]))
                            Selection.Select(i);
                    }
                }
                finally
                {
                    _syncingSelection = false;
                }

                SyncSelection();
            }

            RefreshState();
        }

        private bool HasSelection() => SelectedEntries.Count > 0;

        /// <summary>Shows the selected songs' files and folders in the file manager. Right-clicking a song selects it first.</summary>
        [RelayCommand(CanExecute = nameof(HasSelection))]
        private void Reveal() => _reveal.Reveal([.. SelectedEntries.Select(e => e.FullPath)]);

        /// <summary>
        /// Saves a copy of each selected song, unsaved changes and all, as a song folder named
        /// <c>Artist - Title</c> in the download folder.
        /// </summary>
        [RelayCommand(CanExecute = nameof(HasSelection))]
        private Task DownloadAllAsync()
        {
            var songs = SelectedEntries
                .Select(entry =>
                {
                    var item = _itemsByPath.GetValueOrDefault(entry.FullPath);
                    string name = DownloadNames.SongPrefix(item?.Artist, item?.Title, entry.DisplayName);
                    // A copy: the export reads it off the UI thread, while the song can still be edited.
                    return new SongDownload(entry, DraftFor(entry.FullPath)?.Clone(), name);
                })
                .ToList();
            return _downloads.SaveSongFoldersAsync(songs);
        }

        private void OnSelectionPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            // SelectionModel never raises Count. SelectedIndexes covers every change except a
            // source reset, which raises SelectedItem instead.
            if (_syncingSelection || e.PropertyName is not (nameof(Selection.SelectedIndexes) or nameof(Selection.SelectedItem)))
                return;

            SyncSelection();
        }

        private void SyncSelection()
        {
            SelectedCount = Selection.Count;
            var selected = Selection.SelectedIndexes.Order().Select(i => Entries[i].Entry).ToList();

            // Choosing anything else, once the song asked to be played is shown, withdraws the request.
            if (Player.PreviewRequest is { } requested && !(selected.Count == 1 && selected[0] == requested))
                Player.CancelPreviewRequest();

            if (!selected.SequenceEqual(SelectedEntries))
                SelectedEntries = selected;
        }

        /// <summary>Raises change notifications for every derived display state.</summary>
        private void RefreshState()
        {
            OnPropertyChanged(nameof(ShowPrompt));
            OnPropertyChanged(nameof(ShowNotScanned));
            OnPropertyChanged(nameof(IsStale));
            OnPropertyChanged(nameof(HasError));
            OnPropertyChanged(nameof(ShowEmpty));
            OnPropertyChanged(nameof(ShowCenteredSpinner));
            OnPropertyChanged(nameof(ShowNoMatches));
            OnPropertyChanged(nameof(ShowFilterBar));
            OnPropertyChanged(nameof(ShowStatus));
            OnPropertyChanged(nameof(StatusText));
            RefreshOutput();
            RefreshCommands();
        }

        private void RefreshOutput()
        {
            OnPropertyChanged(nameof(OutputFolder));
            OnPropertyChanged(nameof(EffectiveOutputFolder));
            OnPropertyChanged(nameof(HasCustomOutputFolder));
            ResetOutputCommand.NotifyCanExecuteChanged();
        }

        private void RefreshCommands()
        {
            BrowseCommand.NotifyCanExecuteChanged();
            BrowseOutputCommand.NotifyCanExecuteChanged();
            ResetOutputCommand.NotifyCanExecuteChanged();
            RescanCommand.NotifyCanExecuteChanged();
            PackCommand.NotifyCanExecuteChanged();
            UnpackCommand.NotifyCanExecuteChanged();
            MergeCommand.NotifyCanExecuteChanged();
            ValidateCommand.NotifyCanExecuteChanged();
            DeduplicateCommand.NotifyCanExecuteChanged();
            EditChartCommand.NotifyCanExecuteChanged();
        }

        /// <summary>
        /// Takes one scan's reports on the thread pool and has the UI thread list the songs once
        /// input and drawing are done.
        /// </summary>
        private sealed class ScanSink(LibraryViewModel owner) : IProgress<LibraryLoadProgress>
        {
            private int _listPosted;
            private volatile int _found;
            private volatile bool _searchComplete;

            /// <summary>Songs read but not listed yet.</summary>
            public ConcurrentQueue<LoadedSong> Songs { get; } = new();

            public int Found => _found;

            public bool SearchComplete => _searchComplete;

            /// <summary>The load has finished, so every song it read is listed or in <see cref="Songs"/>. UI thread only.</summary>
            public bool IsLoaded { get; set; }

            /// <summary>Set once the scan has listed every song (<see langword="true"/>) or stopped short.</summary>
            public TaskCompletionSource<bool> Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public void Report(LibraryLoadProgress value)
            {
                // Counted before queued, so the found count is never behind the songs listed.
                _found = value.Found;
                _searchComplete = value.SearchComplete;
                foreach (var song in value.Songs)
                    Songs.Enqueue(song);
                RequestList();
            }

            /// <summary>Has the UI thread list what's queued, unless that's already on its way.</summary>
            public void RequestList()
            {
                if (Interlocked.Exchange(ref _listPosted, 1) != 0)
                    return;

                Dispatcher.UIThread.Post(() =>
                {
                    Volatile.Write(ref _listPosted, 0);
                    owner.ListArrived(this);
                }, DispatcherPriority.Background);
            }
        }
    }
}
