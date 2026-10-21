using ChartShared.Core.Charts;

namespace ChartShared.Core.Charting.Authoring;

/// <summary>
/// Where ticks fall for Generate star power (<see cref="StarPowerGenerator"/>): in measures, as the games drain the meter,
/// and in the part's playing, song time and measures with its rests left out.
/// </summary>
/// <remarks>
/// A measure position is the measure's index from the start, with the fraction of it gone: 2.5 is halfway through the
/// third measure, by the time signatures in force. A rest is a gap from where a chord's notes are let go to the next
/// chord of <see cref="StarPowerParameters.RestMeasures"/> measures or <see cref="StarPowerParameters.RestSeconds"/>
/// seconds or more.
/// </remarks>
internal sealed class StarPowerTimeline
{
    private readonly TempoMap _map;

    // The rests, in ticks, in order, and in seconds and measures.
    private readonly (double From, double To)[] _restSeconds;
    private readonly (double From, double To)[] _restMeasures;

    public StarPowerTimeline(TempoMap map, IReadOnlyList<(long Tick, EditableNote[] Notes)> chords, StarPowerParameters p)
    {
        _map = map;
        long last = 0;
        foreach (var chord in chords)
            last = Math.Max(last, EndOf(chord));

        // Lines well past the last note, so any measure the phrases or the meter reach has its end.
        long horizon = last + (64L * 4 * map.Resolution);
        MeasureLines = [.. map.BeatLines(0, horizon).Where(static b => b.IsMeasure).Select(static b => b.Tick)];

        var seconds = new List<(double, double)>();
        var measures = new List<(double, double)>();
        for (int i = 1; i < chords.Count; i++)
        {
            long from = EndOf(chords[i - 1]);
            long to = chords[i].Tick;
            if (to <= from)
                continue;
            double fromSeconds = map.SecondsAt(from), toSeconds = map.SecondsAt(to);
            double fromMeasure = MeasureAt(from), toMeasure = MeasureAt(to);
            if (toMeasure - fromMeasure >= p.RestMeasures || toSeconds - fromSeconds >= p.RestSeconds)
            {
                seconds.Add((fromSeconds, toSeconds));
                measures.Add((fromMeasure, toMeasure));
            }
        }

        _restSeconds = [.. seconds];
        _restMeasures = [.. measures];
    }

    /// <summary>The ticks of the measure lines, from the start to well past the last note.</summary>
    public long[] MeasureLines { get; }

    /// <summary>The tick after the last one <paramref name="chord"/>'s notes are held, a tick past it for notes without a sustain.</summary>
    public static long EndOf((long Tick, EditableNote[] Notes) chord)
    {
        long end = chord.Tick + 1;
        foreach (var note in chord.Notes)
            end = Math.Max(end, note.Tick + Math.Max(1, note.Length));
        return end;
    }

    /// <summary>The song time at <paramref name="tick"/>, in seconds.</summary>
    public double SecondsAt(long tick) => _map.SecondsAt(tick);

    /// <summary>The index of the measure <paramref name="tick"/> is in.</summary>
    public int MeasureIndexAt(long tick)
    {
        int index = Array.BinarySearch(MeasureLines, tick);
        return Math.Clamp(index >= 0 ? index : ~index - 1, 0, MeasureLines.Length - 1);
    }

    /// <summary>The measure position of <paramref name="tick"/>.</summary>
    public double MeasureAt(long tick)
    {
        int m = MeasureIndexAt(tick);
        long length = m + 1 < MeasureLines.Length ? MeasureLines[m + 1] - MeasureLines[m] : MeasureLines[m] - (m > 0 ? MeasureLines[m - 1] : 0);
        return m + ((double)(tick - MeasureLines[m]) / Math.Max(1, length));
    }

    /// <summary>The tick at the measure position <paramref name="measure"/>, rounded up.</summary>
    public long TickAt(double measure)
    {
        int m = Math.Clamp((int)Math.Floor(measure), 0, MeasureLines.Length - 2);
        long length = MeasureLines[m + 1] - MeasureLines[m];
        return MeasureLines[m] + (long)Math.Ceiling((measure - m) * length - 1e-9);
    }

    /// <summary>The song time at <paramref name="tick"/> less the rests before it, in seconds: the part's playing up to it.</summary>
    public double ActiveSecondsAt(long tick) => Active(_map.SecondsAt(tick), _restSeconds);

    /// <summary>The measure position of <paramref name="tick"/> less the rests before it: the part's playing up to it, in measures.</summary>
    public double ActiveMeasureAt(long tick) => Active(MeasureAt(tick), _restMeasures);

    // A position less the rests before it, and the part of a rest it's in.
    private static double Active(double at, (double From, double To)[] rests)
    {
        double active = at;
        foreach (var (from, to) in rests)
        {
            if (from >= at)
                break;
            active -= Math.Min(at, to) - from;
        }

        return active;
    }
}
