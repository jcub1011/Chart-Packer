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
using ChartPacker.Core.Ini;
using ChartPacker.Core.Metadata;
using ChartPacker.Core.Scanning;
using ChartPacker.Core.Sng;
using ChartPacker.Core.Validation;
using ChartPacker.Formatting;
using ChartPacker.Resources;
using ChartPacker.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChartPacker.ViewModels
{
    /// <summary>A file in the song, with its size already formatted.</summary>
    /// <param name="Marker"><c>+</c> for a file a staged change adds.</param>
    /// <param name="IsRemoved">A staged change removes or replaces the file.</param>
    /// <param name="CanRemove">The file can be removed, or, when it's added, the add dropped.</param>
    /// <param name="CanRevert">The staged change that removes or replaces the file can be dropped.</param>
    public sealed record FileRow(string Path, string Size, string? Marker = null, bool IsRemoved = false, bool CanRemove = false, bool CanRevert = false)
    {
        public bool IsAdded => Marker is not null;
    }

    /// <summary>A chart file of the song, with its size already formatted.</summary>
    /// <param name="Marker"><c>+</c> for a chart a staged change adds.</param>
    /// <param name="IsRemoved">A staged change removes or replaces the file.</param>
    public sealed record ChartRow(string Name, string Size, string? Marker = null, bool IsRemoved = false)
    {
        public bool IsKept => !IsRemoved;
    }

    /// <summary>A track a staged change removes, shown struck through so that its removal can be undone.</summary>
    public sealed record RemovedTrackRow(string Name, string Size);

    /// <summary>A structural problem found in a <c>.sng</c>.</summary>
    public sealed record FindingRow(string RuleId, string Message, bool IsError);

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

        // A square tile at its 512px largest, at 2x scaling.
        private const int ArtDecodeWidth = 1024;

        private readonly MetadataDraftStore _drafts;
        private readonly JobViewModel _job;
        private readonly IImagePickerService _imagePicker;
        private readonly IFilePickerService _filePicker;
        private CancellationTokenSource? _loadCts;

        // What's shown: the selection, and the songs among it whose metadata could be read.
        private IReadOnlyList<SongEntry> _entries = [];
        private IReadOnlyList<EditableSong> _songs = [];

        // Why there's no metadata, from loading, e.g. the folder has no song.ini.
        private string? _loadNote;

        // The one selected song's files as they are on disk; the tracks, charts and other files add the staged changes to them.
        private IReadOnlyList<DiskFile> _diskFiles = [];

        // The one selected song's total size on disk.
        private ulong _totalSize;

        // Decoded album art by image hash: the first song's art, and every staged replacement.
        private Dictionary<string, Bitmap> _artImages = [];

        // Staged replacement images by source path.
        private Dictionary<string, StagedArt> _stagedArt = new(StringComparer.Ordinal);

        // One command per stem for the Add track menu, whose labels change with the song's tracks.
        private readonly Dictionary<string, IAsyncRelayCommand> _addTrackCommands = [];

        public SongDetailViewModel(MetadataDraftStore drafts, JobViewModel job, IImagePickerService imagePicker, IFilePickerService filePicker, PlayerViewModel player)
        {
            _drafts = drafts;
            _job = job;
            _imagePicker = imagePicker;
            _filePicker = filePicker;
            Player = player;
            Background = new BackgroundViewModel(() => Player.CurrentPosition);
            Background.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(BackgroundViewModel.ShownKind))
                    RemoveBackgroundCommand.NotifyCanExecuteChanged();
            };
            foreach (string stem in AudioStems.Stems)
                _addTrackCommands[stem] = new AsyncRelayCommand(() => AddTrackAsync(stem), CanChangeFiles);
            _job.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(JobViewModel.IsRunning))
                    RefreshCommands();
            };
        }

        public AvaloniaList<MetadataRowViewModel> Metadata { get; } = [];

        /// <summary>The one selected song's files that aren't its album art, background, tracks, chart or song.ini.</summary>
        public AvaloniaList<FileRow> Files { get; } = [];

        /// <summary>The one selected song's chart files: normally one, <c>notes.chart</c> or <c>notes.mid</c>.</summary>
        public AvaloniaList<ChartRow> Charts { get; } = [];

        /// <summary>The one selected song's tracks that a staged change removes, and nothing replaces.</summary>
        public AvaloniaList<RemovedTrackRow> RemovedTracks { get; } = [];

        public AvaloniaList<FindingRow> Findings { get; } = [];

        /// <summary>Plays the one selected song's audio tracks.</summary>
        public PlayerViewModel Player { get; }

        /// <summary>The selected songs' background image or video, beside the album art.</summary>
        public BackgroundViewModel Background { get; }

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

        /// <summary>Why some of the chosen files couldn't be added.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasFilesError))]
        public partial string? FilesError { get; set; }

        /// <summary>Why the chosen file couldn't be added as a track.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasTracksError))]
        public partial string? TracksError { get; set; }

        public bool HasArtImage => ArtImage is not null;

        /// <summary>The art tile's width over its height: the shown image's, or square when there's none.</summary>
        public double ArtAspect => ArtImage is { Size: { Width: > 0, Height: > 0 } size } ? size.Width / size.Height : 1;

        public bool HasCharter => Charter is not null;

        public bool HasSizeText => SizeText is not null;

        public bool HasChartError => ChartError is not null;

        public bool HasArtError => ArtError is not null;

        public bool HasBackgroundError => BackgroundError is not null;

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

        public bool ShowNoOtherFiles => !IsBulk && Files.Count == 0;

        public bool HasRemovedTracks => RemovedTracks.Count > 0;

        /// <summary>The song will have no chart once its draft is saved.</summary>
        public bool ShowChartMissing => !IsBulk && Charts.All(c => c.IsRemoved);

        /// <summary>Why there's no chart: there's none, or a staged change removes it.</summary>
        public string ChartMissingNote => Charts.Count > 0 ? Strings.ChartWillBeRemoved : Strings.NoChart;

        /// <summary>The one selected song's chart has a staged change.</summary>
        public bool IsChartChanged => Charts.Any(c => c.IsRemoved || c.Marker is not null);

        /// <summary>Every selected song was read and can be written back.</summary>
        public bool IsEditable => _songs.Count > 0 && _songs.Count == _entries.Count && ReadOnlyNote is null;

        public bool CanEdit => IsEditable && !IsSaving;

        public bool ShowAddRow => CanEdit && !IsAdding;

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
        public async Task ShowAsync(IReadOnlyList<SongEntry> entries)
        {
            _loadCts?.Cancel();
            using var cts = new CancellationTokenSource();
            _loadCts = cts;

            // Playback is of what's shown, and holds its files open; so does the background video.
            Player.Unload();
            Background.Unload();

            _entries = entries;
            _songs = [];
            _loadNote = null;
            _diskFiles = [];
            _totalSize = 0;
            ReleaseArt();
            Metadata.Clear();
            Files.Clear();
            Charts.Clear();
            RemovedTracks.Clear();
            Findings.Clear();
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
            FilesError = null;
            TracksError = null;
            ChartError = null;
            CancelAdd();
            IsLoading = entries.Count > 0;
            RefreshState();

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
                                selection.Songs.Select(s => s.Pairs is null ? null : s.Entry).ToList(), stagedBackgrounds, cts.Token);
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
                    Apply(loaded);
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

        /// <summary>Ends editing on every row but <paramref name="row"/>, dropping text that wasn't committed.</summary>
        internal void CancelOtherEdits(MetadataRowViewModel row)
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
                ArtError = Strings.ArtNotAnImage;
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
            foreach (string stored in ChartFiles.FilesOf(_diskFiles.Select(f => f.Path)))
                draft.RemoveFile(stored);
            ChartError = null;
            OnDraftsChanged();
        }

        private bool CanRevertChart() => CanChangeFiles() && IsChartChanged;

        /// <summary>Drops every staged change to the selected song's chart.</summary>
        [RelayCommand(CanExecute = nameof(CanRevertChart))]
        private void RevertChart()
        {
            var draft = _songs[0].Item.Draft;
            RevertAddedCharts(draft);
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

        /// <summary>Writes the selected songs' drafts. Songs that fail keep their drafts.</summary>
        [RelayCommand(CanExecute = nameof(CanSave))]
        private async Task SaveAsync()
        {
            var entries = _entries;
            var dirty = _songs.Where(s => !s.Item.Draft.IsEmpty).ToList();
            var failures = new List<string>();

            CancelAdd();
            IsSaving = true;
            SaveError = null;

            // Playback holds the song's files open, which would stop them being replaced or deleted;
            // so does the background video.
            Player.Unload();
            Background.Unload();
            try
            {
                await Player.WhenFilesClosedAsync();
                await Background.WhenClosedAsync();

                foreach (var song in dirty)
                {
                    string? error;
                    try
                    {
                        error = await Task.Run(() => SaveSongAsync(song));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                    {
                        error = ex.Message;
                    }

                    if (error is null)
                    {
                        song.Item.Draft.Clear();
                        _drafts.NotifyChanged(song.Entry.FullPath);
                    }
                    else
                    {
                        failures.Add(string.Format(CultureInfo.CurrentCulture, Strings.SaveFailedItemFormat, song.Entry.DisplayName, error));
                    }
                }
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

        private bool CanDiscard() => HasDrafts && !IsSaving;

        [RelayCommand(CanExecute = nameof(CanDiscard))]
        private void Discard()
        {
            CancelAdd();
            BackgroundError = null;
            FilesError = null;
            TracksError = null;
            ChartError = null;
            foreach (var song in _songs)
                song.Item.Draft.Clear();
            OnDraftsChanged();
        }

        /// <summary>Re-reads the song and writes it back with only the draft's keys, album art and files changed.</summary>
        /// <returns>Why it couldn't be saved, or <see langword="null"/> on success.</returns>
        private static async Task<string?> SaveSongAsync(EditableSong song)
        {
            var draft = song.Item.Draft;
            if (song.Entry.Kind == SongEntryKind.SngFile)
                return FirstError(await DraftWriter.SaveSngAsync(song.Entry.FullPath, draft).ConfigureAwait(false));

            await DraftWriter.SaveFolderAsync(song.Entry.FullPath, song.SongIniPath!, draft).ConfigureAwait(false);
            return null;
        }

        private static string? FirstError(IEnumerable<Finding> findings) =>
            findings.FirstOrDefault(f => f.Severity == Severity.Error)?.Message;

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
            foreach (var song in _songs)
                _drafts.NotifyChanged(song.Entry.FullPath);
            RebuildRows();
            RefreshArt();
        }

        private void Apply(LoadedSelection loaded)
        {
            _songs = loaded.Songs
                .Select((s, i) => (Song: s, ArtHash: loaded.Art.DiskHashes[i], Backgrounds: loaded.Backgrounds.Stored.ElementAtOrDefault(i) ?? StoredBackgrounds.None))
                .Where(x => x.Song.Pairs is not null)
                .Select(x => new EditableSong(x.Song.Entry, new MetadataItem(x.Song.Pairs!, _drafts.Get(x.Song.Entry.FullPath)), x.Song.SongIniPath, x.ArtHash, x.Backgrounds))
                .ToList();
            _artImages = loaded.Art.Images;
            _stagedArt = loaded.Art.Staged;
            _diskFiles = loaded.Files;
            _totalSize = loaded.TotalSize;
            Background.Apply(loaded.Backgrounds);

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
            ReadOnlyNote = loaded.Songs.Select(s => s.ReadOnlyReason).FirstOrDefault(r => r is not null);
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

            Findings.AddRange(loaded.Findings);
            RebuildRows();
            RefreshArt();
        }

        /// <summary>
        /// Merges the songs' metadata and drafts into rows, updating the existing row view models in
        /// place so that a row being clicked isn't replaced under the pointer.
        /// </summary>
        private void RebuildRows()
        {
            var byKey = Metadata.ToDictionary(r => r.Key, StringComparer.OrdinalIgnoreCase);
            var next = BulkMetadata.BuildRows(Items())
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

            MetadataNote = _loadNote ?? (Metadata.Count == 0 ? Strings.NoMetadata : null);
            RefreshAvailableKeys();
            RefreshHeadline();
            RefreshState();
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
        /// adding it back is allowed; a hidden unset difficulty is offered too, and adding it edits it.
        /// This is the same test <see cref="CommitAdd"/> uses to reject a key.
        /// </summary>
        private void RefreshAvailableKeys()
        {
            var used = Metadata.Where(r => !r.IsRemoved).Select(r => r.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var available = KnownKeys.All.Where(k => !used.Contains(k.Key)).ToList();
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
                    [SngSong(entry, result)],
                    size,
                    result.Files.Select(f => new DiskFile(f.Name, f.Length)).ToList(),
                    result.Findings.Select(f => new FindingRow(f.RuleId, f.Message, f.Severity == Severity.Error)).ToList(),
                    MetadataNote: null);
            }

            var files = await SongFolderListing.ListAsync(entry.FullPath, cancellationToken).ConfigureAwait(false);

            // Only a song.ini directly in the folder counts; one in a subfolder belongs to something else.
            var songIni = files.FirstOrDefault(f => f.RelativePath.Equals(SongIni.FileName, StringComparison.OrdinalIgnoreCase));
            var (song, note) = await LoadFolderSongAsync(
                entry, songIni is null ? null : Path.Combine(entry.FullPath, songIni.RelativePath), cancellationToken).ConfigureAwait(false);

            return new LoadedSelection(
                [song],
                (ulong)files.Sum(f => f.Length),
                files.Select(f => new DiskFile(f.RelativePath, (ulong)f.Length)).ToList(),
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
                    songs[i] = entry.Kind == SongEntryKind.SngFile
                        ? SngSong(entry, await SngReader.ReadAsync(entry.FullPath, token).ConfigureAwait(false))
                        : (await LoadFolderSongAsync(entry, FindSongIni(entry.FullPath), token).ConfigureAwait(false)).Song;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    songs[i] = new LoadedSong(entry, null, null,
                        string.Format(CultureInfo.CurrentCulture, Strings.ItemUnreadableFormat, entry.DisplayName, ex.Message));
                }
            }).ConfigureAwait(false);

            return new LoadedSelection(songs, 0, [], [], MetadataNote: null);
        }

        private static LoadedSong SngSong(SongEntry entry, SngReadResult result) => new(
            entry,
            result.Metadata.Select(p => KeyValuePair.Create(p.Key, p.Value)).ToList(),
            SongIniPath: null,
            result.HasErrors ? string.Format(CultureInfo.CurrentCulture, Strings.SngHasErrorsFormat, entry.DisplayName) : null);

        /// <summary>Reads a folder's song.ini. With none, the metadata is empty and saving creates one.</summary>
        private static async Task<(LoadedSong Song, string? Note)> LoadFolderSongAsync(SongEntry entry, string? songIniPath, CancellationToken cancellationToken)
        {
            if (songIniPath is null)
                return (new LoadedSong(entry, [], Path.Combine(entry.FullPath, SongIni.FileName), null), Strings.NoSongIni);

            var result = await SongIni.ReadAsync(songIniPath, cancellationToken).ConfigureAwait(false);
            return result.Status switch
            {
                SongIniStatus.TooLarge => (
                    new LoadedSong(entry, [], songIniPath, string.Format(CultureInfo.CurrentCulture, Strings.SongIniTooLargeFormat, entry.DisplayName)),
                    Strings.SongIniTooLarge),
                SongIniStatus.NoSongSection => (new LoadedSong(entry, [], songIniPath, null), Strings.SongIniNoSongSection),
                _ => (new LoadedSong(entry, result.Pairs, songIniPath, null), null),
            };
        }

        /// <summary>The folder's own song.ini, matched ignoring case, without listing the whole folder.</summary>
        private static string? FindSongIni(string folder) =>
            Directory.EnumerateFiles(folder)
                .FirstOrDefault(f => Path.GetFileName(f).Equals(SongIni.FileName, StringComparison.OrdinalIgnoreCase));

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
            Background.Refresh(_songs.Select(BackgroundSongOf).ToList());
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
                .Select(f => new ChartRow(f.Path, ByteSizeFormatter.Format(f.Length), IsRemoved: IsRemoved(f.Path)))
                .ToList();
            if (draft is not null)
            {
                charts.InsertRange(0, draft.AddedFiles
                    .Where(f => ChartFiles.IsChartName(f.StoredName))
                    .Select(f => new ChartRow(f.StoredName, ByteSizeFormatter.Format((ulong)f.Length), Marker: "+")));
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
                        IsRemoved: removed, CanRemove: draft is not null && !removed, CanRevert: draft is not null && removed);
                })
                .ToList();

            // Added tracks and charts are shown with the tracks and the chart; staged art and backgrounds on their tiles.
            if (draft is not null)
            {
                rows.AddRange(draft.AddedFiles
                    .Where(f => AudioStems.StemOf(f.StoredName) is null && !ChartFiles.IsChartName(f.StoredName))
                    .Select(f => new FileRow(f.StoredName, ByteSizeFormatter.Format((ulong)f.Length), Marker: "+", CanRemove: true)));
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
                RemovedTracks.Clear();
                OnPropertyChanged(nameof(HasRemovedTracks));
                AddTrackOptions = [];
                return;
            }

            var song = _songs.Count == 1 ? _songs[0] : null;
            var draft = song?.Item.Draft;
            var tracks = SongTracks.Resolve(_diskFiles.Select(f => f.Path), draft);
            Player.Load(_entries[0], tracks, song is null ? null : PreviewStartOf(song));
            foreach (var row in Player.Tracks)
                row.Size = TrackSize(row.Track, draft);

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
            AddTrackOptions = AudioStems.Stems
                .Select(stem => new StemOption(
                    stem,
                    stems.Contains(stem) ? string.Format(CultureInfo.CurrentCulture, Strings.ReplaceTrackFormat, stem) : stem,
                    _addTrackCommands[stem]))
                .ToList();
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
                    return (new StagedArt(null, length, string.Format(CultureInfo.CurrentCulture, Strings.ArtTooLargeFormat, limit)), null);
                }

                bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return (new StagedArt(null, 0, string.Format(CultureInfo.CurrentCulture, Strings.ArtUnreadableFormat, ex.Message)), null);
            }

            return Decode(bytes) is { } bitmap
                ? (new StagedArt(Hash(bytes), bytes.Length, null), bitmap)
                : (new StagedArt(null, bytes.Length, Strings.ArtNotAnImage), null);
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
            OnPropertyChanged(nameof(HasError));
            OnPropertyChanged(nameof(ShowDetails));
            OnPropertyChanged(nameof(HasFindings));
            OnPropertyChanged(nameof(HasMetadataNote));
            OnPropertyChanged(nameof(HasReadOnlyNote));
            OnPropertyChanged(nameof(ShowSongFiles));
            OnPropertyChanged(nameof(ShowNoOtherFiles));
            OnPropertyChanged(nameof(ShowChartMissing));
            OnPropertyChanged(nameof(IsEditable));
            OnPropertyChanged(nameof(CanEdit));
            OnPropertyChanged(nameof(ShowAddRow));
            OnPropertyChanged(nameof(HasDrafts));
            OnPropertyChanged(nameof(MetadataHeader));
            OnPropertyChanged(nameof(OtherFilesHeader));
            OnPropertyChanged(nameof(IsArtChanged));
            OnPropertyChanged(nameof(ArtMarker));
            OnPropertyChanged(nameof(IsBackgroundChanged));
            RefreshCommands();
        }

        private void RefreshCommands()
        {
            SaveCommand.NotifyCanExecuteChanged();
            DiscardCommand.NotifyCanExecuteChanged();
            BeginAddCommand.NotifyCanExecuteChanged();
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
            foreach (var command in _addTrackCommands.Values)
                command.NotifyCanExecuteChanged();
            foreach (var row in Metadata)
                row.RefreshCommands();
        }

        /// <summary>A selected song whose metadata was read, with its draft.</summary>
        /// <param name="SongIniPath">For a folder, the song.ini to edit (which may not exist yet).</param>
        /// <param name="DiskArtHash">The hash of its album art as stored, or <see langword="null"/> when it has none that could be read.</param>
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
        private sealed record LoadedSong(SongEntry Entry, IReadOnlyList<KeyValuePair<string, string>>? Pairs, string? SongIniPath, string? ReadOnlyReason);

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
