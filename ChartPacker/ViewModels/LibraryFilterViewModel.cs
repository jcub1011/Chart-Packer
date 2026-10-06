using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using ChartPacker.Core.Charts;
using ChartPacker.Core.Library;
using ChartPacker.Core.Merging;
using ChartPacker.Resources;
using ChartPacker.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChartPacker.ViewModels
{
    /// <summary>A sort order as offered in the sort picker.</summary>
    public sealed record LibrarySortOption(LibrarySortField Value, string Label);

    /// <summary>The library list's search box and its sort and filter flyout.</summary>
    /// <remarks>
    /// The sort is remembered in the settings. The search and filters start empty each launch, so
    /// songs never look missing without a visible reason.
    /// </remarks>
    public partial class LibraryFilterViewModel : ViewModelBase
    {
        private const decimal MsPerMinute = 60_000;
        private const decimal BytesPerMegabyte = 1024 * 1024;

        private readonly SettingsService _settings;

        // Set while several properties change together, so the query is rebuilt once.
        private bool _suspendRebuild;

        public LibraryFilterViewModel(SettingsService settings)
        {
            _settings = settings;
            _suspendRebuild = true;

            SortOptions =
            [
                new(LibrarySortField.ScanOrder, Strings.SortScanOrder),
                new(LibrarySortField.Title, Strings.FieldTitle),
                new(LibrarySortField.Artist, Strings.FieldArtist),
                new(LibrarySortField.Album, Strings.FieldAlbum),
                new(LibrarySortField.Genre, Strings.FieldGenre),
                new(LibrarySortField.Charter, Strings.FieldCharter),
                new(LibrarySortField.Year, Strings.FieldYear),
                new(LibrarySortField.Length, Strings.FieldLength),
                new(LibrarySortField.Size, Strings.FieldSize),
                new(LibrarySortField.Type, Strings.FieldType),
                new(LibrarySortField.FileName, Strings.FieldFileName),
            ];
            Instruments = new FilterChipGroupViewModel(
                Strings.FilterInstrumentsLabel,
                MidiPartCatalog.Known.Select(p => (ChartLabels.PartName(p), (int)ChartContents.FlagOf(p.Id))),
                OnChipChanged);
            Difficulties = new FilterChipGroupViewModel(
                Strings.FilterDifficultiesLabel,
                Enum.GetValues<ChartDifficulty>().Select(d => (ChartLabels.DifficultyName(d), 1 << (int)d)),
                OnChipChanged);
            Stems = new FilterChipGroupViewModel(
                Strings.FilterStemsLabel,
                [
                    (Strings.FilterStemSong, (int)LibraryStems.Song),
                    (SharedStrings.InstrumentGuitar, (int)LibraryStems.Guitar),
                    (SharedStrings.InstrumentRhythm, (int)LibraryStems.Rhythm),
                    (SharedStrings.InstrumentBass, (int)LibraryStems.Bass),
                    (SharedStrings.InstrumentKeys, (int)LibraryStems.Keys),
                    (SharedStrings.InstrumentDrums, (int)LibraryStems.Drums),
                    (Strings.FilterStemVocals, (int)LibraryStems.Vocals),
                    (Strings.FilterStemCrowd, (int)LibraryStems.Crowd),
                    (Strings.FilterStemPreview, (int)LibraryStems.Preview),
                ],
                OnChipChanged);
            VideoChip = new FilterChipViewModel(Strings.FilterAssetVideo, 0, OnChipChanged);
            ImageChip = new FilterChipViewModel(Strings.FilterAssetImage, 0, OnChipChanged);
            AlbumArtChip = new FilterChipViewModel(Strings.FilterAssetAlbumArt, 0, OnChipChanged);
            AssetChips = [VideoChip, ImageChip, AlbumArtChip];

            SelectedSortOption = SortOptions.First(o => o.Value == settings.Settings.LibrarySortField);
            SortDescending = settings.Settings.LibrarySortDescending;
            Query = Build();
            _suspendRebuild = false;
        }

        /// <summary>The search, filters and sort changed.</summary>
        public event Action? QueryChanged;

        /// <summary>The query the list shows.</summary>
        public LibraryQuery Query { get; private set; }

        public IReadOnlyList<LibrarySortOption> SortOptions { get; }

        [ObservableProperty]
        public partial LibrarySortOption SelectedSortOption { get; set; }

        [ObservableProperty]
        public partial bool SortDescending { get; set; }

        /// <summary>Words to find in the file name, title or artist.</summary>
        [ObservableProperty]
        public partial string? SearchText { get; set; }

        [ObservableProperty]
        public partial bool ShowSng { get; set; } = true;

        [ObservableProperty]
        public partial bool ShowFolders { get; set; } = true;

        [ObservableProperty]
        public partial string? TitleContains { get; set; }

        [ObservableProperty]
        public partial string? ArtistContains { get; set; }

        [ObservableProperty]
        public partial string? AlbumContains { get; set; }

        [ObservableProperty]
        public partial string? GenreContains { get; set; }

        [ObservableProperty]
        public partial string? CharterContains { get; set; }

        [ObservableProperty]
        public partial decimal? YearMin { get; set; }

        [ObservableProperty]
        public partial decimal? YearMax { get; set; }

        /// <summary>In minutes.</summary>
        [ObservableProperty]
        public partial decimal? LengthMin { get; set; }

        /// <summary>In minutes.</summary>
        [ObservableProperty]
        public partial decimal? LengthMax { get; set; }

        /// <summary>In megabytes (MiB, as the list shows sizes).</summary>
        [ObservableProperty]
        public partial decimal? SizeMin { get; set; }

        /// <summary>In megabytes (MiB, as the list shows sizes).</summary>
        [ObservableProperty]
        public partial decimal? SizeMax { get; set; }

        [ObservableProperty]
        public partial bool OnlyUnsaved { get; set; }

        /// <summary>The parts the chart has notes for.</summary>
        public FilterChipGroupViewModel Instruments { get; }

        /// <summary>The difficulties any part of the chart has notes for.</summary>
        public FilterChipGroupViewModel Difficulties { get; }

        /// <summary>The audio stems, grouped: Drums is every drums stem, and so on.</summary>
        public FilterChipGroupViewModel Stems { get; }

        /// <summary>The set groups, in the order the flyout shows them.</summary>
        public IReadOnlyList<FilterChipGroupViewModel> ChipGroups => [Instruments, Difficulties, Stems];

        public FilterChipViewModel VideoChip { get; }

        public FilterChipViewModel ImageChip { get; }

        public FilterChipViewModel AlbumArtChip { get; }

        /// <summary>The video, background image and album art chips.</summary>
        public IReadOnlyList<FilterChipViewModel> AssetChips { get; }

        /// <summary>How many filters are narrowing the list, for the badge on the filter button.</summary>
        public int ActiveFilterCount => Query.ActiveFilterCount;

        public bool HasActiveFilters => ActiveFilterCount > 0;

        public bool HasSearch => !string.IsNullOrWhiteSpace(SearchText);

        /// <summary>Puts every filter back to showing everything. The search and sort are kept.</summary>
        [RelayCommand]
        private void ClearFilters()
        {
            Batch(() =>
            {
                ShowSng = true;
                ShowFolders = true;
                TitleContains = null;
                ArtistContains = null;
                AlbumContains = null;
                GenreContains = null;
                CharterContains = null;
                YearMin = null;
                YearMax = null;
                LengthMin = null;
                LengthMax = null;
                SizeMin = null;
                SizeMax = null;
                OnlyUnsaved = false;
                foreach (var group in ChipGroups)
                    group.Clear();
                foreach (var chip in AssetChips)
                    chip.State = FilterPresence.Either;
            });
        }

        [RelayCommand]
        private void ClearSearch() => SearchText = null;

        /// <summary>Clears the search and every filter, e.g. from the "no matches" state.</summary>
        [RelayCommand]
        private void ClearAll() => Batch(() =>
        {
            ClearSearch();
            ClearFilters();
        });

        partial void OnSelectedSortOptionChanged(LibrarySortOption value)
        {
            if (value is not null)
                _settings.Settings.LibrarySortField = value.Value;
        }

        partial void OnSortDescendingChanged(bool value) => _settings.Settings.LibrarySortDescending = value;

        protected override void OnPropertyChanged(PropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);

            if (e.PropertyName is nameof(ActiveFilterCount) or nameof(HasActiveFilters) or nameof(HasSearch))
                return;

            if (e.PropertyName == nameof(SearchText))
                base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(HasSearch)));

            if (!_suspendRebuild)
                Rebuild();
        }

        private void OnChipChanged()
        {
            if (!_suspendRebuild)
                Rebuild();
        }

        private void Batch(Action changes)
        {
            bool wasSuspended = _suspendRebuild;
            _suspendRebuild = true;
            try
            {
                changes();
            }
            finally
            {
                _suspendRebuild = wasSuspended;
            }

            if (!wasSuspended)
                Rebuild();
        }

        private void Rebuild()
        {
            var query = Build();
            if (query == Query)
                return;

            Query = query;
            OnPropertyChanged(nameof(ActiveFilterCount));
            OnPropertyChanged(nameof(HasActiveFilters));
            QueryChanged?.Invoke();
        }

        private LibraryQuery Build() => new()
        {
            Search = SearchText?.Trim() ?? string.Empty,
            SortField = SelectedSortOption?.Value ?? LibrarySortField.ScanOrder,
            Descending = SortDescending,
            IncludeSng = ShowSng,
            IncludeFolders = ShowFolders,
            TitleContains = TitleContains,
            ArtistContains = ArtistContains,
            AlbumContains = AlbumContains,
            GenreContains = GenreContains,
            CharterContains = CharterContains,
            YearMin = ToInt(YearMin),
            YearMax = ToInt(YearMax),
            LengthMinMs = Scale<long>(LengthMin, MsPerMinute),
            LengthMaxMs = Scale<long>(LengthMax, MsPerMinute),
            SizeMinBytes = Scale<ulong>(SizeMin, BytesPerMegabyte),
            SizeMaxBytes = Scale<ulong>(SizeMax, BytesPerMegabyte),
            OnlyUnsaved = OnlyUnsaved,
            Instruments = new((LibraryInstruments)Instruments.Included, (LibraryInstruments)Instruments.Excluded, Instruments.Match),
            Difficulties = new((LibraryDifficulties)Difficulties.Included, (LibraryDifficulties)Difficulties.Excluded, Difficulties.Match),
            Stems = new((LibraryStems)Stems.Included, (LibraryStems)Stems.Excluded, Stems.Match),
            Video = VideoChip.State,
            BackgroundImage = ImageChip.State,
            AlbumArt = AlbumArtChip.State,
        };

        private static int? ToInt(decimal? value) => value is { } v ? (int)Math.Round(v) : null;

        private static T? Scale<T>(decimal? value, decimal unit)
            where T : struct, System.Numerics.INumber<T> =>
            value is { } v ? T.CreateSaturating(Math.Round(Math.Max(v, 0) * unit)) : null;
    }
}
