using System.Collections.Generic;

namespace ChartShared.UI.Services
{
    /// <summary>Shows files and folders in the system's file manager.</summary>
    public interface IFileRevealService
    {
        /// <summary>
        /// Opens the folder each of <paramref name="paths"/> is in, with it selected where the file
        /// manager can select it. Paths in the same folder share one window.
        /// </summary>
        void Reveal(IReadOnlyList<string> paths);

        /// <summary>Opens <paramref name="folder"/> itself in the file manager, showing what's in it. Nothing when it isn't there.</summary>
        void OpenFolder(string folder);
    }
}
