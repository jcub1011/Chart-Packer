using System.Threading.Tasks;

namespace ChartPacker.UI.Services
{
    public interface IFolderPickerService
    {
        /// <summary>
        /// Shows a folder picker, opened at <paramref name="startFolder"/> when it exists, and returns the
        /// chosen local path, or <see langword="null"/> if cancelled.
        /// </summary>
        Task<string?> PickFolderAsync(string title, string? startFolder = null);
    }
}
