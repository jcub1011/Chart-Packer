using ChartPacker.Core.Scanning;

namespace ChartPacker.Core.Jobs;

/// <summary>How a job arranges outputs under the destination.</summary>
public enum OutputLayout
{
    /// <summary>Each output keeps its item's place under the scanned folder (project spec F4).</summary>
    Mirror,

    /// <summary>Every output goes directly in the destination folder. Different items can then want the same output.</summary>
    Flat,
}

/// <summary>Where a job writes each item's output.</summary>
/// <remarks>
/// By default outputs mirror each item's place under the scanned folder (project spec F4), so with
/// the default destination a folder packs to a <c>.sng</c> beside it and a <c>.sng</c> unpacks to a
/// folder beside it (F1, F2).
/// </remarks>
public static class OutputPaths
{
    private const string SngExtension = ".sng";

    /// <summary>Compares paths the way the current OS's file system usually does.</summary>
    public static StringComparer PathComparer { get; } =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>The <c>.sng</c> path for a song folder.</summary>
    public static string PackTarget(SongEntry entry, string scanRoot, string destinationRoot, OutputLayout layout = OutputLayout.Mirror)
    {
        ArgumentNullException.ThrowIfNull(entry);
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(entry.FullPath));
        return Path.Combine(OutputFolder(entry, scanRoot, destinationRoot, layout), name + SngExtension);
    }

    /// <summary>The song-folder path for a <c>.sng</c>.</summary>
    public static string UnpackTarget(SongEntry entry, string scanRoot, string destinationRoot, OutputLayout layout = OutputLayout.Mirror)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return Path.Combine(OutputFolder(entry, scanRoot, destinationRoot, layout), Path.GetFileNameWithoutExtension(entry.FullPath));
    }

    /// <summary>
    /// Applies <paramref name="policy"/> to <paramref name="target"/>. Returns <paramref name="target"/>
    /// when it's free or may be overwritten, the first free <c>Name (n)</c> when renaming, or
    /// <see langword="null"/> when the item should be skipped.
    /// </summary>
    /// <param name="target">The preferred output path.</param>
    /// <param name="policy">What to do if it exists.</param>
    /// <param name="isDirectory">Whether the output is a folder, which has no extension to keep.</param>
    public static string? Resolve(string target, OverwritePolicy policy, bool isDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        if (!Exists(target))
            return target;

        switch (policy)
        {
            case OverwritePolicy.Overwrite:
                return target;
            case OverwritePolicy.Rename:
                string folder = Path.GetDirectoryName(target) ?? "";
                string stem = isDirectory ? Path.GetFileName(target) : Path.GetFileNameWithoutExtension(target);
                string extension = isDirectory ? "" : Path.GetExtension(target);
                for (int n = 2; ; n++)
                {
                    string candidate = Path.Combine(folder, $"{stem} ({n}){extension}");
                    if (!Exists(candidate))
                        return candidate;
                }
            default:
                return null;
        }
    }

    /// <summary>
    /// The folder an item's output goes in: the destination itself when flat, otherwise the item's
    /// parent, mirrored from under <paramref name="scanRoot"/> to under <paramref name="destinationRoot"/>.
    /// </summary>
    private static string OutputFolder(SongEntry entry, string scanRoot, string destinationRoot, OutputLayout layout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scanRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);

        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(scanRoot));
        string destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationRoot));
        string item = Path.TrimEndingDirectorySeparator(Path.GetFullPath(entry.FullPath));

        // The scanned folder is itself the song. Writing into it would put the output inside
        // the song, so with the default destination it goes beside the folder instead.
        if (PathComparer.Equals(item, root))
        {
            return PathComparer.Equals(destination, root)
                ? Path.GetDirectoryName(root) ?? destination
                : destination;
        }

        if (layout == OutputLayout.Flat)
            return destination;

        string relativeParent = Path.GetDirectoryName(Path.GetRelativePath(root, item)) ?? "";
        return Path.Combine(destination, relativeParent);
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
}
