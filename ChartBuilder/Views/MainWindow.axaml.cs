using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using ChartBuilder.ViewModels;
using ChartPacker.Views;

namespace ChartBuilder.Views
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            Opened += (_, _) => WindowChrome.UseDarkTitleBar(this);

            // Audio files dropped anywhere on the window start a new chart (charter-spec C1). The Editor can't use
            // them yet, and says so.
            DragDrop.SetAllowDrop(this, true);
            AddHandler(DragDrop.DragOverEvent, OnDragOver);
            AddHandler(DragDrop.DropEvent, OnDrop);
        }

        private void OnDragOver(object? sender, DragEventArgs e) =>
            e.DragEffects = DroppedFiles.AudioOf(e).Count > 0 ? DragDropEffects.Copy : DragDropEffects.None;

        private void OnDrop(object? sender, DragEventArgs e)
        {
            var files = DroppedFiles.AudioOf(e);
            // Posted, since it closes the launcher (New chart opens the editor) and the drop is still being handled.
            if (files.Count > 0 && DataContext is MainWindowViewModel vm)
                Dispatcher.UIThread.Post(() => vm.StartNewChart(files));
        }
    }
}
