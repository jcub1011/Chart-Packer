using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ChartPacker.Resources;
using ChartShared.Core.Metadata;
using ChartShared.Core.Scanning;
using ChartShared.Core.Validation;

namespace ChartPacker.Services
{
    /// <summary>A song to save, with the draft to write.</summary>
    /// <param name="Entry">The song.</param>
    /// <param name="SongIniPath">For a folder, the song.ini to edit (which may not exist yet); <see langword="null"/> for a <c>.sng</c>.</param>
    /// <param name="Draft">Its unsaved changes, cleared once they're written.</param>
    public sealed record SaveTarget(SongEntry Entry, string? SongIniPath, MetadataDraft Draft);

    /// <summary>
    /// Writes songs' drafts to disk, for the inspector and the Validator alike: each song is re-read and written back
    /// with only its draft's changes, and its draft cleared once it's saved.
    /// </summary>
    /// <param name="songFiles">Keeps saving a song apart from copies of it being downloaded.</param>
    public sealed class DraftSaver(SongFileGate songFiles)
    {
        /// <summary>
        /// Saves each of <paramref name="targets"/> in turn. A song that fails keeps its draft. Call it on the UI thread,
        /// with nothing holding the songs' files open.
        /// </summary>
        /// <param name="onSaved">Called on the calling thread after each song is saved and its draft cleared.</param>
        /// <returns>A line for each song that couldn't be saved, saying why; empty when every one was.</returns>
        public async Task<IReadOnlyList<string>> SaveAsync(IReadOnlyList<SaveTarget> targets, Action<SongEntry> onSaved)
        {
            var failures = new List<string>();

            // Downloads of these songs finish first, and new ones wait until they're saved.
            using (await songFiles.WriteAsync(targets.Select(t => t.Entry.FullPath)))
            {
                foreach (var target in targets)
                {
                    string? error;
                    try
                    {
                        error = await Task.Run(() => SaveSongAsync(target));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                    {
                        error = ex.Message;
                    }

                    if (error is null)
                    {
                        target.Draft.Clear();
                        onSaved(target.Entry);
                    }
                    else
                    {
                        failures.Add(string.Format(CultureInfo.CurrentCulture, Strings.SaveFailedItemFormat, target.Entry.DisplayName, error));
                    }
                }
            }

            return failures;
        }

        /// <summary>Re-reads the song and writes it back with only the draft's keys, album art and files changed.</summary>
        /// <returns>Why it couldn't be saved, or <see langword="null"/> on success.</returns>
        private static async Task<string?> SaveSongAsync(SaveTarget target)
        {
            if (target.Entry.Kind == SongEntryKind.SngFile)
                return FirstError(await DraftWriter.SaveSngAsync(target.Entry.FullPath, target.Draft).ConfigureAwait(false));

            await DraftWriter.SaveFolderAsync(target.Entry.FullPath, target.SongIniPath!, target.Draft).ConfigureAwait(false);
            return null;
        }

        private static string? FirstError(IEnumerable<Finding> findings) =>
            findings.FirstOrDefault(f => f.Severity == Severity.Error)?.Message;
    }
}
