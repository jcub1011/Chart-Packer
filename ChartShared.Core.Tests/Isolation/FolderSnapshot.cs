using System.Security.Cryptography;

namespace ChartShared.Core.Tests.Isolation;

/// <summary>The observable state of a folder tree: whether it exists, and each file's size, timestamp and hash.</summary>
internal sealed class FolderSnapshot
{
    private readonly bool _exists;
    private readonly SortedDictionary<string, FileState> _files;

    private FolderSnapshot(bool exists, SortedDictionary<string, FileState> files)
    {
        _exists = exists;
        _files = files;
    }

    public static FolderSnapshot Take(string folder)
    {
        var files = new SortedDictionary<string, FileState>(StringComparer.Ordinal);
        if (!Directory.Exists(folder))
            return new FolderSnapshot(false, files);

        foreach (var path in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            var info = new FileInfo(path);
            string hash;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                hash = Convert.ToHexString(SHA256.HashData(stream));

            files[Path.GetRelativePath(folder, path)] = new FileState(info.Length, info.LastWriteTimeUtc, hash);
        }

        return new FolderSnapshot(true, files);
    }

    /// <summary>Describes how <paramref name="after"/> differs from this snapshot; empty when identical.</summary>
    public IReadOnlyList<string> DiffTo(FolderSnapshot after) =>
        [.. Changes(after).Select(static c => c.Path is null ? $"folder was {c.What}" : $"'{c.Path}' was {c.What}")];

    /// <summary>
    /// The files, relative to the folder, that differ in <paramref name="after"/>: created, modified or deleted; null for
    /// the folder itself, when it was created or deleted.
    /// </summary>
    public IReadOnlyList<string?> ChangedPaths(FolderSnapshot after) => [.. Changes(after).Select(static c => c.Path)];

    private IEnumerable<(string? Path, string What)> Changes(FolderSnapshot after)
    {
        if (_exists != after._exists)
            yield return (null, after._exists ? "created" : "deleted");

        foreach (var (path, state) in _files)
        {
            if (!after._files.TryGetValue(path, out var afterState))
                yield return (path, "deleted");
            else if (state != afterState)
                yield return (path, "modified");
        }

        foreach (var path in after._files.Keys)
        {
            if (!_files.ContainsKey(path))
                yield return (path, "created");
        }
    }

    private readonly record struct FileState(long Length, DateTime LastWriteUtc, string Sha256);
}
