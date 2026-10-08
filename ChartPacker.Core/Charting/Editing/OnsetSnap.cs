using ChartPacker.Core.Charting.Analysis;
using ChartPacker.Core.Charts;

namespace ChartPacker.Core.Charting.Editing;

/// <summary>Where the onsets found in a lane's audio are, for snapping and nudging notes to them (charter-spec C14).</summary>
public interface IOnsetSource
{
    /// <summary>
    /// The onsets of the audio <paramref name="key"/>'s lane is drawn over, at its sensitivity, in audio seconds,
    /// ascending; every drum band's together. Empty until they're found.
    /// </summary>
    IReadOnlyList<double> OnsetsFor(ChartTrackKey key);
}

/// <summary>
/// Snap to onset (charter-spec C14 item 2): a note placed or moved goes to the nearest onset within half the grid step,
/// and to the grid when none is that near.
/// </summary>
public static class OnsetSnap
{
    /// <summary>How far from <paramref name="tick"/> an onset may be to snap to, in seconds: half of <paramref name="grid"/>'s step there.</summary>
    /// <remarks>With snap off, it's still half the snap's step, so the reach stays what the toolbar shows.</remarks>
    public static double WindowSeconds(double tick, SnapGrid grid, TempoMap map)
    {
        ArgumentNullException.ThrowIfNull(map);
        double from = Math.Max(0, tick);
        double half = grid.StepTicks(map.Resolution) / 2;
        return Math.Abs(map.SecondsAt(from + half) - map.SecondsAt(from));
    }

    /// <summary>
    /// The tick of the onset nearest <paramref name="tick"/> within <see cref="WindowSeconds"/>, rounded to the nearest
    /// tick and never before the start; <see langword="null"/> when there's none that near.
    /// </summary>
    /// <param name="onsets">Audio seconds, ascending.</param>
    /// <param name="delaySeconds">The song's delay: audio time is chart time plus this.</param>
    public static long? Nearest(double tick, IReadOnlyList<double> onsets, TempoMap map, double delaySeconds, SnapGrid grid)
    {
        ArgumentNullException.ThrowIfNull(onsets);
        ArgumentNullException.ThrowIfNull(map);
        double seconds = map.SecondsAt(Math.Max(0, tick)) + delaySeconds;
        int index = BeatMath.NearestIndex(onsets, seconds, WindowSeconds(tick, grid, map));
        return index < 0 ? null : Math.Max(0, (long)Math.Round(map.TickAt(onsets[index] - delaySeconds)));
    }
}
