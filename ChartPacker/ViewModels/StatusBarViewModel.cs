using System.ComponentModel;
using ChartPacker.Resources;
using ChartShared.UI.ViewModels;
using CommunityToolkit.Mvvm.Input;

namespace ChartPacker.ViewModels
{
    /// <summary>
    /// The status bar along the bottom of the window: what the pack/unpack job is doing, or has done, or how far the
    /// library's scan has got. The two never run at once: a job waits for the scan, and a scan for the job. While either
    /// runs, the bar fills with its progress and its spinner cancels it; while no scan runs, clicking the bar opens the
    /// job's progress window.
    /// </summary>
    public partial class StatusBarViewModel : ViewModelBase
    {
        private readonly LibraryViewModel _library;

        public StatusBarViewModel(LibraryViewModel library)
        {
            _library = library;
            library.PropertyChanged += OnLibraryChanged;
            library.Job.PropertyChanged += OnJobChanged;
        }

        private JobViewModel Job => _library.Job;

        /// <summary>The library is being scanned, rather than a job running.</summary>
        public bool IsScanning => _library.IsScanning;

        /// <summary>A job or a scan is running, so the bar shows its progress and can cancel it.</summary>
        public bool IsBusy => Job.IsRunning || IsScanning;

        /// <summary>The scan hasn't found anything yet, so there's no telling how far along it is.</summary>
        public bool IsIndeterminate => IsScanning && _library.IsScanProgressIndeterminate;

        /// <summary>How far the job or scan has got, as a percentage.</summary>
        public double Progress => IsScanning ? _library.ScanProgress : Job.Progress;

        public string Text => IsScanning ? _library.ScanProgressText : Job.SummaryText;

        /// <summary>Nothing has run yet, so the bar only says it's waiting.</summary>
        public bool IsMuted => !IsBusy && Job.IsWaiting;

        public string CancelToolTip => IsScanning ? Strings.CancelScanToolTip : Strings.CancelJobToolTip;

        [RelayCommand(CanExecute = nameof(IsBusy))]
        private void Cancel()
        {
            if (IsScanning)
                _library.CancelScanCommand.Execute(null);
            else
                Job.CancelCommand.Execute(null);
        }

        /// <summary>The progress window is the job's, so it doesn't open for the scan's progress.</summary>
        private bool CanShowDetails() => !IsScanning;

        [RelayCommand(CanExecute = nameof(CanShowDetails))]
        private void ShowDetails() => Job.ShowDetailsCommand.Execute(null);

        private void OnLibraryChanged(object? sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(LibraryViewModel.IsScanning):
                case nameof(LibraryViewModel.IsScanProgressIndeterminate):
                case nameof(LibraryViewModel.ScanProgress):
                case nameof(LibraryViewModel.ScanProgressText):
                    Refresh();
                    break;
            }
        }

        private void OnJobChanged(object? sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(JobViewModel.IsRunning):
                case nameof(JobViewModel.Progress):
                case nameof(JobViewModel.SummaryText):
                case nameof(JobViewModel.IsWaiting):
                    Refresh();
                    break;
            }
        }

        /// <summary>Raises change notifications for everything the bar shows, all of which comes from the library and the job.</summary>
        private void Refresh()
        {
            OnPropertyChanged(nameof(IsScanning));
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(IsIndeterminate));
            OnPropertyChanged(nameof(Progress));
            OnPropertyChanged(nameof(Text));
            OnPropertyChanged(nameof(IsMuted));
            OnPropertyChanged(nameof(CancelToolTip));
            CancelCommand.NotifyCanExecuteChanged();
            ShowDetailsCommand.NotifyCanExecuteChanged();
        }
    }
}
