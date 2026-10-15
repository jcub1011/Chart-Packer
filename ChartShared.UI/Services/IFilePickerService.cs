using System.Collections.Generic;
using System.Threading.Tasks;

namespace ChartShared.UI.Services
{
    public interface IFilePickerService
    {
        /// <summary>Shows a picker for any number of files and returns their local paths, or none if cancelled.</summary>
        Task<IReadOnlyList<string>> PickFilesAsync(string title);

        /// <summary>Shows a picker for one audio file (ogg, opus, mp3 or wav) and returns its local path, or <see langword="null"/> if cancelled.</summary>
        Task<string?> PickAudioAsync(string title);

        /// <summary>Shows a picker for any number of audio files (ogg, opus, mp3 or wav) and returns their local paths, or none if cancelled.</summary>
        Task<IReadOnlyList<string>> PickAudioFilesAsync(string title);

        /// <summary>
        /// Shows a picker for one background image or video, filtered to both kinds by default, and
        /// returns its local path, or <see langword="null"/> if cancelled.
        /// </summary>
        Task<string?> PickBackgroundAsync(string title);

        /// <summary>Shows a picker for one chart (.chart, .mid or .midi) and returns its local path, or <see langword="null"/> if cancelled.</summary>
        Task<string?> PickChartAsync(string title);

        /// <summary>Shows a picker for one chart (.chart, .mid or .midi) or .sng to take lyrics from, and returns its local path, or <see langword="null"/> if cancelled.</summary>
        Task<string?> PickLyricsSourceAsync(string title);

        /// <summary>
        /// Shows a picker for one song: a .sng, or a song folder's song.ini or chart, which stands for the folder. Returns
        /// the picked file's local path, or <see langword="null"/> if cancelled.
        /// </summary>
        Task<string?> PickSongAsync(string title);
    }
}
