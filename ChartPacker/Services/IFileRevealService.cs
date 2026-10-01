using System.Collections.Generic;

namespace ChartPacker.Services
{
    /// <summary>Shows files and folders in the system's file manager.</summary>
    public interface IFileRevealService
    {
        /// <summary>
        /// Opens the folder each of <paramref name="paths"/> is in, with it selected where the file
        /// manager can select it. Paths in the same folder share one window.
        /// </summary>
        void Reveal(IReadOnlyList<string> paths);
    }
}
