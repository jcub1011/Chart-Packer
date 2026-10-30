using Avalonia.Controls;
using Avalonia.Interactivity;
using ChartBuilder.ViewModels;
using ChartShared.UI.Views;

namespace ChartBuilder.Views
{
    /// <summary>Asks a new chart's name and artist before its first save (charter-spec C1). Closes with true once Save is chosen, false otherwise.</summary>
    public partial class SongInfoWindow : Window
    {
        private SongInfoViewModel? _viewModel;

        public SongInfoWindow()
        {
            InitializeComponent();
            WindowChrome.ShowIcon(this, WindowTitleBar);
            Opened += (_, _) =>
            {
                WindowChrome.UseDarkTitleBar(this);

                // Starts on whatever is still to fill in.
                var box = DataContext is SongInfoViewModel { HasName: true } ? ArtistBox : NameBox;
                box.Focus();
                box.SelectAll();
            };
        }

        protected override void OnDataContextChanged(System.EventArgs e)
        {
            base.OnDataContextChanged(e);
            if (_viewModel is not null)
                _viewModel.Confirmed -= OnConfirmed;

            _viewModel = DataContext as SongInfoViewModel;
            if (_viewModel is not null)
                _viewModel.Confirmed += OnConfirmed;
        }

        private void OnConfirmed(object? sender, System.EventArgs e) => Close(true);

        private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
    }
}
