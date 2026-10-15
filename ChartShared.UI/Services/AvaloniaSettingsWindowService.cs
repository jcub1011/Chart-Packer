using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using ChartShared.UI.ViewModels.Settings;
using ChartShared.UI.Views;

namespace ChartShared.UI.Services
{
    /// <param name="createViewModel">
    /// Makes the window's view model from a folder picker owned by the Settings window, so a picker opened
    /// from it is modal to it rather than to the main window.
    /// </param>
    public sealed class AvaloniaSettingsWindowService(Func<Window?> owner, Func<IFolderPickerService, SettingsWindowViewModel> createViewModel)
        : ISettingsWindowService
    {
        public async Task ShowAsync()
        {
            if (owner() is not { } window)
                return;

            var dialog = new SettingsWindow();
            dialog.DataContext = createViewModel(new AvaloniaFolderPickerService(() => dialog));
            WindowChrome.UseOwnerIcon(dialog, window);
            await dialog.ShowDialog(window);
        }
    }
}
