using ChartPacker.Core.Charts;
using ChartPacker.Core.Merging;

namespace ChartPacker.Core.Charting;

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
}
