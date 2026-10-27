using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ChartBuilder.ViewModels;
using ChartShared.UI.Controls.Highway;
using ChartShared.UI.Views;

namespace ChartBuilder.Views
{
    /// <summary>
    /// Generate star power's window (charter-spec C8 item 1): the preview, the regions and the timeline, closing with
    /// whether the plan was applied.
    /// </summary>
    public partial class GenerateStarPowerWindow : Window
    {
        public GenerateStarPowerWindow()
        {
            InitializeComponent();
            WindowChrome.ShowIcon(this, WindowTitleBar);
            Opened += (_, _) => WindowChrome.UseDarkTitleBar(this);
        }

        private GenerateStarPowerViewModel? ViewModel => DataContext as GenerateStarPowerViewModel;

        private void OnApply(object? sender, RoutedEventArgs e)
        {
            if (ViewModel is { } viewModel && viewModel.Apply())
                Close(true);
        }

        private void OnCancel(object? sender, RoutedEventArgs e)
        {
            ViewModel?.Cancel();
            Close(false);
        }

        private void OnPhraseDrawStarted(object? sender, System.EventArgs e) => ViewModel?.StartDrawing();

        private void OnPhraseDrawn(object? sender, PhraseDrawEventArgs e) => ViewModel?.Draw(e.Start, e.End);

        private void OnPhraseResized(object? sender, PhraseResizeEventArgs e) => ViewModel?.Resize(e.Index, e.Start, e.End);

        private void OnPhraseResizeEnded(object? sender, System.EventArgs e) => ViewModel?.EndResize();

        private void OnPhraseSelected(object? sender, int index)
        {
            if (ViewModel is not { } viewModel || index < 0 || index >= viewModel.Regions.Count)
                return;
            viewModel.StartDrawing();
            viewModel.SelectRegionForEdit(viewModel.Regions[index]);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Handled || ViewModel is not { } viewModel)
                return;

            switch (e.Key)
            {
                case Key.Space when e.KeyModifiers == KeyModifiers.None:
                    if (viewModel.Playback.PlayPauseCommand.CanExecute(null))
                        viewModel.Playback.PlayPauseCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Key.OemOpenBrackets when e.KeyModifiers == KeyModifiers.None:
                    viewModel.PreviousRegion();
                    e.Handled = true;
                    break;
                case Key.OemCloseBrackets when e.KeyModifiers == KeyModifiers.None:
                    viewModel.NextRegion();
                    e.Handled = true;
                    break;
                case Key.Delete when e.KeyModifiers == KeyModifiers.None:
                    viewModel.RemoveSelected();
                    e.Handled = true;
                    break;
                case Key.Enter when e.KeyModifiers == KeyModifiers.Control:
                    OnApply(null, new RoutedEventArgs());
                    e.Handled = true;
                    break;
            }
        }
    }
}
