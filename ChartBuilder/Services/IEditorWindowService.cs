using System.Collections.Generic;
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
    }
}
