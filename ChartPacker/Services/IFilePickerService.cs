using System.Collections.Generic;
using System.Threading.Tasks;

namespace ChartPacker.Services
{
    public interface IFilePickerService
    {
        /// <summary>Shows a picker for any number of files and returns their local paths, or none if cancelled.</summary>
        Task<IReadOnlyList<string>> PickFilesAsync(string title);
    }
}
