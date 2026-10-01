namespace ChartPacker.Core.Charts;

/// <summary>A tempo change, at a tick.</summary>
/// <param name="MicrosecondsPerQuarter">The length of a beat (a quarter note), as MIDI gives it.</param>
public readonly record struct TempoChange(long Tick, long MicrosecondsPerQuarter);

/// <summary>A time-signature change, at a tick.</summary>
/// <param name="Denominator">The note value of a beat: 4 for quarter notes, 8 for eighths.</param>
public readonly record struct TimeSignature(long Tick, int Numerator, int Denominator);

/// <summary>A beat line of the highway.</summary>
/// <param name="IsMeasure">The beat starts a measure.</param>
public readonly record struct BeatLine(long Tick, bool IsMeasure);

/// <summary>
/// Turns chart ticks into song time and back, from a chart's resolution and its tempo and
/// time-signature changes. Before the first change a chart is 120 BPM in 4/4.
/// </summary>
public sealed class TempoMap
{
    /// <summary>120 BPM.</summary>
    public const long DefaultMicrosecondsPerQuarter = 500_000;

    private readonly TempoChange[] _tempos;
    private readonly TimeSignature[] _signatures;

    // The song time, in seconds, at each tempo change.
    private readonly double[] _starts;

    /// <param name="resolution">Ticks per beat.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="resolution"/> isn't positive.</exception>
    public TempoMap(int resolution, IEnumerable<TempoChange> tempos, IEnumerable<TimeSignature> signatures)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(resolution);
        ArgumentNullException.ThrowIfNull(tempos);
        ArgumentNullException.ThrowIfNull(signatures);

        Resolution = resolution;
        _tempos = Normalize(tempos.Where(t => t.Tick >= 0 && t.MicrosecondsPerQuarter > 0), t => t.Tick, new TempoChange(0, DefaultMicrosecondsPerQuarter));
        _signatures = Normalize(
            signatures.Where(s => s.Tick >= 0 && s.Numerator > 0 && s.Denominator > 0 && (s.Denominator & (s.Denominator - 1)) == 0),
            s => s.Tick,
            new TimeSignature(0, 4, 4));

        _starts = new double[_tempos.Length];
        for (int i = 1; i < _tempos.Length; i++)
            _starts[i] = _starts[i - 1] + SecondsOf(_tempos[i].Tick - _tempos[i - 1].Tick, _tempos[i - 1]);
    }

    /// <summary>Ticks per beat.</summary>
    public int Resolution { get; }

    /// <summary>The tempo changes, one per tick, starting at tick 0.</summary>
    public IReadOnlyList<TempoChange> Tempos => _tempos;

    /// <summary>The time-signature changes, one per tick, starting at tick 0.</summary>
    public IReadOnlyList<TimeSignature> TimeSignatures => _signatures;

    /// <summary>The song time, in seconds, at <paramref name="tick"/>, which may be fractional or negative.</summary>
    public double SecondsAt(double tick)
    {
        int i = tick <= 0 ? 0 : LastAtOrBefore(_tempos, t => t.Tick, tick);
        return _starts[i] + SecondsOf(tick - _tempos[i].Tick, _tempos[i]);
    }

    /// <summary>The tick, unrounded, at <paramref name="seconds"/> of song time.</summary>
    public double TickAt(double seconds)
    {
        int lo = 0, hi = _starts.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (_starts[mid] <= seconds)
                lo = mid;
            else
                hi = mid - 1;
        }

        var tempo = _tempos[lo];
        return tempo.Tick + ((seconds - _starts[lo]) * 1_000_000 * Resolution / tempo.MicrosecondsPerQuarter);
    }

    /// <summary>
    /// The tick that's <paramref name="offset"/> of song time after <paramref name="tick"/>, to the
    /// nearest tick and never before the start. A later offset is positive.
    /// </summary>
    public long Shift(long tick, TimeSpan offset)
    {
        if (offset == TimeSpan.Zero)
            return tick;

        double shifted = Math.Round(TickAt(SecondsAt(tick) + offset.TotalSeconds), MidpointRounding.AwayFromZero);
        return shifted <= 0 ? 0 : (long)shifted;
    }

    /// <summary>
    /// The song time of the note <paramref name="steps"/> on from <paramref name="seconds"/>, or back from it when negative,
    /// on a grid of notes <paramref name="noteQuarters"/> quarter notes long from the start (a half for an eighth note, a
    /// third for an eighth-note triplet). The first step is to the next note on the grid, or the one before, so a time
    /// between two is put on one, and a time already on one, or within <see cref="OnNoteSeconds"/> of it, moves a whole
    /// note. It can be before the start, as the map runs on back.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="noteQuarters"/> isn't positive.</exception>
    public double StepNotes(double seconds, int steps, double noteQuarters)
    {
        if (!(noteQuarters > 0) || double.IsInfinity(noteQuarters))
            throw new ArgumentOutOfRangeException(nameof(noteQuarters), noteQuarters, "A note has to be some length.");
        if (steps == 0)
            return seconds;

        double noteTicks = Resolution * noteQuarters;
        double note = TickAt(seconds) / noteTicks;
        double At(double n) => SecondsAt(n * noteTicks);

        // The first note on, or back; one just ahead of the time, or just behind it, is the note the time is on.
        double first;
        if (steps > 0)
        {
            first = Math.Floor(note) + 1;
            if (At(first) - seconds <= OnNoteSeconds)
                first++;
        }
        else
        {
            first = Math.Ceiling(note) - 1;
            if (seconds - At(first) <= OnNoteSeconds)
                first--;
        }

        return At(first + steps - Math.Sign(steps));
    }

    /// <summary>
    /// How near a note a time is on it, in seconds, for <see cref="StepNotes"/>. Audio seeks to a whole frame, which can be
    /// a little before the note it was sent to; that's still on the note, so the next step moves on from it rather than
    /// back to it. Far less than the shortest note stepped by, so no note is skipped.
    /// </summary>
    public const double OnNoteSeconds = 0.001;

    /// <summary>The beat lines from <paramref name="fromTick"/> up to and including <paramref name="toTick"/>, in order.</summary>
    public IEnumerable<BeatLine> BeatLines(long fromTick, long toTick)
    {
        for (int i = 0; i < _signatures.Length; i++)
        {
            var signature = _signatures[i];
            long end = i + 1 < _signatures.Length ? _signatures[i + 1].Tick : long.MaxValue;
            if (end <= fromTick)
                continue;
            if (signature.Tick > toTick)
                yield break;

            long beat = Math.Max(1, Resolution * 4L / signature.Denominator);
            long first = fromTick <= signature.Tick ? 0 : (fromTick - signature.Tick + beat - 1) / beat;
            for (long k = first; ; k++)
            {
                long tick = signature.Tick + (k * beat);
                if (tick >= end || tick > toTick)
                    break;
                yield return new BeatLine(tick, k % signature.Numerator == 0);
            }
        }
    }

    /// <summary>Whether <paramref name="other"/> has the same resolution and changes.</summary>
    public bool SameAs(TempoMap other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Resolution == other.Resolution && _tempos.AsSpan().SequenceEqual(other._tempos) && _signatures.AsSpan().SequenceEqual(other._signatures);
    }

    private double SecondsOf(double ticks, TempoChange tempo) => ticks * tempo.MicrosecondsPerQuarter / 1_000_000 / Resolution;

    /// <summary>Sorts <paramref name="items"/> by tick, keeping the last at each tick, and starts them at tick 0.</summary>
    private static T[] Normalize<T>(IEnumerable<T> items, Func<T, long> tickOf, T initial)
    {
        var byTick = new SortedDictionary<long, T>();
        foreach (var item in items)
            byTick[tickOf(item)] = item;

        var result = new List<T>(byTick.Count + 1);
        if (!byTick.ContainsKey(0))
            result.Add(initial);
        result.AddRange(byTick.Values);
        return [.. result];
    }

    private static int LastAtOrBefore<T>(T[] items, Func<T, long> tickOf, double tick)
    {
        int lo = 0, hi = items.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (tickOf(items[mid]) <= tick)
                lo = mid;
            else
                hi = mid - 1;
        }

        return lo;
    }
}
