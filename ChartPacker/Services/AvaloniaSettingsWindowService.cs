using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using ChartPacker.ViewModels.Settings;
using ChartPacker.Views;

namespace ChartPacker.Services
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
            await dialog.ShowDialog(window);
        }
    }
}
