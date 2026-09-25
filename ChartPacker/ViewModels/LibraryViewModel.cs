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
    public partial class LibraryViewModel : ViewModelBase
    {
        private readonly IFolderPickerService _folderPicker;
        private readonly SettingsService _settings;
        private CancellationTokenSource? _scanCts;

        public LibraryViewModel(IFolderPickerService folderPicker, SettingsService settings)
        {
            _folderPicker = folderPicker;
            _settings = settings;
            _scanDepth = settings.Settings.ScanMaxDepth;
        }

        public AvaloniaList<SongEntry> Entries { get; } = new();

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ShowPrompt), nameof(ShowEmpty), nameof(ShowStatus))]
        private string? _folderPath;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ShowEmpty), nameof(ShowCenteredSpinner), nameof(ShowStatus), nameof(StatusText))]
        private bool _isScanning;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasError), nameof(ShowEmpty), nameof(ShowStatus))]
        private string? _errorMessage;

        [ObservableProperty]
        private SongEntry? _selectedEntry;

        /// <summary>Max scan depth. Nullable decimal to match <c>NumericUpDown.Value</c>.</summary>
        [ObservableProperty]
        private decimal? _scanDepth;

        public int MaxScanDepth => AppSettings.MaxScanMaxDepth;

        public bool ShowPrompt => FolderPath is null;

        public bool HasError => ErrorMessage is not null;

        public bool ShowEmpty => FolderPath is not null && !IsScanning && !HasError && Entries.Count == 0;

        public bool ShowCenteredSpinner => IsScanning && Entries.Count == 0;

        /// <summary>The bottom status line; hidden while a centered state (spinner, empty, error) says it instead.</summary>
        public bool ShowStatus => FolderPath is not null && !HasError && Entries.Count > 0;

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
            _ = ScanAsync();
        }

        partial void OnScanDepthChanged(decimal? value)
        {
            if (value is null)
            {
                // The box was cleared; put the last good value back.
                ScanDepth = _settings.Settings.ScanMaxDepth;
                return;
            }

            int depth = Math.Clamp((int)Math.Round(value.Value), 0, AppSettings.MaxScanMaxDepth);
            if (depth == _settings.Settings.ScanMaxDepth)
                return;

            _settings.Settings.ScanMaxDepth = depth;
            if (FolderPath is not null)
                _ = ScanAsync();
        }

        private async Task ScanAsync()
        {
            _scanCts?.Cancel();
            using var cts = new CancellationTokenSource();
            _scanCts = cts;

            SelectedEntry = null;
            Entries.Clear();
            ErrorMessage = null;
            IsScanning = true;
            RefreshCounts();

            // Progress<T> posts to the UI thread in order, ahead of this method's continuation,
            // so every batch has landed by the time IsScanning is cleared below.
            var progress = new Progress<IReadOnlyList<SongEntry>>(batch =>
            {
                if (_scanCts != cts)
                    return;
                Entries.AddRange(batch);
                RefreshCounts();
            });

            try
            {
                await SongLibraryScanner.ScanAsync(FolderPath!, _settings.Settings.ScanMaxDepth, progress, cts.Token);
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
                    RefreshCounts();
                }
            }
        }

        private void RefreshCounts()
        {
            OnPropertyChanged(nameof(ShowEmpty));
            OnPropertyChanged(nameof(ShowCenteredSpinner));
            OnPropertyChanged(nameof(ShowStatus));
            OnPropertyChanged(nameof(StatusText));
        }
    }
}
