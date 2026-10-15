using ChartPacker.Core.Charts;

namespace ChartPacker.Core.Charting;

/// <summary>What a <see cref="Phrase"/> marks.</summary>
public enum PhraseKind
{
    /// <summary>Star power (pitch 116).</summary>
    StarPower,

    /// <summary>A solo (pitch 103).</summary>
    Solo,

    /// <summary>A drum fill, or star-power activation lane (pitches 120 to 124 on drums).</summary>
    DrumFill,

    /// <summary>A drum roll, on one lane (pitch 126).</summary>
    Roll,

    /// <summary>A drum trill, between two lanes (pitch 127).</summary>
    Trill,

    /// <summary>A big rock ending (pitches 120 to 124 held together; on drums, after <c>[coda]</c>).</summary>
    BigRockEnding,
}

/// <summary>A phrase of a part, which every difficulty shares (charter-spec §4.1).</summary>
/// <param name="Tick">Where it starts, in chart ticks.</param>
/// <param name="Length">How long it lasts, in ticks, exactly as the MIDI marker is held.</param>
/// <param name="LowestDifficulty">
/// For a roll or trill only, the lowest difficulty it reaches from Expert: <see cref="ChartDifficulty.Expert"/> when it
/// isn't extended down. Null for every other kind.
/// </param>
public readonly record struct Phrase(PhraseKind Kind, long Tick, long Length, ChartDifficulty? LowestDifficulty = null)
{
    /// <summary>
    /// The tick after the last one it covers: it covers from its start to the tick before its end, or its start alone
    /// when it has no length, as the games read a marker.
    /// </summary>
    public long CoverEnd => Tick + Math.Max(1, Length);

    /// <summary>Whether it covers <paramref name="tick"/>, as the games read it (see <see cref="CoverEnd"/>).</summary>
    public bool Covers(long tick) => tick >= Tick && tick < CoverEnd;

    /// <summary>Whether it and <paramref name="other"/> cover a tick in common.</summary>
    public bool Overlaps(Phrase other) => Tick < other.CoverEnd && other.Tick < CoverEnd;

    /// <summary>Whether a phrase of <paramref name="kind"/> has a <see cref="LowestDifficulty"/>.</summary>
    public static bool HasLowestDifficulty(PhraseKind kind) => kind is PhraseKind.Roll or PhraseKind.Trill;

    /// <summary>The order of a part's phrases: by tick, then kind, length and lowest difficulty.</summary>
    internal static int Order(Phrase a, Phrase b)
    {
        int c = a.Tick.CompareTo(b.Tick);
        if (c == 0)
            c = a.Kind.CompareTo(b.Kind);
        if (c == 0)
            c = a.Length.CompareTo(b.Length);
        return c != 0 ? c : Nullable.Compare(a.LowestDifficulty, b.LowestDifficulty);
    }
}
