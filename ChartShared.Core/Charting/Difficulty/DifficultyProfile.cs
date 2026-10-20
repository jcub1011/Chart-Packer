namespace ChartShared.Core.Charting.Difficulty;

/// <summary>The difficulty at a moment: each attribute's normalized value, and all five combined (D).</summary>
public readonly record struct DifficultyMoment(DifficultyAttributes Attributes, double Overall);

/// <summary>The difficulty just after a note, or at a window's start, as a pass found it.</summary>
public readonly record struct DifficultySample(double TimeMs, DifficultyAttributes Attributes, double Overall);

/// <summary>A stretch of a chart, in milliseconds from its start: <see cref="FromMs"/> included, <see cref="ToMs"/> not.</summary>
public readonly record struct DifficultySpan(double FromMs, double ToMs);

/// <summary>The peak envelope at evenly spaced points from <see cref="FromMs"/> to <see cref="ToMs"/>, both included.</summary>
public sealed record DifficultyCurve(double FromMs, double ToMs, IReadOnlyList<DifficultyMoment> Points)
{
    /// <summary>The time of point <paramref name="index"/>.</summary>
    public double TimeOf(int index) => Points.Count < 2 ? FromMs : FromMs + ((ToMs - FromMs) * index / (Points.Count - 1));
}

/// <summary>
/// A chart's difficulty over time, as one pass of the model found it (docs/difficulty-model.md §5 and §6): its value at
/// any time, its peak envelope, and the score of the whole chart or of any stretch of it.
/// </summary>
/// <remarks>
/// <para>Scores take the peak of each window of a fixed grid from the chart's start, clipped to the stretch, and average
/// them hardest first, each counting <see cref="DifficultyParameters.PeakWeight"/> times the one before. A difficulty that
/// stays the same scores itself.</para>
/// <para>Immutable once made, so it's read from any thread.</para>
/// </remarks>
public sealed class DifficultyProfile
{
    // Each attribute's series, in DifficultyAttribute's order, then the overall one's.
    private const int OverallSeries = 5;
    private const int SeriesCount = 6;

    private readonly DifficultyModel _model;
    private readonly DifficultySample[] _samples;
    private readonly double[] _times;
    private readonly double _window;

    // The peak of each series in each whole window of the grid.
    private readonly double[][] _peaks;

    internal DifficultyProfile(DifficultyModel model, DifficultySample[] samples, double endMs)
    {
        _model = model;
        _samples = samples;
        _times = [.. samples.Select(static s => s.TimeMs)];
        _window = model.Parameters.WindowMs;
        EndMs = endMs;
        int windows = Math.Max(1, (int)Math.Ceiling(endMs / _window));
        _peaks = [.. Enumerable.Range(0, SeriesCount).Select(_ => new double[windows])];
        foreach (var sample in samples)
        {
            int j = (int)Math.Floor(sample.TimeMs / _window);
            if (j < 0 || j >= windows)
                continue;
            for (int series = 0; series < SeriesCount; series++)
                _peaks[series][j] = Math.Max(_peaks[series][j], ValueOf(sample.Attributes, sample.Overall, series));
        }

        ChartScore = Score(0, endMs);
        ChartAttributes = AttributeScores(0, endMs);
    }

    public DifficultyParameters Parameters => _model.Parameters;

    public DifficultyReferences References => _model.References;

    /// <summary>Where the chart is scored to: a window past its last note, or the end of the stretch analyzed alone.</summary>
    public double EndMs { get; }

    /// <summary>What the pass found just after each note and at the start of each window, in time order.</summary>
    public IReadOnlyList<DifficultySample> Samples => _samples;

    /// <summary>The score of the whole chart (§6.2).</summary>
    public double ChartScore { get; }

    /// <summary>Each attribute's score over the whole chart, for the spider graph (§6.3).</summary>
    public DifficultyAttributes ChartAttributes { get; }

    /// <summary>The chart's rank, continuous (§6.2).</summary>
    public double Rank => RankCurve.Continuous(ChartScore, Parameters);

    /// <summary>The chart's whole rank: 0 and up, 7 and up for an overchart.</summary>
    public int Tier => RankCurve.Tier(ChartScore, Parameters);

    /// <summary>The difficulty at <paramref name="timeMs"/>: what it was after the last note or window start, decayed since.</summary>
    public DifficultyMoment ValueAt(double timeMs)
    {
        int index = LastAtOrBefore(timeMs);
        if (index < 0)
            return default;
        var sample = _samples[index];
        var levels = _model.Integrate(sample.Attributes, timeMs - sample.TimeMs);
        return new DifficultyMoment(levels, _model.Combine(levels));
    }

    /// <summary>
    /// The peak envelope at <paramref name="timeMs"/> (§5.4): the most each attribute, and the overall difficulty, reached
    /// over the window before it. It has none of the strain's sawtooth, and is what ranks are read from.
    /// </summary>
    public DifficultyMoment EnvelopeAt(double timeMs)
    {
        Span<double> peaks = stackalloc double[SeriesCount];
        Include(peaks, ValueAt(timeMs - _window));
        Include(peaks, ValueAt(timeMs));
        for (int i = LastAtOrBefore(timeMs - _window) + 1; i < _samples.Length && _samples[i].TimeMs <= timeMs; i++)
            Include(peaks, new DifficultyMoment(_samples[i].Attributes, _samples[i].Overall));
        return new DifficultyMoment(new DifficultyAttributes(peaks[0], peaks[1], peaks[2], peaks[3], peaks[4]), peaks[OverallSeries]);
    }

    /// <summary>The rank the difficulty at <paramref name="timeMs"/> is worth (§5.3), from the peak envelope.</summary>
    public double RankAt(double timeMs) => RankCurve.Continuous(EnvelopeAt(timeMs).Overall, Parameters);

    /// <summary>The attribute that adds the most to the difficulty at <paramref name="timeMs"/>, from the peak envelope.</summary>
    public DifficultyAttribute DominantAt(double timeMs)
    {
        var levels = EnvelopeAt(timeMs).Attributes;
        var best = DifficultyAttribute.Strum;
        double most = double.NegativeInfinity;
        foreach (var attribute in DifficultyAttributes.All)
        {
            double share = _model.Weights[(int)attribute] * Math.Pow(Math.Max(0, levels[attribute]), Parameters.PowerMean);
            if (share > most)
                (best, most) = (attribute, share);
        }

        return best;
    }

    /// <summary>The score of the stretch from <paramref name="fromMs"/> to <paramref name="toMs"/> (§6.1), in context.</summary>
    public double Score(double fromMs, double toMs) => ScoreOf(OverallSeries, fromMs, toMs);

    /// <summary>Each attribute's score over the stretch from <paramref name="fromMs"/> to <paramref name="toMs"/>, in context.</summary>
    public DifficultyAttributes AttributeScores(double fromMs, double toMs) => new(
        ScoreOf((int)DifficultyAttribute.Strum, fromMs, toMs),
        ScoreOf((int)DifficultyAttribute.Note, fromMs, toMs),
        ScoreOf((int)DifficultyAttribute.Rhythm, fromMs, toMs),
        ScoreOf((int)DifficultyAttribute.Pattern, fromMs, toMs),
        ScoreOf((int)DifficultyAttribute.Endurance, fromMs, toMs));

    /// <summary>
    /// The windows that count the most towards <see cref="ChartScore"/>, hardest first: as many as make up about
    /// <paramref name="share"/> of it, each window counting its weight times its peak.
    /// </summary>
    public IReadOnlyList<DifficultySpan> Contributors(double share = 0.65)
    {
        var peaks = _peaks[OverallSeries];
        var order = Enumerable.Range(0, peaks.Length).Where(j => peaks[j] > 0).OrderByDescending(j => peaks[j]).ToList();
        double total = 0, weight = 1;
        foreach (int j in order)
        {
            total += weight * peaks[j];
            weight *= Parameters.PeakWeight;
        }

        var spans = new List<DifficultySpan>();
        double sum = 0;
        weight = 1;
        foreach (int j in order)
        {
            if (sum >= share * total)
                break;
            spans.Add(new DifficultySpan(j * _window, Math.Min((j + 1) * _window, EndMs)));
            sum += weight * peaks[j];
            weight *= Parameters.PeakWeight;
        }

        return spans;
    }

    /// <summary>The peak envelope at <paramref name="points"/> evenly spaced times from <paramref name="fromMs"/> to <paramref name="toMs"/>.</summary>
    public DifficultyCurve Curve(double fromMs, double toMs, int points)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(points, 2);
        var values = new DifficultyMoment[points];
        for (int k = 0; k < points; k++)
            values[k] = EnvelopeAt(fromMs + ((toMs - fromMs) * k / (points - 1)));
        return new DifficultyCurve(fromMs, toMs, values);
    }

    private double ScoreOf(int series, double fromMs, double toMs)
    {
        if (!(toMs > fromMs))
            return 0;
        int first = Math.Max(0, (int)Math.Floor(fromMs / _window));
        int last = (int)Math.Ceiling(toMs / _window) - 1;
        var peaks = new List<double>(Math.Max(0, last - first + 1));
        for (int j = first; j <= last; j++)
        {
            double start = Math.Max(fromMs, j * _window), end = Math.Min(toMs, (j + 1) * _window);
            if (end <= start)
                continue;
            bool whole = start == j * _window && end == (j + 1) * _window && j < _peaks[series].Length;
            peaks.Add(whole ? _peaks[series][j] : PeakIn(series, start, end));
        }

        peaks.Sort(static (a, b) => b.CompareTo(a));
        double sum = 0, total = 0, weight = 1;
        foreach (double peak in peaks)
        {
            sum += weight * peak;
            total += weight;
            weight *= Parameters.PeakWeight;
        }

        return total <= 0 ? 0 : sum / total;
    }

    // The most a series reaches from start (included) to end (not).
    private double PeakIn(int series, double start, double end)
    {
        var at = ValueAt(start);
        double peak = ValueOf(at.Attributes, at.Overall, series);
        for (int i = LastAtOrBefore(start) + 1; i < _samples.Length && _samples[i].TimeMs < end; i++)
            peak = Math.Max(peak, ValueOf(_samples[i].Attributes, _samples[i].Overall, series));
        return peak;
    }

    private static double ValueOf(DifficultyAttributes attributes, double overall, int series) =>
        series == OverallSeries ? overall : attributes[(DifficultyAttribute)series];

    private static void Include(Span<double> peaks, DifficultyMoment moment)
    {
        for (int series = 0; series < SeriesCount; series++)
            peaks[series] = Math.Max(peaks[series], ValueOf(moment.Attributes, moment.Overall, series));
    }

    // The last sample at or before timeMs, or -1 for none.
    private int LastAtOrBefore(double timeMs)
    {
        int low = 0, high = _times.Length;
        while (low < high)
        {
            int mid = (low + high) >>> 1;
            if (_times[mid] <= timeMs)
                low = mid + 1;
            else
                high = mid;
        }

        return low - 1;
    }
}
