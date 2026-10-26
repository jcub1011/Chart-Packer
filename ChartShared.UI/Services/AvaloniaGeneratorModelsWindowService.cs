using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using ChartShared.Core.Generation;
using ChartShared.UI.ViewModels;
using ChartShared.UI.Views;

namespace ChartShared.UI.Services;

/// <summary>
/// Opens the generator model storage dialog (issue #154) both apps share, as each app's own
/// window. Only one is open at a time; opening it again brings the one open to the front.
/// </summary>
/// <param name="owner">The app's window, which an ownerless open falls back to.</param>
/// <param name="store">The shared model storage.</param>
public sealed class AvaloniaGeneratorModelsWindowService(Func<Window?> owner, IGeneratorModelStore store)
    : IGeneratorModelsWindowService
{
    private GeneratorModelsWindow? _open;

    public async Task ShowAsync(Window? explicitOwner = null)
    {
        if (_open is { } open)
        {
            open.Activate();
            return;
        }

        var window = new GeneratorModelsWindow();
        window.DataContext = new GeneratorModelsViewModel(
            store,
            new AvaloniaFilePickerService(() => window),
            new AvaloniaFolderPickerService(() => window),
            new AvaloniaConfirmationService(() => window),
            new FileRevealService(() => window));
        _open = window;
        try
        {
            if ((explicitOwner ?? owner()) is not { } parent)
            {
                var closed = new TaskCompletionSource();
                window.Closed += (_, _) => closed.TrySetResult();
                window.Show();
                await closed.Task;
            }
            else
            {
                WindowChrome.UseOwnerIcon(window, parent);
                await window.ShowDialog(parent);
            }
        }
        finally
        {
            _open = null;
        }
    }
}
