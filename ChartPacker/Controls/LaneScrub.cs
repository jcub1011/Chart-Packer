using System;
using System.Globalization;
using Avalonia;
using Avalonia.Input;
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
}
