using System;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using ChartPacker.Services;
using ChartPacker.ViewModels;
using ChartShared.UI.Keybinds;
using ChartShared.UI.Views;

namespace ChartPacker.Views
{
    public partial class MainWindow : Window
    {
        private const double SplitterWidth = 4;

        private ColumnDefinition LibraryColumn => RootGrid.ColumnDefinitions[0];

        private ColumnDefinition DetailColumn => RootGrid.ColumnDefinitions[2];

        public MainWindow()
        {
            // Before the panels are built, so the library and the detail panel find the dispatcher as they attach.
            RegisterKeys();
            InitializeComponent();

            // The splitter positions are view state, so they're applied and captured here rather than bound.
            Opened += (_, _) =>
            {
                WindowChrome.UseDarkTitleBar(this);
                RestoreLibraryPaneWidth();
            };
            Closing += OnClosing;
            // After the splitters' capture, so the one save as the window closes keeps them too.
            WindowMemory.Remember(this, position: true);

            // A songs folder dropped anywhere on the window becomes the library and is scanned.
            DragDrop.SetAllowDrop(this, true);
            AddHandler(DragDrop.DragOverEvent, OnDragOver);
            AddHandler(DragDrop.DropEvent, OnDrop);

            // Coming back from Chart Builder, the songs it may have saved are read again (charter-spec §4.8), and so are
            // the keys it may have rebound.
            Activated += (_, _) =>
            {
                AppKeys.ReloadIfChanged();
                if (DataContext is MainWindowViewModel vm)
                    _ = vm.OnActivatedAsync();
            };
        }

        /// <summary>
        /// The window's keys are the app's <see cref="KeyMap"/>, not bindings of its own, so rebinding one moves it here too. A
        /// command that can't run now leaves its key to whatever else would take it.
        /// </summary>
        private void RegisterKeys()
        {
            var keys = new KeyDispatcher(AppKeys.Map, PackerKeys.Main);
            keys.Attach(this);

            keys.On(PackerKeys.Rescan, () => Run(vm => vm.Library.RescanCommand));
            keys.On(SharedKeys.Settings, () => Run(vm => vm.OpenSettingsCommand));
            keys.On(SharedKeys.Save, () => Run(vm => vm.Detail.SaveCommand));
            keys.On(SharedKeys.Undo, () => Run(vm => vm.Detail.UndoCommand));
            keys.On(SharedKeys.Redo, () => Run(vm => vm.Detail.RedoCommand));
        }

        private bool Run(Func<MainWindowViewModel, ICommand> command)
        {
            if (DataContext is not MainWindowViewModel vm || command(vm) is not { } run || !run.CanExecute(null))
                return false;

            run.Execute(null);
            return true;
        }

        private void OnDragOver(object? sender, DragEventArgs e)
        {
            bool accepted = DataContext is MainWindowViewModel vm && vm.Library.CanBrowse() && DroppedFolder.Of(e) is not null;
            e.DragEffects = accepted ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void OnDrop(object? sender, DragEventArgs e)
        {
            e.Handled = true;
            if (DataContext is not MainWindowViewModel vm || DroppedFolder.Of(e) is not { } folder)
                return;

            // Posted, since it replaces the library and the drop is still being handled.
            Dispatcher.UIThread.Post(async () =>
            {
                // Replacing the library discards the unsaved metadata changes, so ask first, as closing does.
                if (vm.HasUnsavedDrafts && !await vm.ConfirmDiscardDraftsForDropAsync())
                    return;

                vm.Library.OpenDroppedFolder(folder);
            });
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
