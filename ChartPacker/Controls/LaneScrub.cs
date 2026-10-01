using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using ChartPacker.Core.Metadata;

namespace ChartPacker.Controls
{
    /// <summary>Where a lane is being scrubbed to: a time in the song, in seconds.</summary>
    public sealed class ScrubEventArgs(double seconds) : EventArgs
    {
        public double Seconds { get; } = seconds;
    }

    /// <summary>
    /// What <see cref="NoteHighwayView"/> and <see cref="LyricLaneView"/> share as they show a whole chart to be scrubbed
    /// through: the part already played shaded, a line where the audio is and another where the pointer is, with its time.
    /// </summary>
    internal static class LaneScrub
    {
        /// <summary>
        /// How far a pixel dragged with Ctrl held moves the audio, in seconds, unless the settings say otherwise: the same
        /// however long the song is, so a fine drag moves as far for the same distance on any song.
        /// </summary>
        public const double DefaultFineSecondsPerPixel = 0.005;

        /// <summary>Whether Ctrl is held, which makes a drag fine, and shows a lane as it plays rather than its whole chart.</summary>
        public static bool IsFine(KeyModifiers modifiers) => modifiers.HasFlag(KeyModifiers.Control);

        /// <summary>Whether Ctrl is held once <paramref name="e"/> has been taken, whatever the modifiers say of a Ctrl key itself going down or up.</summary>
        public static bool IsFine(KeyEventArgs e) => e.Key is Key.LeftCtrl or Key.RightCtrl
            ? e.RoutedEvent == InputElement.KeyDownEvent
            : IsFine(e.KeyModifiers);

        /// <summary>The pointer over a lane that can be scrubbed.</summary>
        public static readonly Cursor HandCursor = new(StandardCursorType.Hand);

        /// <summary>Over what's been played, so what's to come stands out.</summary>
        public static readonly IBrush PlayedBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x70, 0x00, 0x00, 0x00));

        /// <summary>Where the audio is.</summary>
        public static readonly IPen PlayheadPen = new ImmutablePen(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF).ToUInt32(), 2);

        /// <summary>Where the pointer is: where a click would move the audio to.</summary>
        public static readonly IPen HoverPen = new ImmutablePen(Color.FromArgb(0xB0, 0xFF, 0xD8, 0x2E).ToUInt32(), 1);

        private static readonly IBrush TagBrush = new ImmutableSolidColorBrush(Color.FromArgb(0xD0, 0x00, 0x00, 0x00));
        private static readonly IBrush TagTextBrush = new ImmutableSolidColorBrush(Color.FromRgb(0xFF, 0xE6, 0x80));
        private static readonly Typeface TagTypeface = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);

        /// <summary>
        /// Writes <paramref name="seconds"/> as a time on a dark tag beside the pointer's line, kept inside
        /// <paramref name="bounds"/>: above a line across a highway, or right of a line down the lyrics.
        /// </summary>
        public static void DrawTimeTag(DrawingContext context, double seconds, Rect bounds, double at, bool isAcross)
        {
            var text = new FormattedText(MetadataTimes.FormatDuration((long)(Math.Max(0, seconds) * 1000)), CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, TagTypeface, 10.5, TagTextBrush);
            var size = new Size(text.Width + 6, text.Height + 2);
            Point origin;
            if (isAcross)
            {
                // Above the line, or below it near the top.
                double y = at - size.Height - 2 >= bounds.Top ? at - size.Height - 2 : at + 2;
                origin = new Point(bounds.X + ((bounds.Width - size.Width) / 2), y);
            }
            else
            {
                // Right of the line, or left of it near the right edge.
                double x = at + 4 + size.Width <= bounds.Right ? at + 4 : at - 4 - size.Width;
                origin = new Point(x, bounds.Top + 4);
            }

            var tag = new Rect(origin, size);
            context.DrawRectangle(TagBrush, null, tag, 3, 3);
            context.DrawText(text, new Point(tag.X + 3, tag.Y + 1));
        }
    }

    /// <summary>
    /// Whether Ctrl makes a scrubbed lane fine, for <see cref="NoteHighwayView"/> and <see cref="LyricLaneView"/>: it's
    /// watched in the window's keys while <paramref name="watchesCtrl"/> says so (the pointer is over the lane, or it's
    /// being scrubbed), so it's seen with the pointer still, whatever has focus. A fine drag holds the pointer still.
    /// </summary>
    /// <param name="owner">The lane, drawn again whenever it turns fine or back.</param>
    internal sealed class LaneFineScrub(Control owner, Func<bool> watchesCtrl)
    {
        private readonly FineDrag _drag = new(owner);

        // The window whose keys say when Ctrl is pressed or let go with the pointer still.
        private TopLevel? _keysFrom;

        /// <summary>Ctrl is held over the lane, or while it's scrubbed.</summary>
        public bool IsFine { get; private set; }

        /// <summary>Starts watching the keys of the window the lane is now in.</summary>
        public void Attach()
        {
            // Tunnelled, and handled ones too, so Ctrl is seen whatever has focus.
            _keysFrom = TopLevel.GetTopLevel(owner);
            _keysFrom?.AddHandler(InputElement.KeyDownEvent, OnModifierKey, RoutingStrategies.Tunnel, handledEventsToo: true);
            _keysFrom?.AddHandler(InputElement.KeyUpEvent, OnModifierKey, RoutingStrategies.Tunnel, handledEventsToo: true);
        }

        public void Detach()
        {
            _keysFrom?.RemoveHandler(InputElement.KeyDownEvent, OnModifierKey);
            _keysFrom?.RemoveHandler(InputElement.KeyUpEvent, OnModifierKey);
            _keysFrom = null;
        }

        /// <summary>Takes whether Ctrl is held from <paramref name="modifiers"/>, as a pointer event has them.</summary>
        public void Set(KeyModifiers modifiers) => Set(LaneScrub.IsFine(modifiers));

        /// <summary>Turns fine or back, ending a fine drag when it's no longer fine.</summary>
        public void Set(bool isFine)
        {
            if (isFine == IsFine)
                return;

            IsFine = isFine;
            if (!isFine)
                _drag.Stop();
            owner.InvalidateVisual();
        }

        /// <summary>Starts a fine drag with the pointer at <paramref name="at"/>, in the lane; already started, it goes on as it is.</summary>
        public void Start(Point at) => _drag.Start(at);

        /// <summary>How far the pointer has moved to <paramref name="at"/> since it was last seen, starting a fine drag if there isn't one.</summary>
        public Vector Move(Point at)
        {
            _drag.Start(at);
            return _drag.Move(at);
        }

        /// <summary>Ends a fine drag, showing the pointer again; the lane stays fine while Ctrl is held.</summary>
        public void Stop() => _drag.Stop();

        /// <summary>Ctrl pressed or let go with the pointer over the lane, or while it's scrubbed.</summary>
        private void OnModifierKey(object? sender, KeyEventArgs e)
        {
            if (watchesCtrl())
                Set(LaneScrub.IsFine(e));
        }
    }
}
