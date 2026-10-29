using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using ChartShared.UI.Keybinds;
using ChartShared.UI.Resources;
using ChartShared.UI.ViewModels.Keybinds;
using ChartShared.UI.Views;

namespace ChartShared.UI.Services
{
    /// <summary>
    /// Shows the keyboard shortcuts window over the owner, modal to it and at the size it last closed at
    /// (<see cref="WindowMemory"/>). It edits the running app's keys (<see cref="AppKeys"/>) and, on OK, writes them to
    /// <c>keybinds.json</c>.
    /// </summary>
    /// <param name="owner">The window it opens over.</param>
    /// <param name="appName">The program's name, which screen readers give the title bar's icon.</param>
    public sealed class AvaloniaKeybindsWindowService(Func<Window?> owner, string appName) : IKeybindsWindowService
    {
        private static readonly FilePickerFileType LayoutFiles = new(SharedStrings.KeybindsFileTypeName)
        {
            Patterns = ["*.json"],
            MimeTypes = ["application/json"],
        };

        public async Task ShowAsync()
        {
            if (owner() is not { } window || AppKeys.Service is not { } keys)
                return;

            var dialog = new KeybindsWindow { AppName = appName };
            dialog.DataContext = new KeybindsWindowViewModel(
                keys.Map,
                () => PickToImportAsync(dialog),
                () => PickToExportAsync(dialog),
                new AvaloniaConfirmationService(() => dialog),
                keys.Save);
            WindowChrome.UseOwnerIcon(dialog, window);

            // Over the owner's frame, fitted to its screen, as the Settings window is.
            if (WindowPlacement.CenterOf(window) is { } center)
                WindowPlacement.CenterOn(dialog, center);

            await dialog.ShowDialog(window);
        }

        private static async Task<string?> PickToImportAsync(Window dialog)
        {
            var files = await dialog.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = SharedStrings.KeybindsImportTitle,
                AllowMultiple = false,
                FileTypeFilter = [LayoutFiles],
            });
            return files.FirstOrDefault()?.TryGetLocalPath();
        }

        private static async Task<string?> PickToExportAsync(Window dialog)
        {
            var file = await dialog.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = SharedStrings.KeybindsExportTitle,
                SuggestedFileName = "keybinds",
                DefaultExtension = "json",
                FileTypeChoices = [LayoutFiles],
                ShowOverwritePrompt = true,
            });
            return file?.TryGetLocalPath();
        }
    }
}
