using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Difficulty;
using ChartShared.Core.Charts;

namespace ChartShared.Core.Generation.Neural;

/// <summary>
/// A beat's per-beat inputs before scaling, as <see cref="BeatFrame.Scalars"/> gives them and the trainer's prepared set
/// keeps them. <see cref="BeatFeatures.Write"/> scales them for the network.
/// </summary>
public readonly record struct BeatScalars(
    int BeatInMeasure,
    int GapBucket,
    bool PhraseStart,
    bool SectionStart,
    bool HasSustain,
    bool StarPower,
    bool Solo,
    int RiffRepeat,
    int OnsetCount,
    bool HasChord,
    bool MelodyTurn,
    float SecondsPerBeat);

/// <summary>
/// The network's per-beat scalar features (spec §3.3 inputs): the one encoder both the apps' inference
/// (<see cref="BiGruCrf"/>) and the trainer use, so the two can't scale them differently.
/// </summary>
public static class BeatFeatures
{
    /// <summary>How many scalars each beat has after the embeddings.</summary>
    public const int Count = 13;

    /// <summary>
    /// Writes <paramref name="beat"/>'s scalars, each roughly in [-1, 1], into <paramref name="dest"/>; the last is the
    /// source's rank (<see cref="NeuralConditioning.SourceRank"/>), 0 when the pack doesn't use it
    /// (<paramref name="useRank"/>) or the source can't be rated.
    /// </summary>
    public static void Write(in BeatScalars beat, double? sourceRank, bool useRank, Span<float> dest)
    {
        if (dest.Length < Count)
            throw new ArgumentException($"A beat has {Count} features.", nameof(dest));
        static float Flag(bool on) => on ? 1f : -1f;
        dest[0] = Math.Clamp(beat.BeatInMeasure, 0, 8) / 4f;
        dest[1] = (Math.Clamp(beat.GapBucket, 0, 7) / 7f * 2f) - 1f;
        dest[2] = Flag(beat.PhraseStart);
        dest[3] = Flag(beat.SectionStart);
        dest[4] = Flag(beat.HasSustain);
        dest[5] = Flag(beat.StarPower);
        dest[6] = Flag(beat.Solo);
        dest[7] = (Math.Clamp(beat.RiffRepeat, 0, 7) / 7f * 2f) - 1f;
        // Tempo: 120 BPM (half a second a beat) is 0, each halving or doubling of the beat half a unit.
        dest[8] = beat.SecondsPerBeat > 0 ? Math.Clamp(MathF.Log2(beat.SecondsPerBeat / 0.5f) / 2f, -1f, 1f) : 0f;
        dest[9] = (Math.Clamp(beat.OnsetCount, 0, 8) / 8f * 2f) - 1f;
        dest[10] = Flag(beat.HasChord);
        dest[11] = Flag(beat.MelodyTurn);
        dest[12] = useRank && sourceRank is { } rank ? ((float)Math.Clamp(rank, 0, 7) / 7f * 2f) - 1f : 0f;
    }
}

/// <summary>The run-level conditioning both the apps and the trainer work out the same way.</summary>
public static class NeuralConditioning
{
    /// <summary>
    /// The source difficulty's rank by the difficulty model (<see cref="DifficultyEstimator"/>), as the games read the
    /// chart with <paramref name="options"/>: continuous, 0 to 6 for charts written to be played, 7 and up for overcharts.
    /// The model is consistent where song.ini tiers follow each charter's own house rules. Null when the source can't be
    /// rated (fewer than two chords).
    /// </summary>
    public static double? SourceRank(EditableChart chart, ChartInstrument part, ChartDifficulty source, ChartReadOptions? options) =>
        SourceRanks(chart, part, options).TryGetValue(source, out double rank) ? rank : null;

    /// <summary>Each charted difficulty's <see cref="SourceRank"/>, rated in one go.</summary>
    public static IReadOnlyDictionary<ChartDifficulty, double> SourceRanks(EditableChart chart, ChartInstrument part, ChartReadOptions? options)
    {
        ArgumentNullException.ThrowIfNull(chart);
        return DifficultyEstimator.EstimateDifficulties(chart, part, options ?? ChartReadOptions.Default)
            .ToDictionary(static e => e.Key, static e => e.Value.Rating.Rank);
    }

    /// <summary>The pair's index in <paramref name="pairs"/> (<c>From-&gt;To</c>), or Expert's to the target, or -1.</summary>
    public static int PairIndex(IReadOnlyList<string> pairs, ChartDifficulty source, ChartDifficulty target)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        int exact = IndexOf(pairs, PairName(source, target));
        return exact >= 0 ? exact : IndexOf(pairs, PairName(ChartDifficulty.Expert, target));
    }

    /// <summary>A source–target pair as packs and training sets name it.</summary>
    public static string PairName(ChartDifficulty source, ChartDifficulty target) => $"{source}->{target}";

    /// <summary>The part's index in <paramref name="instruments"/>, or -1.</summary>
    public static int InstrumentIndex(IReadOnlyList<string> instruments, ChartInstrument part)
    {
        ArgumentNullException.ThrowIfNull(instruments);
        return IndexOf(instruments, part.ToString());
    }

    private static int IndexOf(IReadOnlyList<string> names, string name)
    {
        for (int i = 0; i < names.Count; i++)
        {
            if (string.Equals(names[i], name, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }
}
