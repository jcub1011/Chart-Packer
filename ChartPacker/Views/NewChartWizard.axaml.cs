using System;
using Avalonia.Controls;
using Avalonia.Input;
using ChartPacker.ViewModels;

namespace ChartPacker.Views
{
    /// <summary>
    /// The New chart wizard. Cancel, Escape or closing the window stops a Create that's running.
    /// Audio files dropped on the Audio step's list are added to it.
    /// </summary>
    public partial class NewChartWizard : Window
    {
        public NewChartWizard()
        {
            InitializeComponent();
            Opened += (_, _) => WindowChrome.UseDarkTitleBar(this);

            // The location may have changed while the window was in the background, e.g. the folder made by hand.
            Activated += (_, _) => (DataContext as NewChartWizardViewModel)?.RefreshLocation();

            AudioDrop.AddHandler(DragDrop.DragEnterEvent, OnDragEnter);
            AudioDrop.AddHandler(DragDrop.DragOverEvent, OnDragOver);
            AudioDrop.AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
            AudioDrop.AddHandler(DragDrop.DropEvent, OnDrop);
        }

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);
            if (DataContext is NewChartWizardViewModel viewModel)
                viewModel.CloseRequested += Close;
        }

        protected override void OnClosing(WindowClosingEventArgs e)
        {
            (DataContext as NewChartWizardViewModel)?.OnClosed();
            base.OnClosing(e);
        }

        private void OnDragEnter(object? sender, DragEventArgs e)
        {
            OnDragOver(sender, e);
            AudioDrop.Classes.Set("drag-over", e.DragEffects != DragDropEffects.None);
        }

        private void OnDragOver(object? sender, DragEventArgs e) =>
            e.DragEffects = DroppedFiles.AudioOf(e).Count > 0 ? DragDropEffects.Copy : DragDropEffects.None;

        private void OnDragLeave(object? sender, DragEventArgs e) => AudioDrop.Classes.Set("drag-over", false);

        private void OnDrop(object? sender, DragEventArgs e)
        {
            AudioDrop.Classes.Set("drag-over", false);
            if (DataContext is NewChartWizardViewModel viewModel)
                viewModel.AddFiles(DroppedFiles.AudioOf(e));
        }
    }
}
