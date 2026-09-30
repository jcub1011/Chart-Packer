using ChartPacker.Core.Charts;

namespace ChartPacker.Core.Scanning;

/// <summary>
/// Which audio stem a chart lane is played against: its instrument's own, or the whole song's when it
/// has none of its own. For drawing a lane over the audio it should line up with.
/// </summary>
public static class LaneStems
{
    /// <summary>The whole song, which a lane without a stem of its own is played against.</summary>
    public const string Song = "song";

    /// <summary>The vocal stems, the lead's first.</summary>
    public static IReadOnlyList<string> Vocals { get; } = ["vocals", "vocals_1", "vocals_2"];

    private static readonly string[] GuitarStems = ["guitar"];
    private static readonly string[] BassStems = ["bass", "rhythm"];
    private static readonly string[] RhythmStems = ["rhythm"];
    private static readonly string[] KeysStems = ["keys"];
    private static readonly string[] DrumsStems = ["drums", "drums_1", "drums_2", "drums_3", "drums_4"];

    /// <summary>
    /// The stems <paramref name="instrument"/> is played against, most fitting first. Bass falls back to
    /// <c>rhythm</c>, which older charts use for it; drums to their split stems, the kick's first.
    /// </summary>
    public static IReadOnlyList<string> For(ChartInstrument instrument) => instrument switch
    {
        ChartInstrument.Guitar or ChartInstrument.GuitarCoop or ChartInstrument.GhlGuitar => GuitarStems,
        ChartInstrument.Bass or ChartInstrument.GhlBass => BassStems,
        ChartInstrument.Rhythm => RhythmStems,
        ChartInstrument.Keys => KeysStems,
        ChartInstrument.Drums => DrumsStems,
        _ => [],
    };

    /// <summary>
    /// The stem of <paramref name="available"/> to draw a lane over: <paramref name="chosen"/> when the
    /// song has it, else the first of <paramref name="preferred"/> it has, else <see cref="Song"/>, else
    /// <see langword="null"/>. The preview is never picked, as it isn't part of the song.
    /// </summary>
    /// <param name="available">The song's stems, lowercased, as <see cref="SongTrack.Stem"/>.</param>
    /// <param name="chosen">A stem picked in place of the lane's own, if one was.</param>
    public static string? Pick(IReadOnlyList<string> preferred, IEnumerable<string> available, string? chosen)
    {
        ArgumentNullException.ThrowIfNull(preferred);
        ArgumentNullException.ThrowIfNull(available);

        var stems = available.Where(s => !s.Equals(AudioStems.Preview, StringComparison.OrdinalIgnoreCase)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (chosen is not null && stems.Contains(chosen))
            return chosen;

        return preferred.FirstOrDefault(stems.Contains) ?? (stems.Contains(Song) ? Song : null);
    }
}
