using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;

namespace ChartPacker.UI.Views
{
    /// <summary>Window frame tweaks shared by both apps' windows.</summary>
    public static class WindowChrome
    {
        // DWMWA_USE_IMMERSIVE_DARK_MODE; Windows 10 20H1 and later. Older builds ignore it and keep a light title bar.
        private const int DwmUseImmersiveDarkMode = 20;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        /// <summary>The app is dark only, but Avalonia leaves the Windows title bar light on Windows 10. Call once the window is open.</summary>
        public static void UseDarkTitleBar(Window window)
        {
            if (!OperatingSystem.IsWindows() || window.TryGetPlatformHandle() is not { } handle)
                return;

            int enabled = 1;
            _ = DwmSetWindowAttribute(handle.Handle, DwmUseImmersiveDarkMode, ref enabled, sizeof(int));
        }

        /// <summary>Gives the window its owner's icon and keeps it in step when the owner's changes. Call before showing it.</summary>
        public static void UseOwnerIcon(Window window, Window owner)
        {
            var link = window.Bind(Window.IconProperty, owner.GetObservable(Window.IconProperty));
            // The owner outlives the window; drop the subscription so a closed window isn't kept alive by it.
            window.Closed += (_, _) => link.Dispose();
        }
    }
}
