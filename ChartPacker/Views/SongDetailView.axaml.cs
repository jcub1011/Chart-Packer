using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.LogicalTree;
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

            // The lanes scrub the same way, showing their whole chart while the pointer is over them.
            HighwayOverlayView.ScrubStarted += (_, _) => Player?.BeginScrub();
            HighwayOverlayView.Scrubbed += (_, e) => ScrubTo(e.Seconds);
            HighwayOverlayView.ScrubEnded += (_, _) => EndScrub();
            HighwayRowView.ScrubStarted += (_, _) => Player?.BeginScrub();
            HighwayRowView.Scrubbed += (_, e) => ScrubTo(e.Seconds);
            HighwayRowView.ScrubEnded += (_, _) => EndScrub();
            LyricLane.ScrubStarted += (_, _) => Player?.BeginScrub();
            LyricLane.Scrubbed += (_, e) => ScrubTo(e.Seconds);
            LyricLane.ScrubEnded += (_, _) => EndScrub();

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
        /// A click on the empty space or a label of a flyout with an offset box in it, the highway options or the video
        /// offset, takes focus from the box, keeping what was typed, and leaves the flyout open. The flyout is a popup
        /// of its own, so <see cref="OnPointerPressedAnywhere"/> doesn't see its clicks. Clicks the controls handle don't reach this.
        /// </summary>
        private void OnOffsetFlyoutPressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is StackPanel panel && e.Source is Visual source && source.FindAncestorOfType<TextBox>(includeSelf: true) is null)
                panel.Focus(NavigationMethod.Pointer);
        }

        /// <summary>While the highway options are open, the notes are drawn as outlines so the waveform behind them shows as offsets are set.</summary>
        private void OnHighwayOptionsOpened(object? sender, EventArgs e) => SetTuning(true);

        private void OnHighwayOptionsClosed(object? sender, EventArgs e) => SetTuning(false);

        // Both placements' views, as the options are shared and only the one in use is shown.
        private void SetTuning(bool isTuning)
        {
            HighwayOverlayView.Classes.Set("tuning", isTuning);
            HighwayRowView.Classes.Set("tuning", isTuning);
        }

        /// <summary>
        /// Choosing a difficulty in an instrument's flyout closes it. The flyout is a popup of its own,
        /// so the button it belongs to is found through the popup it's shown in.
        /// </summary>
        /// <remarks>
        /// A button raises Click before it runs its command, and closing the flyout takes the button
        /// out of the tree, which clears its bound command. So the flyout closes once the command has run.
        /// </remarks>
        private void OnDifficultyChosen(object? sender, RoutedEventArgs e)
        {
            if (sender is ILogical control && control.FindLogicalAncestorOfType<Popup>() is { PlacementTarget: Button { Flyout: { } flyout } })
                Dispatcher.UIThread.Post(flyout.Hide);
        }

        private PlayerViewModel? Player => (DataContext as SongDetailViewModel)?.Player;

        private void EndScrub()
        {
            if (Player is { IsScrubbing: true } player)
                player.EndScrub();
        }

        /// <summary>A lane is being dragged: the position follows it, and playback moves there when it's let go.</summary>
        private void ScrubTo(double seconds)
        {
            if (Player is { IsScrubbing: true } player)
                player.PositionSeconds = seconds;
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
