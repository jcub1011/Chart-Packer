using System.Collections.Generic;
using System.Threading.Tasks;
using ChartPacker.Core.Charting;
using ChartPacker.Core.Scanning;

namespace ChartBuilder.Services
{
    public interface IEditorWindowService
    {
        /// <summary>
        /// Opens a new chart in a new Editor window, closing the window open (charter-spec C1). The chart
        /// has <paramref name="audioFiles"/> as its stems, and no folder until it's first saved.
        /// </summary>
        void OpenNewChart(IReadOnlyList<string> audioFiles);

        /// <summary>
        /// Opens <paramref name="song"/> in a new Editor window, closing the window open. The Editor holds
        /// <paramref name="songLock"/> until it closes (charter-spec §4.8).
        /// </summary>
        void OpenSong(OpenedSong song, SongLock songLock);

        /// <summary>
        /// Opens <paramref name="song"/>, recovered from the autosave <paramref name="recoveryKey"/> (charter-spec C2 item 7),
        /// in a new Editor window, closing the window open. It's unsaved until it's saved. The Editor holds
        /// <paramref name="songLock"/>, if there is one, until it closes.
        /// </summary>
        void OpenRecovered(EditorSong song, SongLock? songLock, string recoveryKey);

        /// <summary>Opens the calibration tool (charter-spec C11) over the window in front, finishing when it closes.</summary>
        Task ShowCalibrationAsync();
    }
}
