using ChartShared.Core.Charts;

namespace ChartShared.Core.Charting.Editing;

/// <summary>
/// The grid the editor snaps to (charter-spec C4 item 4): steps of a whole note divided by <see cref="Division"/>,
/// counted from the time signature in force, so the grid starts again on each one. A step that doesn't land on a whole
/// tick (a custom snap, or a chart at a resolution other than 192, 480 or 960) is put on the nearest one.
/// </summary>
/// <param name="Division">The steps in a whole note: 16 for sixteenths, 12 for eighth-note triplets.</param>
/// <param name="IsOn">
/// Whether notes snap to it. Switched off, notes go to the nearest tick, but the cursor keys still step by the grid.
/// </param>
public readonly record struct SnapGrid(int Division, bool IsOn = true)
{
    /// <summary>The finest grid that can be set: a 1/768 note.</summary>
    public const int MaxDivision = 768;

    /// <summary>The common snaps that <c>[</c> and <c>]</c> step through, coarsest first (1/1 to 1/192, triplets included).</summary>
    public static IReadOnlyList<int> CommonDivisions { get; } = [1, 2, 3, 4, 6, 8, 12, 16, 24, 32, 48, 64, 96, 128, 192];

    /// <summary>1/16, the default snap (charter-spec §10).</summary>
    public static SnapGrid Default { get; } = new(16);

    /// <summary>A grid of <paramref name="division"/> steps per whole note.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="division"/> isn't from 1 to <see cref="MaxDivision"/>.</exception>
    public static SnapGrid Of(int division, bool isOn = true)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(division, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(division, MaxDivision);
        return new SnapGrid(division, isOn);
    }

    /// <summary>Whether <see cref="Division"/> is one of <see cref="CommonDivisions"/>, rather than a custom snap.</summary>
    public bool IsCommon => CommonDivisions.Contains(Division);

    /// <summary>The next coarser common snap, or this one when it's the coarsest.</summary>
    public SnapGrid Coarser()
    {
        int division = Division;
        int coarser = CommonDivisions.LastOrDefault(d => d < division);
        return coarser == 0 ? this : this with { Division = coarser };
    }

    /// <summary>The next finer common snap, or this one when it's the finest.</summary>
    public SnapGrid Finer()
    {
        int division = Division;
        int finer = CommonDivisions.FirstOrDefault(d => d > division);
        return finer == 0 ? this : this with { Division = finer };
    }

    /// <summary>A step of the grid, in ticks, at <paramref name="resolution"/> ticks per beat.</summary>
    public double StepTicks(int resolution) => resolution * 4.0 / Division;

    /// <summary>
    /// The tick a note at <paramref name="tick"/> goes to: the nearest step of the grid, or the nearest tick when the grid
    /// is off. Never before the start.
    /// </summary>
    public long Nearest(double tick, TempoMap map)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (tick <= 0)
            return 0;
        if (!IsOn)
            return Round(tick);

        var (origin, end) = SegmentAt(map, (long)Math.Floor(tick));
        double step = StepTicks(map.Resolution);
        long snapped = Round(origin + (Math.Round((tick - origin) / step, MidpointRounding.AwayFromZero) * step));

        // A grid that doesn't fit the signature's measures ends at the next signature, which starts its own.
        return Math.Min(snapped, end);
    }

    /// <summary>
    /// The tick <paramref name="steps"/> grid steps on from <paramref name="tick"/>, or back when negative. A tick between
    /// two steps goes to the next (or the one before) first. Never before the start.
    /// </summary>
    public long Step(long tick, int steps, TempoMap map)
    {
        ArgumentNullException.ThrowIfNull(map);
        for (int i = 0; i < Math.Abs(steps); i++)
            tick = steps > 0 ? Next(tick, map) : Previous(tick, map);
        return tick;
    }

    /// <summary>
    /// The tick of the measure line <paramref name="measures"/> on from <paramref name="tick"/>, or back when negative. A
    /// tick inside a measure goes to the next measure line (or its own measure's) first.
    /// </summary>
    public static long StepMeasures(long tick, int measures, TempoMap map)
    {
        ArgumentNullException.ThrowIfNull(map);
        for (int i = 0; i < Math.Abs(measures); i++)
        {
            if (measures > 0)
            {
                // A measure is at most 255 whole notes long.
                long limit = tick + (map.Resolution * 4L * 256);
                tick = map.BeatLines(tick + 1, limit).FirstOrDefault(b => b.IsMeasure, new BeatLine(limit, true)).Tick;
            }
            else
            {
                if (tick <= 0)
                    return 0;
                long from = Math.Max(0, tick - (map.Resolution * 4L * 256));
                tick = map.BeatLines(from, tick - 1).LastOrDefault(b => b.IsMeasure, new BeatLine(0, true)).Tick;
            }
        }

        return tick;
    }

    private long Next(long tick, TempoMap map)
    {
        var (origin, end) = SegmentAt(map, tick);
        double step = StepTicks(map.Resolution);
        double k = Math.Floor(((tick - origin) / step) + 1e-9) + 1;
        long next = Round(origin + (k * step));

        // A step finer than a tick can round back onto the tick it left.
        while (next <= tick)
            next = Round(origin + (++k * step));
        return Math.Min(next, end);
    }

    private long Previous(long tick, TempoMap map)
    {
        if (tick <= 0)
            return 0;
        var (origin, _) = SegmentAt(map, tick - 1);
        double step = StepTicks(map.Resolution);
        double k = Math.Ceiling(((tick - origin) / step) - 1e-9) - 1;
        long previous = Round(origin + (k * step));
        while (previous >= tick && k > 0)
            previous = Round(origin + (--k * step));
        return Math.Max(Math.Min(previous, tick - 1), origin);
    }

    /// <summary>The tick of the time signature in force at <paramref name="tick"/>, and of the next one (or the end of time).</summary>
    private static (long Origin, long End) SegmentAt(TempoMap map, long tick)
    {
        var signatures = map.TimeSignatures;
        int lo = 0, hi = signatures.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (signatures[mid].Tick <= tick)
                lo = mid;
            else
                hi = mid - 1;
        }

        return (signatures[lo].Tick, lo + 1 < signatures.Count ? signatures[lo + 1].Tick : long.MaxValue);
    }

    private static long Round(double tick) => tick <= 0 ? 0 : (long)Math.Round(tick, MidpointRounding.AwayFromZero);
}
