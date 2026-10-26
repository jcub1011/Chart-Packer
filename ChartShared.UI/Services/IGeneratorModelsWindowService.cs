using System.Threading.Tasks;
using Avalonia.Controls;

namespace ChartShared.UI.Services;

/// <summary>Opens the generator model storage dialog (issue #154), shared by both apps.</summary>
public interface IGeneratorModelsWindowService
{
    /// <summary>Opens the dialog over <paramref name="owner"/>, or the main window without one, and waits for it to close.</summary>
    Task ShowAsync(Window? owner = null);
}
