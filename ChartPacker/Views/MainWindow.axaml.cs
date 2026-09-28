using System;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using ChartPacker.ViewModels;

namespace ChartPacker.Views
{
    public partial class MainWindow : Window
    {
        private const double SplitterWidth = 4;

        private ColumnDefinition LibraryColumn => RootGrid.ColumnDefinitions[0];

        private ColumnDefinition DetailColumn => RootGrid.ColumnDefinitions[2];

        public MainWindow()
        {
            InitializeComponent();

            // The splitter position is view state, so it's applied and captured here rather than bound.
            Opened += (_, _) =>
            {
                UseDarkTitleBar();
                RestoreLibraryPaneWidth();
            };
            Closing += OnClosing;
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
