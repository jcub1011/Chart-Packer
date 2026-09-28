namespace ChartPacker.Core.Scanning;

/// <summary>
/// The audio files of the filename registry (sng-spec §8): one per stem, each in any of the audio
/// formats, stored directly in the song.
/// </summary>
public static class AudioStems
{
    /// <summary>The stem names, in the order tracks are listed.</summary>
    public static IReadOnlyList<string> Stems { get; } =
    [
        "song", "guitar", "rhythm", "bass", "keys", "drums", "drums_1", "drums_2", "drums_3", "drums_4",
        "vocals", "vocals_1", "vocals_2", "crowd", "preview",
    ];

    /// <summary>The audio formats' extensions, with the dot.</summary>
    public static IReadOnlyList<string> Extensions { get; } = [".ogg", ".opus", ".mp3", ".wav"];

    /// <summary>The stem that isn't part of the song: a separate clip played in the song list.</summary>
    public const string Preview = "preview";

    private static readonly Dictionary<string, int> StemOrder = Stems
        .Select((stem, index) => KeyValuePair.Create(stem, index))
        .ToDictionary(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> ExtensionSet = new(Extensions, StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="name"/>, a path relative to the song, is a top-level file with an audio extension.</summary>
    public static bool IsAudioFile(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return !name.Contains('/') && ExtensionSet.Contains(Path.GetExtension(name));
    }

    /// <summary>Whether <paramref name="extension"/> (with the dot) is an audio format's, ignoring case.</summary>
    public static bool IsAudioExtension(string extension)
    {
        ArgumentNullException.ThrowIfNull(extension);
        return ExtensionSet.Contains(extension);
    }

    /// <summary>
    /// The stem <paramref name="name"/> is stored as, lowercased, or <see langword="null"/> when it
    /// isn't a registered audio file, e.g. <c>Guitar.OGG</c> is <c>guitar</c>.
    /// </summary>
    public static string? StemOf(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!IsAudioFile(name))
            return null;

        string stem = Path.GetFileNameWithoutExtension(name);
        return StemOrder.ContainsKey(stem) ? stem.ToLowerInvariant() : null;
    }

    /// <summary>Whether <paramref name="stem"/> is a registered stem name, ignoring case.</summary>
    public static bool IsStem(string stem)
    {
        ArgumentNullException.ThrowIfNull(stem);
        return StemOrder.ContainsKey(stem);
    }

    /// <summary>Where <paramref name="stem"/> is listed, for sorting; unknown stems go last.</summary>
    public static int OrderOf(string stem)
    {
        ArgumentNullException.ThrowIfNull(stem);
        return StemOrder.GetValueOrDefault(stem, Stems.Count);
    }
}
