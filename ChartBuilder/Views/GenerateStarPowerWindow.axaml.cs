using System;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
        private Flyout? _regenerateFlyout;

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

        /// <summary>
        /// Regenerates at once when nothing would be lost; otherwise asks in a flyout by the button, so the window's
        /// layout doesn't move to make room for a question.
        /// </summary>
        private void OnRegenerateClick(object? sender, RoutedEventArgs e)
        {
            if (ViewModel is not { } viewModel || sender is not Control anchor)
                return;

            // A second press while the question is up asks it again, rather than taking its own press as the answer.
            if (!viewModel.IsConfirmingRegenerate)
                viewModel.RegenerateCommand.Execute(null);
            if (!viewModel.IsConfirmingRegenerate)
                return;
            if (FlyoutBase.GetAttachedFlyout(anchor) is Flyout flyout)
            {
                _regenerateFlyout = flyout;
                FlyoutBase.ShowAttachedFlyout(anchor);
            }
        }

        private void OnConfirmRegenerate(object? sender, RoutedEventArgs e)
        {
            ViewModel?.ConfirmRegenerate();
            _regenerateFlyout?.Hide();
        }

        private void OnKeepRegions(object? sender, RoutedEventArgs e)
        {
            ViewModel?.CancelRegenerate();
            _regenerateFlyout?.Hide();
        }

        /// <summary>Dismissing the question by clicking away keeps the regions as they are.</summary>
        private void OnRegenerateFlyoutClosed(object? sender, EventArgs e) => ViewModel?.CancelRegenerate();

        private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Handled)
                return;

            // Escape closes the question first, rather than cancelling the window's plan while it's still open.
            if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None && ViewModel is { IsConfirmingRegenerate: true } confirming)
            {
                confirming.CancelRegenerate();
                _regenerateFlyout?.Hide();
                e.Handled = true;
                return;
            }

            if (e.Key != Key.Space || e.KeyModifiers != KeyModifiers.None || ViewModel is not { } viewModel)
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
