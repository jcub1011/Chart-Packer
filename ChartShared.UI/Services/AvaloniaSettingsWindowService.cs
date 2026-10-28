using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using ChartShared.UI.ViewModels.Settings;
using ChartShared.UI.Views;

namespace ChartShared.UI.Services
{
    /// <summary>
    /// Shows the Settings window, modal to and centered over its owner, at the size it last closed at (<see cref="WindowMemory"/>).
    /// </summary>
    /// <param name="createViewModel">
    /// Makes the window's view model from a folder picker owned by the Settings window, so a picker opened
    /// from it is modal to it rather than to the main window.
    /// </param>
    /// <param name="appName">The program's name, which screen readers give the title bar's icon.</param>
    public sealed class AvaloniaSettingsWindowService(
        Func<Window?> owner,
        Func<IFolderPickerService, SettingsWindowViewModel> createViewModel,
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

            // Over the owner's frame, fitted to its screen. The window's own CenterOwner is left for when the owner's
            // middle isn't known.
            if (WindowPlacement.CenterOf(window) is { } center)
                WindowPlacement.CenterOn(dialog, center);

            await dialog.ShowDialog(window);
        }
    }
}
