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

            // Audio files dropped anywhere on the window start a new chart (charter-spec C1). The editor doesn't
            // take them yet.
            DragDrop.SetAllowDrop(this, true);
            AddHandler(DragDrop.DragOverEvent, OnDragOver);
            AddHandler(DragDrop.DropEvent, OnDrop);
        }

        private void OnDragOver(object? sender, DragEventArgs e) =>
            e.DragEffects = DroppedFiles.AudioOf(e).Count > 0 ? DragDropEffects.Copy : DragDropEffects.None;

        private void OnDrop(object? sender, DragEventArgs e)
        {
            if (DroppedFiles.AudioOf(e).Count > 0 && DataContext is MainWindowViewModel vm)
                vm.NewChart();
        }
    }
}
