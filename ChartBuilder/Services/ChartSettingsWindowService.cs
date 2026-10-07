using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using ChartBuilder.ViewModels;
using ChartBuilder.Views;
using ChartPacker.Core.Charting;
using ChartPacker.Core.Charting.Commands;
using ChartPacker.Services;
using ChartPacker.Views;

namespace ChartBuilder.Services
{
    /// <summary>Opens Chart settings (charter-spec C1).</summary>
    public interface IChartSettingsWindowService
    {
        /// <summary>Opens Chart settings for <paramref name="chart"/>, the chart open, finishing when it closes.</summary>
        /// <returns>The command OK made, to execute, or null when nothing changed or it was cancelled.</returns>
        Task<IChartCommand?> EditAsync(EditableChart chart);
    }

    /// <summary>Opens <see cref="ChartSettingsWindow"/> over <paramref name="owner"/>, with prompts of its own.</summary>
    public sealed class ChartSettingsWindowService(Func<Window?> owner, BuilderSettingsService settings) : IChartSettingsWindowService
    {
        public async Task<IChartCommand?> EditAsync(EditableChart chart)
        {
            if (owner() is not { } window)
                return null;

            var dialog = new ChartSettingsWindow();
            var viewModel = new ChartSettingsViewModel(settings, new AvaloniaConfirmationService(() => dialog), chart);
            dialog.DataContext = viewModel;
            WindowChrome.UseOwnerIcon(dialog, window);
            await dialog.ShowDialog(window);
            return viewModel.Command;
        }
    }
}
