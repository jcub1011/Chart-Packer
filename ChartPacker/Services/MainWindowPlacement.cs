using Avalonia.Controls;
using ChartShared.UI.Views;

namespace ChartPacker.Services
{
    /// <summary>
    /// Where the main window opens: where it was when it last closed, maximized if it was, as Chart Builder's Editor
    /// does. Its normal bounds are kept while it's maximized, so restoring it goes back to them.
    /// </summary>
    public static class MainWindowPlacement
    {
        /// <summary>
        /// Puts <paramref name="window"/>, not yet shown, where it was when it last closed; maximized when it closed
        /// that way. When that's unknown, or there are no screens, it's left where MainWindow.axaml puts it: centered.
        /// </summary>
        public static void Restore(Window window, AppSettings settings)
        {
            if (WindowBounds.FromSaved(settings.MainWindowX, settings.MainWindowY, settings.MainWindowWidth, settings.MainWindowHeight) is { } bounds)
                WindowPlacement.Restore(window, bounds);

            if (settings.MainWindowMaximized)
                window.WindowState = WindowState.Maximized;
        }

        /// <summary>
        /// Remembers where <paramref name="window"/>, closing, is: its bounds while it's normal, or that it's maximized.
        /// A minimized window doesn't say how it was shown or where it was, so it changes nothing.
        /// </summary>
        public static void Capture(Window window, AppSettings settings)
        {
            if (!WindowPlacement.TryCapture(window, out var state, out var bounds))
                return;

            if (bounds is { } normal)
            {
                settings.MainWindowX = normal.Position.X;
                settings.MainWindowY = normal.Position.Y;
                settings.MainWindowWidth = normal.Size.Width;
                settings.MainWindowHeight = normal.Size.Height;
            }

            settings.MainWindowMaximized = state != WindowState.Normal;
        }
    }
}
