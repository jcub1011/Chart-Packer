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
using ChartPacker.Core.Text;
using ChartPacker.Core.Validation;
using ChartPacker.Formatting;
using ChartPacker.Resources;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChartPacker.ViewModels
{
    /// <summary>A metadata key/value pair as shown in the detail panel.</summary>
    /// <param name="Key">The metadata key.</param>
    /// <param name="Value">The value to show. Times are formatted as HH:MM:SS:mmm; rich-text tags are left for the view to render.</param>
    /// <param name="ToolTip">The stored value, when what's shown differs from it; otherwise <see langword="null"/>.</param>
    public sealed record MetadataRow(string Key, string Value, string? ToolTip)
    {
        public static MetadataRow Create(string key, string value)
        {
            if (MetadataTimes.TryFormat(key, value, out string? time))
                return new(key, time, string.Format(CultureInfo.CurrentCulture, Strings.StoredMillisecondsFormat, value));

            return new(key, value, RichText.HasMarkup(value) ? value : null);
        }
    }

    /// <summary>A file in the song, with its size already formatted.</summary>
    public sealed record FileRow(string Path, string Size);

    /// <summary>A structural problem found in a <c>.sng</c>.</summary>
    public sealed record FindingRow(string RuleId, string Message, bool IsError);

    /// <summary>
    /// The right panel: the metadata and file list of the song selected in the library. For a
    /// <c>.sng</c> both come from the container; for a folder, from its <c>song.ini</c> and a listing.
    /// </summary>
    public partial class SongDetailViewModel : ViewModelBase
    {
        private CancellationTokenSource? _loadCts;

        public AvaloniaList<MetadataRow> Metadata { get; } = new();

        public AvaloniaList<FileRow> Files { get; } = new();

        public AvaloniaList<FindingRow> Findings { get; } = new();

        [ObservableProperty]
        private SongEntry? _entry;

        [ObservableProperty]
        private bool _isLoading;

        [ObservableProperty]
        private string? _errorMessage;

        /// <summary>Why there is no metadata table, e.g. the folder has no song.ini.</summary>
        [ObservableProperty]
        private string? _metadataNote;

        /// <summary>The full path and total size.</summary>
        [ObservableProperty]
        private string? _subtitle;

        /// <summary>How many items are selected in the library. Details are only shown for one.</summary>
        [ObservableProperty]
        private int _selectionCount;

        public bool ShowPlaceholder => Entry is null;

        public string PlaceholderTitle => SelectionCount > 1
            ? string.Format(CultureInfo.CurrentCulture, Strings.MultipleSelectedFormat, SelectionCount)
            : Strings.DetailPlaceholderTitle;

        public string PlaceholderBody => SelectionCount > 1 ? Strings.MultipleSelectedBody : Strings.DetailPlaceholderBody;

        public bool HasError => ErrorMessage is not null;

        public bool ShowDetails => Entry is not null && !IsLoading && !HasError;

        public bool HasFindings => Findings.Count > 0;

        public bool HasMetadataNote => MetadataNote is not null;

        public bool ShowNoFiles => Files.Count == 0;

        public string MetadataHeader => string.Format(CultureInfo.CurrentCulture, Strings.MetadataHeaderFormat, Metadata.Count);

        public string FilesHeader => string.Format(CultureInfo.CurrentCulture, Strings.FilesHeaderFormat, Files.Count);

        /// <summary>Loads <paramref name="entry"/>, replacing whatever is shown. A newer call cancels an older one.</summary>
        public async Task ShowAsync(SongEntry? entry)
        {
            _loadCts?.Cancel();
            using var cts = new CancellationTokenSource();
            _loadCts = cts;

            Entry = entry;
            Metadata.Clear();
            Files.Clear();
            Findings.Clear();
            MetadataNote = null;
            Subtitle = null;
            ErrorMessage = null;
            IsLoading = entry is not null;
            RefreshState();

            if (entry is null)
            {
                _loadCts = null;
                return;
            }

            try
            {
                var details = await Task.Run(
                    () => entry.Kind == SongEntryKind.SngFile
                        ? LoadSngAsync(entry.FullPath, cts.Token)
                        : LoadFolderAsync(entry.FullPath, cts.Token),
                    cts.Token);

                if (_loadCts == cts)
                    Apply(entry, details);
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

        partial void OnIsLoadingChanged(bool value) => RefreshState();

        partial void OnErrorMessageChanged(string? value) => RefreshState();

        partial void OnMetadataNoteChanged(string? value) => RefreshState();

        partial void OnSelectionCountChanged(int value)
        {
            OnPropertyChanged(nameof(PlaceholderTitle));
            OnPropertyChanged(nameof(PlaceholderBody));
        }

        private void Apply(SongEntry entry, LoadedDetails details)
        {
            Subtitle = string.Format(CultureInfo.CurrentCulture, Strings.SubtitleFormat, entry.FullPath, ByteSizeFormatter.Format(details.TotalSize));
            Metadata.AddRange(details.Metadata);
            Files.AddRange(details.Files);
            Findings.AddRange(details.Findings);
            MetadataNote = details.MetadataNote ?? (details.Metadata.Count == 0 ? Strings.NoMetadata : null);
        }

        private static async Task<LoadedDetails> LoadSngAsync(string path, CancellationToken cancellationToken)
        {
            var result = await SngReader.ReadAsync(path, cancellationToken).ConfigureAwait(false);
            ulong size = (ulong)new FileInfo(path).Length;

            return new LoadedDetails(
                size,
                ToMetadataRows(result.Metadata.Select(p => KeyValuePair.Create(p.Key, p.Value))),
                result.Files.Select(f => new FileRow(f.Name, ByteSizeFormatter.Format(f.Length))).ToList(),
                result.Findings.Select(f => new FindingRow(f.RuleId, f.Message, f.Severity == Severity.Error)).ToList(),
                MetadataNote: null);
        }

        private static async Task<LoadedDetails> LoadFolderAsync(string folder, CancellationToken cancellationToken)
        {
            var files = await SongFolderListing.ListAsync(folder, cancellationToken).ConfigureAwait(false);

            // Only a song.ini directly in the folder counts; one in a subfolder belongs to something else.
            var songIni = files.FirstOrDefault(f => f.RelativePath.Equals(SongIni.FileName, StringComparison.OrdinalIgnoreCase));

            IReadOnlyList<KeyValuePair<string, string>> pairs = [];
            string? note = null;
            if (songIni is null)
            {
                note = Strings.NoSongIni;
            }
            else
            {
                var result = await SongIni.ReadAsync(Path.Combine(folder, songIni.RelativePath), cancellationToken).ConfigureAwait(false);
                pairs = result.Pairs;
                note = result.Status switch
                {
                    SongIniStatus.NoSongSection => Strings.SongIniNoSongSection,
                    SongIniStatus.TooLarge => Strings.SongIniTooLarge,
                    _ => null,
                };
            }

            return new LoadedDetails(
                (ulong)files.Sum(f => f.Length),
                ToMetadataRows(pairs),
                files.Select(f => new FileRow(f.RelativePath, ByteSizeFormatter.Format((ulong)f.Length))).ToList(),
                [],
                note);
        }

        /// <summary>Builds the rows to show, leaving out difficulties that are unset (negative).</summary>
        private static List<MetadataRow> ToMetadataRows(IEnumerable<KeyValuePair<string, string>> pairs) =>
            pairs.Where(p => !MetadataDifficulty.IsUnset(p.Key, p.Value))
                 .Select(p => MetadataRow.Create(p.Key, p.Value))
                 .ToList();

        /// <summary>Raises change notifications for every derived display state.</summary>
        private void RefreshState()
        {
            OnPropertyChanged(nameof(ShowPlaceholder));
            OnPropertyChanged(nameof(HasError));
            OnPropertyChanged(nameof(ShowDetails));
            OnPropertyChanged(nameof(HasFindings));
            OnPropertyChanged(nameof(HasMetadataNote));
            OnPropertyChanged(nameof(ShowNoFiles));
            OnPropertyChanged(nameof(MetadataHeader));
            OnPropertyChanged(nameof(FilesHeader));
        }

        /// <summary>Everything loaded off the UI thread, applied in one go.</summary>
        private sealed record LoadedDetails(
            ulong TotalSize,
            IReadOnlyList<MetadataRow> Metadata,
            IReadOnlyList<FileRow> Files,
            IReadOnlyList<FindingRow> Findings,
            string? MetadataNote);
    }
}
