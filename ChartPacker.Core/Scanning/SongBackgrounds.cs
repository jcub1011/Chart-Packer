namespace ChartPacker.Core.Scanning;

/// <summary>The two kinds of background a song can have.</summary>
public enum BackgroundKind
{
    /// <summary><c>background.png</c>, <c>.jpg</c> or <c>.jpeg</c>.</summary>
    Image,

    /// <summary><c>video.mp4</c>, <c>.avi</c>, <c>.webm</c>, <c>.vp8</c>, <c>.ogv</c> or <c>.mpeg</c>.</summary>
    Video,
}

/// <summary>
/// A song's background image and background video (sng-spec §8). A song can have both; games
/// play the video when video backgrounds are on, and show the image otherwise.
/// </summary>
public static class SongBackgrounds
{
    /// <summary>The background image's stem.</summary>
    public const string ImageStem = "background";

    /// <summary>The background video's stem.</summary>
    public const string VideoStem = "video";

    /// <summary>The background image's extensions, in preference order.</summary>
    public static IReadOnlyList<string> ImageExtensions { get; } = [".png", ".jpg", ".jpeg"];

    /// <summary>The background video's extensions, in preference order.</summary>
    public static IReadOnlyList<string> VideoExtensions { get; } = [".mp4", ".avi", ".webm", ".vp8", ".ogv", ".mpeg"];

    /// <summary>Both kinds, the video first, as games prefer it.</summary>
    public static IReadOnlyList<BackgroundKind> Kinds { get; } = [BackgroundKind.Video, BackgroundKind.Image];

    /// <summary>The stem files of <paramref name="kind"/> are stored under.</summary>
    public static string StemOf(BackgroundKind kind) => kind == BackgroundKind.Image ? ImageStem : VideoStem;

    /// <summary>The extensions of <paramref name="kind"/>, in preference order.</summary>
    public static IReadOnlyList<string> ExtensionsOf(BackgroundKind kind) => kind == BackgroundKind.Image ? ImageExtensions : VideoExtensions;

    /// <summary>Which kind of background a file with <paramref name="path"/>'s extension can be, ignoring case; <see langword="null"/> for neither.</summary>
    public static BackgroundKind? KindOf(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string extension = Path.GetExtension(path);
        if (ImageExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            return BackgroundKind.Image;
        if (VideoExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            return BackgroundKind.Video;
        return null;
    }

    /// <summary>
    /// Which kind of background the stored name <paramref name="name"/> is, ignoring case, e.g.
    /// <see cref="BackgroundKind.Image"/> for <c>Background.JPG</c>; <see langword="null"/> for
    /// neither. Names with a folder never are.
    /// </summary>
    public static BackgroundKind? KindOfName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Contains('/') || name.Contains('\\'))
            return null;

        foreach (var kind in Kinds)
        {
            if (IsName(name, kind))
                return kind;
        }

        return null;
    }

    /// <summary>Whether <paramref name="name"/> is a stored name of <paramref name="kind"/>, ignoring case. Names with a folder never are.</summary>
    public static bool IsName(string name, BackgroundKind kind)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Contains('/') || name.Contains('\\'))
            return false;

        string stem = Path.GetFileNameWithoutExtension(name);
        return stem.Equals(StemOf(kind), StringComparison.OrdinalIgnoreCase)
            && ExtensionsOf(kind).Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The name to store the file at <paramref name="sourcePath"/> under: <c>background.</c> or
    /// <c>video.</c> plus its lowercased extension, or <see langword="null"/> when its extension
    /// isn't one of <paramref name="kind"/>'s.
    /// </summary>
    public static string? StoredNameFor(BackgroundKind kind, string sourcePath)
    {
        ArgumentNullException.ThrowIfNull(sourcePath);
        string name = StemOf(kind) + Path.GetExtension(sourcePath).ToLowerInvariant();
        return IsName(name, kind) ? name : null;
    }

    /// <summary>The first of <paramref name="items"/> whose name is the most preferred name of <paramref name="kind"/>.</summary>
    public static T? Pick<T>(IEnumerable<T> items, Func<T, string> name, BackgroundKind kind) where T : class
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(name);

        var list = items as IReadOnlyList<T> ?? items.ToList();
        string stem = StemOf(kind);
        foreach (string extension in ExtensionsOf(kind))
        {
            string candidate = stem + extension;
            foreach (T item in list)
            {
                if (name(item).Equals(candidate, StringComparison.OrdinalIgnoreCase))
                    return item;
            }
        }

        return null;
    }
}
