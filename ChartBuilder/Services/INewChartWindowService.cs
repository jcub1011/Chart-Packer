using System.Collections.Generic;
using System.Threading.Tasks;

namespace ChartBuilder.Services
{
    public interface INewChartWindowService
    {
        /// <summary>Shows the New chart wizard and completes when it closes.</summary>
        /// <param name="audioFiles">Audio files to start the wizard with, e.g. ones dropped on the main window.</param>
        /// <returns>The song folder made, or <see langword="null"/> when none was.</returns>
        Task<string?> ShowAsync(IReadOnlyList<string> audioFiles);
    }
}
