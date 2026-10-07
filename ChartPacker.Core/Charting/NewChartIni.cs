using System.Globalization;
using ChartPacker.Core.Charts;
using ChartPacker.Core.Metadata;

namespace ChartPacker.Core.Charting;

/// <summary>The <c>song.ini</c> a new chart starts with (charter-spec C1, Create).</summary>
public static class NewChartIni
{
    /// <summary>
    /// The <c>[song]</c> pairs for <paramref name="request"/>: the metadata that isn't empty,
    /// <c>song_length</c>, <c>delay</c> when there's a lead-in, an unrated (<c>-1</c>) difficulty for
    /// each part, and the drum layout keys.
    /// </summary>
    /// <param name="request">What the wizard asked for.</param>
    /// <param name="songLengthMs">The song's length in milliseconds, if it's known.</param>
    /// <remarks>The keys of the parts are <see cref="SongIniPartKeys"/>'.</remarks>
    public static IReadOnlyList<KeyValuePair<string, string>> Pairs(NewChartRequest request, long? songLengthMs)
    {
        ArgumentNullException.ThrowIfNull(request);

        var pairs = new List<KeyValuePair<string, string>>();
        void Add(string key, string value) => pairs.Add(KeyValuePair.Create(key, value));
        void AddText(string key, string value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                Add(key, value.Trim());
        }

        AddText("name", request.Name);
        AddText("artist", request.Artist);
        AddText("album", request.Album);
        AddText("genre", request.Genre);
        AddText("year", request.Year);
        AddText("charter", request.Charter);
        if (songLengthMs is { } length)
            Add("song_length", length.ToString(CultureInfo.InvariantCulture));
        if (request.LeadInMs != 0)
            Add("delay", request.LeadInMs.ToString(CultureInfo.InvariantCulture));

        var chart = request.Chart;
        foreach (var instrument in NewChartSettings.Instruments.Where(chart.Parts.Contains))
        {
            foreach (var key in SongIniPartKeys.DifficultyKeysOf(instrument, chart.Drums))
                Add(key, MetadataDifficulty.UnsetValue);
        }

        if (chart.Parts.Contains(ChartInstrument.Drums))
            pairs.Add(SongIniPartKeys.NewChartDrumsPair(chart.Drums));

        return pairs;
    }
}
