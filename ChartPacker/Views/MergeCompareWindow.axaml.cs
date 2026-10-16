using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ChartPacker.ViewModels;
using ChartShared.UI.Views;

namespace ChartPacker.Views
{
    /// <summary>
    /// Lines one of the songs being merged up by eye with the kept audio's song. Applying takes the offset; Cancel, Escape or
    /// closing the window leaves the song where it was.
    /// </summary>
    public partial class MergeCompareWindow : Window
    {
        public MergeCompareWindow()
        {
            InitializeComponent();
            WindowChrome.ShowIcon(this, WindowTitleBar);
            Opened += OnOpened;
        }

        private async void OnOpened(object? sender, EventArgs e)
        {
            WindowChrome.UseDarkTitleBar(this);
            if (DataContext is MergeCompareViewModel viewModel)
                await viewModel.LoadAsync();
        }

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);
            if (DataContext is MergeCompareViewModel viewModel)
                viewModel.CloseRequested += Close;
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            (DataContext as MergeCompareViewModel)?.OnClosed();
        }

        private void OnZoomIn(object? sender, RoutedEventArgs e) => Timeline.ZoomIn();

        private void OnZoomOut(object? sender, RoutedEventArgs e) => Timeline.ZoomOut();

        private void OnShowStart(object? sender, RoutedEventArgs e) => Timeline.ShowStart();

        private void OnShowAll(object? sender, RoutedEventArgs e) => Timeline.ShowAll();
    }
}
