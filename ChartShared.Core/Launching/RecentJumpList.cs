namespace ChartShared.Core.Launching;

/// <summary>One song in the taskbar's Jump List: what it's called, where it is, and the arguments that open it.</summary>
/// <param name="Title">What the entry is called: "Artist – Name", or the song's file or folder name.</param>
/// <param name="Path">The song's full path, which the entry shows as its tooltip.</param>
/// <param name="Arguments">The command line that opens the song in Chart Builder, as one string: the path, quoted.</param>
public sealed record JumpListEntry(string Title, string Path, string Arguments);

/// <summary>Works out what the taskbar's Jump List lists from the recent songs.</summary>
public static class RecentJumpList
{
    /// <summary>
    /// The entries for <paramref name="songs"/>, the latest first: those still there, each once, no more than
    /// <see cref="RecentSongs.Max"/>, and none the user took off the list.
    /// </summary>
    /// <param name="songs">The recent songs, the latest first.</param>
    /// <param name="exists">Whether a song's folder or file is still there.</param>
    /// <param name="removedArguments">The arguments of the entries the user removed from the list, which Windows won't have listed again.</param>
    public static IReadOnlyList<JumpListEntry> Build(IEnumerable<RecentSong>? songs, Func<string, bool> exists, IEnumerable<string>? removedArguments = null)
    {
        ArgumentNullException.ThrowIfNull(exists);
        var removed = new HashSet<string>(removedArguments ?? [], StringComparer.OrdinalIgnoreCase);
        List<JumpListEntry> entries = [];
        foreach (var song in songs ?? [])
        {
            if (entries.Count == RecentSongs.Max)
                break;
            if (string.IsNullOrWhiteSpace(song.Path) || !exists(song.Path))
                continue;

            // A path that isn't one (from a hand-edited settings file) can't be opened, so it isn't listed.
            if (!TryFullPath(song.Path, out string path))
                continue;

            string arguments = Quote(path);
            if (removed.Contains(arguments) || entries.Any(e => RecentSongs.SamePath(e.Path, path)))
                continue;

            string title = string.IsNullOrWhiteSpace(song.Title) ? NameOf(path) : song.Title;
            entries.Add(new JumpListEntry(title, path, arguments));
        }

        return entries;
    }

    /// <summary><paramref name="argument"/> as one command-line argument, in quotes so that spaces stay in it.</summary>
    /// <remarks>Backslashes at the end of it are doubled, so they don't escape the closing quote.</remarks>
    public static string Quote(string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);
        int trailing = argument.Length - argument.TrimEnd('\\').Length;
        return "\"" + argument + new string('\\', trailing) + "\"";
    }

    private static bool TryFullPath(string path, out string full)
    {
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            full = "";
            return false;
        }
    }

    private static string NameOf(string path) => Path.GetFileName(path) is { Length: > 0 } name ? name : path;
}
