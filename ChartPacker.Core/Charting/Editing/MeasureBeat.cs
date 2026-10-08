using ChartPacker.Core.Charts;

namespace ChartPacker.Core.Charting.Editing;

/// <summary>
/// Where a tick is in the song's measures, as Properties, Findings and the Sections tab show it ("m41 · beat 3",
/// charter-spec C3 item 4): its measure and its beat in it, both counted from 1. A measure starts at each time signature,
/// and every signature's number of beats after it, as <see cref="TempoMap.BeatLines"/> draws them.
/// </summary>
/// <param name="Measure">The measure, from 1.</param>
/// <param name="Beat">The beat in the measure, from 1, with a fraction between beats.</param>
public readonly record struct MeasureBeat(long Measure, double Beat)
{
    /// <summary>
    /// Where <paramref name="tick"/> is. It's counted from the time signatures, not by walking the beat lines, so a tick far
    /// into the song costs no more than one near its start.
    /// </summary>
    public static MeasureBeat At(TempoMap map, long tick)
    {
        ArgumentNullException.ThrowIfNull(map);
        var signatures = map.TimeSignatures;
        long measures = 0;
        long measureStart = 0;
        TimeSignature? at = null;
        for (int i = 0; i < signatures.Count; i++)
        {
            var signature = signatures[i];
            if (signature.Tick > tick)
                break;
            long end = i + 1 < signatures.Count ? signatures[i + 1].Tick : long.MaxValue;
            if (end <= signature.Tick)
                continue;

            // The measure lines from this signature up to the tick, or up to the next signature.
            long beat = Math.Max(1, map.Resolution * 4L / signature.Denominator);
            long measure = beat * Math.Max(1, signature.Numerator);
            long last = Math.Min(tick, end - 1) - signature.Tick;
            long lines = (last / measure) + 1;
            measures += lines;
            measureStart = signature.Tick + ((lines - 1) * measure);
            at = signature;
        }

        if (at is not { } current)
            return new MeasureBeat(1, 1);
        double beatTicks = map.Resolution * 4.0 / current.Denominator;
        return new MeasureBeat(Math.Max(1, measures), 1 + ((tick - measureStart) / beatTicks));
    }
}
