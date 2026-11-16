using System.Collections.Concurrent;
using ChartShared.Audio.Dsp;
using ChartShared.Audio.Spectrum;

namespace ChartShared.Audio.Analysis;

/// <summary>
/// The spectra of two real frames at once, Hann-windowed, with where each bin's energy really is: how far from the bin's
/// own frequency, and how far from the frame's middle (the reassignment method of Auger and Flandrin). A steady tone's
/// bins all point at the tone's frequency, and a click's at the click's time, so a spectrogram drawn from them is sharp
/// where one drawn from the bins themselves is as blurred as the window is wide.
/// </summary>
/// <remarks>
/// <para>The shifts are ratios of the frame's spectrum through three windows: the Hann window, its slope, and it times
/// the time from its middle. A Hann window is a constant less a cosine a window long, so a spectrum through it, or
/// through its slope, is a sum of three neighbouring bins of the frame's spectrum through no window at all. So each
/// frame takes two real transforms, of it and of it times the time from its middle, and the two frames' four share two
/// complex FFTs, as in <see cref="RealFftPair"/>.</para>
/// <para>Not thread-safe: it has buffers of its own. The FFT's tables are shared between instances of a size.</para>
/// </remarks>
internal sealed class ReassignedPair
{
    /// <summary>A bin pointing further than this from its own frequency, in bins, is taken as noise and left where it is.</summary>
    public const float MaxBinShift = 4;

    private const float Silent = 1e-30f;

    private static readonly ConcurrentDictionary<int, Fft> Ffts = new();

    private readonly Fft _fft;
    private readonly float[] _re;
    private readonly float[] _im;
    private readonly float[] _timedRe;
    private readonly float[] _timedIm;

    // Each frame's spectrum through no window, plain and timed, as real and imaginary parts: bins 0 to half the size and one more.
    private readonly float[][] _a = new float[4][];
    private readonly float[][] _b = new float[4][];

    public ReassignedPair(int size)
    {
        _fft = Ffts.GetOrAdd(size, s => new Fft(s));
        _re = new float[size];
        _im = new float[size];
        _timedRe = new float[size];
        _timedIm = new float[size];
        for (int i = 0; i < 4; i++)
        {
            _a[i] = new float[(size / 2) + 2];
            _b[i] = new float[(size / 2) + 2];
        }

        double squares = 0;
        foreach (float w in Windowing.PeriodicHann(size))
            squares += (double)w * w;
        TonePower = size * squares / 4;
        A = new Frame(size);
        B = new Frame(size);
    }

    public int Size => _fft.Size;

    /// <summary>What a full-scale sine's bins' powers add up to.</summary>
    public double TonePower { get; }

    /// <summary>The first frame's bins, as last analysed.</summary>
    public Frame A { get; }

    /// <summary>The second frame's.</summary>
    public Frame B { get; }

    /// <summary>
    /// Analyses bins <paramref name="firstBin"/> to <paramref name="lastBin"/> of <paramref name="a"/> into <see cref="A"/>
    /// and of <paramref name="b"/> into <see cref="B"/>; from bin 1 to half the size.
    /// </summary>
    public void Analyse(ReadOnlySpan<float> a, ReadOnlySpan<float> b, int firstBin, int lastBin)
    {
        int size = Size;
        int middle = size / 2;
        for (int n = 0; n < size; n++)
        {
            float time = n - middle;
            _re[n] = a[n];
            _im[n] = b[n];
            _timedRe[n] = a[n] * time;
            _timedIm[n] = b[n] * time;
        }

        _fft.Forward(_re, _im);
        _fft.Forward(_timedRe, _timedIm);

        // X = A + iB, with A and B the spectra of two real frames: A_k = (X_k + conj X_(N-k)) / 2, B_k = (X_k - conj X_(N-k)) / 2i.
        for (int k = firstBin - 1; k <= lastBin + 1; k++)
        {
            int m = (size - k) & (size - 1);
            _a[0][k] = (_re[k] + _re[m]) * 0.5f;
            _a[1][k] = (_im[k] - _im[m]) * 0.5f;
            _b[0][k] = (_im[k] + _im[m]) * 0.5f;
            _b[1][k] = (_re[m] - _re[k]) * 0.5f;
            _a[2][k] = (_timedRe[k] + _timedRe[m]) * 0.5f;
            _a[3][k] = (_timedIm[k] - _timedIm[m]) * 0.5f;
            _b[2][k] = (_timedIm[k] + _timedIm[m]) * 0.5f;
            _b[3][k] = (_timedRe[m] - _timedRe[k]) * 0.5f;
        }

        Point(_a, A, firstBin, lastBin, middle);
        Point(_b, B, firstBin, lastBin, middle);
    }

    private static void Point(float[][] spectrum, Frame frame, int firstBin, int lastBin, float furthest)
    {
        float[] plainRe = spectrum[0], plainIm = spectrum[1], timedRe = spectrum[2], timedIm = spectrum[3];
        float[] powers = frame.Power, binShift = frame.BinShift, timeShift = frame.TimeShift;
        for (int k = firstBin; k <= lastBin; k++)
        {
            // Through the Hann window: half the bin, less a quarter of each neighbour.
            float re = (0.5f * plainRe[k]) - (0.25f * (plainRe[k - 1] + plainRe[k + 1]));
            float im = (0.5f * plainIm[k]) - (0.25f * (plainIm[k - 1] + plainIm[k + 1]));
            float power = (re * re) + (im * im);
            if (!(power > Silent))
            {
                powers[k] = 0;
                binShift[k] = 0;
                timeShift[k] = 0;
                continue;
            }

            // Through its slope, (π/N) sin(2πn/N): (π/N) (X_(k-1) - X_(k+1)) / 2i. The frequency is the bin's less
            // Im(X_slope / X) radians a sample, which in bins is half of Im((X_(k-1) - X_(k+1)) / 2i / X).
            float slopeRe = (plainIm[k - 1] - plainIm[k + 1]) * 0.5f;
            float slopeIm = (plainRe[k + 1] - plainRe[k - 1]) * 0.5f;
            float bins = -0.5f * ((slopeIm * re) - (slopeRe * im)) / power;

            // Through it times the time from its middle; the time is the frame's plus Re(X_timed / X).
            float tre = (0.5f * timedRe[k]) - (0.25f * (timedRe[k - 1] + timedRe[k + 1]));
            float tim = (0.5f * timedIm[k]) - (0.25f * (timedIm[k - 1] + timedIm[k + 1]));
            float samples = ((tre * re) + (tim * im)) / power;

            powers[k] = power;
            binShift[k] = Math.Abs(bins) <= MaxBinShift ? bins : 0;
            timeShift[k] = Math.Abs(samples) <= furthest ? samples : 0;
        }
    }

    /// <summary>A frame's bins, 0 to half the size.</summary>
    internal sealed class Frame(int size)
    {
        /// <summary>Each bin's squared magnitude.</summary>
        public float[] Power { get; } = new float[(size / 2) + 1];

        /// <summary>How far from each bin's own frequency its energy is, in bins.</summary>
        public float[] BinShift { get; } = new float[(size / 2) + 1];

        /// <summary>How far from the frame's middle each bin's energy is, in samples.</summary>
        public float[] TimeShift { get; } = new float[(size / 2) + 1];
    }
}
