using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using ChartBuilder.ViewModels;
using ChartBuilder.Views;
using ChartPacker.Services;
using ChartPacker.Views;

namespace ChartBuilder.Services
{
    /// <param name="settings">The wizard's defaults; the folder it used is saved back.</param>
    public sealed class AvaloniaNewChartWindowService(Func<Window?> owner, BuilderSettingsService settings) : INewChartWindowService
    {
        public async Task<string?> ShowAsync(IReadOnlyList<string> audioFiles)
        {
            if (owner() is not { } window)
                return null;

            var dialog = new NewChartWizard();

            // The pickers belong to the wizard, so they open over it rather than the main window. Chart Builder has
            // no library folder to offer, so the wizard starts on the last folder used.
            var viewModel = new NewChartWizardViewModel(
                settings,
                libraryFolder: null,
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
