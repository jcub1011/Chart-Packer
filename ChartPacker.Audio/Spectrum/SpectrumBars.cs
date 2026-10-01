namespace ChartPacker.Audio.Spectrum;

/// <summary>
/// Spectrum levels as an old-style analyser shows them: each bar jumps up to a louder level at once and falls
/// back slowly, and a cap marks its recent peak, holding there a moment before it falls too.
/// </summary>
/// <remarks>Not thread-safe.</remarks>
public sealed class SpectrumBars
{
    /// <summary>How far a bar falls in a second, in full heights.</summary>
    public const float FallPerSecond = 1.5f;

    /// <summary>How long a cap stays at a peak before falling.</summary>
    public static readonly TimeSpan PeakHold = TimeSpan.FromSeconds(0.4);

    /// <summary>How far a cap falls in a second, once it's held, in full heights.</summary>
    public const float PeakFallPerSecond = 1f;

    private readonly float[] _levels;
    private readonly float[] _peaks;
    private readonly double[] _peakAges;

    public SpectrumBars(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        _levels = new float[count];
        _peaks = new float[count];
        _peakAges = new double[count];
    }

    public int Count => _levels.Length;

    /// <summary>Each bar's height, from 0 to 1.</summary>
    public IReadOnlyList<float> Levels => _levels;

    /// <summary>Each bar's cap, at or above the bar.</summary>
    public IReadOnlyList<float> Peaks => _peaks;

    /// <summary>Every bar and cap has fallen to nothing.</summary>
    public bool IsSettled => _peaks.All(p => p == 0);

    /// <summary>Drops every bar and cap to nothing at once.</summary>
    public void Reset()
    {
        Array.Clear(_levels);
        Array.Clear(_peaks);
        Array.Clear(_peakAges);
    }

    /// <summary>Moves the bars on by <paramref name="elapsed"/> towards <paramref name="levels"/>.</summary>
    /// <param name="levels">Each bar's level now, from 0 to 1; empty when there's nothing to show, so the bars fall away.</param>
    public void Update(ReadOnlySpan<float> levels, TimeSpan elapsed)
    {
        if (!levels.IsEmpty && levels.Length != Count)
            throw new ArgumentException($"There must be {Count} levels, or none.", nameof(levels));

        double seconds = Math.Max(0, elapsed.TotalSeconds);
        float fall = (float)(FallPerSecond * seconds);
        float peakFall = (float)(PeakFallPerSecond * seconds);
        for (int i = 0; i < Count; i++)
        {
            float target = levels.IsEmpty ? 0 : Math.Clamp(levels[i], 0, 1);
            _levels[i] = target >= _levels[i] ? target : Math.Max(target, _levels[i] - fall);

            if (_levels[i] >= _peaks[i])
            {
                _peaks[i] = _levels[i];
                _peakAges[i] = 0;
                continue;
            }

            _peakAges[i] += seconds;
            if (_peakAges[i] > PeakHold.TotalSeconds)
                _peaks[i] = Math.Max(_levels[i], _peaks[i] - peakFall);
        }
    }
}
