using ChartShared.Core.Charts;
using ChartShared.Core.Merging;

namespace ChartShared.Core.Charting;

/// <summary>Maps from a chart's ticks under one tempo map to its ticks under another, for re-ticking (charter-spec C4).</summary>
public static class TickMaps
{
    /// <summary>
    /// The tick of <paramref name="to"/> at the same chart time as each tick of <paramref name="from"/>, to the nearest
    /// tick (halves away from zero), for a tempo change that keeps times (charter-spec §4.2, C4 item 3). Tick for tick
    /// when the maps are the same.
    /// </summary>
    /// <remarks>
    /// <para>It's <see cref="MidiMerger.Retime"/> with the same delay on both sides: audio time is chart time plus the
    /// delay, and a tempo edit doesn't change the delay (§4.4.1), so keeping chart time keeps audio time.</para>
    /// <para>It never decreases: a later tick never maps before an earlier one, though ticks the new map squeezes
    /// together can map to one. A tick never maps before 0.</para>
    /// <para>Keeping ticks, the default, doesn't re-tick at all: the markers change and every item stays at its tick, so
    /// nothing is mapped and the passthrough tracks keep their bytes.</para>
    /// </remarks>
    public static Func<long, long> KeepTimes(TempoMap from, TempoMap to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        return MidiMerger.MapOf(from, TimeSpan.Zero, to, TimeSpan.Zero);
    }

    /// <summary>
    /// The ticks of a stretch or squeeze of the ticks from <paramref name="from"/> to <paramref name="to"/> onto those from
    /// <paramref name="from"/> to <paramref name="newTo"/> (charter-spec C15 item 4): ticks before it stay, ticks inside it
    /// are scaled, to the nearest tick, and ticks after it move by the change in its length. It never decreases.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The span, or the one it becomes, has no length.</exception>
    public static Func<long, long> Stretch(long from, long to, long newTo)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(from);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(to, from);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(newTo, from);
        long length = to - from, newLength = newTo - from;
        return tick =>
        {
            if (tick <= from)
                return tick;
            if (tick >= to)
                return tick + (newTo - to);
            return from + (long)Math.Round((double)(tick - from) * newLength / length, MidpointRounding.AwayFromZero);
        };
    }
}
