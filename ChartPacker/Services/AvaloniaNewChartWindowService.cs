using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using ChartPacker.ViewModels;
using ChartPacker.Views;

namespace ChartPacker.Services
{
    /// <param name="settings">The wizard's defaults; the folder it used is saved back.</param>
    public sealed class AvaloniaNewChartWindowService(Func<Window?> owner, SettingsService settings) : INewChartWindowService
    {
        public async Task<string?> ShowAsync(IReadOnlyList<string> audioFiles, string? libraryFolder)
        {
            if (owner() is not { } window)
                return null;

            var dialog = new NewChartWizard();

            // The pickers belong to the wizard, so they open over it rather than the main window.
            var viewModel = new NewChartWizardViewModel(
                settings,
                libraryFolder,
                audioFiles,
                new AvaloniaFilePickerService(() => dialog),
                new AvaloniaImagePickerService(() => dialog),
                new AvaloniaFolderPickerService(() => dialog));
            dialog.DataContext = viewModel;
            WindowChrome.UseOwnerIcon(dialog, window);
            await dialog.ShowDialog(window);

            // A Create still cleaning up after a cancel finishes first, so nothing is left behind once this returns.
            await viewModel.ShutdownAsync();
            settings.Save();
            return viewModel.CreatedPath;
        }
    }
}
