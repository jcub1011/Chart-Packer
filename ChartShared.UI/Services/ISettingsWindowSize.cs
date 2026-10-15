using Avalonia;
using ChartShared.UI.Views;

namespace ChartShared.UI.Services
{
    /// <summary>
    /// The Settings window's normal size when it last closed, kept in a program's settings, in device-independent pixels.
    /// Null for none yet.
    /// </summary>
    public interface ISettingsWindowSize
    {
        double? SettingsWindowWidth { get; set; }

        double? SettingsWindowHeight { get; set; }
    }

    /// <summary>Reads and tidies an <see cref="ISettingsWindowSize"/>, as <see cref="WindowBounds.SizeFromSaved"/> reads it.</summary>
    public static class SettingsWindowSize
    {
        /// <summary>The saved size, clamped; null when it's missing, half-saved or not a positive number.</summary>
        public static Size? Saved(ISettingsWindowSize settings) =>
            WindowBounds.SizeFromSaved(settings.SettingsWindowWidth, settings.SettingsWindowHeight);

        /// <summary>Writes back the saved size as <see cref="Saved"/> reads it: clamped, or dropped whole.</summary>
        public static void Sanitize(ISettingsWindowSize settings)
        {
            var size = Saved(settings);
            settings.SettingsWindowWidth = size?.Width;
            settings.SettingsWindowHeight = size?.Height;
        }
    }
}
