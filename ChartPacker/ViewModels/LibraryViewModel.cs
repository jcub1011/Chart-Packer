using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Avalonia.Collections;
using Avalonia.Controls.Selection;
using ChartPacker.Core.Jobs;
using ChartPacker.Core.Metadata;
using ChartPacker.Core.Scanning;
using ChartPacker.Resources;
using ChartPacker.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChartPacker.ViewModels
{
    /// <summary>An overwrite policy as offered in the policy picker.</summary>
    public sealed record OverwriteOption(OverwritePolicy Value, string Label);

    /// <summary>The left panel: a picked folder, the song sources found under it, and Pack/Unpack.</summary>
    /// <remarks>
    /// Scanning only happens on an explicit <see cref="RescanCommand"/>. Picking a folder, changing
    /// the depth or launching the app never scans; the list is flagged as out of date instead.
    /// The same goes for a pack or unpack job, which changes what's on disk.
    /// </remarks>
    public partial class LibraryViewModel : ViewModelBase
    {
        private const int SummaryParallelism = 4;

        private readonly IFolderPickerService _folderPicker;
        private readonly SettingsService _settings;
        private readonly MetadataDraftStore _drafts;
        private readonly Dictionary<string, LibraryItemViewModel> _itemsByPath = new(StringComparer.Ordinal);
        private CancellationTokenSource? _scanCts;

        private readonly AlbumArtLoader _albumArt;

        // Per-item background loads (metadata and album art) for the current list. They outlive
        // the scan, so the list is usable meanwhile.
        private CancellationTokenSource _itemsCts = new();

        // What the list currently reflects: the folder and depth of the most recent scan.
        private string? _scannedFolder;
        private int _scannedDepth;

        // A job has written to disk since the last scan.
        private bool _outputsChanged;

        // The item whose art shows a stop button, because it's the song being played.
        private LibraryItemViewModel? _previewingItem;

        /// <param name="player">Plays a song from the list's art buttons; the detail panel shows the same one.</param>
        public LibraryViewModel(IFolderPickerService folderPicker, SettingsService settings, MetadataDraftStore drafts, JobViewModel job, PlayerViewModel player)
        {
            _folderPicker = folderPicker;
            _settings = settings;
            _drafts = drafts;
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

            Selection = new SelectionModel<LibraryItemViewModel> { SingleSelect = false, Source = Entries };
            Selection.PropertyChanged += OnSelectionPropertyChanged;

            _drafts.DraftsChanged += path =>
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
                    RefreshCommands();
            };
        }

        public JobViewModel Job { get; }

        public PlayerViewModel Player { get; }

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
        public partial bool IsScanning { get; set; }

        [ObservableProperty]
        public partial string? ErrorMessage { get; set; }

        /// <summary>The selected items, in list order rather than the order they were clicked.</summary>
        [ObservableProperty]
        public partial IReadOnlyList<SongEntry> SelectedEntries { get; set; } = [];

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(PackCommand))]
        [NotifyCanExecuteChangedFor(nameof(UnpackCommand))]
        public partial int SelectedCount { get; set; }

        /// <summary>Max scan depth. Nullable decimal to match <c>NumericUpDown.Value</c>.</summary>
        [ObservableProperty]
        public partial decimal? ScanDepth { get; set; }

        public int MaxScanDepth => AppSettings.MaxScanMaxDepth;

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
        public bool ShowEmpty => HasScanned && ScannedFolderIsSelected && !IsScanning && !HasError && Entries.Count == 0;

        public bool ShowCenteredSpinner => IsScanning && Entries.Count == 0;

        /// <summary>The bottom status line; hidden while a centered state (spinner, empty, error) says it instead.</summary>
        public bool ShowStatus => HasScanned && !HasError && Entries.Count > 0;

        public string StatusText => IsScanning
            ? string.Format(CultureInfo.CurrentCulture, Strings.ScanningCountFormat, Entries.Count)
            : Entries.Count == 1
                ? Strings.SongCountOne
                : string.Format(CultureInfo.CurrentCulture, Strings.SongCountFormat, Entries.Count);

        /// <summary>Where Pack and Unpack write: the chosen output folder, or else the scanned folder.</summary>
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

        private bool CanBrowse() => !Job.IsRunning;

        [RelayCommand(CanExecute = nameof(CanBrowse))]
        private async Task BrowseAsync()
        {
            var path = await _folderPicker.PickFolderAsync(Strings.BrowseDialogTitle);
            if (path is null)
                return;

            FolderPath = path;
            _settings.Settings.LastFolder = path;
        }

        private bool CanRescan() => FolderPath is not null && !Job.IsRunning;

        /// <summary>Scans the chosen folder. Re-running it mid-scan cancels and restarts.</summary>
        [RelayCommand(CanExecute = nameof(CanRescan))]
        private void Rescan() => _ = ScanAsync(FolderPath!, _settings.Settings.ScanMaxDepth);

        [RelayCommand(CanExecute = nameof(CanBrowse))]
        private async Task BrowseOutputAsync()
        {
            var path = await _folderPicker.PickFolderAsync(Strings.BrowseOutputDialogTitle);
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

        partial void OnSelectedOverwriteOptionChanged(OverwriteOption value) => _settings.Settings.OverwritePolicy = value.Value;

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

        private async Task ScanAsync(string folder, int depth)
        {
            _scanCts?.Cancel();
            using var cts = new CancellationTokenSource();
            _scanCts = cts;

            _itemsCts.Cancel();
            _itemsCts.Dispose();
            _itemsCts = new CancellationTokenSource();
            var itemsToken = _itemsCts.Token;
            var summaryQueue = Channel.CreateUnbounded<LibraryItemViewModel>(new UnboundedChannelOptions { SingleWriter = true });
            _ = LoadSummariesAsync(summaryQueue.Reader, itemsToken);

            _scannedFolder = folder;
            _scannedDepth = depth;
            _outputsChanged = false;
            Selection.Clear();
            Entries.Clear();
            _itemsByPath.Clear();
            _previewingItem = null;
            _albumArt.Reset(itemsToken);
            ErrorMessage = null;
            IsScanning = true;
            RefreshState();

            // Progress<T> posts to the UI thread in order, ahead of this method's continuation,
            // so every batch has landed by the time IsScanning is cleared below.
            var progress = new Progress<IReadOnlyList<SongEntry>>(batch =>
            {
                if (_scanCts != cts)
                    return;
                var items = batch.Select(e => new LibraryItemViewModel(e, _drafts.HasDrafts(e.FullPath))).ToList();
                foreach (var item in items)
                    _itemsByPath[item.Entry.FullPath] = item;
                Entries.AddRange(items);
                RefreshPreviewing();
                foreach (var item in items)
                    summaryQueue.Writer.TryWrite(item);
                RefreshState();
            });

            try
            {
                await SongLibraryScanner.ScanAsync(folder, depth, progress, cts.Token);

                // Drafts for songs that were deleted or moved can never be saved.
                if (_scanCts == cts)
                    _drafts.DiscardMissing();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (_scanCts == cts)
                    ErrorMessage = string.Format(CultureInfo.CurrentCulture, Strings.FolderUnreadableFormat, ex.Message);
            }
            finally
            {
                summaryQueue.Writer.TryComplete();
                if (_scanCts == cts)
                {
                    _scanCts = null;
                    IsScanning = false;
                }
            }
        }

        /// <summary>Reads the metadata of each item as the scan queues it, a few at a time, and shows it.</summary>
        private async Task LoadSummariesAsync(ChannelReader<LibraryItemViewModel> queue, CancellationToken cancellationToken)
        {
            // Progress<T> posts back to the UI thread, where the items live.
            var loaded = new Progress<(LibraryItemViewModel Item, SongSummary? Summary)>(result =>
            {
                if (!cancellationToken.IsCancellationRequested)
                    result.Item.SetSummary(result.Summary, DraftFor(result.Item.Entry.FullPath));
            });
            IProgress<(LibraryItemViewModel, SongSummary?)> report = loaded;
            var options = new ParallelOptions { MaxDegreeOfParallelism = SummaryParallelism, CancellationToken = cancellationToken };

            try
            {
                await Parallel.ForEachAsync(queue.ReadAllAsync(cancellationToken), options, async (item, token) =>
                    report.Report((item, await TryReadSummaryAsync(item.Entry, token).ConfigureAwait(false)))).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        /// <summary>Re-reads one item's metadata, e.g. after its changes were saved.</summary>
        private async Task ReloadSummaryAsync(LibraryItemViewModel item)
        {
            var token = _itemsCts.Token;
            try
            {
                var summary = await TryReadSummaryAsync(item.Entry, token);
                if (!token.IsCancellationRequested)
                    item.SetSummary(summary, DraftFor(item.Entry.FullPath));
            }
            catch (OperationCanceledException)
            {
            }
        }

        /// <summary>The entry's summary, or <see langword="null"/> when it can't be read.</summary>
        private static async Task<SongSummary?> TryReadSummaryAsync(SongEntry entry, CancellationToken cancellationToken)
        {
            try
            {
                return await SongSummaryReader.ReadAsync(entry, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>The list created a row for <paramref name="item"/>; its album art is loaded if it hasn't been.</summary>
        public void OnItemRealized(LibraryItemViewModel item) => _albumArt.Realized(item);

        /// <summary>The list let go of <paramref name="item"/>'s row.</summary>
        public void OnItemUnrealized(LibraryItemViewModel item) => _albumArt.Unrealized(item);

        /// <summary>The song's unsaved changes, without creating an empty draft for every listed song.</summary>
        private MetadataDraft? DraftFor(string path) => _drafts.HasDrafts(path) ? _drafts.Get(path) : null;

        private void OnSelectionPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            // SelectionModel never raises Count. SelectedIndexes covers every change except a
            // source reset, which raises SelectedItem instead.
            if (e.PropertyName is not (nameof(Selection.SelectedIndexes) or nameof(Selection.SelectedItem)))
                return;

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
        }
    }
}
