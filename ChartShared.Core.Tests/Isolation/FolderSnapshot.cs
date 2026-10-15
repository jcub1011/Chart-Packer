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
    public IReadOnlyList<string> DiffTo(FolderSnapshot after)
    {
        var changes = new List<string>();
        if (_exists != after._exists)
            changes.Add(after._exists ? "folder was created" : "folder was deleted");

        foreach (var (path, state) in _files)
        {
            if (!after._files.TryGetValue(path, out var afterState))
                changes.Add($"'{path}' was deleted");
            else if (state != afterState)
                changes.Add($"'{path}' was modified");
        }

        foreach (var path in after._files.Keys)
        {
            if (!_files.ContainsKey(path))
                changes.Add($"'{path}' was created");
        }

        return changes;
    }

    private readonly record struct FileState(long Length, DateTime LastWriteUtc, string Sha256);
}
