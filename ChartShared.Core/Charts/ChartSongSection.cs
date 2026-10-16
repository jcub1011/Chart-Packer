using System.Globalization;

namespace ChartShared.Core.Charts;

/// <summary>
/// A <c>.chart</c>'s <c>[Song]</c> metadata, as the <c>song.ini</c> keys it stands for (charter-spec C2 item 3), so an
/// import can carry it into <c>song.ini</c> and removing <c>notes.chart</c> loses nothing.
/// </summary>
/// <remarks>
/// <c>Name</c>, <c>Artist</c>, <c>Album</c>, <c>Genre</c> and <c>Charter</c> become their own keys; <c>Year</c> becomes
/// <c>year</c>, without the <c>", "</c> Moonscraper writes before it; <c>PreviewStart</c>, in seconds, becomes
/// <c>preview_start_time</c> in milliseconds; and <c>Difficulty</c> becomes <c>diff_band</c>. <c>Offset</c> isn't among
/// them: it's the import's delay. The rest (the resolution, the audio streams, <c>MediaType</c>) belongs to the
/// <c>.chart</c> alone.
/// </remarks>
public static class ChartSongSection
{
    private static readonly (string ChartKey, string IniKey)[] TextKeys =
    [
        ("Name", "name"),
        ("Artist", "artist"),
        ("Album", "album"),
        ("Genre", "genre"),
        ("Charter", "charter"),
    ];

    /// <summary>
    /// The <c>song.ini</c> pairs <paramref name="chartBytes"/>' <c>[Song]</c> section holds, in the order above. Empty
    /// values are left out, and a key given twice keeps its first value, as the section's other keys do.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> Read(byte[] chartBytes)
    {
        ArgumentNullException.ThrowIfNull(chartBytes);
        return Read(ChartTextReader.Sections(ChartTextReader.Decode(chartBytes)));
    }

    /// <summary>As <see cref="Read(byte[])"/>, from the file's sections as <see cref="ChartTextReader.Sections"/> finds them.</summary>
    internal static IReadOnlyList<KeyValuePair<string, string>> Read(List<(string Name, List<string> Lines)> sections)
    {
        if (sections.Count < 1 || sections[0].Name != "Song")
            return [];

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in sections[0].Lines)
        {
            int at = line.IndexOf('=');
            if (at < 0)
                continue;
            string key = line[..at].Trim();
            string value = line[(at + 1)..].Trim().Trim('"').Trim();
            values.TryAdd(key, value);
        }

        var pairs = new List<KeyValuePair<string, string>>();
        void Add(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                pairs.Add(KeyValuePair.Create(key, value));
        }

        foreach (var (chartKey, iniKey) in TextKeys)
            Add(iniKey, values.GetValueOrDefault(chartKey));
        Add("year", YearOf(values.GetValueOrDefault("Year")));
        Add("preview_start_time", PreviewStartOf(values.GetValueOrDefault("PreviewStart")));
        Add("diff_band", WholeNumberOf(values.GetValueOrDefault("Difficulty")));
        return pairs;
    }

    // Moonscraper writes the year after ", ", for the games that show it after the album.
    private static string? YearOf(string? value) => value?.TrimStart(',', ' ');

    // In seconds in the .chart, and in milliseconds in song.ini. A preview at 0 is the same as none.
    private static string? PreviewStartOf(string? value)
    {
        if (SongIniValues.Real(value) is not { } seconds || !double.IsFinite(seconds) || seconds <= 0 || seconds >= 86_400)
            return null;
        return ((long)Math.Round(seconds * 1000, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture);
    }

    private static string? WholeNumberOf(string? value) =>
        int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int number) ? number.ToString(CultureInfo.InvariantCulture) : null;
}
