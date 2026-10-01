using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Collections;
using Avalonia.Threading;
using ChartPacker.Core.Metadata;
using ChartPacker.Core.Scanning;
using ChartPacker.Core.Validation;
using ChartPacker.Resources;
using ChartPacker.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChartPacker.ViewModels
{
    /// <summary>
    /// The Validator window: checks the selected songs' metadata, as drafted, lists the ones that fail, and shows the
    /// one picked in a full inspector beside the list, where it can be fixed and saved.
    /// </summary>
    /// <remarks>
    /// The inspector shares the main window's drafts, so an edit made here is the same draft the main window shows,
    /// saved or not. Each listed song is checked again whenever its draft changes, and read again from disk once its
    /// draft is saved or discarded. A song that comes to pass stays listed, marked as passing, so the list doesn't
    /// move under the pointer.
    /// </remarks>
    public sealed partial class ValidatorWindowViewModel : ViewModelBase
    {
        private readonly IReadOnlyList<SongEntry> _entries;
        private readonly MetadataDraftStore _drafts;
        private readonly int _parallelism;
        private readonly CancellationTokenSource _closing = new();

        // The listed songs, by full path.
        private readonly Dictionary<string, ValidatorSongViewModel> _byPath = new(StringComparer.Ordinal);

        /// <param name="entries">The songs to check, in the order they're listed.</param>
        /// <param name="drafts">The main window's drafts, which the songs are checked as.</param>
        /// <param name="inspector">The inspector the picked song is shown in, sharing <paramref name="drafts"/>.</param>
        /// <param name="parallelism">How many songs are read at once.</param>
        public ValidatorWindowViewModel(IReadOnlyList<SongEntry> entries, MetadataDraftStore drafts, SongDetailViewModel inspector, int parallelism)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(parallelism, 1);
            _entries = entries;
            _drafts = drafts;
            _parallelism = parallelism;
            Inspector = inspector;
            _drafts.DraftsChanged += OnDraftsChanged;
        }

        /// <summary>The picked song, editable and playable as in the main window.</summary>
        public SongDetailViewModel Inspector { get; }

        /// <summary>The songs that failed when checked, in selection order.</summary>
        public AvaloniaList<ValidatorSongViewModel> Songs { get; } = [];

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ShowPickPrompt), nameof(ShowInspector))]
        public partial ValidatorSongViewModel? SelectedSong { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(StatusText), nameof(ShowAllPass))]
        public partial bool IsChecking { get; set; }

        /// <summary>How many songs have been read.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(StatusText))]
        public partial int Checked { get; set; }

        public int Total => _entries.Count;

        /// <summary>How many listed songs still fail.</summary>
        public int FailingCount => Songs.Count(s => !s.IsFixed);

        public string StatusText =>
            IsChecking ? string.Format(CultureInfo.CurrentCulture, Strings.ValidatorCheckingFormat, Checked, Total)
            : FailingCount == 0 ? string.Format(CultureInfo.CurrentCulture, Strings.ValidatorAllPassFormat, Total)
            : string.Format(CultureInfo.CurrentCulture, Strings.ValidatorSummaryFormat, FailingCount, Total);

        /// <summary>Every song was checked and none failed.</summary>
        public bool ShowAllPass => !IsChecking && Songs.Count == 0;

        public bool ShowInspector => SelectedSong is not null;

        public bool ShowPickPrompt => SelectedSong is null;

        /// <summary>Reads and checks every song, listing the ones that fail as they're found.</summary>
        public async Task LoadAsync()
        {
            IsChecking = true;
            var options = new ParallelOptions { MaxDegreeOfParallelism = _parallelism, CancellationToken = _closing.Token };
            try
            {
                await Parallel.ForEachAsync(Enumerable.Range(0, _entries.Count), options, async (i, token) =>
                {
                    var report = await SongMetadataCheck.ReadAsync(_entries[i], token).ConfigureAwait(false);

                    // Drafts live on the UI thread, so the check against them is made there.
                    await Dispatcher.UIThread.InvokeAsync(() => Add(i, report));
                });
            }
            catch (OperationCanceledException)
            {
                return;
            }

            IsChecking = false;
        }

        /// <summary>Stops checking, lets go of the inspector's song and player, and stops following the drafts.</summary>
        public async Task ShutdownAsync()
        {
            _closing.Cancel();
            _drafts.DraftsChanged -= OnDraftsChanged;
            await Inspector.ShowAsync([]);
            Inspector.Detach();
            await Inspector.Player.WhenFilesClosedAsync();
            Inspector.Player.Dispose();
        }

        partial void OnSelectedSongChanged(ValidatorSongViewModel? value) =>
            _ = Inspector.ShowAsync(value is null ? [] : [value.Entry]);

        private void Add(int index, SongMetadataReport report)
        {
            if (_closing.IsCancellationRequested)
                return;

            Checked++;
            var song = new ValidatorSongViewModel(index, report);
            song.Check(DraftOf(report.Entry));
            if (song.IsFixed)
                return;

            // Reads finish in any order; the list keeps the selection's.
            int at = 0;
            while (at < Songs.Count && Songs[at].Index < index)
                at++;
            Songs.Insert(at, song);
            _byPath[report.Entry.FullPath] = song;
            RefreshCounts();
        }

        /// <summary>A listed song's draft changed, here or anywhere: it's checked again, from disk once the draft is gone.</summary>
        private void OnDraftsChanged(string path)
        {
            if (!_byPath.TryGetValue(path, out var song))
                return;

            if (_drafts.HasDrafts(path))
            {
                song.Check(DraftOf(song.Entry));
                RefreshCounts();
            }
            else
            {
                _ = RereadAsync(song);
            }
        }

        private async Task RereadAsync(ValidatorSongViewModel song)
        {
            try
            {
                var report = await SongMetadataCheck.ReadAsync(song.Entry, _closing.Token);
                song.Update(report, DraftOf(song.Entry));
                RefreshCounts();
            }
            catch (OperationCanceledException)
            {
            }
        }

        private MetadataDraft? DraftOf(SongEntry entry) => _drafts.HasDrafts(entry.FullPath) ? _drafts.Get(entry.FullPath) : null;

        private void RefreshCounts()
        {
            OnPropertyChanged(nameof(FailingCount));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(ShowAllPass));
        }
    }

    /// <summary>A song in the Validator's list: what it's called, and which of its keys fail and why.</summary>
    public sealed partial class ValidatorSongViewModel : ObservableObject
    {
        // How many failing keys are spelled out; the rest are counted.
        private const int ListedIssues = 3;

        private SongMetadataReport _report;

        public ValidatorSongViewModel(int index, SongMetadataReport report)
        {
            Index = index;
            _report = report;
            Title = report.Entry.DisplayName;
        }

        /// <summary>Where the song is in the selection.</summary>
        public int Index { get; }

        public SongEntry Entry => _report.Entry;

        public string Path => _report.Entry.FullPath;

        /// <summary>"Name - Artist" as drafted, or the file or folder name.</summary>
        [ObservableProperty]
        public partial string Title { get; set; }

        /// <summary>The first few failing keys, each with why.</summary>
        [ObservableProperty]
        public partial IReadOnlyList<string> IssueLines { get; set; } = [];

        [ObservableProperty]
        public partial string? IssueCountText { get; set; }

        /// <summary>The song passes now: it was fixed since it was listed.</summary>
        [ObservableProperty]
        public partial bool IsFixed { get; set; }

        /// <summary>The metadata was read again with a new result, e.g. after saving.</summary>
        public void Update(SongMetadataReport report, MetadataDraft? draft)
        {
            _report = report;
            Check(draft);
        }

        /// <summary>Checks the metadata as <paramref name="draft"/> would save it.</summary>
        public void Check(MetadataDraft? draft)
        {
            var pairs = draft?.ApplyTo(_report.Pairs) ?? _report.Pairs;
            var headline = SongHeadline.From(pairs);
            Title = headline switch
            {
                { Name: { } name, Artist: { } artist } => string.Format(CultureInfo.CurrentCulture, Strings.TitleFormat, name, artist),
                { Name: { } name } => name,
                _ => _report.Entry.DisplayName,
            };

            if (_report.ReadError is { } error)
            {
                IssueLines = [string.Format(CultureInfo.CurrentCulture, Strings.ValidatorUnreadableFormat, error)];
                IssueCountText = null;
                IsFixed = false;
                return;
            }

            // One line per failing key, its messages joined.
            var byKey = MetadataValidator.Validate(pairs, _report.DuplicateKeys)
                .Where(MetadataValidator.IsFailing)
                .GroupBy(f => f.Key ?? "", StringComparer.OrdinalIgnoreCase)
                .Select(g => string.Format(CultureInfo.CurrentCulture, Strings.ValidatorIssueLineFormat, g.Key, string.Join(" ", g.Select(f => f.Message).Distinct(StringComparer.Ordinal))))
                .ToList();

            IsFixed = byKey.Count == 0;
            IssueCountText = IsFixed ? Strings.ValidatorFixed : string.Format(CultureInfo.CurrentCulture, Strings.ValidatorIssueCountFormat, byKey.Count);
            IssueLines = byKey.Count > ListedIssues
                ? [.. byKey.Take(ListedIssues), string.Format(CultureInfo.CurrentCulture, Strings.ValidatorMoreIssuesFormat, byKey.Count - ListedIssues)]
                : byKey;
        }
    }
}
