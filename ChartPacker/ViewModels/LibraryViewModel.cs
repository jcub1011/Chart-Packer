using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Collections;
using Avalonia.Controls.Selection;
using ChartPacker.Core.Jobs;
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
        private readonly IFolderPickerService _folderPicker;
        private readonly SettingsService _settings;
        private CancellationTokenSource? _scanCts;

        // What the list currently reflects: the folder and depth of the most recent scan.
        private string? _scannedFolder;
        private int _scannedDepth;

        // A job has written to disk since the last scan.
        private bool _outputsChanged;

        public LibraryViewModel(IFolderPickerService folderPicker, SettingsService settings, JobViewModel job)
        {
            _folderPicker = folderPicker;
            _settings = settings;
            Job = job;
            _scanDepth = settings.Settings.ScanMaxDepth;
            _folderPath = settings.Settings.LastFolder;

            OverwriteOptions =
            [
                new(OverwritePolicy.Skip, Strings.OverwriteSkip),
                new(OverwritePolicy.Overwrite, Strings.OverwriteOverwrite),
                new(OverwritePolicy.Rename, Strings.OverwriteRename),
            ];
            _selectedOverwriteOption = OverwriteOptions.First(o => o.Value == settings.Settings.OverwritePolicy);
            _flatPackOutput = settings.Settings.FlatPackOutput;

            Selection = new SelectionModel<SongEntry> { SingleSelect = false, Source = Entries };
            Selection.PropertyChanged += OnSelectionPropertyChanged;

            Job.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(JobViewModel.IsRunning))
                    RefreshCommands();
            };
        }

        public JobViewModel Job { get; }

        public AvaloniaList<SongEntry> Entries { get; } = new();

        /// <summary>The list's selection. Several items can be selected for Pack and Unpack.</summary>
        public SelectionModel<SongEntry> Selection { get; }

        public IReadOnlyList<OverwriteOption> OverwriteOptions { get; }

        [ObservableProperty]
        private OverwriteOption _selectedOverwriteOption;

        /// <summary>Pack writes every .sng directly in the output folder. Unpack always mirrors.</summary>
        [ObservableProperty]
        private bool _flatPackOutput;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(RescanCommand))]
        private string? _folderPath;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(PackCommand))]
        [NotifyCanExecuteChangedFor(nameof(UnpackCommand))]
        private bool _isScanning;

        [ObservableProperty]
        private string? _errorMessage;

        /// <summary>The selected item when exactly one is selected; otherwise <see langword="null"/>.</summary>
        [ObservableProperty]
        private SongEntry? _selectedEntry;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(PackCommand))]
        [NotifyCanExecuteChangedFor(nameof(UnpackCommand))]
        private int _selectedCount;

        /// <summary>Max scan depth. Nullable decimal to match <c>NumericUpDown.Value</c>.</summary>
        [ObservableProperty]
        private decimal? _scanDepth;

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
        public string? EffectiveOutputFolder => _settings.Settings.OutputFolder ?? _scannedFolder ?? FolderPath;

        public bool HasCustomOutputFolder => _settings.Settings.OutputFolder is not null;

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

            _settings.Settings.OutputFolder = path;
            RefreshOutput();
        }

        private bool CanResetOutput() => HasCustomOutputFolder && !Job.IsRunning;

        /// <summary>Goes back to writing next to the scanned songs.</summary>
        [RelayCommand(CanExecute = nameof(CanResetOutput))]
        private void ResetOutput()
        {
            _settings.Settings.OutputFolder = null;
            RefreshOutput();
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

            // In list order rather than the order the items were clicked.
            var entries = Selection.SelectedIndexes.Order().Select(i => Entries[i]).ToList();
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

            _scannedFolder = folder;
            _scannedDepth = depth;
            _outputsChanged = false;
            Selection.Clear();
            Entries.Clear();
            ErrorMessage = null;
            IsScanning = true;
            RefreshState();

            // Progress<T> posts to the UI thread in order, ahead of this method's continuation,
            // so every batch has landed by the time IsScanning is cleared below.
            var progress = new Progress<IReadOnlyList<SongEntry>>(batch =>
            {
                if (_scanCts != cts)
                    return;
                Entries.AddRange(batch);
                RefreshState();
            });

            try
            {
                await SongLibraryScanner.ScanAsync(folder, depth, progress, cts.Token);
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
                if (_scanCts == cts)
                {
                    _scanCts = null;
                    IsScanning = false;
                }
            }
        }

        private void OnSelectionPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            // SelectionModel never raises Count. SelectedIndexes covers every change except a
            // source reset, which raises SelectedItem instead.
            if (e.PropertyName is not (nameof(Selection.SelectedIndexes) or nameof(Selection.SelectedItem)))
                return;

            SelectedCount = Selection.Count;
            SelectedEntry = Selection.Count == 1 ? Selection.SelectedItem : null;
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
