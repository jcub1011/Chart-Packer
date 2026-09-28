using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using ChartPacker.Services;
using ChartPacker.ViewModels;

namespace ChartPacker.Views
{
    public partial class MainWindow : Window
    {
        private const double SplitterWidth = 4;

        private ColumnDefinition LibraryColumn => RootGrid.ColumnDefinitions[0];

        private ColumnDefinition DetailColumn => RootGrid.ColumnDefinitions[2];

        private RowDefinition ToolbarRow => RootGrid.RowDefinitions[0];

        private RowDefinition PanelsRow => RootGrid.RowDefinitions[1];

        private RowDefinition JobRow => RootGrid.RowDefinitions[3];

        // The view model the Job subscription below belongs to.
        private MainWindowViewModel? _subscribed;

        public MainWindow()
        {
            InitializeComponent();

            // The splitter positions are view state, so they're applied and captured here rather than bound.
            Opened += (_, _) =>
            {
                UseDarkTitleBar();
                RestoreLibraryPaneWidth();
                ApplyJobPanelHeight();
            };
            Closing += OnClosing;
            DataContextChanged += (_, _) => SubscribeToJob();
        }

        private void SubscribeToJob()
        {
            if (_subscribed is not null)
                _subscribed.Job.PropertyChanged -= OnJobPropertyChanged;

            _subscribed = DataContext as MainWindowViewModel;
            if (_subscribed is not null)
                _subscribed.Job.PropertyChanged += OnJobPropertyChanged;
        }

        private void OnJobPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(JobViewModel.IsExpanded) || _subscribed is not { } vm)
                return;

            // Collapsing: keep the height the user dragged to, for the next expand. Layout hasn't run
            // yet, so the row still has its expanded height.
            if (!vm.Job.IsExpanded)
                vm.JobPanelHeight = JobRow.ActualHeight;
            ApplyJobPanelHeight();
        }

        /// <summary>Collapsed, the results row fits its summary line; expanded, it's the remembered height and the splitter resizes it.</summary>
        private void ApplyJobPanelHeight()
        {
            if (DataContext is not MainWindowViewModel vm)
                return;

            if (!vm.Job.IsExpanded)
            {
                JobRow.MinHeight = 0;
                JobRow.Height = GridLength.Auto;
                return;
            }

            // Keep the panels at least their minimum height if the window is smaller than when the height was saved.
            double available = ClientSize.Height - ToolbarRow.ActualHeight - PanelsRow.MinHeight - SplitterWidth;
            double height = Math.Clamp(vm.JobPanelHeight, AppSettings.MinJobPanelHeight, Math.Max(AppSettings.MinJobPanelHeight, available));
            JobRow.MinHeight = AppSettings.MinJobPanelHeight;
            JobRow.Height = new GridLength(height, GridUnitType.Pixel);
        }

        // DWMWA_USE_IMMERSIVE_DARK_MODE; Windows 10 20H1 and later. Older builds ignore it and keep a light title bar.
        private const int DwmUseImmersiveDarkMode = 20;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        /// <summary>The app is dark only, but Avalonia leaves the Windows title bar light on Windows 10.</summary>
        private void UseDarkTitleBar()
        {
            if (!OperatingSystem.IsWindows() || TryGetPlatformHandle() is not { } handle)
                return;

            int enabled = 1;
            _ = DwmSetWindowAttribute(handle.Handle, DwmUseImmersiveDarkMode, ref enabled, sizeof(int));
        }

        // Set once the user has agreed to lose their unsaved metadata changes.
        private bool _discardConfirmed;

        private async void OnClosing(object? sender, WindowClosingEventArgs e)
        {
            if (DataContext is not MainWindowViewModel vm)
                return;

            // Closing can't wait for a dialog, so cancel it, ask, and close again if the user agrees.
            if (!_discardConfirmed && vm.HasUnsavedDrafts)
            {
                e.Cancel = true;
                if (await vm.ConfirmDiscardDraftsAsync())
                {
                    _discardConfirmed = true;
                    Close();
                }

                return;
            }

            vm.LibraryPaneWidth = LibraryColumn.ActualWidth;
            if (vm.Job.IsExpanded)
                vm.JobPanelHeight = JobRow.ActualHeight;
        }

        private void RestoreLibraryPaneWidth()
        {
            if (DataContext is not MainWindowViewModel vm)
                return;

            // Keep the detail panel at least its minimum width if the window opens smaller than last time.
            double max = Math.Max(LibraryColumn.MinWidth, ClientSize.Width - DetailColumn.MinWidth - SplitterWidth);
            double width = Math.Clamp(vm.LibraryPaneWidth, LibraryColumn.MinWidth, max);
            LibraryColumn.Width = new GridLength(width, GridUnitType.Pixel);
        }
    }
}
