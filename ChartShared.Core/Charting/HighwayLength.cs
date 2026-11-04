namespace ChartShared.Core.Charting;

/// <summary>
/// How long a highway is, as it's set: a <see cref="HighwayLookAhead"/> or a <see cref="HighwayPixelsPerSecond"/>, whichever
/// <paramref name="Mode"/> says is in use. Both are kept, so the one not in use isn't lost when the mode changes.
/// </summary>
/// <param name="Mode">Which of the two the highway is sized by.</param>
/// <param name="LookAhead">Seconds of song from the strike line to the far end, in <see cref="HighwayLengthMode.LookAhead"/>.</param>
/// <param name="PixelsPerSecond">Pixels a second of song takes up, in <see cref="HighwayLengthMode.PixelsPerSecond"/>.</param>
public readonly record struct HighwayLength(HighwayLengthMode Mode, double LookAhead, double PixelsPerSecond)
{
    /// <summary>Whether the highway is sized by pixels per second, so its look-ahead follows its height.</summary>
    public bool IsPixelsPerSecond => Mode == HighwayLengthMode.PixelsPerSecond;

    /// <summary>
    /// The seconds of song the highway shows from its strike line to its far end, when that's <paramref name="pixelsAhead"/>
    /// long: <see cref="LookAhead"/> as it is, or what <see cref="PixelsPerSecond"/> makes of the pixels.
    /// </summary>
    public double LookAheadFor(double pixelsAhead) =>
        IsPixelsPerSecond ? HighwayPixelsPerSecond.LookAheadFor(pixelsAhead, PixelsPerSecond) : LookAhead;
}
