using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ChartShared.UI.Controls;

namespace ChartShared.UI.Views
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

        /// <summary>
        /// <paramref name="icon"/> as an image, for a <see cref="ChartShared.UI.Controls.TitleBar"/> in a window that only has its
        /// owner's icon (<see cref="UseOwnerIcon"/>). Null for no icon.
        /// </summary>
        /// <remarks>
        /// Decoded once per icon and shared, as an owner's icon is the same each time a window takes it; the image lives as
        /// long as the icon does.
        /// </remarks>
        public static IImage? ImageOf(WindowIcon? icon) => icon is null ? null : IconImages.GetValue(icon, Decode);

        /// <summary>
        /// Shows <paramref name="window"/>'s icon in its <paramref name="titleBar"/>, as it has it now and as it gets it later:
        /// a window with its owner's icon (<see cref="UseOwnerIcon"/>) gets it only once shown. Call after InitializeComponent.
        /// </summary>
        /// <remarks>The title bar is the window's, so the binding lasts as long as both and needn't be undone.</remarks>
        public static void ShowIcon(Window window, TitleBar titleBar) =>
            titleBar.Bind(TitleBar.IconProperty, window.GetObservable(Window.IconProperty, ImageOf));

        private static readonly ConditionalWeakTable<WindowIcon, IImage> IconImages = new();

        private static IImage Decode(WindowIcon icon)
        {
            using var stream = new MemoryStream();
            icon.Save(stream);
            stream.Position = 0;
            return new Bitmap(stream);
        }
    }
}
