namespace ChartPacker.Core.Scanning;

/// <summary>
/// A song's chart: <c>notes.chart</c> or <c>notes.mid</c>, stored directly in the song
/// (sng-spec §8). A song normally has one of them; a song with neither can't be played.
/// </summary>
public static class ChartFiles
{
    /// <summary>The <c>.chart</c> format's stored name.</summary>
    public const string ChartName = "notes.chart";

    /// <summary>The MIDI format's stored name.</summary>
    public const string MidiName = "notes.mid";

    /// <summary>The extensions a chart can be chosen from, with the dot.</summary>
    public static IReadOnlyList<string> Extensions { get; } = [".chart", ".mid", ".midi"];

    /// <summary>Whether <paramref name="name"/>, a path relative to the song, is a chart's stored name, ignoring case. Names with a folder never are.</summary>
    public static bool IsChartName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return name.Equals(ChartName, StringComparison.OrdinalIgnoreCase) || name.Equals(MidiName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The name to store the file at <paramref name="sourcePath"/> under: <c>notes.chart</c> for a
    /// <c>.chart</c>, <c>notes.mid</c> for a <c>.mid</c> or <c>.midi</c>, ignoring case; otherwise
    /// <see langword="null"/>.
    /// </summary>
    public static string? StoredNameFor(string sourcePath)
    {
        ArgumentNullException.ThrowIfNull(sourcePath);
        string extension = Path.GetExtension(sourcePath);
        if (extension.Equals(".chart", StringComparison.OrdinalIgnoreCase))
            return ChartName;
        if (extension.Equals(".mid", StringComparison.OrdinalIgnoreCase) || extension.Equals(".midi", StringComparison.OrdinalIgnoreCase))
            return MidiName;
        return null;
    }

    /// <summary>The chart files among <paramref name="storedNames"/>, in the order given.</summary>
    public static IReadOnlyList<string> FilesOf(IEnumerable<string> storedNames)
    {
        ArgumentNullException.ThrowIfNull(storedNames);
        return [.. storedNames.Where(IsChartName)];
    }
}
