using System.Collections.Generic;
using System.Threading.Tasks;

namespace ChartPacker.Services
{
    public interface INewChartWindowService
    {
        /// <summary>Shows the New chart wizard and completes when it closes.</summary>
        /// <param name="audioFiles">Audio files to start the wizard with, e.g. ones dropped on the main window.</param>
        /// <param name="libraryFolder">The library's folder, where the song is created by default.</param>
        /// <returns>The song folder made, or <see langword="null"/> when none was.</returns>
        Task<string?> ShowAsync(IReadOnlyList<string> audioFiles, string? libraryFolder);
    }
}
