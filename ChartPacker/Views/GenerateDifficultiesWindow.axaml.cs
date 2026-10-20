using System;
using System.ComponentModel;
using Avalonia.Controls;
using ChartPacker.ViewModels;
using ChartShared.UI.ViewModels;
using ChartShared.UI.Views;

namespace ChartPacker.Views
{
    /// <summary>
    /// The Generate difficulties dialog (<see cref="GenerateDifficultiesViewModel"/>). It closes once Generate or Cancel is
    /// pressed; closing it otherwise cancels it. While the preview is shown it takes the rest of the window's width, and
    /// Space plays or pauses and N goes to the next change (Shift+N the one before) (<see cref="ReductionPreviewKeys"/>);
    /// hidden, the songs take it.
    /// </summary>
    public partial class GenerateDifficultiesWindow : Window
    {
        private GenerateDifficultiesViewModel? _viewModel;

        public GenerateDifficultiesWindow()
        {
            InitializeComponent();
            WindowChrome.ShowIcon(this, WindowTitleBar);
            Opened += (_, _) => WindowChrome.UseDarkTitleBar(this);
            Closed += (_, _) => Watch(null);
            ReductionPreviewKeys.Attach(this, () => _viewModel?.Preview);
        }

        protected override void OnDataContextChanged(EventArgs e)
        {
            Watch(DataContext as GenerateDifficultiesViewModel);
            ApplyPreview();
            base.OnDataContextChanged(e);
        }

        private void Watch(GenerateDifficultiesViewModel? viewModel)
        {
            if (_viewModel is not null)
            {
                _viewModel.Done -= OnDone;
                _viewModel.Preview.PropertyChanged -= OnPreviewChanged;
            }

            _viewModel = viewModel;
            if (_viewModel is not null)
            {
                _viewModel.Done += OnDone;
                _viewModel.Preview.PropertyChanged += OnPreviewChanged;
            }
        }

        private void OnDone(object? sender, EventArgs e) => Close();

        private void OnPreviewChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ReductionPreviewViewModel.IsExpanded))
                ApplyPreview();
        }

        // The preview's columns shown or hidden, as the view model says: the songs take the width it leaves.
        private void ApplyPreview()
        {
            bool shown = _viewModel?.Preview.IsExpanded == true;
            var columns = Body.ColumnDefinitions;
            PreviewPane.IsVisible = shown;
            PreviewSplitter.IsVisible = shown;
            columns[1].Width = shown ? new GridLength(360) : GridLength.Star;
            columns[3].Width = shown ? GridLength.Star : new GridLength(0);
            columns[3].MinWidth = shown ? 360 : 0;
        }
    }
}
