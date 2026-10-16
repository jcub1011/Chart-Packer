using System.Collections.Concurrent;

namespace ChartShared.Audio.Analysis;

/// <summary>
/// The spectrogram's frequency rows (charter-spec C13 item 1): 256 of them, evenly spaced in pitch from 30 Hz to 16 kHz,
/// each about 0.42 of a semitone wide. Row 0 is the lowest.
/// </summary>
public static class SpectrogramRows
{
    public const int Count = 256;
    public const double LowHz = 30;
    public const double HighHz = 16000;

    private static readonly double LogRatio = Math.Log(HighHz / LowHz);

    /// <summary>The frequency at the middle of <paramref name="row"/>.</summary>
    public static double HzOf(int row) => EdgeHz(row + 0.5);

    /// <summary>The frequency at <paramref name="row"/>, counted fractionally: row <c>r</c> runs from <c>EdgeHz(r)</c> to <c>EdgeHz(r + 1)</c>.</summary>
    public static double EdgeHz(double row) => LowHz * Math.Exp(LogRatio * row / Count);

    /// <summary>The row <paramref name="hz"/> is in, counted fractionally from the middle of row 0.</summary>
    public static double RowOf(double hz) => (Count * Math.Log(hz / LowHz) / LogRatio) - 0.5;

    /// <summary>The rows whose middles are between <paramref name="lowHz"/> and <paramref name="highHz"/>, clamped to the rows there are.</summary>
    public static (int First, int Last) RowsOf(double lowHz, double highHz)
    {
        int first = Math.Clamp((int)Math.Ceiling(RowOf(lowHz)), 0, Count - 1);
        int last = Math.Clamp((int)Math.Floor(RowOf(highHz)), 0, Count - 1);
        return (first, Math.Max(first, last));
    }
}

/// <summary>
/// Maps a 2048-point power spectrum at one sample rate to the spectrogram's rows, as bytes of absolute level
/// (<see cref="Spectrogram.LevelOf"/>).
/// </summary>
/// <remarks>
/// A row that holds an FFT bin's centre takes the loudest of its bins, so a sine keeps its level. Below about 870 Hz the
/// rows are narrower than the bins, and hold none: each is read off a parabola through the levels of the louder of the
/// two bins either side of it and that bin's neighbours, which puts a low sine's peak in the right row. Rows above half the sample rate are left silent.
/// </remarks>
internal sealed class SpectrogramRowMap
{
    private static readonly ConcurrentDictionary<int, SpectrogramRowMap> Maps = new();

    // Per row: the first and last bin it takes the loudest of, or, for an interpolated row (First = -1), the bin just
    // below the row's middle and how far above it the middle is, in bins. A silent row has First = Last = -2.
    private readonly int[] _first = new int[SpectrogramRows.Count];
    private readonly int[] _last = new int[SpectrogramRows.Count];
    private readonly float[] _offset = new float[SpectrogramRows.Count];
    private readonly int _bins;

    private SpectrogramRowMap(int sampleRate, int fftSize)
    {
        _bins = (fftSize / 2) + 1;
        double binHz = (double)sampleRate / fftSize;
        double nyquist = sampleRate / 2.0;
        for (int row = 0; row < SpectrogramRows.Count; row++)
        {
            double centre = SpectrogramRows.HzOf(row);
            if (centre >= nyquist)
            {
                _first[row] = _last[row] = -2;
                continue;
            }

            int first = (int)Math.Ceiling(SpectrogramRows.EdgeHz(row) / binHz);
            int last = (int)Math.Ceiling(SpectrogramRows.EdgeHz(row + 1) / binHz) - 1;
            last = Math.Min(last, _bins - 1);
            if (first <= last)
            {
                _first[row] = first;
                _last[row] = last;
            }
            else
            {
                double x = centre / binHz;
                int below = Math.Clamp((int)Math.Floor(x), 1, _bins - 3);
                _first[row] = -1;
                _last[row] = below;
                _offset[row] = (float)(x - below);
            }
        }
    }

    public static SpectrogramRowMap For(int sampleRate) => Maps.GetOrAdd(sampleRate, rate => new SpectrogramRowMap(rate, Spectrogram.WindowSize));

    /// <param name="power">Bins 0 to <c>WindowSize / 2</c>.</param>
    /// <param name="decibelOffset">Added to <c>10 log10(power)</c> so that a full-scale sine reads 0 dB.</param>
    public void Map(ReadOnlySpan<float> power, Span<byte> column, double decibelOffset)
    {
        for (int row = 0; row < SpectrogramRows.Count; row++)
        {
            int first = _first[row];
            double db;
            if (first >= 0)
            {
                float loudest = 0;
                for (int k = first; k <= _last[row]; k++)
                    loudest = Math.Max(loudest, power[k]);
                db = Decibels(loudest);
            }
            else if (first == -1)
            {
                // Centred on the louder of the bins either side, so the parabola's peak is the spectrum's.
                int k = _last[row];
                double d = _offset[row];
                if (power[k + 1] > power[k])
                {
                    k++;
                    d--;
                }

                double below = Decibels(power[k - 1]);
                double at = Decibels(power[k]);
                double above = Decibels(power[k + 1]);
                db = at + (d * (above - below) / 2) + (d * d * (above - (2 * at) + below) / 2);
            }
            else
            {
                column[row] = 0;
                continue;
            }

            column[row] = Spectrogram.LevelOf(db + decibelOffset);
        }
    }

    private static double Decibels(float power) => power <= 1e-24f ? -240 : 10 * Math.Log10(power);
}
