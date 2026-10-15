namespace ChartShared.Core.Charting.Editing;

/// <summary>
/// Tap tempo (charter-spec C4 item 5): taps in time with the playing song give a tempo from the median interval between
/// them, which a stray tap moves little. A pause of <see cref="ResetSeconds"/> starts a new run of taps.
/// </summary>
public sealed class TapTempo
{
    /// <summary>How long a pause between taps starts a new run, in seconds.</summary>
    public const double ResetSeconds = 2;

    /// <summary>The fewest taps that give a tempo.</summary>
    public const int MinTaps = 3;

    private readonly List<double> _taps = [];

    /// <summary>How many taps the run has.</summary>
    public int Count => _taps.Count;

    /// <summary>The tempo of the run so far, in beats per minute, or null with fewer than <see cref="MinTaps"/> taps.</summary>
    public double? Bpm => Interval is { } interval ? 60 / interval : null;

    /// <summary>The median interval between the run's taps, in seconds, or null with fewer than <see cref="MinTaps"/> taps.</summary>
    public double? Interval
    {
        get
        {
            if (_taps.Count < MinTaps)
                return null;
            var intervals = new double[_taps.Count - 1];
            for (int i = 1; i < _taps.Count; i++)
                intervals[i - 1] = _taps[i] - _taps[i - 1];
            Array.Sort(intervals);
            int middle = intervals.Length / 2;
            return intervals.Length % 2 == 1 ? intervals[middle] : (intervals[middle - 1] + intervals[middle]) / 2;
        }
    }

    /// <summary>A tap at <paramref name="seconds"/> of song time. A tap that's not later than the last, or long after it, starts a new run.</summary>
    /// <returns>Whether it started a new run.</returns>
    public bool Tap(double seconds)
    {
        bool fresh = _taps.Count == 0 || seconds <= _taps[^1] || seconds - _taps[^1] > ResetSeconds;
        if (fresh)
            _taps.Clear();
        _taps.Add(seconds);
        return fresh;
    }

    /// <summary>Forgets the run's taps.</summary>
    public void Reset() => _taps.Clear();
}
