using ChartShared.Core.Charts;

namespace ChartShared.Core.Charting.Authoring;

/// <summary>
/// Which of a chart's lower difficulties <see cref="DifficultyGenerator"/> fills: those of <paramref name="Targets"/>
/// below each part's highest charted difficulty, of the parts in <paramref name="Parts"/>, and, unless
/// <paramref name="Replace"/>, only those with no notes yet.
/// </summary>
/// <param name="Targets">The difficulties to fill.</param>
/// <param name="Parts">The parts to fill; null for every part.</param>
/// <param name="Replace">Whether a difficulty with notes is drafted again in place of what it has.</param>
public sealed record LowerDifficultyScope(IReadOnlySet<ChartDifficulty> Targets, IReadOnlySet<ChartInstrument>? Parts, bool Replace)
{
    /// <summary>Every missing difficulty of every part, nothing replaced: what CHR087 finds.</summary>
    public static LowerDifficultyScope Missing { get; } = new(new HashSet<ChartDifficulty>(Enum.GetValues<ChartDifficulty>()), null, false);

    /// <summary>Whether <paramref name="part"/> is filled at all: it's in <see cref="Parts"/>, and its kind can be filled.</summary>
    public bool Covers(ChartInstrument part) =>
        (Parts is null || Parts.Contains(part)) && DifficultyGenerator.CanGenerate(ChartInstruments.KindOf(part));

    /// <summary>
    /// The difficulties of <paramref name="part"/> to fill, highest first, given those with notes: each of
    /// <see cref="Targets"/> below the highest of <paramref name="charted"/>, less those charted unless
    /// <see cref="Replace"/>. None for a part not covered, or with nothing charted.
    /// </summary>
    public IReadOnlyList<ChartDifficulty> TargetsOf(ChartInstrument part, IReadOnlySet<ChartDifficulty> charted)
    {
        ArgumentNullException.ThrowIfNull(charted);
        if (!Covers(part) || charted.Count == 0)
            return [];
        var highest = charted.Max();
        return [.. Enum.GetValues<ChartDifficulty>()
            .Where(d => d < highest && Targets.Contains(d) && (Replace || !charted.Contains(d)))
            .OrderDescending()];
    }
}
