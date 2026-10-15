using ChartPacker.Core.Charts;

namespace ChartPacker.Core.Charting;

/// <summary>What a new chart starts with: its timing and its parts (charter-spec C1, Timing and Parts).</summary>
/// <param name="SongName">The song's name, which the tempo track is named after.</param>
/// <param name="Resolution">Ticks per beat: one of <see cref="Resolutions"/>.</param>
/// <param name="Bpm">The starting tempo, in beats per minute.</param>
/// <param name="Numerator">The starting time signature's beats per measure.</param>
/// <param name="Denominator">The starting time signature's note value: a power of two.</param>
/// <param name="Parts">The parts to create, from <see cref="Instruments"/>.</param>
/// <param name="Drums">How the drums are charted, when <paramref name="Parts"/> has drums.</param>
public sealed record NewChartSettings(
    string SongName,
    int Resolution,
    double Bpm,
    int Numerator,
    int Denominator,
    IReadOnlySet<ChartInstrument> Parts,
    DrumsVariant Drums)
{
    /// <summary>The resolution new charts use unless the user picks another.</summary>
    public const int DefaultResolution = 480;

    /// <summary>The starting tempo new charts use unless the user enters another.</summary>
    public const double DefaultBpm = 120;

    /// <summary>The slowest starting tempo: well inside what a MIDI tempo event can hold.</summary>
    public const double MinBpm = 10;

    /// <summary>The fastest starting tempo.</summary>
    public const double MaxBpm = 1000;

    /// <summary>The most beats a measure can have.</summary>
    public const int MaxNumerator = Commands.AddTimeSignature.MaxNumerator;

    /// <summary>The resolutions at which every common snap lands on a whole tick.</summary>
    public static IReadOnlyList<int> Resolutions { get; } = [192, 480, 960];

    /// <summary>The time-signature note values a chart can use.</summary>
    public static IReadOnlyList<int> Denominators { get; } = [1, 2, 4, 8, 16, 32];

    /// <summary>The parts a new chart can have in v1, in the order they're offered.</summary>
    public static IReadOnlyList<ChartInstrument> Instruments { get; } =
    [
        ChartInstrument.Guitar, ChartInstrument.Bass, ChartInstrument.Rhythm, ChartInstrument.GuitarCoop,
        ChartInstrument.GhlGuitar, ChartInstrument.GhlBass, ChartInstrument.GhlRhythm, ChartInstrument.GhlCoop,
        ChartInstrument.Drums,
    ];
}
