using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ChartPacker.ViewModels;

namespace ChartPacker.Views
{
    public partial class SongDetailView : UserControl
    {
        public SongDetailView()
        {
            InitializeComponent();

            // The new-row key box acts as a dropdown of known keys: it opens when the editor appears
            // and whenever the box is clicked, not only on Down/F4 as AutoCompleteBox does by itself.
            NewKeyBox.PropertyChanged += (_, e) =>
            {
                if (e.Property == IsVisibleProperty && e.NewValue is true)
                    Dispatcher.UIThread.Post(OpenKeyDropDown, DispatcherPriority.Loaded);
            };
            NewKeyBox.AddHandler(PointerReleasedEvent, OnKeyBoxPointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
            NewKeyBox.AddHandler(KeyDownEvent, OnKeyBoxKeyDown, RoutingStrategies.Tunnel);

            // While the waveform is held the position stops following playback; letting go seeks there.
            SeekBar.ScrubStarted += (_, _) => Player?.BeginScrub();
            SeekBar.ScrubEnded += (_, _) => EndScrub();

            AddHandler(PointerPressedEvent, OnPointerPressedAnywhere, RoutingStrategies.Bubble, handledEventsToo: true);
        }

        /// <summary>
        /// A click outside the focused edit box takes focus from it, so it commits as it does when focus
        /// moves to another control. Clicks on empty space and labels otherwise leave the box focused.
        /// Runs after the clicked control's own handling, so a click that already moved focus is left alone.
        /// </summary>
        private void OnPointerPressedAnywhere(object? sender, PointerPressedEventArgs e)
        {
            if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is not TextBox box
                || !this.IsVisualAncestorOf(box))
                return;

            if (e.Source is Visual source && (source == box || box.IsVisualAncestorOf(source)))
                return;

            Focus();
        }

        /// <summary>
        /// A click on the highway options' empty space or a label takes focus from an offset box, keeping
        /// what was typed, and leaves the flyout open. The flyout is a popup of its own, so
        /// <see cref="OnPointerPressedAnywhere"/> doesn't see its clicks. Clicks the controls handle don't reach this.
        /// </summary>
        private void OnHighwayOptionsPressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is StackPanel panel && e.Source is Visual source && source.FindAncestorOfType<TextBox>(includeSelf: true) is null)
                panel.Focus(NavigationMethod.Pointer);
        }

        private PlayerViewModel? Player => (DataContext as SongDetailViewModel)?.Player;

        private void EndScrub()
        {
            if (Player is { IsScrubbing: true } player)
                player.EndScrub();
        }

        /// <summary>Clicking away from a value's edit box keeps what was typed, like pressing Enter.</summary>
        private void OnEditLostFocus(object? sender, RoutedEventArgs e)
        {
            if (sender is Control { DataContext: MetadataRowViewModel row })
                row.CommitCommand.Execute(null);
        }

        private void OpenKeyDropDown()
        {
            if (NewKeyBox.IsEffectivelyVisible)
                NewKeyBox.IsDropDownOpen = true;
        }

        private void OnKeyBoxPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            // Only clicks on the text box itself: a click on a suggestion bubbles here too, just as
            // the dropdown closes, and must not open it again.
            if (e.Source is Visual source && source.FindAncestorOfType<TextBox>(includeSelf: true) is not null)
                OpenKeyDropDown();
        }

        /// <summary>
        /// With the dropdown closed, Enter moves on to the value and Escape cancels the new row. With it
        /// open, AutoCompleteBox keeps both keys to pick or dismiss a suggestion.
        /// </summary>
        private void OnKeyBoxKeyDown(object? sender, KeyEventArgs e)
        {
            if (NewKeyBox.IsDropDownOpen || DataContext is not SongDetailViewModel vm)
                return;

            if (e.Key == Key.Enter)
            {
                NewValueBox.Focus();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                vm.CancelAddCommand.Execute(null);
                e.Handled = true;
            }
        }
    }
}
