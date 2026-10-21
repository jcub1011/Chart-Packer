using ChartShared.Core.Charts;

namespace ChartShared.Core.Charting;

/// <summary>
/// The <c>song.ini</c> keys Chart Builder writes for a chart's parts (charter-spec §7.5): each part's difficulty rating,
/// and the drum layout.
/// </summary>
public static class SongIniPartKeys
{
    /// <summary>The key of Pro drums' own rating, which they have beside <c>diff_drums</c>.</summary>
    public const string ProDrumsDifficulty = "diff_drums_real";

    /// <summary>The difficulty-rating key of each part a new chart can have, as <see cref="Merging.MidiPartCatalog"/> lists them.</summary>
    private static readonly Dictionary<ChartInstrument, string> DifficultyKeys = new()
    {
        [ChartInstrument.Guitar] = "diff_guitar",
        [ChartInstrument.Bass] = "diff_bass",
        [ChartInstrument.Rhythm] = "diff_rhythm",
        [ChartInstrument.GuitarCoop] = "diff_guitar_coop",
        [ChartInstrument.Keys] = "diff_keys",
        [ChartInstrument.GhlGuitar] = "diff_guitarghl",
        [ChartInstrument.GhlBass] = "diff_bassghl",
        [ChartInstrument.GhlRhythm] = "diff_rhythm_ghl",
        [ChartInstrument.GhlCoop] = "diff_guitar_coop_ghl",
        [ChartInstrument.Drums] = "diff_drums",
    };

    /// <summary>
    /// The difficulty-rating keys of <paramref name="instrument"/>, one of <see cref="NewChartSettings.Instruments"/>:
    /// its own, and for Pro drums <see cref="ProDrumsDifficulty"/> as well.
    /// </summary>
    /// <param name="drums">How the drums are charted, for drums.</param>
    /// <exception cref="ArgumentException"><paramref name="instrument"/> isn't a part Chart Builder edits.</exception>
    public static IReadOnlyList<string> DifficultyKeysOf(ChartInstrument instrument, DrumsVariant? drums)
    {
        if (!DifficultyKeys.TryGetValue(instrument, out var key))
            throw new ArgumentException($"{instrument} isn't a part Chart Builder edits.", nameof(instrument));
        return instrument == ChartInstrument.Drums && drums == DrumsVariant.Pro ? [key, ProDrumsDifficulty] : [key];
    }

    /// <summary>
    /// The drum layout keys a new chart's <c>song.ini</c> says <paramref name="variant"/> with. The games read MIDI drums
    /// as Pro unless <c>pro_drums</c> is <c>False</c>, so 4-lane drums need it said; Pro drums say it too, as
    /// <c>pro_drums = True</c>, and 5-lane drums say <c>five_lane_drums = True</c>.
    /// </summary>
    public static KeyValuePair<string, string> NewChartDrumsPair(DrumsVariant variant) => variant switch
    {
        DrumsVariant.FourLane => KeyValuePair.Create("pro_drums", "False"),
        DrumsVariant.Pro => KeyValuePair.Create("pro_drums", "True"),
        DrumsVariant.FiveLane => KeyValuePair.Create("five_lane_drums", "True"),
        _ => throw new ArgumentOutOfRangeException(nameof(variant), variant, null),
    };
}
