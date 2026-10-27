using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ChartBuilder.ViewModels;
using ChartShared.UI.Controls;
using ChartShared.UI.Views;

namespace ChartBuilder.Views
{
    /// <summary>
    /// Generate star power's window (charter-spec C8 item 1): the preview, the regions and the timeline, closing with
    /// whether the plan was applied.
    /// </summary>
    public partial class GenerateStarPowerWindow : Window
    {
        private bool _spaceHeld;

        public GenerateStarPowerWindow()
        {
            InitializeComponent();
            WindowChrome.ShowIcon(this, WindowTitleBar);
            Opened += (_, _) => WindowChrome.UseDarkTitleBar(this);

            // Space is the transport's, never a focused button's: a button would click on the key up of a space it never
            // saw pressed, so both are taken first.
            AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
            AddHandler(KeyUpEvent, OnPreviewKeyUp, RoutingStrategies.Tunnel);
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

        private void OnHighwayScrubbed(object? sender, ScrubEventArgs e) => ViewModel?.Playback.SeekTo(e.Seconds);

        private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Handled || e.Key != Key.Space || e.KeyModifiers != KeyModifiers.None || ViewModel is not { } viewModel)
                return;

            if (!_spaceHeld && viewModel.Playback.PlayPauseCommand.CanExecute(null))
                viewModel.Playback.PlayPauseCommand.Execute(null);
            _spaceHeld = true;
            e.Handled = true;
        }

        private void OnPreviewKeyUp(object? sender, KeyEventArgs e)
        {
            if (e.Key != Key.Space || e.KeyModifiers != KeyModifiers.None)
                return;

            _spaceHeld = false;
            e.Handled = true;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Handled || ViewModel is not { } viewModel)
                return;

            switch (e.Key)
            {
                case Key.OemOpenBrackets when e.KeyModifiers == KeyModifiers.None:
                    viewModel.PreviousRegion();
                    e.Handled = true;
                    break;
                case Key.OemCloseBrackets when e.KeyModifiers == KeyModifiers.None:
                    viewModel.NextRegion();
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
