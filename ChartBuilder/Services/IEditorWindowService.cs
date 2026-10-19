using System.Collections.Generic;
using System.Threading.Tasks;
using ChartShared.Core.Charting;
using ChartShared.Core.Scanning;

namespace ChartBuilder.Services
{
    public interface IEditorWindowService
    {
        /// <summary>
        /// Opens a new chart in the Editor window, in place of what's open (charter-spec C1). The chart
        /// has <paramref name="audioFiles"/> as its stems, and no folder until it's first saved.
        /// </summary>
        void OpenNewChart(IReadOnlyList<string> audioFiles);

        /// <summary>
        /// Opens <paramref name="song"/> in the Editor window, in place of what's open. The Editor holds
        /// <paramref name="songLock"/> until it closes (charter-spec §4.8).
        /// </summary>
        void OpenSong(OpenedSong song, SongLock songLock);

        /// <summary>
        /// Opens <paramref name="song"/>, recovered from the autosave <paramref name="recoveryKey"/> (charter-spec C2 item 7),
        /// in the Editor window, in place of what's open. It's unsaved until it's saved. The Editor holds
        /// <paramref name="songLock"/>, if there is one, until it closes.
        /// </summary>
        void OpenRecovered(EditorSong song, SongLock? songLock, string recoveryKey);

        /// <summary>
        /// Finishes once the Editor window has drawn what's changed in it, so it's on screen before work that holds the UI
        /// thread up, such as making a song's Editor.
        /// </summary>
        Task WaitForRenderAsync();

        /// <summary>Opens the calibration tool (charter-spec C11) over the window in front, finishing when it closes.</summary>
        Task ShowCalibrationAsync();
    }
}
