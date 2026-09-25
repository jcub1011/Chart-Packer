using System.Diagnostics;

namespace ChartPacker.Core.Scanning;

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
    private const int MaxBatchSize = 256;
    private static readonly TimeSpan BatchInterval = TimeSpan.FromMilliseconds(100);

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
        return Walk(fullRoot, maxDepth, cancellationToken).OfType<SongEntry>();
    }

    /// <summary>
    /// Scans <paramref name="root"/> on the thread pool, reporting entries in batches as they're
    /// found (at most every ~100 ms, or sooner when a batch fills).
    /// </summary>
    /// <returns>The total number of entries found.</returns>
    /// <exception cref="DirectoryNotFoundException"><paramref name="root"/> doesn't exist.</exception>
    /// <exception cref="OperationCanceledException">The scan was cancelled.</exception>
    public static Task<int> ScanAsync(
        string root,
        int maxDepth,
        IProgress<IReadOnlyList<SongEntry>> progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(progress);
        string fullRoot = ValidateRoot(root, maxDepth);

        return Task.Run(() =>
        {
            int total = 0;
            var batch = new List<SongEntry>();
            var sinceLastReport = Stopwatch.StartNew();

            foreach (SongEntry? entry in Walk(fullRoot, maxDepth, cancellationToken))
            {
                if (entry is not null)
                {
                    batch.Add(entry);
                    total++;
                }

                if (batch.Count >= MaxBatchSize || (batch.Count > 0 && sinceLastReport.Elapsed >= BatchInterval))
                {
                    progress.Report(batch);
                    batch = [];
                    sinceLastReport.Restart();
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (batch.Count > 0)
                progress.Report(batch);

            return total;
        }, cancellationToken);
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

    /// <summary>
    /// Depth-first walk. Yields each entry found, plus a <see langword="null"/> after every folder
    /// so <see cref="ScanAsync"/> can flush a pending batch while nothing new is being found.
    /// </summary>
    private static IEnumerable<SongEntry?> Walk(string root, int maxDepth, CancellationToken cancellationToken)
    {
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((root, 0));

        var sngFiles = new List<string>();
        var subfolders = new List<string>();

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (string folder, int depth) = pending.Pop();

            sngFiles.Clear();
            subfolders.Clear();
            bool isSongFolder = false;

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
                        isSongFolder = true;
                        break;
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
                continue;
            }

            if (isSongFolder)
            {
                yield return new SongEntry(
                    SongEntryKind.SongFolder,
                    folder,
                    Path.GetRelativePath(root, folder),
                    new DirectoryInfo(folder).Name);
            }
            else
            {
                sngFiles.Sort(StringComparer.OrdinalIgnoreCase);
                foreach (string file in sngFiles)
                {
                    yield return new SongEntry(
                        SongEntryKind.SngFile,
                        file,
                        Path.GetRelativePath(root, file),
                        Path.GetFileNameWithoutExtension(file));
                }

                if (depth < maxDepth)
                {
                    // Pushed in reverse so the stack pops them in ascending order.
                    subfolders.Sort(StringComparer.OrdinalIgnoreCase);
                    for (int i = subfolders.Count - 1; i >= 0; i--)
                        pending.Push((subfolders[i], depth + 1));
                }
            }

            yield return null;
        }
    }
}
