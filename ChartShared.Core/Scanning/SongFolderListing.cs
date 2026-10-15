namespace ChartShared.Core.Scanning;

/// <summary>A file inside a song folder.</summary>
/// <param name="RelativePath">Path from the song folder, with <c>/</c> separators, as a <c>.sng</c> would store it.</param>
/// <param name="Length">Size in bytes.</param>
public sealed record FolderFileEntry(string RelativePath, long Length);

/// <summary>A file inside a song folder, with the time it was last written.</summary>
/// <param name="RelativePath">Path from the song folder, with <c>/</c> separators.</param>
/// <param name="Length">Size in bytes.</param>
/// <param name="LastWriteTimeUtc">When the file was last written.</param>
public sealed record FolderFileStamp(string RelativePath, long Length, DateTime LastWriteTimeUtc);

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
    public static IReadOnlyList<FolderFileEntry> List(string folder, CancellationToken cancellationToken = default) =>
        [.. Enumerate(folder, cancellationToken).Select(f => new FolderFileEntry(f.Relative, f.File.Length))];

    /// <summary>Lists the files under <paramref name="folder"/> like <see cref="List"/>, with their write times.</summary>
    /// <inheritdoc cref="ListAsync" path="/exception"/>
    public static IReadOnlyList<FolderFileStamp> ListStamped(string folder, CancellationToken cancellationToken = default) =>
        [.. Enumerate(folder, cancellationToken).Select(f => new FolderFileStamp(f.Relative, f.File.Length, f.File.LastWriteTimeUtc))];

    private static List<(string Relative, FileInfo File)> Enumerate(string folder, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        var root = new DirectoryInfo(Path.GetFullPath(folder));
        if (!root.Exists)
            throw new DirectoryNotFoundException($"The folder '{root.FullName}' doesn't exist.");

        var files = new List<(string Relative, FileInfo File)>();
        foreach (FileInfo file in root.EnumerateFiles("*", Options))
        {
            if ((files.Count & 255) == 0)
                cancellationToken.ThrowIfCancellationRequested();

            string relative = Path.GetRelativePath(root.FullName, file.FullName).Replace(Path.DirectorySeparatorChar, '/');
            files.Add((relative, file));
        }

        files.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Relative, b.Relative));
        return files;
    }
}
