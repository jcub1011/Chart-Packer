using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Collections;
using ChartPacker.Core.Scanning;
using ChartPacker.Resources;
using ChartPacker.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChartPacker.ViewModels
{
    /// <summary>The left panel: a picked folder and the song sources found under it.</summary>
    /// <remarks>
    /// Scanning only happens on an explicit <see cref="RescanCommand"/>. Picking a folder, changing
    /// the depth or launching the app never scans; the list is flagged as out of date instead.
    /// </remarks>
    public partial class LibraryViewModel : ViewModelBase
    {
        private readonly IFolderPickerService _folderPicker;
        private readonly SettingsService _settings;
        private CancellationTokenSource? _scanCts;

        // What the list currently reflects: the folder and depth of the most recent scan.
        private string? _scannedFolder;
        private int _scannedDepth;

        public LibraryViewModel(IFolderPickerService folderPicker, SettingsService settings)
        {
            _folderPicker = folderPicker;
            _settings = settings;
            _scanDepth = settings.Settings.ScanMaxDepth;
            _folderPath = settings.Settings.LastFolder;
        }

        public AvaloniaList<SongEntry> Entries { get; } = new();

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(RescanCommand))]
        private string? _folderPath;

        [ObservableProperty]
        private bool _isScanning;

        [ObservableProperty]
        private string? _errorMessage;

        [ObservableProperty]
        private SongEntry? _selectedEntry;

        /// <summary>Max scan depth. Nullable decimal to match <c>NumericUpDown.Value</c>.</summary>
        [ObservableProperty]
        private decimal? _scanDepth;

        public int MaxScanDepth => AppSettings.MaxScanMaxDepth;

        private bool HasScanned => _scannedFolder is not null;

        private bool ScannedFolderIsSelected => string.Equals(FolderPath, _scannedFolder, StringComparison.Ordinal);

        public bool ShowPrompt => FolderPath is null;

        /// <summary>A folder is chosen (e.g. restored at launch) but hasn't been scanned yet.</summary>
        public bool ShowNotScanned => FolderPath is not null && !HasScanned && !IsScanning;

        /// <summary>The list was scanned with a different folder or depth than is now selected.</summary>
        public bool IsStale => HasScanned && !IsScanning &&
            (!ScannedFolderIsSelected || _settings.Settings.ScanMaxDepth != _scannedDepth);

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

        [RelayCommand]
        private async Task BrowseAsync()
        {
            var path = await _folderPicker.PickFolderAsync(Strings.BrowseDialogTitle);
            if (path is null)
                return;

            FolderPath = path;
            _settings.Settings.LastFolder = path;
        }

        private bool CanRescan() => FolderPath is not null;

        /// <summary>Scans the chosen folder. Re-running it mid-scan cancels and restarts.</summary>
        [RelayCommand(CanExecute = nameof(CanRescan))]
        private void Rescan() => _ = ScanAsync(FolderPath!, _settings.Settings.ScanMaxDepth);

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
            SelectedEntry = null;
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
        }
    }
}
