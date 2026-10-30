using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ChartBuilder.Services;
using ChartBuilder.ViewModels;
using ChartShared.Core.Keybinds;
using ChartShared.UI.Controls;
using ChartShared.UI.Keybinds;
using ChartShared.UI.Views;

namespace ChartBuilder.Views
{
    /// <summary>
    /// The window Generate star power and Generate fills share (charter-spec C6 item 5, C8 item 1): the preview, the
    /// proposals and the timeline, closing with whether the plan was applied.
    /// </summary>
    public partial class GeneratePhrasesWindow : Window
    {
        private Flyout? _regenerateFlyout;
        private GeneratePhrasesViewModel? _attached;

        public GeneratePhrasesWindow()
        {
            // Before the window's content is built, so what it holds finds the dispatcher as it attaches.
            RegisterKeys();
            InitializeComponent();
            WindowChrome.ShowIcon(this, WindowTitleBar);
            WindowMemory.Remember(this);
            Opened += (_, _) => WindowChrome.UseDarkTitleBar(this);
            DataContextChanged += (_, _) => AttachViewModel();
            Closed += (_, _) => DetachViewModel();

            // Esc closes the question first, rather than cancelling the window's plan while it's still open. Not a shortcut
            // to change: it's what the window's Cancel does, as its default button does Enter.
            AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        }

        /// <summary>
        /// The window's keys are the app's <see cref="KeyMap"/>. Space is the transport's, never a focused button's: the dispatcher
        /// takes the press and its release, so a button doesn't click on the key up of a space it never saw pressed.
        /// </summary>
        private void RegisterKeys()
        {
            var keys = new KeyDispatcher(AppKeys.Map, BuilderKeys.GeneratePhrases);
            keys.Attach(this);

            keys.On(SharedKeys.PlayPause, () =>
            {
                if (ViewModel is not { } viewModel)
                    return false;
                if (viewModel.Playback.PlayPauseCommand.CanExecute(null))
                    viewModel.Playback.PlayPauseCommand.Execute(null);
                return true;
            });
            keys.On(BuilderKeys.PreviousRegion, () =>
            {
                if (ViewModel is not { } viewModel)
                    return false;
                viewModel.PreviousRegion();
                return true;
            });
            keys.On(BuilderKeys.NextRegion, () =>
            {
                if (ViewModel is not { } viewModel)
                    return false;
                viewModel.NextRegion();
                return true;
            });
            keys.On(BuilderKeys.ApplyPhrases, () =>
            {
                if (ViewModel is null)
                    return false;
                OnApply(null, new RoutedEventArgs());
                return true;
            });
        }

        private GeneratePhrasesViewModel? ViewModel => DataContext as GeneratePhrasesViewModel;

        // Follows the question as it opens, whichever asked it: Regenerate, or a change of mode.
        private void AttachViewModel()
        {
            DetachViewModel();
            _attached = ViewModel;
            if (_attached is { } viewModel)
                viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        private void DetachViewModel()
        {
            if (_attached is { } viewModel)
                viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _attached = null;
        }

        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(GeneratePhrasesViewModel.IsConfirmingRegenerate))
                return;
            if (ViewModel is { IsConfirmingRegenerate: true })
            {
                // After the event that asked, so the mode picker's own popup can't dismiss the question as it closes.
                Dispatcher.UIThread.Post(() =>
                {
                    if (ViewModel is { IsConfirmingRegenerate: true })
                        ShowRegenerateFlyout();
                });
            }
            else if (_regenerateFlyout is { IsOpen: true } flyout)
            {
                flyout.Hide();
            }
        }

        private void ShowRegenerateFlyout()
        {
            if (_regenerateFlyout is { IsOpen: true } || FlyoutBase.GetAttachedFlyout(RegenerateButton) is not Flyout flyout)
                return;
            _regenerateFlyout = flyout;
            FlyoutBase.ShowAttachedFlyout(RegenerateButton);
        }

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
        /// Regenerates at once when nothing would be lost; otherwise the question opens in a flyout by the button, so the
        /// window's layout doesn't move to make room for it.
        /// </summary>
        private void OnRegenerateClick(object? sender, RoutedEventArgs e)
        {
            // A second press while the question is up asks it again, rather than taking its own press as the answer.
            if (ViewModel is not { IsConfirmingRegenerate: false } viewModel)
                return;
            viewModel.RegenerateCommand.Execute(null);
        }

        private void OnConfirmRegenerate(object? sender, RoutedEventArgs e) => ViewModel?.ConfirmRegenerate();

        private void OnKeepProposals(object? sender, RoutedEventArgs e) => ViewModel?.CancelRegenerate();

        /// <summary>Dismissing the question by clicking away keeps the proposals as they are.</summary>
        private void OnRegenerateFlyoutClosed(object? sender, EventArgs e) => ViewModel?.CancelRegenerate();

        private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Handled)
                return;

            // Escape closes the question first, rather than cancelling the window's plan while it's still open.
            if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None && ViewModel is { IsConfirmingRegenerate: true } confirming)
            {
                confirming.CancelRegenerate();
                e.Handled = true;
            }
        }
    }
}
