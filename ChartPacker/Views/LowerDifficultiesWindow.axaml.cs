using System;
using System.ComponentModel;
using Avalonia.Controls;
using ChartPacker.ViewModels;
using ChartShared.UI.ViewModels;
using ChartShared.UI.Views;

namespace ChartPacker.Views
{
    /// <summary>
    /// The Generate lower difficulties window (<see cref="LowerDifficultiesViewModel"/>). It closes once Close is pressed,
    /// and not while songs are saved. While the preview is shown it takes the width the songs leave, and Space plays or
    /// pauses and N goes to the next change (Shift+N the one before) (<see cref="ReductionPreviewKeys"/>); hidden, the
    /// songs take it.
    /// </summary>
    public partial class LowerDifficultiesWindow : Window
    {
        // How wide the songs are while the preview takes the rest.
        private const double SongsWidthWithPreview = 600;

        private LowerDifficultiesViewModel? _viewModel;

        public LowerDifficultiesWindow()
        {
            InitializeComponent();
            WindowChrome.ShowIcon(this, WindowTitleBar);
            Opened += (_, _) => WindowChrome.UseDarkTitleBar(this);
            Closed += (_, _) => Watch(null);
            ReductionPreviewKeys.Attach(this, () => _viewModel?.Preview);
        }

        protected override void OnDataContextChanged(EventArgs e)
        {
            Watch(DataContext as LowerDifficultiesViewModel);
            ApplyPreview();
            base.OnDataContextChanged(e);
        }

        protected override void OnClosing(WindowClosingEventArgs e)
        {
            // A save can't be left half done; anything else stops as the window closes.
            if (_viewModel is { IsSaving: true })
                e.Cancel = true;
            base.OnClosing(e);
        }

        private void Watch(LowerDifficultiesViewModel? viewModel)
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

        // The preview's column shown or hidden, as the view model says: shown, it takes the width the songs leave; hidden,
        // the songs take it.
        private void ApplyPreview()
        {
            bool shown = _viewModel?.Preview.IsExpanded == true;
            var columns = Body.ColumnDefinitions;
            PreviewPane.IsVisible = shown;
            PreviewSplitter.IsVisible = shown;
            columns[1].Width = shown ? new GridLength(SongsWidthWithPreview) : GridLength.Star;
            columns[3].Width = shown ? GridLength.Star : new GridLength(0);
        }
    }
}
