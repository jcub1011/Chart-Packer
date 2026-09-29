namespace ChartPacker.Audio.Spectrum;

/// <summary>
/// How loud each of <see cref="BandCount"/> frequency bands is in a window of audio, for drawing a spectrum as
/// bars: the bands are spaced evenly in pitch from 40 Hz to 16 kHz, and each level runs from 0 (60 dB down, or
/// quieter) to 1 (a full-scale sine).
/// </summary>
/// <remarks>
/// A Hann-windowed radix-2 FFT (Cooley–Tukey, decimation in time). Each band takes its loudest bin, tilted up
/// 3 dB an octave about 1 kHz, as music has less energy the higher it goes. Nothing is allocated per window.
/// Not thread-safe.
/// </remarks>
public sealed class SpectrumAnalyzer
{
    /// <summary>How many samples each analysis takes: about 43 ms at 48 kHz.</summary>
    public const int FftSize = 2048;

    public const int BandCount = 24;

    private const double LowestHz = 40;
    private const double HighestHz = 16000;
    private const double FloorDb = -60;
    private const double TiltDbPerOctave = 3;
    private const double TiltCentreHz = 1000;

    private readonly float[] _window = new float[FftSize];
    private readonly float _windowSum;
    private readonly int[] _reversed = new int[FftSize];
    private readonly float[] _cos = new float[FftSize / 2];
    private readonly float[] _sin = new float[FftSize / 2];
    private readonly float[] _re = new float[FftSize];
    private readonly float[] _im = new float[FftSize];

    // Each band's bins, first and last inclusive, and its tilt in dB, for _bandsRate.
    private readonly (int First, int Last, float TiltDb)[] _bands = new (int, int, float)[BandCount];
    private int _bandsRate;

    public SpectrumAnalyzer()
    {
        int bits = (int)Math.Log2(FftSize);
        for (int i = 0; i < FftSize; i++)
        {
            // The periodic Hann window.
            _window[i] = (float)(0.5 * (1 - Math.Cos(2 * Math.PI * i / FftSize)));
            _windowSum += _window[i];

            int reversed = 0;
            for (int b = 0; b < bits; b++)
                reversed |= ((i >> b) & 1) << (bits - 1 - b);
            _reversed[i] = reversed;
        }

        for (int i = 0; i < FftSize / 2; i++)
        {
            _cos[i] = (float)Math.Cos(2 * Math.PI * i / FftSize);
            _sin[i] = (float)Math.Sin(2 * Math.PI * i / FftSize);
        }
    }

    /// <summary>Fills <paramref name="levels"/> with the level of each band in <paramref name="samples"/>, at <paramref name="sampleRate"/>.</summary>
    /// <param name="samples"><see cref="FftSize"/> mono samples, oldest first.</param>
    /// <param name="levels"><see cref="BandCount"/> levels, lowest band first.</param>
    public void Analyze(ReadOnlySpan<float> samples, int sampleRate, Span<float> levels)
    {
        if (samples.Length != FftSize)
            throw new ArgumentException($"There must be {FftSize} samples.", nameof(samples));
        if (levels.Length != BandCount)
            throw new ArgumentException($"There must be {BandCount} levels.", nameof(levels));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);

        if (sampleRate != _bandsRate)
            PlaceBands(sampleRate);

        Transform(samples);

        for (int band = 0; band < BandCount; band++)
        {
            var (first, last, tilt) = _bands[band];
            float loudest = 0;
            for (int bin = first; bin <= last; bin++)
                loudest = Math.Max(loudest, (_re[bin] * _re[bin]) + (_im[bin] * _im[bin]));

            // A sine of amplitude A peaks at A times half the window's sum.
            double amplitude = 2 * Math.Sqrt(loudest) / _windowSum;
            double db = amplitude > 0 ? (20 * Math.Log10(amplitude)) + tilt : double.NegativeInfinity;
            levels[band] = (float)Math.Clamp((db - FloorDb) / -FloorDb, 0, 1);
        }
    }

    /// <summary>The FFT of the windowed <paramref name="samples"/>, into <see cref="_re"/> and <see cref="_im"/>.</summary>
    private void Transform(ReadOnlySpan<float> samples)
    {
        for (int i = 0; i < FftSize; i++)
        {
            _re[_reversed[i]] = samples[i] * _window[i];
            _im[_reversed[i]] = 0;
        }

        for (int size = 2; size <= FftSize; size *= 2)
        {
            int half = size / 2;
            int step = FftSize / size;
            for (int start = 0; start < FftSize; start += size)
            {
                for (int k = 0; k < half; k++)
                {
                    // The twiddle factor e^(-2πik/size).
                    float wr = _cos[k * step];
                    float wi = -_sin[k * step];
                    int a = start + k;
                    int b = a + half;
                    float xr = (_re[b] * wr) - (_im[b] * wi);
                    float xi = (_re[b] * wi) + (_im[b] * wr);
                    _re[b] = _re[a] - xr;
                    _im[b] = _im[a] - xi;
                    _re[a] += xr;
                    _im[a] += xi;
                }
            }
        }
    }

    /// <summary>Works out which bins each band covers at <paramref name="sampleRate"/>.</summary>
    private void PlaceBands(int sampleRate)
    {
        double binHz = (double)sampleRate / FftSize;
        int lastBin = (FftSize / 2) - 1;
        double top = Math.Min(HighestHz, sampleRate / 2.0);
        for (int band = 0; band < BandCount; band++)
        {
            double low = LowestHz * Math.Pow(top / LowestHz, (double)band / BandCount);
            double high = LowestHz * Math.Pow(top / LowestHz, (double)(band + 1) / BandCount);
            double centre = Math.Sqrt(low * high);

            int first = (int)Math.Ceiling(low / binHz);
            int last = (int)Math.Ceiling(high / binHz) - 1;

            // A band narrower than a bin, low down, takes the bin nearest its centre.
            if (last < first)
                first = last = (int)Math.Round(centre / binHz);

            first = Math.Clamp(first, 1, lastBin);
            last = Math.Clamp(last, first, lastBin);
            _bands[band] = (first, last, (float)(TiltDbPerOctave * Math.Log2(centre / TiltCentreHz)));
        }

        _bandsRate = sampleRate;
    }
}
