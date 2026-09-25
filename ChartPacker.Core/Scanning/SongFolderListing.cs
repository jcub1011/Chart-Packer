namespace ChartPacker.Core.Scanning;

/// <summary>A file inside a song folder.</summary>
/// <param name="RelativePath">Path from the song folder, with <c>/</c> separators, as a <c>.sng</c> would store it.</param>
/// <param name="Length">Size in bytes.</param>
public sealed record FolderFileEntry(string RelativePath, long Length);

/// <summary>Lists every file in a song folder, including files in subfolders.</summary>
public static class SongFolderListing
{
    private static readonly EnumerationOptions Options = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = true,
        // Hidden and system files are listed so junk like desktop.ini stays visible. Reparse
        // points are skipped so symlinked or junctioned folders can't create cycles.
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    /// <summary>Lists the files under <paramref name="folder"/> on the thread pool, sorted by path ignoring case.</summary>
    /// <exception cref="DirectoryNotFoundException"><paramref name="folder"/> doesn't exist.</exception>
    /// <exception cref="IOException">The folder can't be read.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the folder is denied.</exception>
    /// <exception cref="OperationCanceledException">The listing was cancelled.</exception>
    public static Task<IReadOnlyList<FolderFileEntry>> ListAsync(string folder, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        return Task.Run(() => List(folder, cancellationToken), cancellationToken);
    }

    /// <inheritdoc cref="ListAsync"/>
    public static IReadOnlyList<FolderFileEntry> List(string folder, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        var root = new DirectoryInfo(Path.GetFullPath(folder));
        if (!root.Exists)
            throw new DirectoryNotFoundException($"The folder '{root.FullName}' doesn't exist.");

        var files = new List<FolderFileEntry>();
        foreach (FileInfo file in root.EnumerateFiles("*", Options))
        {
            if ((files.Count & 255) == 0)
                cancellationToken.ThrowIfCancellationRequested();

            string relative = Path.GetRelativePath(root.FullName, file.FullName).Replace(Path.DirectorySeparatorChar, '/');
            files.Add(new FolderFileEntry(relative, file.Length));
        }

        files.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.RelativePath, b.RelativePath));
        return files;
    }
}
