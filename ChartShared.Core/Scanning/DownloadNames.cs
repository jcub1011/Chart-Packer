using System.Text;

namespace ChartShared.Core.Scanning;

/// <summary>Names for files and folders saved out of a song, so they say which song they're from and never overwrite.</summary>
public static class DownloadNames
{
    private static readonly HashSet<string> WindowsReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    // Invalid on any of the systems the app runs on, so a name saved on one can be copied to another.
    private const string InvalidChars = "<>:\"/\\|?*";

    /// <summary>
    /// What names a song's saved files: <c>Artist - Title</c>, the title alone when there's no
    /// artist, or <paramref name="fallback"/> when there's no title. Made safe as a file name.
    /// </summary>
    /// <param name="fallback">The song's name in the library, e.g. its file or folder name.</param>
    public static string SongPrefix(string? artist, string? title, string fallback)
    {
        ArgumentNullException.ThrowIfNull(fallback);

        string? cleanTitle = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
        string name = cleanTitle is null ? fallback
            : string.IsNullOrWhiteSpace(artist) ? cleanTitle
            : $"{artist.Trim()} - {cleanTitle}";
        return SafeName(name);
    }

    /// <summary>
    /// The name of one of a song's files saved on its own: <paramref name="prefix"/>, then the file's
    /// own name, e.g. <c>Artist - Title - album.png</c>. A file in a subfolder keeps only its last segment.
    /// </summary>
    public static string FileName(string prefix, string storedName)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        ArgumentException.ThrowIfNullOrEmpty(storedName);

        string file = storedName[(storedName.LastIndexOf('/') + 1)..];
        return SafeName(prefix.Length == 0 ? file : $"{prefix} - {file}");
    }

    /// <summary>
    /// <paramref name="name"/> as a file or folder name: characters no file system allows, and
    /// control characters, become <c>_</c>; trailing dots and spaces go, and a Windows device name
    /// such as <c>CON</c> gets a <c>_</c> after it. An empty result is <c>_</c>.
    /// </summary>
    public static string SafeName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var builder = new StringBuilder(name.Length);
        foreach (char c in name)
            builder.Append(char.IsControl(c) || InvalidChars.Contains(c) ? '_' : c);

        string safe = builder.ToString().Trim().TrimEnd('.', ' ');
        if (safe.Length == 0)
            return "_";

        int dot = safe.IndexOf('.');
        string stem = dot < 0 ? safe : safe[..dot];
        return WindowsReservedNames.Contains(stem.TrimEnd()) ? stem + "_" + safe[stem.Length..] : safe;
    }

    /// <summary>
    /// The path in <paramref name="folder"/> for <paramref name="name"/> that nothing has yet: the name
    /// itself, else <c>name (2).ext</c>, <c>name (3).ext</c> and so on. A file or a folder takes a name.
    /// </summary>
    /// <param name="isFolder">The name is a folder's, so a dot in it doesn't start an extension.</param>
    public static string UniquePath(string folder, string name, bool isFolder = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        string stem = isFolder ? name : Path.GetFileNameWithoutExtension(name);
        string extension = isFolder ? string.Empty : Path.GetExtension(name);
        string path = Path.Combine(folder, name);
        for (int n = 2; File.Exists(path) || Directory.Exists(path); n++)
            path = Path.Combine(folder, $"{stem} ({n}){extension}");
        return path;
    }
}
