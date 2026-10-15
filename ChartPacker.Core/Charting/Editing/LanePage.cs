namespace ChartPacker.Core.Charting.Editing;

/// <summary>
/// A stretch of a lane drawn once, reaching past the screen's edges, and then moved along the time axis as the view moves,
/// rather than drawn again every frame the song plays. It does until the view leaves it, or the lane is laid out
/// differently: another size, look-ahead or set of columns.
/// </summary>
/// <param name="Screen">The lane on screen when the page was drawn.</param>
/// <param name="Page">The page: <paramref name="Screen"/> widened past the screen's edges, every second drawn where it was on screen.</param>
public sealed record LanePage(LaneProjection Screen, LaneProjection Page)
{
    /// <summary>
    /// A page of the lane <paramref name="screen"/> shows, reaching <paramref name="screensBefore"/> screens back from its
    /// start and <paramref name="screensAfter"/> beyond its end; both cut back alike, if need be, so the page is no longer
    /// than <paramref name="maxLength"/> pixels along the time axis.
    /// </summary>
    public static LanePage Of(LaneProjection screen, double screensBefore, double screensAfter, double maxLength = double.PositiveInfinity)
    {
        ArgumentNullException.ThrowIfNull(screen);
        ArgumentOutOfRangeException.ThrowIfNegative(screensBefore);
        ArgumentOutOfRangeException.ThrowIfNegative(screensAfter);
        double reach = screensBefore + screensAfter;
        if (reach > 0 && screen.TimeLength * (1 + reach) > maxLength)
        {
            double share = Math.Max(0, ((maxLength / screen.TimeLength) - 1) / reach);
            screensBefore *= share;
            screensAfter *= share;
        }

        return new LanePage(screen, screen.Widened(screen.SecondsOnScreen * screensBefore, screen.SecondsOnScreen * screensAfter));
    }

    /// <summary>How far the page reaches past the screen, both ways together, in pixels.</summary>
    public double Reach => Page.TimeLength - Screen.TimeLength;

    /// <summary>
    /// Whether the page still serves the lane as <paramref name="now"/> shows it: laid out the same, with the same columns,
    /// and all it shows within the page with <paramref name="margin"/> pixels to spare either side, for what's drawn around
    /// a moment rather than at it, such as a gem or a measure's number.
    /// </summary>
    public bool Fits(LaneProjection now, double margin = 0)
    {
        ArgumentNullException.ThrowIfNull(now);
        double spare = margin / now.PixelsPerSecond;
        return now.View == Screen.View && now.TimeFrom == Screen.TimeFrom && now.TimeLength == Screen.TimeLength
            && now.LaneFrom == Screen.LaneFrom && now.LaneSize == Screen.LaneSize && now.SecondsOnScreen == Screen.SecondsOnScreen
            && SameSlots(now.Slots, Screen.Slots)
            && now.Start - spare >= Page.Start && now.End + spare <= Page.End;
    }

    /// <summary>
    /// How far along the time axis the page has to be moved, in pixels, to show what <paramref name="now"/> shows: the
    /// same distance for every second, as long as it <see cref="Fits"/>.
    /// </summary>
    public double ShiftOf(LaneProjection now)
    {
        ArgumentNullException.ThrowIfNull(now);
        return now.AlongOf(Page.Start) - Page.AlongOf(Page.Start);
    }

    // Slots are made again each time they're asked for, so they're the same when their lanes are.
    private static bool SameSlots(LaneSlots a, LaneSlots b) =>
        a.Across == b.Across && a.Slots.SequenceEqual(b.Slots)
        && (a.Shared is null ? b.Shared is null : b.Shared is not null && a.Shared.SequenceEqual(b.Shared));
}
