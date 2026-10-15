using System.Threading.Channels;

namespace ChartShared.Core.Scanning;

/// <summary>A song source the library scan found, and where it comes in scan order.</summary>
public sealed record ScannedSong(SongEntry Entry, ScanPosition Position);

/// <summary>
/// Finds song sources under a folder: <c>.sng</c> files, and folders that contain a <c>song.ini</c>.
/// </summary>
/// <remarks>
/// This is the library browser's scan, not the batch-mode song folder heuristic
/// (sng-spec §9.4). A folder containing <c>song.ini</c> is treated as a leaf: it is listed once and
/// not searched further.
/// </remarks>
public static class SongLibraryScanner
{
    private const string SngExtension = ".sng";
    private const string SongIniName = "song.ini";

    private static readonly EnumerationOptions DirectoryOptions = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        // Reparse points are skipped so symlinked or junctioned folders can't create cycles.
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
    };

    /// <summary>
    /// Lazily enumerates song sources under <paramref name="root"/>, depth-first, with each
    /// folder's contents in case-insensitive name order.
    /// </summary>
    /// <param name="root">The folder to scan. It is depth 0.</param>
    /// <param name="maxDepth">The deepest folder level to examine. 0 examines only the root.</param>
    /// <param name="cancellationToken">Checked once per folder.</param>
    /// <exception cref="DirectoryNotFoundException"><paramref name="root"/> doesn't exist.</exception>
    public static IEnumerable<SongEntry> Enumerate(string root, int maxDepth, CancellationToken cancellationToken = default)
    {
        string fullRoot = ValidateRoot(root, maxDepth);
        return Walk(fullRoot, maxDepth, cancellationToken);
    }

    /// <summary>
    /// Scans <paramref name="root"/> on the thread pool, listing up to <paramref name="parallelism"/>
    /// folders at once, and hands each song source to <paramref name="found"/> as soon as it's found.
    /// </summary>
    /// <remarks>
    /// Songs are found in no particular order; sorting them by <see cref="ScannedSong.Position"/>
    /// gives the order <see cref="Enumerate"/> yields them in.
    /// </remarks>
    /// <param name="found">Called from several threads at once.</param>
    /// <returns>The total number of song sources found.</returns>
    /// <exception cref="DirectoryNotFoundException"><paramref name="root"/> doesn't exist.</exception>
    /// <exception cref="OperationCanceledException">The scan was cancelled.</exception>
    public static Task<int> ScanAsync(
        string root,
        int maxDepth,
        int parallelism,
        Action<ScannedSong> found,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(found);
        ArgumentOutOfRangeException.ThrowIfLessThan(parallelism, 1);
        string fullRoot = ValidateRoot(root, maxDepth);

        return Task.Run(() => DiscoverAsync(fullRoot, maxDepth, parallelism, found, cancellationToken), cancellationToken);
    }

    private static string ValidateRoot(string root, int maxDepth)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentOutOfRangeException.ThrowIfNegative(maxDepth);

        string fullRoot = Path.GetFullPath(root);
        if (!Directory.Exists(fullRoot))
            throw new DirectoryNotFoundException($"The folder '{fullRoot}' doesn't exist.");

        return fullRoot;
    }

    private static async Task<int> DiscoverAsync(
        string root,
        int maxDepth,
        int parallelism,
        Action<ScannedSong> found,
        CancellationToken cancellationToken)
    {
        var folders = Channel.CreateUnbounded<PendingFolder>();
        folders.Writer.TryWrite(new PendingFolder(root, 0, ScanPosition.Root));

        // Folders queued or being listed. The queue is complete once none are left.
        int pending = 1;
        int total = 0;

        // Workers are only started while there are folders waiting, so a huge parallelism is harmless.
        var options = new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cancellationToken };
        await Parallel.ForEachAsync(folders.Reader.ReadAllAsync(cancellationToken), options, (folder, token) =>
        {
            try
            {
                token.ThrowIfCancellationRequested();
                if (List(root, folder.Path) is not { } listing)
                    return ValueTask.CompletedTask;

                if (listing.IsSongFolder)
                {
                    found(new ScannedSong(SongFolderEntry(root, folder.Path), folder.Position));
                    Interlocked.Increment(ref total);
                    return ValueTask.CompletedTask;
                }

                for (int i = 0; i < listing.SngFiles.Count; i++)
                {
                    found(new ScannedSong(SngFileEntry(root, listing.SngFiles[i]), folder.Position.Child(i)));
                    Interlocked.Increment(ref total);
                }

                if (folder.Depth < maxDepth)
                {
                    for (int i = 0; i < listing.Subfolders.Count; i++)
                    {
                        Interlocked.Increment(ref pending);
                        folders.Writer.TryWrite(new PendingFolder(
                            listing.Subfolders[i],
                            folder.Depth + 1,
                            folder.Position.Child(listing.SngFiles.Count + i)));
                    }
                }

                return ValueTask.CompletedTask;
            }
            finally
            {
                if (Interlocked.Decrement(ref pending) == 0)
                    folders.Writer.TryComplete();
            }
        }).ConfigureAwait(false);

        return total;
    }

    /// <summary>Depth-first walk, yielding each entry as it's found.</summary>
    private static IEnumerable<SongEntry> Walk(string root, int maxDepth, CancellationToken cancellationToken)
    {
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((root, 0));

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (string folder, int depth) = pending.Pop();

            if (List(root, folder) is not { } listing)
                continue;

            if (listing.IsSongFolder)
            {
                yield return SongFolderEntry(root, folder);
                continue;
            }

            foreach (string file in listing.SngFiles)
                yield return SngFileEntry(root, file);

            if (depth < maxDepth)
            {
                // Pushed in reverse so the stack pops them in ascending order.
                for (int i = listing.Subfolders.Count - 1; i >= 0; i--)
                    pending.Push((listing.Subfolders[i], depth + 1));
            }
        }
    }

    /// <summary>
    /// What <paramref name="folder"/> holds, each list in case-insensitive name order, or
    /// <see langword="null"/> for a subfolder that vanished or can't be read mid-scan.
    /// </summary>
    /// <exception cref="IOException">The root can't be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The root can't be read.</exception>
    private static FolderListing? List(string root, string folder)
    {
        var sngFiles = new List<string>();
        var subfolders = new List<string>();

        try
        {
            foreach (FileSystemInfo info in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", DirectoryOptions))
            {
                if (info is DirectoryInfo)
                {
                    if (!info.Name.Equals("__MACOSX", StringComparison.OrdinalIgnoreCase))
                        subfolders.Add(info.FullName);
                }
                else if (info.Name.Equals(SongIniName, StringComparison.OrdinalIgnoreCase))
                {
                    return new FolderListing(true, [], []);
                }
                else if (Path.GetExtension(info.Name).Equals(SngExtension, StringComparison.OrdinalIgnoreCase))
                {
                    sngFiles.Add(info.FullName);
                }
            }
        }
        catch (Exception ex) when (folder != root && ex is IOException or UnauthorizedAccessException)
        {
            // A subfolder that vanished or can't be read mid-scan is skipped, not fatal.
            return null;
        }

        sngFiles.Sort(StringComparer.OrdinalIgnoreCase);
        subfolders.Sort(StringComparer.OrdinalIgnoreCase);
        return new FolderListing(false, sngFiles, subfolders);
    }

    private static SongEntry SongFolderEntry(string root, string folder) => new(
        SongEntryKind.SongFolder,
        folder,
        Path.GetRelativePath(root, folder),
        new DirectoryInfo(folder).Name);

    private static SongEntry SngFileEntry(string root, string file) => new(
        SongEntryKind.SngFile,
        file,
        Path.GetRelativePath(root, file),
        Path.GetFileNameWithoutExtension(file));

    private sealed record FolderListing(bool IsSongFolder, List<string> SngFiles, List<string> Subfolders);

    private readonly record struct PendingFolder(string Path, int Depth, ScanPosition Position);
}
