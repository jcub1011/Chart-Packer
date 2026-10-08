using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using ChartPacker.Core.Packing;
using ChartPacker.Core.Scanning;
using ChartPacker.Core.Validation;
using ChartPacker.Resources;
using ChartPacker.UI.Services;
using ChartPacker.ViewModels;

namespace ChartPacker.Services
{
    /// <summary>
    /// Saves to <see cref="Folder"/>, or the system's Downloads folder when none is set, and asks
    /// where instead when <see cref="AskEveryTime"/> is on. A file is written beside its target and
    /// moved into place, so a failure leaves nothing half-written. A song is read only while
    /// <paramref name="songFiles"/> says it isn't being saved.
    /// </summary>
    public sealed class DownloadService(SettingsService settings, Func<TopLevel?> topLevel, NoticeViewModel notice, SongFileGate songFiles) : IDownloadService
    {
        /// <summary>Where downloads go; <see langword="null"/> for the system's Downloads folder.</summary>
        public string? Folder
        {
            get => settings.Settings.DownloadFolder;
            set => settings.Settings.DownloadFolder = string.IsNullOrWhiteSpace(value) ? null : value;
        }

        /// <summary>Whether each download asks where to save, starting in <see cref="Folder"/>.</summary>
        public bool AskEveryTime
        {
            get => settings.Settings.AskDownloadLocation;
            set => settings.Settings.AskDownloadLocation = value;
        }

        public async Task SaveFileAsync(SongEntry? song, string prefix, string storedName, Func<CancellationToken, Task<Stream>> open)
        {
            string name = DownloadNames.FileName(prefix, storedName);
            string? path = null;
            NoticeViewModel.BusyNotice? busy = null;

            // Read once: a file the picker asked about may be overwritten, whatever the setting is by the time it's written.
            bool ask = AskEveryTime;
            try
            {
                string folder = await DefaultFolderAsync();
                path = ask ? await PickFileAsync(folder, name) : DownloadNames.UniquePath(CreateFolder(folder), name);
                if (path is null)
                    return;

                busy = notice.BeginBusy(Format(Strings.DownloadSavingFormat, Path.GetFileName(path)));
                string target = path;
                using (await songFiles.ReadAsync(song is null ? [] : [song.FullPath]))
                    await Task.Run(() => WriteAsync(target, open, overwrite: ask));
                notice.ShowSaved(Format(Strings.DownloadSavedFormat, Path.GetFileName(path), FolderLabel(path)), [path]);
            }
            // NotSupportedException: the platform has no save picker. ArgumentException: the path picked can't be used.
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                notice.ShowError(Format(Strings.DownloadFailedFormat, path is null ? name : Path.GetFileName(path), ex.Message));
            }
            finally
            {
                busy?.Dispose();
            }
        }

        public async Task SaveSongFoldersAsync(IReadOnlyList<SongDownload> songs)
        {
            if (songs.Count == 0)
                return;

            var saved = new List<string>();
            SongDownload? current = null;
            NoticeViewModel.BusyNotice? busy = null;
            try
            {
                string folder = await DefaultFolderAsync();
                string? parent = AskEveryTime ? await PickFolderAsync(folder) : CreateFolder(folder);
                if (parent is null)
                    return;

                foreach (var song in songs)
                {
                    current = song;
                    string target = DownloadNames.UniquePath(parent, DownloadNames.SafeName(song.FolderName), isFolder: true);
                    string message = songs.Count == 1
                        ? Format(Strings.DownloadSavingFormat, Path.GetFileName(target))
                        : Format(Strings.DownloadSavingSongsFormat, saved.Count + 1, songs.Count);
                    if (busy is null)
                        busy = notice.BeginBusy(message);
                    else
                        busy.Update(message);

                    UnpackResult result;
                    using (await songFiles.ReadAsync([song.Entry.FullPath]))
                        result = await SongExport.ExportFolderAsync(song.Entry, song.Draft, target);
                    if (!result.Extracted)
                    {
                        string reason = result.Findings.FirstOrDefault(f => f.Severity == Severity.Error)?.Message ?? Strings.DownloadUnreadable;
                        throw new IOException(reason);
                    }

                    saved.Add(target);
                }

                notice.ShowSaved(saved.Count == 1
                    ? Format(Strings.DownloadSavedFormat, Path.GetFileName(saved[0]), FolderLabel(saved[0]))
                    : Format(Strings.DownloadSavedSongsFormat, saved.Count, FolderLabel(saved[0])), saved);
            }
            // ArgumentException: the draft can't be saved as it is, e.g. an added file has the new album art's name.
            // NotSupportedException: the platform has no folder picker.
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                string name = current?.FolderName ?? songs[0].FolderName;
                notice.ShowError(saved.Count == 0
                    ? Format(Strings.DownloadFailedFormat, name, ex.Message)
                    : Format(Strings.DownloadPartlyFailedFormat, saved.Count, songs.Count, name, ex.Message), saved);
            }
            finally
            {
                busy?.Dispose();
            }
        }

        /// <summary>Copies what <paramref name="open"/> opens to a temp file beside <paramref name="path"/>, then moves it there.</summary>
        private static async Task WriteAsync(string path, Func<CancellationToken, Task<Stream>> open, bool overwrite)
        {
            string temp = StagingPath.For(path);
            try
            {
                await using (var source = await open(CancellationToken.None))
                await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 64 * 1024, useAsync: true))
                    await source.CopyToAsync(output);
                File.Move(temp, path, overwrite);
            }
            finally
            {
                StagingPath.TryDeleteFile(temp);
            }
        }

        /// <summary>The folder set in the settings, or the system's Downloads folder.</summary>
        private async Task<string> DefaultFolderAsync()
        {
            if (Folder is { } folder)
                return folder;

            if (topLevel()?.StorageProvider is { } storage)
            {
                try
                {
                    if (await storage.TryGetWellKnownFolderAsync(WellKnownFolder.Downloads) is { } downloads && downloads.TryGetLocalPath() is { } local)
                        return local;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
                {
                }
            }

            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }

        private static string CreateFolder(string folder)
        {
            Directory.CreateDirectory(folder);
            return folder;
        }

        /// <summary>Asks where to save <paramref name="name"/>, starting in <paramref name="folder"/>; <see langword="null"/> when cancelled.</summary>
        private async Task<string?> PickFileAsync(string folder, string name)
        {
            if (topLevel() is not { } owner)
                return null;

            string extension = Path.GetExtension(name);
            var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = Strings.DownloadPickerTitle,
                SuggestedFileName = name,
                SuggestedStartLocation = await StorageStartFolder.TryGetAsync(owner.StorageProvider, folder),
                DefaultExtension = extension.Length > 0 ? extension[1..] : null,
                FileTypeChoices = extension.Length > 0
                    ? [new FilePickerFileType(extension[1..].ToUpperInvariant()) { Patterns = ["*" + extension] }, FilePickerFileTypes.All]
                    : [FilePickerFileTypes.All],
                ShowOverwritePrompt = true,
            });
            return file?.TryGetLocalPath();
        }

        /// <summary>Asks which folder to save songs in, starting in <paramref name="folder"/>; <see langword="null"/> when cancelled.</summary>
        private async Task<string?> PickFolderAsync(string folder)
        {
            if (topLevel() is not { } owner)
                return null;

            var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = Strings.DownloadFolderPickerTitle,
                AllowMultiple = false,
                SuggestedStartLocation = await StorageStartFolder.TryGetAsync(owner.StorageProvider, folder),
            });
            return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        }

        /// <summary>The name of the folder <paramref name="path"/> is in, for the notice.</summary>
        private static string FolderLabel(string path)
        {
            string folder = Path.GetDirectoryName(path) ?? path;
            return Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)) is { Length: > 0 } name ? name : folder;
        }

        private static string Format(string format, params object[] args) => string.Format(CultureInfo.CurrentCulture, format, args);
    }
}
