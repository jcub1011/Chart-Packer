using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;

namespace ChartPacker.UI.Views
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
        /// Centers <paramref name="window"/>, not yet shown, on <paramref name="center"/>, within the working area of the
        /// screen that point is on.
        /// </summary>
        /// <returns>False, leaving the window where it would have gone, when the point is on none of the screens.</returns>
        public static bool CenterOn(Window window, PixelPoint center)
        {
            if (ScreenOf(window, center) is not { } screen)
                return false;

            // The window isn't on a screen yet, so its size is scaled as the one it's going to.
            var size = PixelSize.FromSize(new Size(window.Width, window.Height), screen.Scaling);
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Position = Centered(center, size, screen.WorkingArea);
            return true;
        }

        private static int Fit(int start, int length, int min, int max) => Math.Max(min, Math.Min(start, max - length));
    }
}
