using ChartShared.Audio.Dsp;
using ChartShared.Audio.Spectrum;

namespace ChartShared.Audio.Analysis;

/// <summary>
/// How strongly each tempo fits the onset flux at each moment (a tempogram), and the most likely tempo through the song.
/// </summary>
/// <remarks>
/// <para>The flux is taken to about 100 frames a second, and each 6 s window of it, every 0.25 s, is autocorrelated. A
/// tempo's score is the autocorrelation at its beat period and the next three multiples, weighted down (a comb), times a
/// preference for 60–200 BPM that falls away outside.</para>
/// <para>The path is the tempo through the windows with the best scores, less a cost for changing tempo that grows with
/// how far it changes, so that a step change is followed once the new tempo clearly wins and an octave flip is rare.</para>
/// </remarks>
internal sealed class Tempogram
{
    public const double MinBpm = 40;
    public const double MaxBpm = 320;
    public const double PreferredMinBpm = 60;
    public const double PreferredMaxBpm = 200;

    /// <summary>Tempos are scored 1% apart.</summary>
    public const double Step = 1.01;

    private const double WindowSeconds = 6;
    private const double HopSeconds = 0.25;

    /// <summary>What changing tempo by an octave costs, against a window's best score of 1.</summary>
    private const double OctaveCost = 4;

    private readonly float[][] _scores;

    private Tempogram(float[][] scores, int firstFrame, double framesPerHop, int[] path)
    {
        _scores = scores;
        FirstFrame = firstFrame;
        FramesPerHop = framesPerHop;
        Path = path;
    }

    public static int TempoCount { get; } = (int)Math.Floor(Math.Log(MaxBpm / MinBpm) / Math.Log(Step)) + 1;

    /// <summary>The flux frame the first window is centred on.</summary>
    public int FirstFrame { get; }

    /// <summary>How many flux frames apart the windows are.</summary>
    public double FramesPerHop { get; }

    public int WindowCount => _scores.Length;

    /// <summary>The tempo (as an index into the grid) the path takes in each window.</summary>
    public int[] Path { get; }

    public static double BpmOf(int tempo) => MinBpm * Math.Pow(Step, tempo);

    public static int TempoAt(double bpm) => Math.Clamp((int)Math.Round(Math.Log(bpm / MinBpm) / Math.Log(Step)), 0, TempoCount - 1);

    /// <summary>How much the analysis prefers <paramref name="bpm"/>: 1 within 60–200 BPM, falling away outside.</summary>
    public static double Prior(double bpm)
    {
        double octaves = bpm < PreferredMinBpm ? Math.Log2(PreferredMinBpm / bpm) : bpm > PreferredMaxBpm ? Math.Log2(bpm / PreferredMaxBpm) : 0;
        return Math.Exp(-0.5 * (octaves / 0.25) * (octaves / 0.25));
    }

    public float Score(int window, int tempo) => _scores[window][tempo];

    /// <summary>The window nearest flux frame <paramref name="frame"/>.</summary>
    public int WindowAt(double frame) => Math.Clamp((int)Math.Round((frame - FirstFrame) / FramesPerHop), 0, WindowCount - 1);

    /// <summary>The tempogram of <paramref name="flux"/> from frame <paramref name="from"/> up to <paramref name="to"/>.</summary>
    /// <param name="framesPerSecond">The flux's frame rate.</param>
    public static Tempogram Of(ReadOnlySpan<float> flux, int from, int to, double framesPerSecond, CancellationToken cancellationToken)
    {
        // Down to about 100 frames a second, keeping each stretch's peak.
        int factor = Math.Max(1, (int)Math.Round(framesPerSecond / 100));
        double rate = framesPerSecond / factor;
        int length = Math.Max(1, (to - from + factor - 1) / factor);
        var reduced = new float[length];
        for (int i = 0; i < length; i++)
        {
            float max = 0;
            for (int m = from + (i * factor); m < Math.Min(to, from + ((i + 1) * factor)); m++)
                max = Math.Max(max, flux[m]);
            reduced[i] = max;
        }

        int window = Math.Max(4, (int)Math.Round(WindowSeconds * rate));
        int hop = Math.Max(1, (int)Math.Round(HopSeconds * rate));
        int windows = Math.Max(1, (int)Math.Ceiling((double)length / hop));
        int size = Fft.SizeFor(window * 2);
        var fft = new Fft(size);
        var hann = Windowing.PeriodicHann(window);
        var re = new float[size];
        var im = new float[size];

        int tempos = TempoCount;
        var lags = new double[tempos];
        var priors = new double[tempos];
        for (int k = 0; k < tempos; k++)
        {
            lags[k] = 60 * rate / BpmOf(k);
            priors[k] = Prior(BpmOf(k));
        }

        var scores = new float[windows][];
        for (int w = 0; w < windows; w++)
        {
            if ((w & 31) == 0)
                cancellationToken.ThrowIfCancellationRequested();

            int start = (w * hop) - (window / 2);
            double mean = 0;
            for (int n = 0; n < window; n++)
                mean += At(reduced, start + n);
            mean /= window;

            Array.Clear(re);
            Array.Clear(im);
            for (int n = 0; n < window; n++)
                re[n] = (float)((At(reduced, start + n) - mean) * hann[n]);

            // The autocorrelation, through the power spectrum.
            fft.Forward(re, im);
            for (int k = 0; k < size; k++)
            {
                re[k] = (re[k] * re[k]) + (im[k] * im[k]);
                im[k] = 0;
            }

            fft.Inverse(re, im);
            var row = new float[tempos];
            scores[w] = row;
            float zero = re[0];
            if (zero <= 1e-12f)
                continue;

            for (int k = 0; k < tempos; k++)
            {
                double comb = 0;
                for (int multiple = 1; multiple <= 4; multiple++)
                {
                    double lag = lags[k] * multiple;
                    if (lag >= window - 1)
                        break;
                    int i = (int)lag;
                    double t = lag - i;
                    double value = (re[i] + ((re[i + 1] - re[i]) * t)) / zero;
                    comb += Math.Max(0, value) / multiple;
                }

                row[k] = (float)(comb * priors[k]);
            }
        }

        return new Tempogram(scores, from + (factor / 2), (double)hop * factor, PathOf(scores, cancellationToken));
    }

    private static float At(float[] values, int index) => index >= 0 && index < values.Length ? values[index] : 0;

    /// <summary>The best path through the windows' tempos (Viterbi), each window's scores scaled to a best of 1.</summary>
    /// <remarks>A change costs in proportion to how far it goes, so the best predecessor of every tempo is found in two sweeps.</remarks>
    private static int[] PathOf(float[][] scores, CancellationToken cancellationToken)
    {
        int windows = scores.Length;
        int tempos = TempoCount;
        double cost = OctaveCost * Math.Log2(Step);
        var value = new double[tempos];
        var next = new double[tempos];
        var best = new double[tempos];
        var from = new int[tempos];
        var back = new int[windows][];

        for (int w = 0; w < windows; w++)
        {
            if ((w & 255) == 0)
                cancellationToken.ThrowIfCancellationRequested();

            var row = scores[w];
            float max = 0;
            foreach (float s in row)
                max = Math.Max(max, s);
            double scale = max > 0 ? 1 / max : 0;

            back[w] = new int[tempos];
            if (w == 0)
            {
                for (int k = 0; k < tempos; k++)
                    value[k] = row[k] * scale;
                continue;
            }

            // best[k] = max over j of value[j] - cost·|k - j|, swept up then down.
            for (int k = 0; k < tempos; k++)
            {
                best[k] = value[k];
                from[k] = k;
                if (k > 0 && best[k - 1] - cost > best[k])
                {
                    best[k] = best[k - 1] - cost;
                    from[k] = from[k - 1];
                }
            }

            for (int k = tempos - 2; k >= 0; k--)
            {
                if (best[k + 1] - cost > best[k])
                {
                    best[k] = best[k + 1] - cost;
                    from[k] = from[k + 1];
                }
            }

            for (int k = 0; k < tempos; k++)
            {
                next[k] = best[k] + (row[k] * scale);
                back[w][k] = from[k];
            }

            (value, next) = (next, value);
        }

        var path = new int[windows];
        int end = 0;
        for (int k = 1; k < tempos; k++)
        {
            if (value[k] > value[end])
                end = k;
        }

        path[windows - 1] = end;
        for (int w = windows - 1; w > 0; w--)
            path[w - 1] = back[w][path[w]];
        return path;
    }
}
