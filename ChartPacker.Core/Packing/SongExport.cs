using ChartPacker.Core.Ini;
using ChartPacker.Core.Metadata;
using ChartPacker.Core.Scanning;

namespace ChartPacker.Core.Packing;

/// <summary>Saves a copy of a song as a song folder, with its unsaved changes, leaving the song itself alone.</summary>
/// <remarks>
/// A <c>.sng</c> is extracted as <see cref="Unpacker"/> extracts it; a folder is copied, subfolders
/// and all. The draft is then saved to the copy, so the folder has what the app shows. It's built
/// in a hidden folder beside the target and moved into place at the end, so a failure or cancel
/// leaves nothing behind.
/// </remarks>
public static class SongExport
{
    private const string TempSuffix = ".chartpacker-tmp";

    /// <summary>Saves <paramref name="entry"/>, with <paramref name="draft"/> applied, as the new folder <paramref name="targetFolder"/>.</summary>
    /// <param name="draft">The song's unsaved changes, or <see langword="null"/> for none.</param>
    /// <returns>
    /// Whether the folder was written, and the findings of extracting a <c>.sng</c>; it isn't written
    /// when the <c>.sng</c> has structural errors.
    /// </returns>
    /// <exception cref="IOException">
    /// <paramref name="targetFolder"/> exists, or a file can't be read or written; or, as a
    /// <see cref="Charts.ChartFormatException"/>, the draft's chart changes can't be applied.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    /// <exception cref="OperationCanceledException">The export was cancelled.</exception>
    public static async Task<UnpackResult> ExportFolderAsync(SongEntry entry, MetadataDraft? draft, string targetFolder, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetFolder);

        string target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetFolder));
        if (Directory.Exists(target) || File.Exists(target))
            throw new IOException($"'{target}' already exists.");

        string parent = Path.GetDirectoryName(target) ?? throw new IOException($"'{target}' has no parent folder.");
        Directory.CreateDirectory(parent);
        string staging = Path.Combine(parent, "." + Path.GetFileName(target) + "." + Guid.NewGuid().ToString("N")[..8] + TempSuffix);

        try
        {
            UnpackResult result;
            if (entry.Kind == SongEntryKind.SngFile)
            {
                result = await Unpacker.UnpackAsync(entry.FullPath, staging, overwrite: false, cancellationToken).ConfigureAwait(false);
                if (!result.Extracted)
                    return result;
            }
            else
            {
                await Task.Run(() => CopyFolder(entry.FullPath, staging, cancellationToken), cancellationToken).ConfigureAwait(false);
                result = new UnpackResult(true, []);
            }

            if (draft is { IsEmpty: false })
                await DraftWriter.SaveFolderAsync(staging, SongIniIn(staging), draft, cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(staging, target);
            return result;
        }
        finally
        {
            TryDeleteDirectory(staging);
        }
    }

    private static void CopyFolder(string source, string destination, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in SongFolderListing.List(source, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string[] segments = file.RelativePath.Split('/');
            string to = Path.Combine([destination, .. segments]);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(Path.Combine([source, .. segments]), to);
        }
    }

    /// <summary>The folder's <c>song.ini</c>, matched ignoring case, or where a new one goes.</summary>
    private static string SongIniIn(string folder) =>
        Directory.EnumerateFiles(folder).FirstOrDefault(p => Path.GetFileName(p).Equals(SongIni.FileName, StringComparison.OrdinalIgnoreCase))
        ?? Path.Combine(folder, SongIni.FileName);

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
