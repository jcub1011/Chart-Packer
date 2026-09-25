using System.Threading.Tasks;

namespace ChartPacker.Services
{
    public interface IFolderPickerService
    {
        /// <summary>Shows a folder picker and returns the chosen local path, or <see langword="null"/> if cancelled.</summary>
        Task<string?> PickFolderAsync(string title);
    }
}
