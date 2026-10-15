using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using ChartShared.Core.Metadata;

namespace ChartShared.UI.Controls
{
    /// <summary>Where a lane is being scrubbed to: a time in the song, in seconds.</summary>
    public sealed class ScrubEventArgs(double seconds) : EventArgs
    {
        public double Seconds { get; } = seconds;
    }

    /// <summary>
    /// What <see cref="Highway.NoteHighwayView"/> and Chart Packer's lyric lane share as they show a whole chart to be scrubbed
    /// through: the part already played shaded, a line where the audio is and another where the pointer is, with its time.
    /// </summary>
    public static class LaneScrub
    {
        /// <summary>
        /// How far a pixel dragged with Ctrl held moves the audio on the seek bar, in seconds, unless the settings say otherwise:
        /// the same however long the song is, so a fine drag moves as far for the same distance on any song.
        /// </summary>
        public const double DefaultFineSecondsPerPixel = 0.005;

        /// <summary>
        /// How many times less far a drag with Ctrl held moves a lane's notes than a plain drag, unless the settings say
        /// otherwise: taken from how far a plain drag moves them, so it's as much finer however tall or wide the lane is.
        /// </summary>
        public const double DefaultFineFactor = 5;

        /// <summary>Whether Ctrl is held, which makes a drag fine.</summary>
        public static bool IsFine(KeyModifiers modifiers) => modifiers.HasFlag(KeyModifiers.Control);

        /// <summary>Whether Shift is held without Ctrl, which shows a lane's whole chart to be scrubbed through.</summary>
        public static bool IsOverview(KeyModifiers modifiers) => modifiers.HasFlag(KeyModifiers.Shift) && !IsFine(modifiers);

        /// <summary>
        /// The modifiers held once <paramref name="e"/> has been taken, whatever the modifiers say of a Ctrl or Shift key itself
        /// going down or up.
        /// </summary>
        public static KeyModifiers ModifiersAfter(KeyEventArgs e)
        {
            var key = e.Key switch
            {
                Key.LeftCtrl or Key.RightCtrl => KeyModifiers.Control,
                Key.LeftShift or Key.RightShift => KeyModifiers.Shift,
                _ => KeyModifiers.None,
            };

            return e.RoutedEvent == InputElement.KeyDownEvent ? e.KeyModifiers | key : e.KeyModifiers & ~key;
        }

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
    /// How a lane is scrubbed, for <see cref="Highway.NoteHighwayView"/> and Chart Packer's lyric lane: a drag grabs the notes and
    /// moves them as far as the pointer goes; with Ctrl held it's fine; either way the pointer is held still, so a drag can go
    /// on as far as it needs. With Shift held (and not Ctrl) the lane shows its whole chart, to scrub through. The keys are
    /// watched in the window's while <paramref name="watchesKeys"/> says so (the pointer is over the lane, or it's being
    /// scrubbed), so they're seen with the pointer still, whatever has focus.
    /// </summary>
    /// <param name="owner">The lane, drawn again whenever its mode changes.</param>
    public sealed class LaneScrubMode(Control owner, Func<bool> watchesKeys)
    {
        private readonly FineDrag _drag = new(owner);

        // The window whose keys say when Ctrl or Shift is pressed or let go with the pointer still.
        private TopLevel? _keysFrom;

        /// <summary>Ctrl is held over the lane, or while it's scrubbed: a drag is fine.</summary>
        public bool IsFine { get; private set; }

        /// <summary>Shift is held without Ctrl over the lane, or while it's scrubbed: it shows its whole chart.</summary>
        public bool IsOverview { get; private set; }

        /// <summary>Starts watching the keys of the window the lane is now in.</summary>
        public void Attach()
        {
            // Tunnelled, and handled ones too, so Ctrl and Shift are seen whatever has focus.
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

        /// <summary>
        /// Takes whether Ctrl and Shift are held from <paramref name="modifiers"/>. Turning to the whole chart ends a drag, as a
        /// drag across it follows the pointer; between fine and not, a drag goes on as it is, only moving the audio further or less far.
        /// </summary>
        public void Set(KeyModifiers modifiers)
        {
            bool isFine = LaneScrub.IsFine(modifiers), isOverview = LaneScrub.IsOverview(modifiers);
            if (isFine == IsFine && isOverview == IsOverview)
                return;

            IsFine = isFine;
            IsOverview = isOverview;
            if (isOverview)
                _drag.Stop();
            owner.InvalidateVisual();
        }

        /// <summary>Starts a drag with the pointer at <paramref name="at"/>, in the lane; already started, it goes on as it is.</summary>
        public void Start(Point at) => _drag.Start(at);

        /// <summary>How far the pointer has moved to <paramref name="at"/> since it was last seen, starting a drag if there isn't one.</summary>
        public Vector Move(Point at)
        {
            _drag.Start(at);
            return _drag.Move(at);
        }

        /// <summary>Ends a drag, showing the pointer again; the mode stays while the keys are held.</summary>
        public void Stop() => _drag.Stop();

        /// <summary>Ctrl or Shift pressed or let go with the pointer over the lane, or while it's scrubbed.</summary>
        private void OnModifierKey(object? sender, KeyEventArgs e)
        {
            if (watchesKeys())
                Set(LaneScrub.ModifiersAfter(e));
        }
    }

    /// <summary>
    /// A lane's whole chart fading in as Shift is pressed over it, and out as it's let go: stepped toward whether it's shown
    /// each frame the lane is drawn, eased at both ends.
    /// </summary>
    public sealed class OverviewFade
    {
        // How long a fade takes from one end to the other.
        private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(160);

        // How far it's faded in, from 0 to 1, and the frame it was last stepped at; null while it's still.
        private double _progress;
        private TimeSpan? _steppedAt;

        /// <summary>How opaque the whole chart is drawn, from 0 to 1; the lane as it plays is drawn under it at the rest.</summary>
        public double Opacity => _progress * _progress * (3 - (2 * _progress));

        /// <summary>
        /// Where the pointer was last over the lane, down or across it: where the whole chart's pointer line is drawn, so it
        /// fades out with the rest once the pointer has gone. NaN until it's been over it.
        /// </summary>
        public double PointerAt { get; set; } = double.NaN;

        /// <summary>Moves toward <paramref name="isShown"/> for the frame drawn at <paramref name="frame"/>.</summary>
        /// <returns>Whether it moved, so the lane is to be drawn again.</returns>
        public bool Step(bool isShown, TimeSpan frame)
        {
            double target = isShown ? 1 : 0;
            if (_progress == target)
            {
                _steppedAt = null;
                return false;
            }

            // A fade's first frame starts it, rather than taking the time since a frame long before as already gone.
            double moved = _steppedAt is { } last ? Math.Max(0, (frame - last) / Duration) : 0;
            _steppedAt = frame;
            _progress = Math.Clamp(_progress + (isShown ? moved : -moved), 0, 1);
            return moved > 0;
        }

        /// <summary>Goes straight to <paramref name="isShown"/>, as while the lane isn't being drawn.</summary>
        public void Finish(bool isShown)
        {
            _progress = isShown ? 1 : 0;
            _steppedAt = null;
        }
    }
}
