using System.Runtime.ExceptionServices;
using ChartPacker.Core.Ini;
using ChartPacker.Core.Metadata;

namespace ChartPacker.Core.Scanning;

/// <summary>Adds files to and removes files from a song folder.</summary>
public static class SongFolderFiles
{
    /// <summary>
    /// Deletes <paramref name="removes"/> from <paramref name="folder"/> and copies
    /// <paramref name="adds"/> into it. An added file replaces one of the same name.
    /// </summary>
    /// <remarks>
    /// <para>Names are relative to the folder, with <c>/</c> between segments. Every added file is
    /// copied to a temp file before anything else changes, so a copy that fails leaves the folder as
    /// it was, and a source may be one of the files being removed or replaced.</para>
    /// <para>The temp files are then moved into place, and only then are the removed files deleted.
    /// So in the worst case, a crash or failure between the moves and the deletes, the folder holds
    /// both the old files and the new ones; it's never left with neither. A removed file that only
    /// differs from an added one in letter case is the exception: it's deleted before the moves,
    /// because on a case-insensitive file system deleting it afterwards would delete the new file.
    /// A removed file with the same name as an added one is left for the add to replace.</para>
    /// <para>A removed file that no longer exists is skipped. A delete that fails doesn't stop the
    /// others. Folders a removal empties are kept.</para>
    /// </remarks>
    /// <exception cref="ArgumentException">A name is the folder's <c>song.ini</c>, which is edited as metadata, or points outside the folder.</exception>
    /// <exception cref="IOException">A file can't be read, written or deleted. The first failed delete is thrown once every delete has been tried.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    /// <exception cref="OperationCanceledException">Cancelled before anything in the folder changed.</exception>
    public static async Task ApplyAsync(
        string folder,
        IReadOnlyCollection<string> removes,
        IReadOnlyList<AddedFile> adds,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentNullException.ThrowIfNull(removes);
        ArgumentNullException.ThrowIfNull(adds);

        string root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var removePaths = removes.Select(name => Resolve(root, name)).ToList();
        var targets = adds.Select(file => Resolve(root, file.StoredName)).ToList();

        // A removal with an add's exact path is replaced by the add's move, so it isn't deleted at all.
        // One that differs only in case is deleted before the moves, the rest after them.
        var exactTargets = targets.ToHashSet(StringComparer.Ordinal);
        var caseTargets = targets.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var deleteFirst = removePaths.Where(path => !exactTargets.Contains(path) && caseTargets.Contains(path)).ToList();
        var deleteLast = removePaths.Where(path => !caseTargets.Contains(path)).ToList();

        var temps = new List<string>(adds.Count);
        int moved = 0;
        try
        {
            for (int i = 0; i < adds.Count; i++)
            {
                // A unique name, so an existing file is never overwritten by a temp file.
                string temp = $"{targets[i]}.{Guid.NewGuid():N}.tmp";
                Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
                temps.Add(temp);

                await using var input = new FileStream(adds[i].SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1, useAsync: true);
                await using var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 64 * 1024, useAsync: true);
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            foreach (string path in deleteFirst)
            {
                if (File.Exists(path))
                    File.Delete(path);
            }

            for (; moved < temps.Count; moved++)
                File.Move(temps[moved], targets[moved], overwrite: true);
        }
        catch
        {
            // A temp that was moved is now the target, which is never deleted here.
            foreach (string temp in temps.Skip(moved))
                TryDelete(temp);
            throw;
        }

        Exception? firstFailure = null;
        foreach (string path in deleteLast)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                firstFailure ??= ex;
            }
        }

        if (firstFailure is not null)
            ExceptionDispatchInfo.Throw(firstFailure);
    }

    /// <summary>The full path of <paramref name="name"/> in the folder at <paramref name="root"/>.</summary>
    private static string Resolve(string root, string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (name.Equals(SongIni.FileName, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"'{name}' is edited as metadata, not added or removed as a file.", nameof(name));

        string path = Path.GetFullPath(Path.Combine([root, .. name.Split('/')]));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || path.Length == root.Length)
            throw new ArgumentException($"'{name}' isn't inside the song folder.", nameof(name));
        return path;
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
