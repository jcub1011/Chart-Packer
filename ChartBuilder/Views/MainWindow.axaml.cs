using Avalonia.Controls;
using Avalonia.Input;
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

            // Audio files dropped anywhere on the window start a new chart with them (charter-spec C1).
            DragDrop.SetAllowDrop(this, true);
            AddHandler(DragDrop.DragOverEvent, OnDragOver);
            AddHandler(DragDrop.DropEvent, OnDrop);
        }

        private void OnDragOver(object? sender, DragEventArgs e) =>
            e.DragEffects = DroppedFiles.AudioOf(e).Count > 0 ? DragDropEffects.Copy : DragDropEffects.None;

        private void OnDrop(object? sender, DragEventArgs e)
        {
            var files = DroppedFiles.AudioOf(e);
            if (files.Count > 0 && DataContext is MainWindowViewModel vm)
                _ = vm.NewChartAsync(files);
        }
    }
}
