using ChartShared.Core.Charts;

namespace ChartShared.Core.Scanning;

/// <summary>
/// Which audio stems a chart lane is played against: its instrument's own, or the whole song's when it
/// has none of its own. For drawing a lane over the audio it should line up with.
/// </summary>
public static class LaneStems
{
    /// <summary>The whole song, which a lane without a stem of its own is played against.</summary>
    public const string Song = "song";

    /// <summary>The vocal stems, the lead's first, each on its own.</summary>
    public static IReadOnlyList<IReadOnlyList<string>> Vocals { get; } = [["vocals"], ["vocals_1"], ["vocals_2"]];

    private static readonly IReadOnlyList<IReadOnlyList<string>> GuitarStems = [["guitar"]];
    private static readonly IReadOnlyList<IReadOnlyList<string>> BassStems = [["bass"], ["rhythm"]];
    private static readonly IReadOnlyList<IReadOnlyList<string>> RhythmStems = [["rhythm"]];
    private static readonly IReadOnlyList<IReadOnlyList<string>> KeysStems = [["keys"]];

    // The split drum stems are one kit between them (kick, snare, cymbals, toms), so they're drawn together.
    private static readonly IReadOnlyList<IReadOnlyList<string>> DrumsStems = [["drums"], ["drums_1", "drums_2", "drums_3", "drums_4"]];

    /// <summary>
    /// The stems <paramref name="instrument"/> is played against, most fitting first, in groups drawn together.
    /// Bass falls back to <c>rhythm</c>, which older charts use for it; drums to their split stems, all of them.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<string>> For(ChartInstrument instrument) => instrument switch
    {
        ChartInstrument.Guitar or ChartInstrument.GuitarCoop or ChartInstrument.GhlGuitar or ChartInstrument.GhlCoop => GuitarStems,
        ChartInstrument.Bass or ChartInstrument.GhlBass => BassStems,
        ChartInstrument.Rhythm or ChartInstrument.GhlRhythm => RhythmStems,
        ChartInstrument.Keys => KeysStems,
        ChartInstrument.Drums => DrumsStems,
        _ => [],
    };

    /// <summary>
    /// The stems of <paramref name="available"/> to draw a lane over, together: <paramref name="chosen"/> when the
    /// song has it, else those it has of the first of <paramref name="preferred"/>'s groups it has any of, else
    /// <see cref="Song"/>, else none. The preview is never picked, as it isn't part of the song.
    /// </summary>
    /// <param name="preferred">Groups of stems, most fitting first, as <see cref="For"/> gives them.</param>
    /// <param name="available">The song's stems, lowercased, as <see cref="SongTrack.Stem"/>.</param>
    /// <param name="chosen">A stem picked in place of the lane's own, if one was.</param>
    public static IReadOnlyList<string> Pick(IReadOnlyList<IReadOnlyList<string>> preferred, IEnumerable<string> available, string? chosen)
    {
        ArgumentNullException.ThrowIfNull(preferred);
        ArgumentNullException.ThrowIfNull(available);

        var stems = available.Where(s => !s.Equals(AudioStems.Preview, StringComparison.OrdinalIgnoreCase)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (chosen is not null && stems.Contains(chosen))
            return [chosen];

        foreach (var group in preferred)
        {
            List<string> present = [.. group.Where(stems.Contains)];
            if (present.Count > 0)
                return present;
        }

        return stems.Contains(Song) ? [Song] : [];
    }
}
