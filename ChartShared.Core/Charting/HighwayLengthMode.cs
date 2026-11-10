namespace ChartShared.Core.Charting;

/// <summary>How a highway's length, the stretch of song it shows ahead of its strike line, is chosen.</summary>
public enum HighwayLengthMode
{
    /// <summary>
    /// By a look-ahead in seconds (<see cref="HighwayLookAhead"/>): that much song is stretched across the highway, so
    /// resizing it changes the distance between notes.
    /// </summary>
    LookAhead,

    /// <summary>
    /// By pixels per second (<see cref="HighwayPixelsPerSecond"/>): a pixel is always the same stretch of song, so the
    /// distance between notes stays put when the highway is resized, and a taller one shows further ahead.
    /// </summary>
    PixelsPerSecond,
}
