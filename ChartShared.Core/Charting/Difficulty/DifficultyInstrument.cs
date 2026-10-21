using ChartShared.Core.Charts;

namespace ChartShared.Core.Charting.Difficulty;

/// <summary>
/// How the difficulty model reads a part (docs/difficulty-model.md §14): which costs its notes have, what they're
/// normalized against, and the rank curve. Each has its own <see cref="DifficultyParameters.For"/>.
/// </summary>
public enum DifficultyInstrument
{
    /// <summary>Five-fret guitar, bass, rhythm and co-op.</summary>
    FiveFret,

    /// <summary>Five-fret keys: read as five-fret, with a rank curve of its own.</summary>
    Keys,

    /// <summary>Guitar Hero Live's six frets: three white and three black, as a grid of three columns.</summary>
    SixFret,

    /// <summary>Drums as 4-lane or 5-lane drums play them: a Pro chart's cymbals read as the toms of their colour.</summary>
    Drums,

    /// <summary>Pro drums: each cymbal a pad of its own, apart from the tom of its colour.</summary>
    ProDrums,
}

/// <summary>Which <see cref="DifficultyInstrument"/> reads a part, and which reads each of its <c>diff_*</c> ratings.</summary>
public static class DifficultyInstruments
{
    /// <summary>
    /// The model <paramref name="part"/> is read with: for drums, the 4-lane view unless <paramref name="proView"/>, which
    /// reads a Pro chart's cymbals as pads of their own.
    /// </summary>
    public static DifficultyInstrument Of(ChartInstrument part, bool proView = false) => part switch
    {
        ChartInstrument.Keys => DifficultyInstrument.Keys,
        ChartInstrument.Drums => proView ? DifficultyInstrument.ProDrums : DifficultyInstrument.Drums,
        _ => ChartInstruments.KindOf(part) == ChartInstrumentKind.SixFret ? DifficultyInstrument.SixFret : DifficultyInstrument.FiveFret,
    };

    /// <summary>The model that rates <paramref name="key"/>, one of <paramref name="part"/>'s: Pro drums for <c>diff_drums_real</c>.</summary>
    public static DifficultyInstrument OfKey(ChartInstrument part, string key) =>
        Of(part, proView: part == ChartInstrument.Drums && string.Equals(key, SongIniPartKeys.ProDrumsDifficulty, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// <paramref name="part"/>'s <c>diff_*</c> keys (<see cref="SongIniPartKeys.DifficultyKeysOf"/>), each with the model
    /// that rates it.
    /// </summary>
    public static IReadOnlyList<(string Key, DifficultyInstrument Instrument)> KeysOf(ChartInstrument part, DrumsVariant? drums) =>
        [.. SongIniPartKeys.DifficultyKeysOf(part, drums).Select(k => (k, OfKey(part, k)))];

    /// <summary>Whether <paramref name="instrument"/> is one of the drums models.</summary>
    public static bool IsDrums(this DifficultyInstrument instrument) => instrument is DifficultyInstrument.Drums or DifficultyInstrument.ProDrums;
}
