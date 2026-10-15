using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using ChartShared.UI.ViewModels.Settings;
using ChartShared.UI.Views;

namespace ChartShared.UI.Services
{
    /// <summary>
    /// Shows the Settings window, modal to and centered over its owner, at the size it last closed at.
    /// </summary>
    /// <param name="createViewModel">
    /// Makes the window's view model from a folder picker owned by the Settings window, so a picker opened
    /// from it is modal to it rather than to the main window.
    /// </param>
    /// <param name="sizeSettings">Where the program's settings keep the window's normal size when it last closed.</param>
    /// <param name="save">Saves the program's settings, after the window's size has been kept in them.</param>
    /// <param name="appName">The program's name, which screen readers give the title bar's icon.</param>
    public sealed class AvaloniaSettingsWindowService(
        Func<Window?> owner,
        Func<IFolderPickerService, SettingsWindowViewModel> createViewModel,
        ISettingsWindowSize sizeSettings,
        Action save,
        string appName)
        : ISettingsWindowService
    {
        public async Task ShowAsync()
        {
            if (owner() is not { } window)
                return;

            var dialog = new SettingsWindow { AppName = appName };
            dialog.DataContext = createViewModel(new AvaloniaFolderPickerService(() => dialog));
            WindowChrome.UseOwnerIcon(dialog, window);

            if (SettingsWindowSize.Saved(sizeSettings) is { } size)
            {
                dialog.Width = size.Width;
                dialog.Height = size.Height;
            }

            // Over the owner's frame, fitted to its screen. The window's own CenterOwner is left for when the owner's
            // middle isn't known.
            if (WindowPlacement.CenterOf(window) is { } center)
                WindowPlacement.CenterOn(dialog, center);

            var opened = new Size(dialog.Width, dialog.Height);

            // A maximized or minimized close keeps the size it last closed at normally.
            dialog.Closing += (_, e) =>
            {
                if (e.Cancel)
                    return;

                // Only a size the user chose: unchanged, or fitted to a small screen by CenterOn, keeps the one saved.
                if (WindowPlacement.TryCapture(dialog, out var _, out var bounds) && bounds is { } normal && normal.Size != opened)
                {
                    sizeSettings.SettingsWindowWidth = normal.Size.Width;
                    sizeSettings.SettingsWindowHeight = normal.Size.Height;
                    save();
                }
            };

            await dialog.ShowDialog(window);
        }
    }
}
