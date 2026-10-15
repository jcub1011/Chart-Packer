namespace ChartShared.Core.Launching;

/// <summary>A song opened in Chart Builder lately, as the Open menu lists it.</summary>
public sealed class RecentSong
{
    /// <summary>The song folder or <c>.sng</c>, in full.</summary>
    public string Path { get; set; } = "";

    /// <summary>What it was called when it was last opened: "Artist – Name", or its file or folder name.</summary>
    public string Title { get; set; } = "";

    /// <summary>When it was last opened.</summary>
    public DateTimeOffset OpenedAt { get; set; }
}

/// <summary>Keeps the list of songs opened lately: the latest first, each once, and no more than <see cref="Max"/>.</summary>
public static class RecentSongs
{
    /// <summary>How many songs are kept.</summary>
    public const int Max = 10;

    /// <summary><paramref name="songs"/> with <paramref name="song"/> first, in place of any earlier entry for its path.</summary>
    public static List<RecentSong> Add(IEnumerable<RecentSong>? songs, RecentSong song)
    {
        ArgumentNullException.ThrowIfNull(song);
        return [song, .. Without(songs, song.Path).Take(Max - 1)];
    }

    /// <summary><paramref name="songs"/> without the entry for <paramref name="path"/>, as when the song is no longer there.</summary>
    public static List<RecentSong> Remove(IEnumerable<RecentSong>? songs, string path) => [.. Without(songs, path)];

    /// <summary>Whether <paramref name="a"/> and <paramref name="b"/> are the same song: the same full path, ignoring case where the file system does.</summary>
    public static bool SamePath(string a, string b)
    {
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(Full(a), Full(b), comparison);

        // A path that isn't one (from a hand-edited settings file) is compared as written.
        static string Full(string path)
        {
            try
            {
                return System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.IO.PathTooLongException)
            {
                return path;
            }
        }
    }

    private static IEnumerable<RecentSong> Without(IEnumerable<RecentSong>? songs, string path) =>
        (songs ?? []).Where(s => !string.IsNullOrWhiteSpace(s.Path) && !SamePath(s.Path, path));
}
