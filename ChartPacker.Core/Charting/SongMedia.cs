using ChartPacker.Core.Scanning;

namespace ChartPacker.Core.Charting;

/// <summary>What kind of file a <see cref="MediaSlot"/> holds.</summary>
public enum MediaKind
{
    /// <summary>The album art: <c>album.png</c>, <c>.jpg</c> or <c>.jpeg</c>.</summary>
    AlbumArt,

    /// <summary>The background image: <c>background.png</c>, <c>.jpg</c> or <c>.jpeg</c>.</summary>
    BackgroundImage,

    /// <summary>The background video: <c>video.mp4</c> and the like.</summary>
    BackgroundVideo,

    /// <summary>An audio track, one per stem: <c>guitar.ogg</c> and the like.</summary>
    Track,
}

/// <summary>
/// A place in the song for one file the Editor can add, replace or remove (charter-spec C1, Art and Tracks): the album
/// art, a background, or the track of one stem.
/// </summary>
/// <param name="Stem">For a track, its stem, lowercased, e.g. <c>guitar</c>; null otherwise.</param>
public readonly record struct MediaSlot(MediaKind Kind, string? Stem = null)
{
    public static MediaSlot AlbumArt { get; } = new(MediaKind.AlbumArt);

    public static MediaSlot BackgroundImage { get; } = new(MediaKind.BackgroundImage);

    public static MediaSlot BackgroundVideo { get; } = new(MediaKind.BackgroundVideo);

    /// <summary>The track of <paramref name="stem"/>, one of <see cref="AudioStems.Stems"/>.</summary>
    /// <exception cref="ArgumentException"><paramref name="stem"/> isn't an audio stem.</exception>
    public static MediaSlot Track(string stem)
    {
        ArgumentException.ThrowIfNullOrEmpty(stem);
        if (!AudioStems.IsStem(stem))
            throw new ArgumentException($"'{stem}' isn't an audio stem.", nameof(stem));
        return new MediaSlot(MediaKind.Track, stem.ToLowerInvariant());
    }

    /// <summary>The background slot of <paramref name="kind"/>.</summary>
    public static MediaSlot Background(BackgroundKind kind) => kind == Scanning.BackgroundKind.Image ? BackgroundImage : BackgroundVideo;

    /// <summary>The background kind it holds; null when it isn't a background.</summary>
    public BackgroundKind? BackgroundKind => Kind switch
    {
        MediaKind.BackgroundImage => Scanning.BackgroundKind.Image,
        MediaKind.BackgroundVideo => Scanning.BackgroundKind.Video,
        _ => null,
    };

    /// <summary>Whether <paramref name="storedName"/>, a file of the song, is in this slot.</summary>
    public bool Holds(string storedName)
    {
        ArgumentNullException.ThrowIfNull(storedName);
        return Kind switch
        {
            MediaKind.AlbumArt => Scanning.AlbumArt.IsAlbumArtName(storedName),
            MediaKind.Track => Stem!.Equals(AudioStems.StemOf(storedName), StringComparison.OrdinalIgnoreCase),
            _ => SongBackgrounds.IsName(storedName, BackgroundKind!.Value),
        };
    }

    /// <summary>
    /// The name a file at <paramref name="sourcePath"/> is stored under in this slot, e.g. <c>album.png</c> or
    /// <c>guitar.ogg</c>; null when the file can't be stored here, being of the wrong kind.
    /// </summary>
    public string? StoredNameFor(string sourcePath)
    {
        ArgumentNullException.ThrowIfNull(sourcePath);
        string extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        return Kind switch
        {
            MediaKind.AlbumArt => Scanning.AlbumArt.StoredNameFor(sourcePath),
            MediaKind.Track => AudioStems.IsAudioExtension(extension) ? Stem + extension : null,
            _ => SongBackgrounds.StoredNameFor(BackgroundKind!.Value, sourcePath),
        };
    }

    public override string ToString() => Kind == MediaKind.Track ? $"Track({Stem})" : Kind.ToString();
}

/// <summary>A pending change to a <see cref="MediaSlot"/>: the file the next save puts in it, or its removal.</summary>
/// <param name="SourcePath">The file to copy in, or null when the slot's file is removed.</param>
public readonly record struct MediaChange(string? SourcePath)
{
    /// <summary>True when the slot's file is removed.</summary>
    public bool IsRemoval => SourcePath is null;
}

/// <summary>
/// What a <see cref="MediaSlot"/> holds as the next save leaves it, as an undoable edit keeps it: nothing, the file the
/// song had stored at a save, or a file to copy in.
/// </summary>
public abstract record MediaRef
{
    private MediaRef()
    {
    }

    /// <summary>Nothing: the slot is empty.</summary>
    public static MediaRef None { get; } = new NoneRef();

    /// <summary>
    /// What the song had stored in the slot as of <paramref name="generation"/> saves (<see cref="EditorSong.SaveGeneration"/>).
    /// Once a later save has replaced it, the session's copy of it stands in (<see cref="EditorSong.Saved"/>).
    /// </summary>
    public static MediaRef Stored(int generation) => new StoredRef(generation);

    /// <summary>The file at <paramref name="path"/>, copied in at the next save.</summary>
    public static MediaRef Source(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return new SourceRef(path);
    }

    /// <summary>The slot is empty.</summary>
    public sealed record NoneRef : MediaRef;

    /// <param name="Generation">The save it's as of.</param>
    public sealed record StoredRef(int Generation) : MediaRef;

    /// <param name="Path">The file to copy in.</param>
    public sealed record SourceRef(string Path) : MediaRef;
}

/// <summary>
/// What a save did to the song's files, for <see cref="EditorSong.Saved"/>: the slots it wrote, and where the session kept
/// a copy of each file it replaced or removed, so undo can still bring that file back (charter-spec C1).
/// </summary>
/// <param name="Changed">The slots the save wrote.</param>
/// <param name="Kept">For each slot whose stored file the save replaced or removed, the copy kept of it.</param>
public sealed record MediaSaveResult(IReadOnlyCollection<MediaSlot> Changed, IReadOnlyDictionary<MediaSlot, string> Kept)
{
    public static MediaSaveResult None { get; } = new([], new Dictionary<MediaSlot, string>());
}
