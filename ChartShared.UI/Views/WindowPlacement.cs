using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;

namespace ChartShared.UI.Views
{
    /// <summary>Where a window goes on the screens, in screen pixels: placing one over where another was.</summary>
    public static class WindowPlacement
    {
        /// <summary>
        /// The middle of <paramref name="window"/>, frame included. Null while it's minimized, when its position isn't
        /// where it shows, or before it has a frame.
        /// </summary>
        public static PixelPoint? CenterOf(Window window)
        {
            if (window.WindowState == WindowState.Minimized || window.FrameSize is not { } frame)
                return null;

            var size = PixelSize.FromSize(frame, window.RenderScaling);
            return new PixelPoint(window.Position.X + (size.Width / 2), window.Position.Y + (size.Height / 2));
        }

        /// <summary>The screen <paramref name="point"/> is on, or null when it's on none of them.</summary>
        public static Screen? ScreenOf(Window window, PixelPoint point) => window.Screens.ScreenFromPoint(point);

        /// <summary>
        /// Where a window of <paramref name="size"/> goes to be centered on <paramref name="center"/>, moved as little as
        /// keeps it inside <paramref name="workingArea"/>. One too big for the area is put at its top left.
        /// </summary>
        public static PixelPoint Centered(PixelPoint center, PixelSize size, PixelRect workingArea) => new(
            Fit(center.X - (size.Width / 2), size.Width, workingArea.X, workingArea.Right),
            Fit(center.Y - (size.Height / 2), size.Height, workingArea.Y, workingArea.Bottom));

        /// <summary>
        /// Where a window of <paramref name="size"/> goes to be at <paramref name="position"/>, moved as little as
        /// keeps it inside <paramref name="workingArea"/>. One too big for the area is put at its top left.
        /// </summary>
        public static PixelPoint PlaceAt(PixelPoint position, PixelSize size, PixelRect workingArea) => new(
            Fit(position.X, size.Width, workingArea.X, workingArea.Right),
            Fit(position.Y, size.Height, workingArea.Y, workingArea.Bottom));

        /// <summary>
        /// <paramref name="size"/> made to fit in <paramref name="workingArea"/>: at most the area, at
        /// least <paramref name="min"/> where the area allows it.
        /// </summary>
        public static PixelSize FitSize(PixelSize size, PixelSize min, PixelRect workingArea) => new(
            Math.Max(Math.Min(size.Width, workingArea.Width), Math.Min(min.Width, workingArea.Width)),
            Math.Max(Math.Min(size.Height, workingArea.Height), Math.Min(min.Height, workingArea.Height)));

        /// <summary>
        /// Centers <paramref name="window"/>, not yet shown, on <paramref name="center"/>, within the working area of the
        /// screen that point is on, its size made to fit that area but no smaller than the window's own minimum where it
        /// allows.
        /// </summary>
        /// <returns>False, leaving the window where it would have gone, when the point is on none of the screens.</returns>
        public static bool CenterOn(Window window, PixelPoint center)
        {
            if (ScreenOf(window, center) is not { } screen)
                return false;

            // The window isn't on a screen yet, so its size is scaled as the one it's going to.
            var area = screen.WorkingArea;
            var size = PixelSize.FromSize(new Size(window.Width, window.Height), screen.Scaling);
            var minPixels = PixelSize.FromSize(new Size(window.MinWidth, window.MinHeight), screen.Scaling);
            size = FitSize(size, minPixels, area);

            window.Width = size.Width / screen.Scaling;
            window.Height = size.Height / screen.Scaling;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Position = Centered(center, size, area);
            return true;
        }

        /// <summary>
        /// Puts <paramref name="window"/>, not yet shown, at the normal <paramref name="bounds"/> it was saved with: on
        /// the screen their top left is on (or their middle is, or the first), their size made to fit that screen's
        /// working area but no smaller than the window's own minimum where it allows, moved as little as keeps it there.
        /// </summary>
        /// <returns>False, leaving the window where it would have gone, when there are no screens.</returns>
        public static bool Restore(Window window, WindowBounds bounds)
        {
            var screens = window.Screens.All;
            if (screens.Count == 0)
                return false;

            // The window isn't on a screen yet, so its middle is found with the size scaled as each screen would show it.
            var target = window.Screens.ScreenFromPoint(bounds.Position)
                ?? screens.FirstOrDefault(s => s.Bounds.Contains(bounds.MiddleOn(s.Scaling)))
                ?? screens[0];
            var area = target.WorkingArea;

            var pixelSize = PixelSize.FromSize(bounds.Size, target.Scaling);
            var minPixels = PixelSize.FromSize(new Size(window.MinWidth, window.MinHeight), target.Scaling);
            pixelSize = FitSize(pixelSize, minPixels, area);

            window.Width = pixelSize.Width / target.Scaling;
            window.Height = pixelSize.Height / target.Scaling;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Position = PlaceAt(bounds.Position, pixelSize, area);
            return true;
        }

        /// <summary>
        /// How <paramref name="window"/>, closing, is shown: its <paramref name="state"/>, and its
        /// <paramref name="normalBounds"/> while that's <see cref="WindowState.Normal"/> (null otherwise).
        /// </summary>
        /// <returns>False while it's minimized, which doesn't say how it was shown or where it was.</returns>
        public static bool TryCapture(Window window, out WindowState state, out WindowBounds? normalBounds)
        {
            state = window.WindowState;
            normalBounds = state == WindowState.Normal
                ? new WindowBounds(window.Position, new Size(window.Width, window.Height))
                : null;
            return state != WindowState.Minimized;
        }

        private static int Fit(int start, int length, int min, int max) => Math.Max(min, Math.Min(start, max - length));
    }
}
