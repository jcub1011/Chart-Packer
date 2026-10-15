using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ChartPacker.Services;
using ChartPacker.ViewModels;

namespace ChartPacker.Views
{
    public partial class SongDetailView : UserControl
    {
        // How far the wheel has turned with Ctrl held towards the next step, in notches.
        private double _wheelNotches;

        // The window whose Space plays and pauses, while the panel is in it.
        private TopLevel? _keysFrom;

        // Space played or paused and hasn't been let go yet, so its auto-repeat doesn't toggle again.
        private bool _isSpaceHeld;

        public SongDetailView()
        {
            InitializeComponent();

            // The tips of everything in the panel show along its bottom rather than beside the pointer.
            TipStrip.Region = this;

            // While the waveform is held the position stops following playback; letting go seeks there.
            SeekBar.ScrubStarted += (_, _) => Player?.BeginScrub();
            SeekBar.ScrubEnded += (_, _) => EndScrub();

            // The lanes scrub the same way, showing their whole chart while Shift is held over them.
            HighwayOverlayView.ScrubStarted += (_, _) => Player?.BeginScrub();
            HighwayOverlayView.Scrubbed += (_, e) => ScrubTo(e.Seconds);
            HighwayOverlayView.ScrubEnded += (_, _) => EndScrub();
            HighwayRowView.ScrubStarted += (_, _) => Player?.BeginScrub();
            HighwayRowView.Scrubbed += (_, e) => ScrubTo(e.Seconds);
            HighwayRowView.ScrubEnded += (_, _) => EndScrub();
            LyricLane.ScrubStarted += (_, _) => Player?.BeginScrub();
            LyricLane.Scrubbed += (_, e) => ScrubTo(e.Seconds);
            LyricLane.ScrubEnded += (_, _) => EndScrub();

            // Ctrl and the wheel over any of them steps by notes. Tunnelled, so the panel doesn't scroll with it.
            foreach (var scrubbed in (Control[])[SeekBar, HighwayOverlayView, HighwayRowView, LyricLane])
                scrubbed.AddHandler(PointerWheelChangedEvent, OnScrubWheel, RoutingStrategies.Tunnel);

            // The speed box takes what was typed on Enter or on leaving it, and a preset once the list closes; not as
            // it's typed, which would play at 1% then 15% on the way to 150%. Posted, so the box's text has caught up.
            PlaybackSpeedBox.DropDownClosed += (_, _) => Dispatcher.UIThread.Post(() => CommitPlaybackSpeed(leave: true), DispatcherPriority.Input);
            PlaybackSpeedBox.LostFocus += (_, _) => CommitPlaybackSpeed(leave: false);
            PlaybackSpeedBox.AddHandler(KeyDownEvent, OnPlaybackSpeedKeyDown, RoutingStrategies.Tunnel);
        }

        /// <summary>Sets the playback speed from the speed box, then shows it there as it was taken: tidied, kept in range, or as before.</summary>
        /// <param name="leave">
        /// Whether a speed was settled on, picked or entered, so focus goes back to the panel, as a click outside a text box
        /// does: Space plays again rather than typing into the box.
        /// </param>
        private void CommitPlaybackSpeed(bool leave)
        {
            if (Player is not { } player)
                return;

            player.TrySetPlaybackSpeed(PlaybackSpeedBox.Text ?? PlaybackSpeedBox.SelectedItem as string);
            PlaybackSpeedBox.SetCurrentValue(ComboBox.TextProperty, player.PlaybackSpeedText);
            if (leave && PlaybackSpeedBox.IsKeyboardFocusWithin)
                Focus();
        }

        /// <summary>Enter takes what's typed in the speed box and Escape puts back the speed, unless its list is open, which they close.</summary>
        private void OnPlaybackSpeedKeyDown(object? sender, KeyEventArgs e)
        {
            if (PlaybackSpeedBox.IsDropDownOpen || Player is not { } player)
                return;

            if (e.Key == Key.Enter)
            {
                CommitPlaybackSpeed(leave: true);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                PlaybackSpeedBox.SetCurrentValue(ComboBox.TextProperty, player.PlaybackSpeedText);
                e.Handled = true;
            }
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _keysFrom = TopLevel.GetTopLevel(this);
            _keysFrom?.AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
            _keysFrom?.AddHandler(KeyUpEvent, OnWindowKeyUp, RoutingStrategies.Tunnel);
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            _keysFrom?.RemoveHandler(KeyDownEvent, OnWindowKeyDown);
            _keysFrom?.RemoveHandler(KeyUpEvent, OnWindowKeyUp);
            _keysFrom = null;
            _isSpaceHeld = false;
            base.OnDetachedFromVisualTree(e);
        }

        /// <summary>
        /// Space anywhere in the window plays or pauses the song shown, once a press however long it's held. Tunnelled,
        /// so a focused button or list plays rather than taking it. Not while a text box has focus: it types a space there,
        /// as typing reaches a box as text rather than as the key, which a key binding would also have caught.
        /// </summary>
        private void OnWindowKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key != Key.Space || e.KeyModifiers != KeyModifiers.None || !IsEffectivelyVisible
                || _keysFrom?.FocusManager?.GetFocusedElement() is TextBox
                || Player is not { } player || !player.PlayPauseCommand.CanExecute(null))
                return;

            e.Handled = true;
            if (_isSpaceHeld)
                return;

            _isSpaceHeld = true;
            player.PlayPauseCommand.Execute(null);
        }

        /// <summary>Letting go of a Space that played or paused, so the focused control doesn't see half a press.</summary>
        private void OnWindowKeyUp(object? sender, KeyEventArgs e)
        {
            if (e.Key != Key.Space || !_isSpaceHeld)
                return;

            _isSpaceHeld = false;
            e.Handled = true;
        }

        /// <summary>
        /// A click on the empty space or a label of a flyout with an offset box in it, the highway options or the video
        /// offset, takes focus from the box, keeping what was typed, and leaves the flyout open. The flyout is a popup
        /// of its own, so the panel's commit on a click away doesn't see its clicks. Clicks the controls handle don't reach this.
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

        /// <summary>
        /// Ctrl and the wheel over the seek bar or a lane moves the audio a note a notch, of the length the settings choose,
        /// to the next on the chart's grid: up later, down earlier. A wheel that turns in parts of a notch, as a touchpad's
        /// does, steps once they add up to one. Not while a lane or the seek bar is being dragged, which seeks when it's let go.
        /// </summary>
        private void OnScrubWheel(object? sender, PointerWheelEventArgs e)
        {
            // A sideways scroll, as a touchpad gives, scrolls the panel and leaves a part-turned notch as it is.
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.Delta.Y == 0
                || DataContext is not SongDetailViewModel { Player: { IsScrubbing: false } player } vm)
                return;

            e.Handled = true;
            if (Math.Sign(e.Delta.Y) != Math.Sign(_wheelNotches))
                _wheelNotches = 0;
            _wheelNotches += e.Delta.Y;

            int steps = (int)Math.Truncate(_wheelNotches);
            if (steps == 0)
                return;

            _wheelNotches -= steps;
            double to = vm.Highway.StepNotes(player.CurrentPosition.TotalSeconds, steps, ScrubSteps.QuartersOf(player.ScrubStep));
            player.PositionSeconds = Math.Clamp(to, 0, player.TimelineSeconds);
        }
    }
}
