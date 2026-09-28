using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Collections;
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
    public sealed record FileRow(string Path, string Size);

    /// <summary>A structural problem found in a <c>.sng</c>.</summary>
    public sealed record FindingRow(string RuleId, string Message, bool IsError);

    /// <summary>
    /// The right panel: the metadata of the songs selected in the library, editable for one song
    /// or for all of them at once, plus the file list when one song is selected. For a <c>.sng</c>
    /// both come from the container; for a folder, from its <c>song.ini</c> and a listing.
    /// </summary>
    /// <remarks>
    /// Edits go into each song's <see cref="MetadataDraft"/> in the shared
    /// <see cref="MetadataDraftStore"/>, so they survive a change of selection. Nothing is written
    /// until <see cref="SaveCommand"/>, which re-reads each file and changes only the edited keys.
    /// </remarks>
    public partial class SongDetailViewModel : ViewModelBase
    {
        private const int BulkLoadParallelism = 4;

        private readonly MetadataDraftStore _drafts;
        private readonly JobViewModel _job;
        private CancellationTokenSource? _loadCts;

        // What's shown: the selection, and the songs among it whose metadata could be read.
        private IReadOnlyList<SongEntry> _entries = [];
        private IReadOnlyList<EditableSong> _songs = [];

        // Why there's no metadata, from loading, e.g. the folder has no song.ini.
        private string? _loadNote;

        public SongDetailViewModel(MetadataDraftStore drafts, JobViewModel job)
        {
            _drafts = drafts;
            _job = job;
            _job.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(JobViewModel.IsRunning))
                    RefreshCommands();
            };
        }

        public AvaloniaList<MetadataRowViewModel> Metadata { get; } = new();

        public AvaloniaList<FileRow> Files { get; } = new();

        public AvaloniaList<FindingRow> Findings { get; } = new();

        /// <summary>The song's name, or how many items are selected.</summary>
        [ObservableProperty]
        private string? _title;

        /// <summary>The full path and total size, or how many of each kind are selected.</summary>
        [ObservableProperty]
        private string? _subtitle;

        [ObservableProperty]
        private bool _isLoading;

        [ObservableProperty]
        private string? _errorMessage;

        /// <summary>Why there is no metadata table, e.g. the folder has no song.ini.</summary>
        [ObservableProperty]
        private string? _metadataNote;

        /// <summary>Why the metadata can't be edited, e.g. a selected .sng has structural errors.</summary>
        [ObservableProperty]
        private string? _readOnlyNote;

        /// <summary>More than one item is selected: only the merged metadata is shown.</summary>
        [ObservableProperty]
        private bool _isBulk;

        [ObservableProperty]
        private bool _isSaving;

        [ObservableProperty]
        private string? _saveError;

        /// <summary>The new-row editor is open.</summary>
        [ObservableProperty]
        private bool _isAdding;

        [ObservableProperty]
        private string _newKey = "";

        [ObservableProperty]
        private string _newValue = "";

        [ObservableProperty]
        private string? _newRowError;

        /// <summary>Known keys to offer for a new row: every one the selection doesn't already use.</summary>
        [ObservableProperty]
        private IReadOnlyList<KnownKey> _availableKeys = [];

        public bool ShowPlaceholder => _entries.Count == 0;

        public bool HasError => ErrorMessage is not null;

        public bool ShowDetails => _entries.Count > 0 && !IsLoading && !HasError;

        public bool HasFindings => Findings.Count > 0;

        public bool HasMetadataNote => MetadataNote is not null;

        public bool HasReadOnlyNote => ReadOnlyNote is not null;

        public bool HasSaveError => SaveError is not null;

        public bool HasNewRowError => NewRowError is not null;

        public bool ShowFiles => !IsBulk;

        public bool ShowNoFiles => !IsBulk && Files.Count == 0;

        /// <summary>Every selected song was read and can be written back.</summary>
        public bool IsEditable => _songs.Count > 0 && _songs.Count == _entries.Count && ReadOnlyNote is null;

        public bool CanEdit => IsEditable && !IsSaving;

        public bool ShowAddRow => CanEdit && !IsAdding;

        /// <summary>A selected song has unsaved changes.</summary>
        public bool HasDrafts => _songs.Any(s => !s.Item.Draft.IsEmpty);

        public string MetadataHeader => string.Format(CultureInfo.CurrentCulture, Strings.MetadataHeaderFormat, Metadata.Count);

        public string FilesHeader => string.Format(CultureInfo.CurrentCulture, Strings.FilesHeaderFormat, Files.Count);

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

            _entries = entries;
            _songs = [];
            _loadNote = null;
            Metadata.Clear();
            Files.Clear();
            Findings.Clear();
            Title = entries.Count switch
            {
                0 => Strings.DetailPlaceholderTitle,
                1 => entries[0].DisplayName,
                _ => string.Format(CultureInfo.CurrentCulture, Strings.MultipleSelectedFormat, entries.Count),
            };
            IsBulk = entries.Count > 1;
            Subtitle = null;
            MetadataNote = null;
            ReadOnlyNote = null;
            ErrorMessage = null;
            SaveError = null;
            CancelAdd();
            IsLoading = entries.Count > 0;
            RefreshState();

            if (entries.Count == 0)
            {
                _loadCts = null;
                return;
            }

            try
            {
                var loaded = await Task.Run(
                    () => entries.Count == 1 ? LoadSingleAsync(entries[0], cts.Token) : LoadBulkAsync(entries, cts.Token),
                    cts.Token);

                if (_loadCts == cts)
                    Apply(loaded);
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
            try
            {
                foreach (var song in dirty)
                {
                    string? error;
                    try
                    {
                        error = await Task.Run(() => SaveSongAsync(song));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
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
            foreach (var song in _songs)
                song.Item.Draft.Clear();
            OnDraftsChanged();
        }

        /// <summary>Re-reads the song and writes it back with only the draft's keys changed.</summary>
        /// <returns>Why it couldn't be saved, or <see langword="null"/> on success.</returns>
        private static async Task<string?> SaveSongAsync(EditableSong song)
        {
            if (song.Entry.Kind == SongEntryKind.SngFile)
            {
                var read = await SngReader.ReadAsync(song.Entry.FullPath).ConfigureAwait(false);
                if (FirstError(read.Findings) is { } readError)
                    return readError;

                var pairs = song.Item.Draft.ApplyTo(read.Metadata.Select(p => KeyValuePair.Create(p.Key, p.Value)).ToList());
                var findings = await SngWriter.RewriteMetadataAsync(song.Entry.FullPath, pairs).ConfigureAwait(false);
                return FirstError(findings);
            }

            await SongIni.ApplyEditsAsync(song.SongIniPath!, song.Item.Draft).ConfigureAwait(false);
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

        private List<MetadataItem> Items() => _songs.Select(s => s.Item).ToList();

        private void OnDraftsChanged()
        {
            foreach (var song in _songs)
                _drafts.NotifyChanged(song.Entry.FullPath);
            RebuildRows();
        }

        private void Apply(LoadedSelection loaded)
        {
            _songs = loaded.Songs
                .Where(s => s.Pairs is not null)
                .Select(s => new EditableSong(s.Entry, new MetadataItem(s.Pairs!, _drafts.Get(s.Entry.FullPath)), s.SongIniPath))
                .ToList();
            ReadOnlyNote = loaded.Songs.Select(s => s.ReadOnlyReason).FirstOrDefault(r => r is not null);
            _loadNote = loaded.MetadataNote;

            if (IsBulk)
            {
                int sngCount = _entries.Count(e => e.Kind == SongEntryKind.SngFile);
                Subtitle = string.Format(CultureInfo.CurrentCulture, Strings.BulkSubtitleFormat, sngCount, _entries.Count - sngCount);
            }
            else
            {
                Subtitle = string.Format(CultureInfo.CurrentCulture, Strings.SubtitleFormat, _entries[0].FullPath, ByteSizeFormatter.Format(loaded.TotalSize));
            }

            Files.AddRange(loaded.Files);
            Findings.AddRange(loaded.Findings);
            RebuildRows();
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
            RefreshState();
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
                    result.Files.Select(f => new FileRow(f.Name, ByteSizeFormatter.Format(f.Length))).ToList(),
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
                files.Select(f => new FileRow(f.RelativePath, ByteSizeFormatter.Format((ulong)f.Length))).ToList(),
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

        /// <summary>Raises change notifications for every derived display state.</summary>
        private void RefreshState()
        {
            OnPropertyChanged(nameof(ShowPlaceholder));
            OnPropertyChanged(nameof(HasError));
            OnPropertyChanged(nameof(ShowDetails));
            OnPropertyChanged(nameof(HasFindings));
            OnPropertyChanged(nameof(HasMetadataNote));
            OnPropertyChanged(nameof(HasReadOnlyNote));
            OnPropertyChanged(nameof(ShowFiles));
            OnPropertyChanged(nameof(ShowNoFiles));
            OnPropertyChanged(nameof(IsEditable));
            OnPropertyChanged(nameof(CanEdit));
            OnPropertyChanged(nameof(ShowAddRow));
            OnPropertyChanged(nameof(HasDrafts));
            OnPropertyChanged(nameof(MetadataHeader));
            OnPropertyChanged(nameof(FilesHeader));
            RefreshCommands();
        }

        private void RefreshCommands()
        {
            SaveCommand.NotifyCanExecuteChanged();
            DiscardCommand.NotifyCanExecuteChanged();
            BeginAddCommand.NotifyCanExecuteChanged();
            foreach (var row in Metadata)
                row.RefreshCommands();
        }

        /// <summary>A selected song whose metadata was read, with its draft.</summary>
        /// <param name="SongIniPath">For a folder, the song.ini to edit (which may not exist yet).</param>
        private sealed record EditableSong(SongEntry Entry, MetadataItem Item, string? SongIniPath);

        /// <summary>One song's metadata as loaded off the UI thread.</summary>
        /// <param name="Pairs">The stored pairs, or <see langword="null"/> when the song couldn't be read.</param>
        /// <param name="ReadOnlyReason">Why the song's metadata can't be edited, if it can't.</param>
        private sealed record LoadedSong(SongEntry Entry, IReadOnlyList<KeyValuePair<string, string>>? Pairs, string? SongIniPath, string? ReadOnlyReason);

        /// <summary>Everything loaded off the UI thread, applied in one go. Files and findings are only loaded for one song.</summary>
        private sealed record LoadedSelection(
            IReadOnlyList<LoadedSong> Songs,
            ulong TotalSize,
            IReadOnlyList<FileRow> Files,
            IReadOnlyList<FindingRow> Findings,
            string? MetadataNote);
    }
}
