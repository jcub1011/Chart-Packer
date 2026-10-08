using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using ChartBuilder.ViewModels;
using ChartPacker.UI.Views;

namespace ChartBuilder.Views
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            Opened += (_, _) => WindowChrome.UseDarkTitleBar(this);

            // Audio files dropped anywhere on the window start a new chart with them as its stems (charter-spec C1). A
            // song folder or .sng opens it (C2).
            DragDrop.SetAllowDrop(this, true);
            AddHandler(DragDrop.DragOverEvent, OnDragOver);
            AddHandler(DragDrop.DropEvent, OnDrop);
        }

        private void OnDragOver(object? sender, DragEventArgs e) =>
            e.DragEffects = DroppedFiles.SongOf(e) is not null || DroppedFiles.AudioOf(e).Count > 0 ? DragDropEffects.Copy : DragDropEffects.None;

        private void OnDrop(object? sender, DragEventArgs e)
        {
            if (DataContext is not MainWindowViewModel vm)
                return;

            // Posted, since it closes the launcher (the editor opens in its place) and the drop is still being handled.
            if (DroppedFiles.SongOf(e) is { } song)
            {
                Dispatcher.UIThread.Post(() => _ = vm.OpenSongAsync(song));
                return;
            }

            var files = DroppedFiles.AudioOf(e);
            if (files.Count > 0)
                Dispatcher.UIThread.Post(() => vm.StartNewChart(files));
        }
    }
}
