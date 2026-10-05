using System.Globalization;
using ChartPacker.Core.Charts;
using ChartPacker.Core.Metadata;

namespace ChartPacker.Core.Charting;

/// <summary>The <c>song.ini</c> a new chart starts with (charter-spec C1, Create).</summary>
public static class NewChartIni
{
    /// <summary>The difficulty-rating key of each part a new chart can have (charter-spec §7.5).</summary>
    private static readonly Dictionary<ChartInstrument, string> DifficultyKeys = new()
    {
        [ChartInstrument.Guitar] = "diff_guitar",
        [ChartInstrument.Bass] = "diff_bass",
        [ChartInstrument.Rhythm] = "diff_rhythm",
        [ChartInstrument.GuitarCoop] = "diff_guitar_coop",
        [ChartInstrument.GhlGuitar] = "diff_guitarghl",
        [ChartInstrument.GhlBass] = "diff_bassghl",
        [ChartInstrument.GhlRhythm] = "diff_rhythm_ghl",
        [ChartInstrument.GhlCoop] = "diff_guitar_coop_ghl",
        [ChartInstrument.Drums] = "diff_drums",
    };

    /// <summary>
    /// The <c>[song]</c> pairs for <paramref name="request"/>: the metadata that isn't empty,
    /// <c>song_length</c>, <c>delay</c> when there's a lead-in, an unrated (<c>-1</c>) difficulty for
    /// each part, and the drum layout keys.
    /// </summary>
    /// <param name="request">What the wizard asked for.</param>
    /// <param name="songLengthMs">The song's length in milliseconds, if it's known.</param>
    /// <remarks>
    /// The games read MIDI drums as pro unless <c>pro_drums</c> is <c>False</c>, so 4-lane drums
    /// need it said; pro drums say it too, as <c>pro_drums = True</c>, and five-lane drums say
    /// <c>five_lane_drums = True</c>.
    /// </remarks>
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
            Add(DifficultyKeys[instrument], MetadataDifficulty.UnsetValue);
            if (instrument == ChartInstrument.Drums && chart.Drums == DrumsVariant.Pro)
                Add("diff_drums_real", MetadataDifficulty.UnsetValue);
        }

        if (chart.Parts.Contains(ChartInstrument.Drums))
        {
            switch (chart.Drums)
            {
                case DrumsVariant.FourLane:
                    Add("pro_drums", "False");
                    break;
                case DrumsVariant.Pro:
                    Add("pro_drums", "True");
                    break;
                case DrumsVariant.FiveLane:
                    Add("five_lane_drums", "True");
                    break;
            }
        }

        return pairs;
    }
}
