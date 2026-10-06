using ChartPacker.Core.Charts;

namespace ChartPacker.Core.Validation;

/// <summary>
/// A place in a chart that a <see cref="Finding"/> is about: one part, optionally one difficulty,
/// and a range of ticks.
/// </summary>
/// <remarks>
/// The tick range is inclusive at both ends: <paramref name="Tick"/> is the first tick it covers and
/// <paramref name="EndTick"/> the last, so a single point has both equal. <paramref name="EndTick"/>
/// must be no less than <paramref name="Tick"/>; the type does not check it.
/// </remarks>
/// <param name="Part">The instrument the finding is about.</param>
/// <param name="Difficulty">
/// The difficulty the finding is about, or <see langword="null"/> for something every difficulty
/// shares, such as star power or tom markers.
/// </param>
/// <param name="Tick">The first tick the finding covers.</param>
/// <param name="EndTick">The last tick the finding covers, equal to <paramref name="Tick"/> for a single point.</param>
public sealed record ChartLocation(ChartInstrument Part, ChartDifficulty? Difficulty, long Tick, long EndTick)
{
    /// <summary>Whether <paramref name="tick"/> falls inside this location's tick range.</summary>
    public bool Covers(long tick) => tick >= Tick && tick <= EndTick;

    /// <summary>
    /// Whether this location and <paramref name="other"/> share any of the chart: the same part,
    /// the same difficulty (a <see langword="null"/> difficulty matches any), and tick ranges that
    /// meet, counting ranges that touch at one tick.
    /// </summary>
    public bool Overlaps(ChartLocation other) =>
        Part == other.Part
        && (Difficulty is null || other.Difficulty is null || Difficulty == other.Difficulty)
        && Tick <= other.EndTick
        && other.Tick <= EndTick;
}
