using System;
using Avalonia;

namespace ChartShared.UI.Views
{
    /// <summary>
    /// A window's normal bounds as saved in settings: its top left in screen pixels, its size in device-independent
    /// pixels.
    /// </summary>
    public readonly record struct WindowBounds(PixelPoint Position, Size Size)
    {
        /// <summary>The widest and tallest a window's normal size is restored as: bigger than an 8K screen.</summary>
        public static readonly Size MaxSize = new(7680, 4320);

        /// <summary>
        /// The bounds saved as <paramref name="x"/>, <paramref name="y"/>, <paramref name="width"/> and
        /// <paramref name="height"/>, the size clamped to <see cref="MaxSize"/>. Null when any part is missing or the
        /// size isn't a positive number, so a half-saved bound is dropped whole.
        /// </summary>
        public static WindowBounds? FromSaved(int? x, int? y, double? width, double? height)
        {
            if (x is not { } left || y is not { } top || width is not { } w || height is not { } h
                || !double.IsFinite(w) || !double.IsFinite(h) || w <= 0 || h <= 0)
                return null;

            return new WindowBounds(
                new PixelPoint(left, top),
                new Size(Math.Min(w, MaxSize.Width), Math.Min(h, MaxSize.Height)));
        }

        /// <summary>The bounds' middle, in screen pixels, with the size scaled as a screen of <paramref name="scaling"/> shows it.</summary>
        public PixelPoint MiddleOn(double scaling)
        {
            var size = PixelSize.FromSize(Size, scaling);
            return new PixelPoint(Position.X + (size.Width / 2), Position.Y + (size.Height / 2));
        }
    }
}
