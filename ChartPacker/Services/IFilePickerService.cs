using System.Collections.Generic;
using System.Threading.Tasks;

namespace ChartPacker.Services
{
    public interface IFilePickerService
    {
        /// <summary>Shows a picker for any number of files and returns their local paths, or none if cancelled.</summary>
        Task<IReadOnlyList<string>> PickFilesAsync(string title);

        /// <summary>Shows a picker for one audio file (ogg, opus, mp3 or wav) and returns its local path, or <see langword="null"/> if cancelled.</summary>
        Task<string?> PickAudioAsync(string title);

        /// <summary>
        /// Shows a picker for one background image or video, filtered to both kinds by default, and
        /// returns its local path, or <see langword="null"/> if cancelled.
        /// </summary>
        Task<string?> PickBackgroundAsync(string title);
    }
}
