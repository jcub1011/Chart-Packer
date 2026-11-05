using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace ChartShared.UI.Controls.Highway
{
    /// <summary>
    /// What a highway or timeline is, to the gestures that move through the song and zoom it: where it's now, how far a
    /// pixel is, and how to move.
    /// </summary>
    public interface IViewportGestureHost
    {
        /// <summary>Where the song is now, in seconds: what a drag moves on from.</summary>
        double Now { get; }

        /// <summary>How far a pixel along the time axis is, in seconds: how far a drag moves the notes as far as the pointer goes.</summary>
        double SecondsPerPixel { get; }

        /// <summary>Whether time runs up and down the view (a highway, where down is later) or across it (a timeline, where right is earlier).</summary>
        bool IsTimeAxisVertical { get; }

        /// <summary>Whether the wheel turned up is later; otherwise it's earlier.</summary>
        bool WheelUpIsLater { get; }

        /// <summary>How many times less far a drag with Ctrl held moves the notes than a plain drag, at least one.</summary>
        double FineFactor { get; }

        /// <summary>A scrub begins: the audio is held still, say, while it goes on.</summary>
        void ScrubStarted();

        /// <summary>Moves the song to <paramref name="seconds"/>, kept to what the host has of it.</summary>
        void ScrubTo(double seconds);

        /// <summary>The scrub is over.</summary>
        void ScrubEnded();

        /// <summary>The wheel moves the song by <paramref name="steps"/>, later when positive: a grid step a notch, in the editor.</summary>
        void Scroll(int steps);

        /// <summary>The wheel with Ctrl held zooms by <paramref name="steps"/>, in when positive: the view shows less of the song.</summary>
        void Zoom(int steps);

        /// <summary>The time at <paramref name="at"/> on the whole chart, which is shown while Shift is held; null when the host has no whole chart to show.</summary>
        double? OverviewTimeAt(Point at);
    }

    /// <summary>
    /// What starts a scrub: <paramref name="Button"/> pressed with every one of <paramref name="Modifiers"/> held. A host picks
    /// its own, as what's free differs: a highway only scrubs, so its left button does, while the editor's is the tools'.
    /// </summary>
    /// <param name="AllowsOverview">Whether Shift (not held as one of <paramref name="Modifiers"/>) shows the whole chart to scrub through.</param>
    public readonly record struct ScrubBinding(MouseButton Button, KeyModifiers Modifiers = KeyModifiers.None, bool AllowsOverview = false);

    /// <summary>
    /// The gestures that move through a song and zoom into it, the same on Chart Packer's highway and in Chart Builder's
    /// lanes: a drag grabs the notes and moves them as far as the pointer goes (Ctrl: finer, Shift: the whole chart, where a
    /// host shows one); the wheel scrolls, Shift four times as fast; Ctrl and the wheel zoom. What a scroll or a scrub moves
    /// is the host's own: the editor's cursor, a note highway's clock.
    /// </summary>
    public sealed class ViewportGestures
    {
        private readonly Control _owner;
        private readonly IViewportGestureHost _host;
        private readonly ScrubBinding _binding;

        // How much of a notch the wheel has turned, carried between events, for scrolling and for zooming.
        private double _scrollTurn;
        private double _zoomTurn;

        public ViewportGestures(Control owner, IViewportGestureHost host, ScrubBinding binding, Func<bool> watchesKeys)
        {
            _owner = owner;
            _host = host;
            _binding = binding;
            Mode = new LaneScrubMode(owner, watchesKeys, binding.AllowsOverview);
        }

        /// <summary>Whether Ctrl is held, or Shift for the whole chart, over the lane or while it's scrubbed.</summary>
        public LaneScrubMode Mode { get; }

        /// <summary>Whether a scrub is going on.</summary>
        public bool IsScrubbing { get; private set; }

        /// <summary>Whether <paramref name="e"/> is the press that starts a scrub.</summary>
        public bool IsScrubPress(PointerPressedEventArgs e)
        {
            var button = e.GetCurrentPoint(_owner).Properties.PointerUpdateKind switch
            {
                PointerUpdateKind.LeftButtonPressed => MouseButton.Left,
                PointerUpdateKind.MiddleButtonPressed => MouseButton.Middle,
                PointerUpdateKind.RightButtonPressed => MouseButton.Right,
                _ => MouseButton.None,
            };
            return button == _binding.Button && (e.KeyModifiers & _binding.Modifiers) == _binding.Modifiers;
        }

        /// <summary>Starts a scrub with the pointer at <paramref name="at"/>, in the owner, as <paramref name="e"/> pressed it; the caller has checked <see cref="IsScrubPress"/>.</summary>
        public void Press(PointerPressedEventArgs e, Point at)
        {
            Mode.Set(e.KeyModifiers);
            IsScrubbing = true;
            e.Pointer.Capture(_owner);
            _host.ScrubStarted();

            // A press on the whole chart moves the song there, and follows the pointer without a drag, which is only started if
            // Shift is let go, by the first move after it (see ScrubTimeAt); otherwise it starts a drag from where the song is.
            if (Mode.IsOverview && _host.OverviewTimeAt(at) is { } time)
                _host.ScrubTo(time);
            else
                Mode.Start(at);
            e.Handled = true;
        }

        /// <summary>Takes the pointer's move while a scrub goes on.</summary>
        /// <returns>Whether it did.</returns>
        public bool Move(PointerEventArgs e)
        {
            if (!IsScrubbing)
                return false;

            Mode.Set(e.KeyModifiers);
            _host.ScrubTo(ScrubTimeAt(e.GetPosition(_owner)));
            return true;
        }

        /// <summary>Ends the scrub as its button is let go.</summary>
        /// <returns>Whether it did: one was going on, and it was this button's.</returns>
        public bool Release(PointerReleasedEventArgs e)
        {
            if (!IsScrubbing || e.InitialPressMouseButton != _binding.Button)
                return false;

            Mode.Set(e.KeyModifiers);
            _host.ScrubTo(ScrubTimeAt(e.GetPosition(_owner)));
            e.Pointer.Capture(null);
            End();
            e.Handled = true;
            return true;
        }

        /// <summary>Ends the scrub as the pointer is taken from the owner.</summary>
        public void CaptureLost() => End();

        private void End()
        {
            if (!IsScrubbing)
                return;

            IsScrubbing = false;
            Mode.Stop();
            _host.ScrubEnded();
        }

        /// <summary>
        /// Where a drag to <paramref name="at"/> moves the song: with the whole chart shown, the time there; otherwise on from
        /// where it is by how far the pointer has gone along the time axis since it last moved, as far as the notes move for it,
        /// or while it's fine, the host's <see cref="IViewportGestureHost.FineFactor"/> times less far; either way holding the
        /// pointer still.
        /// </summary>
        private double ScrubTimeAt(Point at)
        {
            if (Mode.IsOverview && _host.OverviewTimeAt(at) is { } time)
            {
                Mode.Stop();
                return time;
            }

            double perPixel = _host.SecondsPerPixel;
            if (Mode.IsFine)
                perPixel /= Math.Max(1, _host.FineFactor);

            // Down a highway the notes grabbed come with the pointer toward the strike line, which is later; across a
            // timeline they come toward it from the right, so a drag right is earlier. A drag not started yet (the scrub began on
            // the whole chart, and Shift is let go) starts here, at the pointer, and this first move is none: the song stays where
            // the whole chart put it, then the drag moves on from there.
            var moved = Mode.Move(at);
            double along = _host.IsTimeAxisVertical ? moved.Y : -moved.X;
            return _host.Now + (along * perPixel);
        }

        /// <summary>
        /// The wheel: scrolls the song a notch at a time, four with Shift held, or with Ctrl held zooms. At most one notch per
        /// wheel event, so a mouse reporting several units per physical notch still moves a single step.
        /// </summary>
        /// <returns>Whether it took the wheel: it turned some way, so the event is handled; a turn of none is left to whoever's above.</returns>
        public bool Wheel(PointerWheelEventArgs e)
        {
            bool took = Wheel(e.Delta, e.KeyModifiers);
            if (took)
                e.Handled = true;
            return took;
        }

        /// <summary>The wheel turned by <paramref name="turn"/>, with <paramref name="modifiers"/> held; <see cref="Wheel(PointerWheelEventArgs)"/> without the event.</summary>
        internal bool Wheel(Vector turn, KeyModifiers modifiers)
        {
            double delta = turn.Y;
            if ((modifiers & KeyModifiers.Control) != 0)
            {
                if (delta == 0)
                    return false;

                int zoom = WheelSteps.TakeZoomStep(ref _zoomTurn, delta);
                if (zoom != 0)
                    _host.Zoom(zoom);
                return true;
            }

            // Some platforms report Shift and the wheel as a sideways scroll, so that counts too.
            bool fast = (modifiers & KeyModifiers.Shift) != 0;
            double raw = delta != 0 ? delta : (fast ? turn.X : 0);
            if (raw == 0)
                return false;

            int steps = WheelSteps.TakeScrollStep(ref _scrollTurn, WheelSteps.TowardLater(raw, _host.WheelUpIsLater), fast);
            if (steps != 0)
                _host.Scroll(steps);
            return true;
        }
    }
}
