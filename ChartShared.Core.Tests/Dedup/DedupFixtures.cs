using ChartShared.Core.Dedup;
using ChartShared.Core.Packing;
using ChartShared.Core.Scanning;

namespace ChartShared.Core.Tests.Dedup;

/// <summary>Builds song folders and <c>.sng</c> files for the duplicate finder's tests.</summary>
internal static class DedupFixtures
{
    public const string Chart = "[Song]\n{\n  Resolution = 192\n}\n[SyncTrack]\n{\n  0 = B 120000\n}\n[ExpertSingle]\n{\n  0 = N 0 0\n}\n";

    /// <summary>Creates a song folder under <paramref name="root"/> with a <c>song.ini</c> and the given files.</summary>
    public static SongEntry Folder(DirectoryInfo root, string name, string ini, params (string Relative, byte[] Data)[] files)
    {
        var folder = root.CreateSubdirectory(name);
        File.WriteAllText(Path.Combine(folder.FullName, "song.ini"), ini);
        foreach (var (relative, data) in files)
        {
            string path = Path.Combine(folder.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, data);
        }

        return FolderEntry(folder.FullName);
    }

    /// <summary>A song folder with a chart and audio, and a <c>song.ini</c> naming it.</summary>
    public static SongEntry Song(DirectoryInfo root, string name, string artist = "Artist", string title = "Title", string? chart = null, byte[]? audio = null) =>
        Folder(root, name, IniFor(artist, title), ("notes.chart", Utf8(chart ?? Chart)), ("guitar.ogg", audio ?? Bytes(4096, 1)));

    /// <summary>Packs the folder <paramref name="folder"/> into <paramref name="sngPath"/>.</summary>
    public static async Task<SongEntry> PackAsync(SongEntry folder, string sngPath)
    {
        var plan = await PackPlanner.PlanAsync(folder.FullPath, new PackOptions(IncludeSubfolders: true));
        await Packer.PackAsync(plan, sngPath, overwrite: false);
        return SngEntry(sngPath);
    }

    public static SongEntry FolderEntry(string path) =>
        new(SongEntryKind.SongFolder, path, Path.GetFileName(path), Path.GetFileName(path));

    public static SongEntry SngEntry(string path) =>
        new(SongEntryKind.SngFile, path, Path.GetFileName(path), Path.GetFileNameWithoutExtension(path));

    public static string IniFor(string artist, string title, string extra = "") =>
        $"[song]\nname = {title}\nartist = {artist}\n{extra}";

    public static byte[] Utf8(string text) => System.Text.Encoding.UTF8.GetBytes(text);

    /// <summary><paramref name="length"/> bytes that depend on <paramref name="seed"/>.</summary>
    public static byte[] Bytes(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    /// <summary>A fingerprint with only what the keeper rules look at filled in.</summary>
    public static SongFingerprint Fingerprint(
        SongEntryKind kind,
        string path,
        DateTime? written = null,
        bool hasDrafts = false,
        long bytes = 100)
    {
        string name = kind == SongEntryKind.SngFile ? Path.GetFileNameWithoutExtension(path) : Path.GetFileName(path);
        var entry = new SongEntry(kind, path, Path.GetFileName(path), name);
        var stamp = new EntryStamp(bytes, written ?? new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), 1, "");
        return new SongFingerprint(entry, stamp, [], [], [], hasDrafts, ReadOnlyMemory<byte>.Empty, new Dictionary<string, StoredFileSource>());
    }

    /// <summary>An <see cref="IProgress{T}"/> that reports on the calling thread, so tests can act at an exact point.</summary>
    public sealed class Inline<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
