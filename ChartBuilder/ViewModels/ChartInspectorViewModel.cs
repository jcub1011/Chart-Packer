using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ChartBuilder.Resources;
using ChartPacker.Core.Charting;
using ChartPacker.Core.Charting.Commands;
using ChartPacker.Core.Charts;
using ChartPacker.Core.Library;
using ChartPacker.Core.Metadata;
using ChartPacker.Core.Scanning;
using ChartPacker.Core.Sng;
using ChartPacker.Core.Validation.QuickFixes;
using ChartPacker.Resources;
using ChartPacker.UI.Formatting;
using ChartPacker.UI.Services;
using ChartPacker.UI.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChartBuilder.ViewModels
{
    /// <summary>A file of the song in the Chart Inspector, with its size already formatted.</summary>
    /// <param name="Detail">What it is beside its name, such as a track's stem; null for nothing.</param>
    public sealed record InspectorFileRow(string Name, string? Size, string? Detail = null)
    {
        public bool HasSize => Size is not null;

        public bool HasDetail => Detail is not null;
    }

    /// <summary>
    /// The Chart Inspector, the Editor's left panel's first tab (charter-spec C1, §4.5): the song as Chart Packer's
    /// inspector shows it, without the background, the highway and the lyrics, which the editor shows itself. Its
    /// metadata, ratings, delay and album art are edited here; its tracks and files are listed.
    /// </summary>
    /// <remarks>
    /// Every edit goes to <see cref="EditorSong"/> as a pending change, listed in Pending changes, and Revert takes it
    /// back; none is an undo step. Nothing is written until the song is saved, and while it's being saved nothing can be
    /// edited, as the save moves the song on to what it wrote.
    /// </remarks>
    public sealed partial class ChartInspectorViewModel : ViewModelBase, IMetadataEditorHost, IDisposable
    {
        // The tile is 128px at its largest, so twice that covers 2x scaling with room to spare.
        private const int ArtDecodeWidth = 512;

        // A delay being typed is applied once the typing pauses, as Chart settings' BPM is.
        private static readonly TimeSpan DelayTypingPause = TimeSpan.FromMilliseconds(600);

        // How long the chart must be still before its parts are counted again for the instruments.
        private static readonly TimeSpan PartsDelay = TimeSpan.FromMilliseconds(400);

        private readonly EditorSong _song;
        private readonly ChartUndoStack _undo;
        private readonly IImagePickerService _imagePicker;
        private readonly IChartIssuesWindowService _chartIssues;
        private readonly DispatcherTimer _delayTimer;
        private readonly DispatcherTimer _partsTimer;
        private readonly CancellationTokenSource _closing = new();

        private IReadOnlyList<MetadataItem> _items = [];
        private IReadOnlyDictionary<string, ChartPart>? _chartParts;
        private bool _refreshQueued;
        private bool _syncingDelay;
        private bool _isSaving;

        // What the art tile and the file lists show, so they're read again only when that changes.
        private (AlbumArtChange? Change, OpenedSong? Opened)? _artShown;
        private OpenedSong? _filesShown;
        private bool _filesListed;
        private CancellationTokenSource? _artCts;
        private CancellationTokenSource? _filesCts;
        private CancellationTokenSource? _partsCts;

        public ChartInspectorViewModel(EditorSong song, ChartUndoStack undo, FindingsViewModel findings, IImagePickerService imagePicker, IChartIssuesWindowService chartIssues)
        {
            _song = song;
            _undo = undo;
            _imagePicker = imagePicker;
            _chartIssues = chartIssues;
            Findings = findings;
            MetadataEditor = new MetadataEditorViewModel(this);
            _delayTimer = new DispatcherTimer(DelayTypingPause, DispatcherPriority.Background, (_, _) => ApplyDelay());
            _delayTimer.Stop();
            _partsTimer = new DispatcherTimer(PartsDelay, DispatcherPriority.Background, (_, _) => _ = CountPartsAsync());
            _partsTimer.Stop();

            _song.Changed += OnSongChanged;
            _undo.Changed += OnChartChanged;
            Refresh();
            _ = CountPartsAsync();
        }

        /// <summary>The metadata: the difficulties as icons, and a row per other <c>song.ini</c> key.</summary>
        public MetadataEditorViewModel MetadataEditor { get; }

        /// <summary>
        /// The chart's findings, which the Inspector only counts (GitHub issue #53): View issues lists them in a window of
        /// their own, as the Findings tab does.
        /// </summary>
        public FindingsViewModel Findings { get; }

        /// <summary>"Artist – Name" as the next save leaves them, or the song's file or folder name.</summary>
        [ObservableProperty]
        public partial string Title { get; private set; } = "";

        /// <summary>The charter as the next save leaves it, with its rich-text tags left in for the view to render.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasCharter))]
        public partial string? Charter { get; private set; }

        /// <summary>The song's folder or <c>.sng</c>, or that a new chart has none until it's saved.</summary>
        [ObservableProperty]
        public partial string Subtitle { get; private set; } = "";

        /// <summary>The album art the next save leaves the song with, or null when there's none to show.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasArtImage), nameof(ArtAspect))]
        public partial Bitmap? ArtImage { get; private set; }

        /// <summary>The album art's stored name, e.g. <c>album.png</c>, when the song will have one.</summary>
        [ObservableProperty]
        public partial string? ArtFileName { get; private set; }

        /// <summary>Shown in the tile instead of an image: there's none, or it's removed at the next save.</summary>
        [ObservableProperty]
        public partial string? ArtNote { get; private set; }

        /// <summary>Why the chosen image can't be used, or the song's can't be read.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasArtError))]
        public partial string? ArtError { get; private set; }

        /// <summary>The <c>delay</c> in milliseconds as the box shows it: the song's, or what's being typed.</summary>
        [ObservableProperty]
        public partial decimal? DelayMs { get; set; }

        /// <summary>The song's audio tracks.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasNoTracks))]
        public partial IReadOnlyList<InspectorFileRow> Tracks { get; private set; } = [];

        /// <summary>The song's chart files: normally one, <c>notes.mid</c> or <c>notes.chart</c>.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasNoChartFiles))]
        public partial IReadOnlyList<InspectorFileRow> ChartFiles { get; private set; } = [];

        /// <summary>The song's files that aren't its album art, tracks, chart or <c>song.ini</c>.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasNoOtherFiles))]
        public partial IReadOnlyList<InspectorFileRow> OtherFiles { get; private set; } = [];

        public bool HasCharter => Charter is not null;

        public bool HasArtImage => ArtImage is not null;

        public bool HasArtError => ArtError is not null;

        /// <summary>The art tile's width over its height: the image's, or square when there's none.</summary>
        public double ArtAspect => ArtImage is { Size: { Width: > 0, Height: > 0 } size } ? size.Width / size.Height : 1;

        /// <summary>The album art has a pending change.</summary>
        public bool IsArtChanged => _song.AlbumArt is not null;

        /// <summary><c>*</c> beside the tile while the art has a pending change, as an edited row has.</summary>
        public string? ArtMarker => IsArtChanged ? "*" : null;

        public bool HasNoTracks => Tracks.Count == 0;

        public bool HasNoChartFiles => ChartFiles.Count == 0;

        public bool HasNoOtherFiles => OtherFiles.Count == 0;

        /// <summary>The chart file is a <c>notes.chart</c>, which the first save replaces with a <c>notes.mid</c>.</summary>
        public bool IsImport => _song.IsImport;

        /// <summary>Whether the song can be edited now: not while it's being saved.</summary>
        public bool CanEdit => !_isSaving;

        /// <summary>Whether a save is running, which the song can't be edited during.</summary>
        public bool IsSaving
        {
            get => _isSaving;
            set
            {
                if (_isSaving == value)
                    return;
                _isSaving = value;
                OnPropertyChanged(nameof(CanEdit));
                MetadataEditor.RefreshCommands();
                ReplaceArtCommand.NotifyCanExecuteChanged();
                RemoveArtCommand.NotifyCanExecuteChanged();
                RevertArtCommand.NotifyCanExecuteChanged();
            }
        }

        bool IMetadataEditorHost.IsBulk => false;

        IReadOnlyList<MetadataItem> IMetadataEditorHost.Items => _items;

        IReadOnlyDictionary<string, ChartPart>? IMetadataEditorHost.ChartParts => _chartParts;

        string? IMetadataEditorHost.LoadNote => null;

        // song_length follows the audio on its own (charter-spec C17 item 2).
        IRelayCommand? IMetadataEditorHost.AutoSetSongLengthCommand => null;

        bool IMetadataEditorHost.CanEditKey(string key) => _song.CanEdit(key);

        public void SetValue(string key, string value) => _song.SetValue(key, value);

        public void RemoveKey(string key) => _song.RemoveValue(key);

        public void RevertKey(string key) => _song.Revert(key);

        /// <summary>Unsets the difficulty <paramref name="key"/>: <c>-1</c> where the song has one or will, and nothing otherwise.</summary>
        public void UnsetDifficulty(string key)
        {
            bool has = SongIniValuesOf(_song.Pairs, key) is not null || _song.PendingChanges().Any(c => c.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (has)
                _song.SetValue(key, MetadataDifficulty.UnsetValue);
        }

        /// <summary>
        /// Applies every quick fix for <paramref name="key"/>, worked out on a copy of the song's metadata as the next
        /// save leaves it, then made as edits: whatever the fixes changed.
        /// </summary>
        public void ApplyQuickFixes(string key)
        {
            var before = _song.EffectivePairs();
            var item = new MetadataItem(before, new MetadataDraft(), DuplicateKeysOf(before));
            if (QuickFixRegistry.FixKey(item, key) == 0)
                return;

            foreach (var change in item.Draft.Changes)
            {
                if (change.Value is { } value)
                    _song.SetValue(change.Key, value);
                else
                    _song.RemoveValue(change.Key);
            }
        }

        public void Dispose()
        {
            _song.Changed -= OnSongChanged;
            _undo.Changed -= OnChartChanged;
            _delayTimer.Stop();
            _partsTimer.Stop();
            _closing.Cancel();
            _artCts?.Cancel();
            _filesCts?.Cancel();
            _partsCts?.Cancel();
            var image = ArtImage;
            ArtImage = null;
            image?.Dispose();
        }

        /// <summary>Asks for an image and, if it can be used, puts it in place of the song's album art at the next save.</summary>
        [RelayCommand(CanExecute = nameof(CanEdit))]
        private async Task ReplaceArtAsync()
        {
            string? path = await _imagePicker.PickImageAsync(Strings.InspectorPickArtTitle);
            if (path is null || !CanEdit)
                return;

            ArtError = null;
            if (AlbumArt.StoredNameFor(path) is null)
            {
                ArtError = SharedStrings.ArtNotAnImage;
                return;
            }

            // Read now, so an image that can't be used is said before it's staged rather than at the save.
            var (bitmap, error) = await Task.Run(() => LoadStaged(path));
            bitmap?.Dispose();
            if (error is not null)
            {
                ArtError = error;
                return;
            }

            if (CanEdit)
                _song.SetAlbumArt(path);
        }

        private bool CanRemoveArt() => CanEdit && (_song.AlbumArt is { IsRemoval: false } || (_song.AlbumArt is null && StoredArtName() is not null));

        /// <summary>Removes the song's album art at the next save.</summary>
        [RelayCommand(CanExecute = nameof(CanRemoveArt))]
        private void RemoveArt()
        {
            ArtError = null;
            _song.RemoveAlbumArt();
        }

        private bool CanRevertArt() => CanEdit && IsArtChanged;

        /// <summary>Leaves the song's album art as it is.</summary>
        [RelayCommand(CanExecute = nameof(CanRevertArt))]
        private void RevertArt()
        {
            ArtError = null;
            _song.RevertAlbumArt();
        }

        // A delay being typed waits for the typing to pause; one the song moved to is only shown.
        partial void OnDelayMsChanged(decimal? value)
        {
            if (_syncingDelay)
                return;
            _delayTimer.Stop();
            _delayTimer.Start();
        }

        private void ApplyDelay()
        {
            _delayTimer.Stop();
            if (!CanEdit || DelayMs is not { } typed)
            {
                SyncDelay();
                return;
            }

            long ms = (long)decimal.Round(typed, MidpointRounding.AwayFromZero);
            if (ms != CurrentDelayMs())
                _song.SetDelay(ms);
            else
                SyncDelay();
        }

        private void OnSongChanged(object? sender, EventArgs e) => QueueRefresh();

        /// <summary>Lists the chart's findings, grouped by part and difficulty, in a window of their own.</summary>
        [RelayCommand]
        private void ViewIssues() => _chartIssues.Show(Findings.Issues);

        // A part added or removed changes the ratings the song calls for, and the instruments' icons.
        private void OnChartChanged(object? sender, ChartCommandEventArgs e)
        {
            QueueRefresh();
            _partsTimer.Stop();
            _partsTimer.Start();
        }

        // A burst of changes, as a recovered song's are, is shown once.
        private void QueueRefresh()
        {
            if (_refreshQueued)
                return;
            _refreshQueued = true;
            Dispatcher.UIThread.Post(
                () =>
                {
                    _refreshQueued = false;
                    if (!_closing.IsCancellationRequested)
                        Refresh();
                },
                DispatcherPriority.Background);
        }

        /// <summary>Shows the song as it is now: its headline, metadata, delay, album art and files.</summary>
        private void Refresh()
        {
            var pairs = _song.EffectivePairs();
            _items = [new MetadataItem(_song.Pairs, DraftOf(_song.PendingChanges()), DuplicateKeysOf(_song.Pairs))];
            Title = _song.Title ?? Strings.EditorUntitledChart;
            Findings.Issues.Title = string.Format(CultureInfo.CurrentCulture, SharedStrings.ChartIssuesWindowTitleFormat, Title);
            Charter = SongHeadline.CharterMarkup(pairs);
            Subtitle = _song.Entry?.FullPath ?? Strings.InspectorNewChartPath;
            MetadataEditor.Refresh();
            if (!_delayTimer.IsEnabled)
                SyncDelay();
            OnPropertyChanged(nameof(IsArtChanged));
            OnPropertyChanged(nameof(ArtMarker));
            OnPropertyChanged(nameof(IsImport));
            RemoveArtCommand.NotifyCanExecuteChanged();
            RevertArtCommand.NotifyCanExecuteChanged();

            if (_artShown is not { } shown || shown.Change != _song.AlbumArt || !ReferenceEquals(shown.Opened, _song.Opened))
                _ = LoadArtAsync();
            if (!_filesListed || !ReferenceEquals(_filesShown, _song.Opened))
                _ = ListFilesAsync();
        }

        private void SyncDelay()
        {
            _syncingDelay = true;
            try
            {
                DelayMs = CurrentDelayMs();
            }
            finally
            {
                _syncingDelay = false;
            }
        }

        private long CurrentDelayMs() => (long)Math.Round(_song.Delay.TotalMilliseconds, MidpointRounding.AwayFromZero);

        /// <summary>Counts the chart's parts and their notes, off the UI thread, for the instruments' icons and captions.</summary>
        private async Task CountPartsAsync()
        {
            _partsTimer.Stop();
            _partsCts?.Cancel();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
            _partsCts = cts;

            // The chart belongs to the UI thread, so it's copied here, and read as the next save writes it.
            var chart = _song.Chart.Clone();
            var options = ChartReadOptions.From(_song.EffectivePairs());
            IReadOnlyDictionary<string, ChartPart>? parts;
            try
            {
                parts = await Task.Run(() => ChartContents.ReadParts(MidiChartWriter.Write(chart), ChartPacker.Core.Scanning.ChartFiles.MidiName, options), cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is ChartFormatException or InvalidOperationException or ArgumentException)
            {
                // A chart that can't be written yet: the icons show the ratings alone.
                parts = null;
            }
            finally
            {
                if (_partsCts == cts)
                    _partsCts = null;
            }

            if (cts.IsCancellationRequested)
                return;
            _chartParts = parts;
            MetadataEditor.RefreshInstruments();
        }

        /// <summary>Shows the album art the next save leaves the song with, read off the UI thread.</summary>
        private async Task LoadArtAsync()
        {
            _artCts?.Cancel();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
            _artCts = cts;
            try
            {
                await ShowArtAsync(cts.Token);
            }
            finally
            {
                if (_artCts == cts)
                    _artCts = null;
            }
        }

        private async Task ShowArtAsync(CancellationToken cancellationToken)
        {
            var change = _song.AlbumArt;
            var opened = _song.Opened;
            _artShown = (change, opened);

            string? name;
            Bitmap? bitmap = null;
            string? note = null;
            string? error = null;
            if (change is { SourcePath: { } path })
            {
                name = AlbumArt.StoredNameFor(path);
                (bitmap, error) = await Task.Run(() => LoadStaged(path));
            }
            else if (change is { IsRemoval: true } || opened is null || StoredArtName() is null)
            {
                name = null;
                note = change is { IsRemoval: true } ? Strings.InspectorArtRemoved : Strings.InspectorNoArt;
            }
            else
            {
                name = StoredArtName();
                try
                {
                    byte[]? bytes = await AlbumArt.ReadAsync(opened.Entry, AlbumArt.DefaultMaxBytes, cancellationToken);
                    bitmap = bytes is null ? null : await Task.Run(() => Decode(bytes), cancellationToken);
                    if (bitmap is null)
                        note = Strings.InspectorArtUnreadable;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    note = Strings.InspectorArtUnreadable;
                    error = string.Format(CultureInfo.CurrentCulture, SharedStrings.ArtUnreadableFormat, ex.Message);
                }
            }

            if (cancellationToken.IsCancellationRequested)
            {
                bitmap?.Dispose();
                return;
            }

            var old = ArtImage;
            ArtImage = bitmap;
            old?.Dispose();
            ArtFileName = name;
            ArtNote = bitmap is null ? note ?? Strings.InspectorArtUnreadable : null;
            ArtError = error;
        }

        /// <summary>Lists the song's tracks, chart files and other files, with their sizes, read off the UI thread.</summary>
        private async Task ListFilesAsync()
        {
            _filesCts?.Cancel();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
            _filesCts = cts;
            var opened = _song.Opened;
            var tracks = _song.Tracks;
            _filesShown = opened;
            _filesListed = true;

            IReadOnlyDictionary<string, long> sizes;
            try
            {
                sizes = await Task.Run(() => SizesOf(opened, tracks, cts.Token), cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            finally
            {
                if (_filesCts == cts)
                    _filesCts = null;
            }

            if (cts.IsCancellationRequested)
                return;

            string? SizeOf(string key) => sizes.TryGetValue(key, out long size) ? ByteSizeFormatter.Format((ulong)size) : null;
            Tracks = [.. tracks.Select(t => t.StagedSourcePath is { } staged
                ? new InspectorFileRow(Path.GetFileName(staged), SizeOf(staged), t.Stem)
                : new InspectorFileRow(t.StoredName, SizeOf(t.StoredName), t.Stem))];

            var stored = opened?.StoredNames ?? [];
            var trackNames = tracks.Where(t => !t.IsStaged).Select(t => t.StoredName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            ChartFiles = [.. ChartPacker.Core.Scanning.ChartFiles.FilesOf(stored).Select(n => new InspectorFileRow(n, SizeOf(n)))];
            OtherFiles = [.. stored
                .Where(n => !trackNames.Contains(n) && !ChartPacker.Core.Scanning.ChartFiles.IsChartName(n) && !AlbumArt.IsAlbumArtName(n)
                    && !n.Equals("song.ini", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.OrdinalIgnoreCase)
                .Select(n => new InspectorFileRow(n, SizeOf(n)))];
        }

        // Each file's size: by stored name for the song's, by path for a new chart's tracks. One that can't be read has none.
        private static IReadOnlyDictionary<string, long> SizesOf(OpenedSong? opened, IReadOnlyList<SongTrack> tracks, CancellationToken cancellationToken)
        {
            var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (opened?.Entry is { Kind: SongEntryKind.SngFile } sng)
                {
                    foreach (var file in SngReader.Read(sng.FullPath, cancellationToken).Files)
                        sizes[file.Name] = (long)file.Length;
                }
                else if (opened?.Entry is { } folder)
                {
                    foreach (string name in opened.StoredNames)
                    {
                        var info = new FileInfo(Path.Combine(folder.FullPath, name));
                        if (info.Exists)
                            sizes[name] = info.Length;
                    }
                }

                foreach (var track in tracks)
                {
                    if (track.StagedSourcePath is { } path && new FileInfo(path) is { Exists: true } info)
                        sizes[path] = info.Length;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }

            return sizes;
        }

        private string? StoredArtName() => _song.Opened is { } opened ? AlbumArt.Pick(opened.StoredNames, n => n) : null;

        /// <summary>The image at <paramref name="path"/>, decoded at tile size, or why it can't be used.</summary>
        private static (Bitmap? Bitmap, string? Error) LoadStaged(string path)
        {
            byte[] bytes;
            try
            {
                long length = new FileInfo(path).Length;
                if (length > AlbumArt.DefaultMaxBytes)
                    return (null, string.Format(CultureInfo.CurrentCulture, SharedStrings.ArtTooLargeFormat, ByteSizeFormatter.Format((ulong)AlbumArt.DefaultMaxBytes)));
                bytes = File.ReadAllBytes(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return (null, string.Format(CultureInfo.CurrentCulture, SharedStrings.ArtUnreadableFormat, ex.Message));
            }

            return Decode(bytes) is { } bitmap ? (bitmap, null) : (null, SharedStrings.ArtNotAnImage);
        }

        /// <summary>The image decoded at tile size, or null when it can't be decoded.</summary>
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

        // The pending changes as a draft over the song.ini as opened, so each row shows what the next save does to it.
        private static MetadataDraft DraftOf(IEnumerable<PendingSongIniChange> changes)
        {
            var draft = new MetadataDraft();
            foreach (var change in changes)
            {
                if (change.Value is { } value)
                    draft.Set(change.Key, value);
                else
                    draft.Remove(change.Key);
            }

            return draft;
        }

        // The keys song.ini has more than once, which validation marks.
        private static IReadOnlyCollection<string> DuplicateKeysOf(IReadOnlyList<KeyValuePair<string, string>> pairs) =>
            [.. pairs.GroupBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key)];

        private static string? SongIniValuesOf(IReadOnlyList<KeyValuePair<string, string>> pairs, string key) =>
            pairs.LastOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;
    }
}
