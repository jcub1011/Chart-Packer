using ChartPacker.Core.Charting.Analysis;

namespace ChartPacker.Audio.Analysis;

/// <summary>One band's onset flux, normalised so its strong peaks are about 1.</summary>
public sealed class OnsetEnvelope
{
    private readonly float[] _values;

    internal OnsetEnvelope(OnsetBand band, float[] values, double framesPerSecond)
    {
        Band = band;
        _values = values;
        FramesPerSecond = framesPerSecond;
    }

    public OnsetBand Band { get; }

    public ReadOnlySpan<float> Values => _values;

    public int Count => _values.Length;

    public double FramesPerSecond { get; }

    /// <summary>The time frame <paramref name="frame"/> is centred on, in seconds of its audio.</summary>
    public double SecondsOf(double frame) => frame / FramesPerSecond;

    /// <summary>The frame centred on <paramref name="seconds"/>, fractionally.</summary>
    public double FrameAt(double seconds) => seconds * FramesPerSecond;

    /// <summary>The loudest value within <paramref name="radiusSeconds"/> of <paramref name="seconds"/>.</summary>
    public float MaxNear(double seconds, double radiusSeconds)
    {
        int from = Math.Max(0, (int)Math.Floor(FrameAt(seconds - radiusSeconds)));
        int to = Math.Min(_values.Length - 1, (int)Math.Ceiling(FrameAt(seconds + radiusSeconds)));
        float max = 0;
        for (int m = from; m <= to; m++)
            max = Math.Max(max, _values[m]);
        return max;
    }
}

/// <summary>
/// The onsets found in some audio (<see cref="OnsetDetector"/>): every candidate with how far it stands out, so a
/// sensitivity picks a set of them without anything being recomputed.
/// </summary>
public sealed class OnsetAnalysis
{
    /// <summary>The sensitivity candidates are kept at: the most that can be asked for.</summary>
    private const double MostSensitive = 1;

    private readonly Dictionary<OnsetBand, OnsetEnvelope> _envelopes = [];
    private readonly Dictionary<OnsetBand, DetectedOnset[]> _candidates = [];

    internal OnsetAnalysis(int sampleRate, int frameCount, IReadOnlyList<OnsetBand> bands, float[][] flux, float[] levels, string? error)
    {
        SampleRate = sampleRate;
        FrameCount = frameCount;
        FramesPerSecond = (double)sampleRate / OnsetDetector.Hop;
        Bands = bands;
        Error = error;
        for (int b = 0; b < bands.Count; b++)
        {
            var normalised = OnsetPeaks.Normalise(flux[b]);
            _envelopes[bands[b]] = new OnsetEnvelope(bands[b], normalised, FramesPerSecond);
            _candidates[bands[b]] = OnsetPeaks.Pick(normalised, levels, bands[b], sampleRate, OnsetDetector.ThresholdFor(MostSensitive));
        }

        OnsetPeaks.DropLeakage(_candidates, [.. bands.Where(b => b != OnsetBand.Full)]);
    }

    public int SampleRate { get; }

    public int FrameCount { get; }

    public double FramesPerSecond { get; }

    /// <summary><see cref="OnsetBand.Full"/>, and the drum bands when they were asked for.</summary>
    public IReadOnlyList<OnsetBand> Bands { get; }

    /// <summary>Why some of the audio couldn't be read (it was taken as silent), or <see langword="null"/>.</summary>
    public string? Error { get; }

    public bool HasBand(OnsetBand band) => _envelopes.ContainsKey(band);

    /// <exception cref="ArgumentException">The band wasn't analysed.</exception>
    public OnsetEnvelope Envelope(OnsetBand band) =>
        _envelopes.TryGetValue(band, out var envelope) ? envelope : throw new ArgumentException($"{band} wasn't analysed.", nameof(band));

    /// <summary>
    /// The onsets at <paramref name="sensitivity"/>, from 0 (fewest) to 1 (most), in time order: those of the drum bands
    /// when they were analysed, else the whole spectrum's.
    /// </summary>
    public IReadOnlyList<DetectedOnset> Select(double sensitivity)
    {
        double threshold = OnsetDetector.ThresholdFor(sensitivity);
        var bands = Bands.Count > 1 ? Bands.Where(b => b != OnsetBand.Full) : Bands;
        return [.. bands.SelectMany(b => _candidates[b]).Where(o => o.Strength >= threshold).OrderBy(o => o.Seconds)];
    }

    /// <summary>The times of <paramref name="band"/>'s onsets at <paramref name="sensitivity"/>, ascending.</summary>
    public double[] Times(OnsetBand band, double sensitivity)
    {
        double threshold = OnsetDetector.ThresholdFor(sensitivity);
        return [.. Candidates(band).Where(o => o.Strength >= threshold).Select(o => o.Seconds)];
    }

    /// <summary>Every candidate of <paramref name="band"/>, ascending.</summary>
    internal IReadOnlyList<DetectedOnset> Candidates(OnsetBand band) =>
        _candidates.TryGetValue(band, out var candidates) ? candidates : throw new ArgumentException($"{band} wasn't analysed.", nameof(band));
}

/// <summary>Picks onsets out of a flux, and times them off the audio's level.</summary>
internal static class OnsetPeaks
{
    /// <summary>
    /// <paramref name="flux"/> scaled so that its loudest 1% reach about 1 (but no more than a tenth of its peak below):
    /// thresholds then mean the same in quiet audio and loud.
    /// </summary>
    public static float[] Normalise(float[] flux)
    {
        float max = 0;
        foreach (float value in flux)
            max = Math.Max(max, value);
        if (max < 1e-6f)
            return new float[flux.Length];

        var sorted = (float[])flux.Clone();
        Array.Sort(sorted);
        float p99 = sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * 0.99))];
        float scale = Math.Max(p99, max * 0.1f);
        var normalised = new float[flux.Length];
        for (int m = 0; m < flux.Length; m++)
            normalised[m] = flux[m] / scale;
        return normalised;
    }

    /// <summary>The peaks of <paramref name="flux"/> that stand out by at least <paramref name="threshold"/>, timed by <paramref name="levels"/>.</summary>
    public static DetectedOnset[] Pick(float[] flux, float[] levels, OnsetBand band, int sampleRate, double threshold)
    {
        double fps = (double)sampleRate / OnsetDetector.Hop;
        int peakRadius = Math.Max(1, (int)Math.Round(0.03 * fps));
        int before = Math.Max(1, (int)Math.Round(0.1 * fps));
        int after = Math.Max(1, (int)Math.Round(0.07 * fps));

        var sums = new double[flux.Length + 1];
        for (int m = 0; m < flux.Length; m++)
            sums[m + 1] = sums[m] + flux[m];

        var onsets = new List<DetectedOnset>();
        for (int m = 0; m < flux.Length; m++)
        {
            float value = flux[m];
            if (value <= 0 || !IsPeak(flux, m, peakRadius))
                continue;

            int from = Math.Max(0, m - before);
            int to = Math.Min(flux.Length, m + after + 1);
            double mean = (sums[to] - sums[from]) / (to - from);
            double strength = value - mean;
            if (strength >= threshold)
                onsets.Add(new DetectedOnset(TimeOf(m, levels, sampleRate), band, (float)strength));
        }

        // Timing can bring two peaks onto one start, or swap them: keep the stronger of any within 20 ms.
        onsets.Sort((a, b) => a.Seconds.CompareTo(b.Seconds));
        var kept = new List<DetectedOnset>(onsets.Count);
        foreach (var onset in onsets)
        {
            if (kept.Count > 0 && onset.Seconds - kept[^1].Seconds < 0.020)
            {
                if (onset.Strength > kept[^1].Strength)
                    kept[^1] = onset;
                continue;
            }

            kept.Add(onset);
        }

        return [.. kept];
    }

    /// <summary>
    /// Drops a drum band's onsets that are only another band's hit leaking in: weak for their own band (a quarter of its
    /// typical hit), at the same moment as a strong onset in another band (half of that band's typical hit). A hit's
    /// attack spreads across the spectrum, so a loud snare shows faintly in the kick band.
    /// </summary>
    public static void DropLeakage(Dictionary<OnsetBand, DetectedOnset[]> candidates, IReadOnlyList<OnsetBand> bands)
    {
        if (bands.Count < 2)
            return;

        var typical = bands.ToDictionary(b => b, b => Typical(candidates[b]));
        var strong = bands.ToDictionary(b => b, b => candidates[b].Where(o => o.Strength >= typical[b] * 0.5).Select(o => o.Seconds).ToArray());
        foreach (var band in bands)
        {
            candidates[band] = [.. candidates[band].Where(o =>
                o.Strength >= typical[band] * 0.25
                || !bands.Any(other => other != band && strong[other].Any(t => Math.Abs(t - o.Seconds) <= 0.015)))];
        }
    }

    /// <summary>A band's typical hit: the median strength of its stronger half of onsets.</summary>
    private static float Typical(DetectedOnset[] onsets)
    {
        if (onsets.Length == 0)
            return 0;
        var strengths = onsets.Select(o => o.Strength).Order().ToArray();
        return strengths[(int)(strengths.Length * 0.75)];
    }

    /// <summary>Whether frame <paramref name="m"/> is the loudest within <paramref name="radius"/>; of equal frames, the first.</summary>
    private static bool IsPeak(float[] flux, int m, int radius)
    {
        float value = flux[m];
        for (int i = Math.Max(0, m - radius); i <= Math.Min(flux.Length - 1, m + radius); i++)
        {
            if (flux[i] > value || (i < m && flux[i] == value))
                return false;
        }

        return true;
    }

    /// <summary>
    /// When the onset whose flux peaks at frame <paramref name="frame"/> starts: the middle of the 32-sample block, among
    /// those the frame's window could have heard it in, where the level rises most over the two blocks before.
    /// </summary>
    private static double TimeOf(int frame, float[] levels, int sampleRate)
    {
        const int block = OnsetDetector.FineBlock;
        const float floor = 1e-10f;
        long centre = (long)frame * OnsetDetector.Hop;
        int from = (int)Math.Max(2, (centre - (OnsetDetector.WindowSize / 2) - (OnsetDetector.Lag * OnsetDetector.Hop)) / block);
        int to = (int)Math.Min(levels.Length - 1, (centre + (OnsetDetector.WindowSize / 2)) / block);

        int best = -1;
        double bestRise = 0;
        for (int j = from; j <= to; j++)
        {
            double rise = Math.Log(levels[j] + floor) - Math.Log(Math.Max(levels[j - 1], levels[j - 2]) + floor);
            if (rise > bestRise)
            {
                bestRise = rise;
                best = j;
            }
        }

        return best < 0 ? (double)centre / sampleRate : ((best * block) + (block / 2.0)) / sampleRate;
    }
}
