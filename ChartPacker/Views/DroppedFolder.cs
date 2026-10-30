using System.Linq;
using Avalonia.Input;
using Avalonia.Platform.Storage;

namespace ChartPacker.Views
{
    /// <summary>The folder being dragged onto the app.</summary>
    internal static class DroppedFolder
    {
        /// <summary>The local path of the first folder being dragged, or <see langword="null"/> when there's none.</summary>
        public static string? Of(DragEventArgs e) =>
            !e.DataTransfer.Contains(DataFormat.File) ? null : e.DataTransfer.TryGetFiles()?.OfType<IStorageFolder>()
                .Select(f => f.TryGetLocalPath())
                .OfType<string>()
                .FirstOrDefault();
    }
}
