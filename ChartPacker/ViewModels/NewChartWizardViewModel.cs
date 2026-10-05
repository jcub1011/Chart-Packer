using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Collections;
using Avalonia.Media.Imaging;
using ChartPacker.Audio.Decoding;
using ChartPacker.Core.Charting;
using ChartPacker.Core.Charts;
using ChartPacker.Core.Scanning;
using ChartPacker.Core.Validation;
using ChartPacker.Formatting;
using ChartPacker.Resources;
using ChartPacker.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChartPacker.ViewModels
{
    /// <summary>The new-chart wizard's steps, in order.</summary>
    public enum NewChartStep
    {
        Song,
        Audio,
        Timing,
        Parts,
    }

    /// <summary>A part the new chart can be created with, ticked to create it.</summary>
    public sealed partial class NewChartPartViewModel(ChartInstrument instrument, Action changed) : ViewModelBase
    {
        public ChartInstrument Instrument { get; } = instrument;

        public string Label { get; } = ChartLabels.InstrumentName(instrument);

        [ObservableProperty]
        public partial bool IsChecked { get; set; }

        partial void OnIsCheckedChanged(bool value) => changed();
    }

    /// <summary>
    /// The New chart wizard (charter-spec C1): the song's details, its audio, the starting timing and
    /// the parts, then where to create it. Every step's values are kept while moving back and forth;
    /// Create makes the song folder with <see cref="NewChartCreator"/>.
    /// </summary>
    public sealed partial class NewChartWizardViewModel : ViewModelBase
    {
        private const int ArtDecodeWidth = 160;

        private readonly SettingsService _settings;
        private readonly IFilePickerService _filePicker;
        private readonly IImagePickerService _imagePicker;
        private readonly IFolderPickerService _folderPicker;

        // The song fields the user has typed in, which tags never overwrite.
        private readonly HashSet<string> _edited = [];
        private bool _prefilling;

        // The folder name follows the song's name and artist until it's typed in.
        private bool _folderNameEdited;
        private bool _settingFolderName;

        private CancellationTokenSource? _createCts;
        private Task<string>? _creating;
        private bool _closed;

        /// <param name="settings">The defaults for the charter name, resolution and location; the location used is saved back.</param>
        /// <param name="libraryFolder">The library's folder, where the song is created unless another was used last time.</param>
        /// <param name="audioFiles">Audio files to start with, e.g. ones dropped on the main window.</param>
        public NewChartWizardViewModel(
            SettingsService settings,
            string? libraryFolder,
            IReadOnlyList<string> audioFiles,
            IFilePickerService filePicker,
            IImagePickerService imagePicker,
            IFolderPickerService folderPicker)
        {
            _settings = settings;
            _filePicker = filePicker;
            _imagePicker = imagePicker;
            _folderPicker = folderPicker;

            Parts = [.. NewChartSettings.Instruments.Select(i => new NewChartPartViewModel(i, RefreshParts))];
            ResolutionChoices = [.. NewChartSettings.Resolutions.Select(AppSettingsCatalog.ResolutionLabel)];
            ResolutionIndex = Math.Max(0, NewChartSettings.Resolutions.ToList().IndexOf(settings.Settings.DefaultChartResolution));
            DenominatorChoices = [.. NewChartSettings.Denominators.Select(d => d.ToString(CultureInfo.CurrentCulture))];
            DenominatorIndex = NewChartSettings.Denominators.ToList().IndexOf(4);
            DrumsVariantChoices = [Strings.NewChartDrumsFourLane, Strings.NewChartDrumsPro, Strings.NewChartDrumsFiveLane];
            DrumsVariantIndex = (int)DrumsVariant.Pro;

            _prefilling = true;
            Charter = settings.Settings.DefaultCharterName;
            _prefilling = false;

            ParentFolder = settings.Settings.LastNewChartFolder is { } last && Directory.Exists(last) ? last : libraryFolder;
            UpdateFolderName();
            AddFiles(audioFiles);
            Refresh();
        }

        /// <summary>Raised to close the window.</summary>
        public event Action? CloseRequested;

        /// <summary>The song folder made, once Create has finished.</summary>
        public string? CreatedPath { get; private set; }

        // ---- Steps ----

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsSongStep), nameof(IsAudioStep), nameof(IsTimingStep), nameof(IsPartsStep), nameof(IsLastStep))]
        [NotifyCanExecuteChangedFor(nameof(BackCommand), nameof(NextCommand), nameof(CreateCommand))]
        public partial NewChartStep Step { get; set; }

        public bool IsSongStep => Step == NewChartStep.Song;

        public bool IsAudioStep => Step == NewChartStep.Audio;

        public bool IsTimingStep => Step == NewChartStep.Timing;

        public bool IsPartsStep => Step == NewChartStep.Parts;

        public bool IsLastStep => Step == NewChartStep.Parts;

        /// <summary>Why the current step can't go on: Next, or Create on the last step, is disabled while it's set.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasBlockedReason))]
        [NotifyCanExecuteChangedFor(nameof(NextCommand), nameof(CreateCommand))]
        public partial string? BlockedReason { get; set; }

        public bool HasBlockedReason => BlockedReason is not null;

        partial void OnStepChanged(NewChartStep value) => Refresh();

        private bool CanGoBack() => Step > NewChartStep.Song && !IsCreating;

        [RelayCommand(CanExecute = nameof(CanGoBack))]
        private void Back() => Step--;

        private bool CanGoNext() => Step < NewChartStep.Parts && BlockedReason is null && !IsCreating;

        [RelayCommand(CanExecute = nameof(CanGoNext))]
        private void Next() => Step++;

        // ---- Song ----

        [ObservableProperty]
        public partial string Name { get; set; } = "";

        [ObservableProperty]
        public partial string Artist { get; set; } = "";

        [ObservableProperty]
        public partial string Album { get; set; } = "";

        [ObservableProperty]
        public partial string Genre { get; set; } = "";

        [ObservableProperty]
        public partial string Year { get; set; } = "";

        [ObservableProperty]
        public partial string Charter { get; set; } = "";

        partial void OnNameChanged(string value) => SongFieldChanged(nameof(Name), updatesFolderName: true);

        partial void OnArtistChanged(string value) => SongFieldChanged(nameof(Artist), updatesFolderName: true);

        partial void OnAlbumChanged(string value) => SongFieldChanged(nameof(Album));

        partial void OnGenreChanged(string value) => SongFieldChanged(nameof(Genre));

        partial void OnYearChanged(string value) => SongFieldChanged(nameof(Year));

        partial void OnCharterChanged(string value) => SongFieldChanged(nameof(Charter));

        private void SongFieldChanged(string field, bool updatesFolderName = false)
        {
            if (!_prefilling)
                _edited.Add(field);
            if (updatesFolderName)
                UpdateFolderName();
            Refresh();
        }

        /// <summary>The album art to copy in; <see langword="null"/> for none.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasArt))]
        public partial string? ArtPath { get; private set; }

        public bool HasArt => ArtPath is not null;

        /// <summary>A thumbnail of <see cref="ArtPath"/>.</summary>
        [ObservableProperty]
        public partial Bitmap? ArtPreview { get; private set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasArtError))]
        public partial string? ArtError { get; private set; }

        public bool HasArtError => ArtError is not null;

        [RelayCommand]
        private async Task PickArtAsync()
        {
            if (await _imagePicker.PickImageAsync(Strings.NewChartPickArtTitle) is not { } path)
                return;

            ArtError = null;
            if (AlbumArt.StoredNameFor(path) is null)
            {
                ArtError = Strings.ArtNotAnImage;
                return;
            }

            var (preview, problem) = await Task.Run(() => LoadArt(path));
            if (problem is not null)
            {
                ArtError = problem;
                return;
            }

            ArtPreview?.Dispose();
            ArtPreview = preview;
            ArtPath = path;
        }

        [RelayCommand]
        private void ClearArt()
        {
            ArtPath = null;
            ArtPreview?.Dispose();
            ArtPreview = null;
            ArtError = null;
        }

        private static (Bitmap? Preview, string? Problem) LoadArt(string path)
        {
            try
            {
                if (new FileInfo(path).Length > AlbumArt.DefaultMaxBytes)
                    return (null, string.Format(CultureInfo.CurrentCulture, Strings.ArtTooLargeFormat, ByteSizeFormatter.Format((ulong)AlbumArt.DefaultMaxBytes)));

                using var stream = File.OpenRead(path);
                return (Bitmap.DecodeToWidth(stream, ArtDecodeWidth, BitmapInterpolationMode.HighQuality), null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return (null, string.Format(CultureInfo.CurrentCulture, Strings.ArtUnreadableFormat, ex.Message));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Corrupt or unsupported images surface as assorted decoder exceptions.
                return (null, Strings.ArtNotAnImage);
            }
        }

        // ---- Audio ----

        public AvaloniaList<NewChartAudioFileViewModel> Files { get; } = [];

        public bool HasFiles => Files.Count > 0;

        /// <summary>The song has no audio yet, and is charted against a silent timeline of <see cref="LengthText"/>.</summary>
        [ObservableProperty]
        public partial bool NoAudio { get; set; }

        [ObservableProperty]
        public partial string LengthText { get; set; } = NewChartLength.Format(NewChartLength.Default);

        partial void OnNoAudioChanged(bool value) => Refresh();

        partial void OnLengthTextChanged(string value) => Refresh();

        [RelayCommand]
        private async Task AddFilesAsync()
        {
            var paths = await _filePicker.PickAudioFilesAsync(Strings.NewChartPickAudioTitle);
            AddFiles(paths);
        }

        /// <summary>
        /// Adds audio files, each with the stem its name gives it (<see cref="NewChartStems.Assign"/>), and
        /// checks each one decodes. Files already listed, and files that aren't audio, are skipped.
        /// </summary>
        public void AddFiles(IEnumerable<string> paths)
        {
            var added = paths
                .Where(p => AudioStems.IsAudioExtension(Path.GetExtension(p)))
                .Where(p => !Files.Any(f => string.Equals(f.Path, p, StringComparison.OrdinalIgnoreCase)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (added.Count == 0)
                return;

            var stems = NewChartStems.Assign(Files.Select(f => f.Stem), added);
            for (int i = 0; i < added.Count; i++)
            {
                var file = new NewChartAudioFileViewModel(added[i], stems[i], RemoveFile, Refresh);
                Files.Add(file);
                _ = CheckAsync(file);
            }

            NoAudio = false;
            OnPropertyChanged(nameof(HasFiles));
            Refresh();
        }

        private void RemoveFile(NewChartAudioFileViewModel file)
        {
            if (IsCreating)
                return;

            Files.Remove(file);
            OnPropertyChanged(nameof(HasFiles));
            Refresh();
        }

        /// <summary>Reads the file's tags and length, and decodes a little of it to catch a file that isn't what its extension says.</summary>
        private async Task CheckAsync(NewChartAudioFileViewModel file)
        {
            var (info, problem) = await Task.Run(() =>
            {
                try
                {
                    var info = AudioFileInfo.Read(file.Path);
                    AudioDecoder.Check(file.Path);
                    return (info, (string?)null);
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
                {
                    return ((AudioFileInfo?)null, ex.Message);
                }
            });

            file.Info = info;
            file.Details = info is null ? null : DetailsOf(info);
            file.Problem = problem is null ? null : string.Format(CultureInfo.CurrentCulture, Strings.NewChartAudioUnusableFormat, problem);
            file.IsChecking = false;

            if (problem is null && info is not null && Files.Count > 0 && ReferenceEquals(Files[0], file))
                Prefill(info);

            Refresh();
        }

        private static string DetailsOf(AudioFileInfo info)
        {
            var parts = new List<string>();
            if (info.DurationMs is { } ms)
                parts.Add(Core.Metadata.MetadataTimes.FormatDuration(ms));
            if (info.Format is { } format)
                parts.Add(info.SampleRate is { } rate
                    ? string.Format(CultureInfo.CurrentCulture, Strings.NewChartAudioFormatFormat, format, rate / 1000.0)
                    : format);
            return string.Join("   ", parts);
        }

        /// <summary>Fills in the song fields the user hasn't typed in with what the first file's tags say.</summary>
        private void Prefill(AudioFileInfo info)
        {
            _prefilling = true;
            try
            {
                if (!_edited.Contains(nameof(Name)) && Name.Length == 0 && info.Title is { } title)
                    Name = title;
                if (!_edited.Contains(nameof(Artist)) && Artist.Length == 0 && info.Artist is { } artist)
                    Artist = artist;
                if (!_edited.Contains(nameof(Album)) && Album.Length == 0 && info.Album is { } album)
                    Album = album;
                if (!_edited.Contains(nameof(Genre)) && Genre.Length == 0 && info.Genre is { } genre)
                    Genre = genre;
                if (!_edited.Contains(nameof(Year)) && Year.Length == 0 && info.Year is { } year)
                    Year = year;
            }
            finally
            {
                _prefilling = false;
            }
        }

        // ---- Timing ----

        [ObservableProperty]
        public partial decimal? Bpm { get; set; } = (decimal)NewChartSettings.DefaultBpm;

        [ObservableProperty]
        public partial decimal? Numerator { get; set; } = 4;

        public IReadOnlyList<string> DenominatorChoices { get; }

        [ObservableProperty]
        public partial int DenominatorIndex { get; set; }

        public IReadOnlyList<string> ResolutionChoices { get; }

        [ObservableProperty]
        public partial int ResolutionIndex { get; set; }

        /// <summary>The audio lead-in, in milliseconds, saved as <c>delay</c>.</summary>
        [ObservableProperty]
        public partial decimal? LeadInMs { get; set; } = 0;

        public decimal MinBpm => (decimal)NewChartSettings.MinBpm;

        public decimal MaxBpm => (decimal)NewChartSettings.MaxBpm;

        public decimal MaxNumerator => NewChartSettings.MaxNumerator;

        public decimal MaxLeadInMs => NewChartRequest.MaxLeadInMs;

        partial void OnBpmChanged(decimal? value) => Refresh();

        partial void OnNumeratorChanged(decimal? value) => Refresh();

        partial void OnLeadInMsChanged(decimal? value) => Refresh();

        // ---- Parts ----

        public IReadOnlyList<NewChartPartViewModel> Parts { get; }

        public IEnumerable<NewChartPartViewModel> FiveFretParts =>
            Parts.Where(p => ChartInstruments.KindOf(p.Instrument) == ChartInstrumentKind.FiveFret);

        public IEnumerable<NewChartPartViewModel> GhlParts =>
            Parts.Where(p => ChartInstruments.KindOf(p.Instrument) == ChartInstrumentKind.SixFret);

        public NewChartPartViewModel DrumsPart => Parts.First(p => p.Instrument == ChartInstrument.Drums);

        public IReadOnlyList<string> DrumsVariantChoices { get; }

        [ObservableProperty]
        public partial int DrumsVariantIndex { get; set; }

        /// <summary>The drums part is ticked, so its variant can be chosen.</summary>
        public bool HasDrums => Parts.Any(p => p.Instrument == ChartInstrument.Drums && p.IsChecked);

        private void RefreshParts()
        {
            OnPropertyChanged(nameof(HasDrums));
            Refresh();
        }

        /// <summary>The folder the song's folder is made in.</summary>
        [ObservableProperty]
        public partial string? ParentFolder { get; set; }

        [ObservableProperty]
        public partial string FolderName { get; set; } = "";

        partial void OnParentFolderChanged(string? value) => Refresh();

        partial void OnFolderNameChanged(string value)
        {
            if (!_settingFolderName)
                _folderNameEdited = true;
            Refresh();
        }

        private void UpdateFolderName()
        {
            if (_folderNameEdited)
                return;

            _settingFolderName = true;
            FolderName = DownloadNames.SongPrefix(Artist, Name, Strings.NewChartDefaultFolderName);
            _settingFolderName = false;
        }

        [RelayCommand]
        private async Task BrowseFolderAsync()
        {
            if (await _folderPicker.PickFolderAsync(Strings.NewChartPickFolderTitle, ParentFolder) is { } folder)
                ParentFolder = folder;
        }

        /// <summary>Re-checks the location, e.g. after the window regains focus and the folder may have appeared.</summary>
        public void RefreshLocation() => Refresh();

        // ---- Create ----

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(BackCommand), nameof(NextCommand), nameof(CreateCommand))]
        public partial bool IsCreating { get; private set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasError))]
        public partial string? ErrorMessage { get; private set; }

        public bool HasError => ErrorMessage is not null;

        private bool CanCreate() => Step == NewChartStep.Parts && BlockedReason is null && !IsCreating;

        [RelayCommand(CanExecute = nameof(CanCreate))]
        private async Task CreateAsync()
        {
            if (Request() is not { } request)
                return;

            _createCts?.Dispose();
            _createCts = new CancellationTokenSource();
            var token = _createCts.Token;
            ErrorMessage = null;
            IsCreating = true;
            var creating = Task.Run(() => NewChartCreator.CreateAsync(request, token), token);
            _creating = creating;
            try
            {
                CreatedPath = await creating;
                _settings.Settings.LastNewChartFolder = request.ParentFolder;
                if (!_closed)
                    CloseRequested?.Invoke();
            }
            catch (OperationCanceledException)
            {
                ErrorMessage = Strings.NewChartCanceled;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                ErrorMessage = string.Format(CultureInfo.CurrentCulture, Strings.NewChartFailedFormat, ex.Message);
            }
            finally
            {
                IsCreating = false;
                Refresh();
            }
        }

        /// <summary>What Create makes, or <see langword="null"/> when something's missing.</summary>
        private NewChartRequest? Request()
        {
            if (ParentFolder is not { } parent || Bpm is not { } bpm || Numerator is not { } numerator)
                return null;

            TimeSpan? length = null;
            if (Files.Count == 0 && NewChartLength.TryParse(LengthText, out var entered))
                length = entered;

            var parts = Parts.Where(p => p.IsChecked).Select(p => p.Instrument).ToHashSet();
            return new NewChartRequest
            {
                ParentFolder = parent,
                FolderName = FolderName,
                Name = Name,
                Artist = Artist,
                Album = Album,
                Genre = Genre,
                Year = Year,
                Charter = Charter,
                AlbumArtPath = ArtPath,
                Stems = [.. Files.Select(f => new NewChartStem(f.Path, f.Stem!))],
                Length = length,
                LeadInMs = (long)(LeadInMs ?? 0),
                Chart = new NewChartSettings(
                    Name.Trim(),
                    NewChartSettings.Resolutions[ResolutionIndex],
                    (double)bpm,
                    (int)numerator,
                    NewChartSettings.Denominators[DenominatorIndex],
                    parts,
                    (DrumsVariant)DrumsVariantIndex),
            };
        }

        /// <summary>Cancels Create while it runs; otherwise closes the window.</summary>
        [RelayCommand]
        private void Close()
        {
            if (IsCreating)
            {
                _createCts?.Cancel();
                return;
            }

            CloseRequested?.Invoke();
        }

        /// <summary>The window is closing some other way; stops a Create that's running.</summary>
        public void OnClosed()
        {
            _closed = true;
            _createCts?.Cancel();
        }

        /// <summary>Once the window has closed: waits for a cancelled Create to clean up after itself, and lets go of the art preview.</summary>
        public async Task ShutdownAsync()
        {
            OnClosed();
            if (_creating is { } creating)
            {
                try
                {
                    // A Create that got as far as moving the folder into place still made the song.
                    CreatedPath ??= await creating;
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or UnauthorizedAccessException or ArgumentException)
                {
                }
            }

            ArtPreview?.Dispose();
            ArtPreview = null;
        }

        // ---- State ----

        /// <summary>Works out <see cref="BlockedReason"/> for the current step.</summary>
        private void Refresh()
        {
            BlockedReason = Step switch
            {
                NewChartStep.Song => SongProblem(),
                NewChartStep.Audio => AudioProblem(),
                NewChartStep.Timing => TimingProblem(),
                _ => PartsProblem(),
            };
        }

        private string? SongProblem()
        {
            (string Key, string Value, string Label)[] fields =
            [
                ("name", Name, Strings.NewChartNameLabel),
                ("artist", Artist, Strings.NewChartArtistLabel),
                ("album", Album, Strings.NewChartAlbumLabel),
                ("genre", Genre, Strings.NewChartGenreLabel),
                ("year", Year, Strings.NewChartYearLabel),
                ("charter", Charter, Strings.NewChartCharterLabel),
            ];

            foreach (var (key, value, label) in fields)
            {
                if (string.IsNullOrWhiteSpace(value))
                    continue;

                if (MetadataValidator.ValidatePair(key, value.Trim()).FirstOrDefault(MetadataValidator.IsFailing) is { } finding)
                    return string.Format(CultureInfo.CurrentCulture, Strings.NewChartFieldProblemFormat, label, finding.Message);
            }

            return null;
        }

        private string? AudioProblem()
        {
            if (Files.Count == 0)
            {
                if (!NoAudio)
                    return Strings.NewChartNoAudioChosen;
                return NewChartLength.TryParse(LengthText, out _) ? null : Strings.NewChartLengthInvalid;
            }

            if (Files.Any(f => f.IsChecking))
                return Strings.NewChartAudioChecking;

            if (Files.FirstOrDefault(f => f.HasProblem) is { } broken)
                return string.Format(CultureInfo.CurrentCulture, Strings.NewChartFileProblemFormat, broken.FileName, broken.Problem);

            if (NewChartStems.Problems([.. Files.Select(f => f.Stem)]) is [var problem, ..])
            {
                return problem.Kind == NewChartStemProblemKind.Unassigned
                    ? string.Format(CultureInfo.CurrentCulture, Strings.NewChartStemUnassignedFormat, Files[problem.Indexes[0]].FileName)
                    : string.Format(CultureInfo.CurrentCulture, Strings.NewChartStemSharedFormat,
                        string.Join(", ", problem.Indexes.Select(i => Files[i].FileName)), problem.Stem);
            }

            return null;
        }

        private string? TimingProblem()
        {
            if (Bpm is null)
                return Strings.NewChartBpmMissing;
            if (Numerator is null)
                return Strings.NewChartTimeSignatureMissing;
            return LeadInMs is null ? Strings.NewChartLeadInMissing : null;
        }

        private string? PartsProblem()
        {
            if (!Parts.Any(p => p.IsChecked))
                return Strings.NewChartNoParts;

            return NewChartFolder.ProblemOf(ParentFolder, FolderName) switch
            {
                null => null,
                { Kind: NewChartFolderProblemKind.NoParent } => Strings.NewChartNoParent,
                { Kind: NewChartFolderProblemKind.ParentMissing } => Strings.NewChartParentMissing,
                { Kind: NewChartFolderProblemKind.Exists } => string.Format(CultureInfo.CurrentCulture, Strings.NewChartFolderExistsFormat, FolderName),
                { Reason: var reason } => string.Format(CultureInfo.CurrentCulture, Strings.NewChartFolderInvalidFormat, reason),
            };
        }
    }
}
