using System;
using Avalonia.Controls;
using ChartPacker.UI.Views;
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

            // The splitter positions are view state, so they're applied and captured here rather than bound.
            Opened += (_, _) =>
            {
                WindowChrome.UseDarkTitleBar(this);
                RestoreLibraryPaneWidth();
            };
            Closing += OnClosing;

            // Coming back from Chart Builder, the songs it may have saved are read again (charter-spec §4.8).
            Activated += (_, _) =>
            {
                if (DataContext is MainWindowViewModel vm)
                    _ = vm.OnActivatedAsync();
            };
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
