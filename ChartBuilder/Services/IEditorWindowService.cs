using System.Collections.Generic;
using System.Threading.Tasks;
using ChartPacker.Core.Charting;
using ChartPacker.Core.Scanning;

namespace ChartBuilder.Services
{
    public interface IEditorWindowService
    {
        /// <summary>
        /// Opens a new Editor window in place of the launcher, which closes. <paramref name="audioFiles"/> are the
        /// ones New chart was given; the Editor can't use them yet, so it says they weren't added.
        /// </summary>
        void OpenEditor(IReadOnlyList<string> audioFiles);

        /// <summary>
        /// Opens <paramref name="song"/> in a new Editor window in place of the launcher, which closes. The Editor holds
        /// <paramref name="songLock"/> until it closes (charter-spec §4.8).
        /// </summary>
        void OpenSong(OpenedSong song, SongLock songLock);

        /// <summary>Opens the calibration tool (charter-spec C11) over the window in front, finishing when it closes.</summary>
        Task ShowCalibrationAsync();
    }
}
