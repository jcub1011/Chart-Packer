using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ChartShared.Audio.Decoding;
using ChartShared.Core.Scanning;
using ChartShared.UI.Resources;

namespace ChartShared.UI.ViewModels
{
    /// <summary>A file checked to be added as a track: its size, or why it can't be one.</summary>
    /// <param name="Error">Why the file can't be a track, said for the user; null when it can.</param>
    public sealed record TrackFileCheck(long Length, string? Error)
    {
        public bool IsUsable => Error is null;
    }

    /// <summary>
    /// What Add track and Replace do with the file chosen, in Chart Packer's inspector and Chart Builder's Chart settings:
    /// check it's audio, name it after its stem, and work out what it replaces.
    /// </summary>
    public static class TrackStaging
    {
        /// <summary>
        /// Checks that the file at <paramref name="path"/> can be a track: one of the audio extensions, and audio in that
        /// format, as a little of it decodes. The decoding runs off the calling thread.
        /// </summary>
        public static async Task<TrackFileCheck> CheckAsync(string path)
        {
            ArgumentException.ThrowIfNullOrEmpty(path);
            string fileName = Path.GetFileName(path);
            if (!AudioStems.IsAudioExtension(Path.GetExtension(path)))
                return new TrackFileCheck(0, string.Format(CultureInfo.CurrentCulture, SharedStrings.TrackNotAudioFormat, fileName));

            // Decoding a little of it now catches a file that isn't what its extension says.
            return await Task.Run(() =>
            {
                try
                {
                    AudioDecoder.Check(path);
                    return new TrackFileCheck(new FileInfo(path).Length, null);
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
                {
                    return new TrackFileCheck(0, string.Format(CultureInfo.CurrentCulture, SharedStrings.TrackUnusableFormat, fileName, ex.Message));
                }
            }).ConfigureAwait(true);
        }

        /// <summary>The name a file at <paramref name="path"/> is stored under as <paramref name="stem"/>'s track: the stem with its extension.</summary>
        public static string StoredNameFor(string stem, string path)
        {
            ArgumentException.ThrowIfNullOrEmpty(stem);
            ArgumentException.ThrowIfNullOrEmpty(path);
            return stem + Path.GetExtension(path).ToLowerInvariant();
        }

        /// <summary>
        /// The stored files a track stored as <paramref name="storedName"/> replaces: every file of
        /// <paramref name="stem"/> in any format, but one already stored under that very name, which the add overwrites.
        /// A stored file that differs only in case is still replaced, on a case-sensitive file system too.
        /// </summary>
        public static IReadOnlyList<string> Replaced(IEnumerable<string> storedNames, string stem, string storedName)
        {
            ArgumentNullException.ThrowIfNull(storedNames);
            return [.. SongTracks.FilesOfStem(storedNames, stem).Where(stored => !stored.Equals(storedName, StringComparison.Ordinal))];
        }

        /// <summary>The title of the file picker for <paramref name="stem"/>'s track, with the stem in title case as the rest is.</summary>
        public static string PickTitleFor(string stem) =>
            string.Format(CultureInfo.CurrentCulture, SharedStrings.PickTrackTitleFormat, CultureInfo.CurrentCulture.TextInfo.ToTitleCase(stem));

        /// <summary>
        /// The Add track menu's entries: every stem, labelled with just its name, each running
        /// <paramref name="commandFor"/>'s command.
        /// </summary>
        public static IReadOnlyList<StemOption> OptionsFor(Func<string, System.Windows.Input.ICommand> commandFor)
        {
            ArgumentNullException.ThrowIfNull(commandFor);
            return [.. AudioStems.Stems.Select(stem => new StemOption(stem, stem, commandFor(stem)))];
        }
    }
}
