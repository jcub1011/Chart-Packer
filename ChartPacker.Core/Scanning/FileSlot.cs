namespace ChartPacker.Core.Scanning;

/// <summary>
/// A file a song holds one of, under any of several names: its album art, its background image or
/// its background video. Replacing it replaces every variant, e.g. <c>album.jpg</c> with <c>album.png</c>.
/// </summary>
public static class FileSlot
{
    /// <summary>
    /// Deletes every file directly in <paramref name="folder"/> that <paramref name="isSlotName"/>
    /// matches, and, when <paramref name="sourcePath"/> is given, copies it in as
    /// <paramref name="storedName"/>. Files in subfolders are left alone.
    /// </summary>
    /// <remarks>
    /// The replacement is copied to a temp file before anything is deleted, so a copy that fails
    /// leaves the old file in place, and the source may be one of the files being replaced.
    /// </remarks>
    /// <param name="isSlotName">Whether a file name (without a folder) is one of the slot's names.</param>
    /// <param name="storedName">The name to store the replacement under; required with <paramref name="sourcePath"/>.</param>
    /// <param name="sourcePath">The replacement, or <see langword="null"/> to only delete.</param>
    /// <exception cref="IOException">A file can't be read, written or deleted.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    /// <exception cref="OperationCanceledException">Cancelled before anything was deleted.</exception>
    public static async Task ApplyToFolderAsync(
        string folder, Func<string, bool> isSlotName, string? storedName, string? sourcePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentNullException.ThrowIfNull(isSlotName);

        string? target = null;
        string? tempPath = null;
        if (sourcePath is not null)
        {
            ArgumentException.ThrowIfNullOrEmpty(storedName);
            target = Path.Combine(folder, storedName);
            tempPath = target + ".tmp";

            try
            {
                await using (var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1, useAsync: true))
                await using (var output = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 64 * 1024, useAsync: true))
                    await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                TryDelete(tempPath);
                throw;
            }
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (string path in Directory.EnumerateFiles(folder).Where(p => isSlotName(Path.GetFileName(p))).ToList())
                File.Delete(path);

            if (tempPath is not null)
                File.Move(tempPath, target!, overwrite: true);
        }
        catch
        {
            if (tempPath is not null)
                TryDelete(tempPath);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
