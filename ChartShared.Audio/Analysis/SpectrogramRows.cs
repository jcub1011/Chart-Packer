namespace ChartShared.Audio.Analysis;

/// <summary>
/// The spectrogram's frequency rows (charter-spec C13 item 1): 768 of them, evenly spaced in pitch from 30 Hz to 16 kHz,
/// each about a seventh of a semitone wide. Row 0 is the lowest.
/// </summary>
public static class SpectrogramRows
{
    public const int Count = 768;
    public const double LowHz = 30;
    public const double HighHz = 16000;

    private static readonly double LogRatio = Math.Log(HighHz / LowHz);

    /// <summary>How many rows a factor of <c>e</c> in frequency spans.</summary>
    internal static readonly double RowsPerLog = Count / LogRatio;

    /// <summary>The frequency at the middle of <paramref name="row"/>.</summary>
    public static double HzOf(int row) => EdgeHz(row + 0.5);

    /// <summary>The frequency at <paramref name="row"/>, counted fractionally: row <c>r</c> runs from <c>EdgeHz(r)</c> to <c>EdgeHz(r + 1)</c>.</summary>
    public static double EdgeHz(double row) => LowHz * Math.Exp(LogRatio * row / Count);

    /// <summary>Where <paramref name="hz"/> is up the rows, counted fractionally from the bottom edge of row 0: <see cref="EdgeHz"/> undone.</summary>
    public static double EdgeOf(double hz) => Count * Math.Log(hz / LowHz) / LogRatio;

    /// <summary>The row <paramref name="hz"/> is in, counted fractionally from the middle of row 0.</summary>
    public static double RowOf(double hz) => (Count * Math.Log(hz / LowHz) / LogRatio) - 0.5;

    /// <summary>How many rows a semitone spans: the rows are evenly spaced in pitch, so every semitone is as many.</summary>
    public static readonly double RowsPerSemitone = Count * Math.Log(2) / 12 / LogRatio;

    /// <summary>The frequency of the note <paramref name="pitch"/>, a MIDI number, in equal temperament with A4 (69) at 440 Hz.</summary>
    public static double HzOfPitch(double pitch) => 440 * Math.Pow(2, (pitch - 69) / 12);

    /// <summary>
    /// Where the middle of <paramref name="pitch"/>'s note is down the spectrogram's picture, counted in rows from its top edge,
    /// which is the top edge of the highest row: the picture is drawn with the lowest row at the bottom, so this is
    /// <see cref="Count"/> less the pitch's fractional row from the picture's bottom edge.
    /// </summary>
    public static double PictureYOfPitch(double pitch) => Count - (RowOf(HzOfPitch(pitch)) + 0.5);

    /// <summary>The rows whose middles are between <paramref name="lowHz"/> and <paramref name="highHz"/>, clamped to the rows there are.</summary>
    public static (int First, int Last) RowsOf(double lowHz, double highHz)
    {
        int first = Math.Clamp((int)Math.Ceiling(RowOf(lowHz)), 0, Count - 1);
        int last = Math.Clamp((int)Math.Floor(RowOf(highHz)), 0, Count - 1);
        return (first, Math.Max(first, last));
    }
}

/// <summary>
/// Maps a power spectrum of one size at one sample rate to the spectrogram's rows, as levels in dB, each bin's level
/// where the bin is: the spectrogram as it's drawn without sharpening (<see cref="SpectrogramTier"/>).
/// </summary>
/// <remarks>
/// A row that holds a bin's centre takes the loudest of its bins, so a sine keeps its level. Where the rows are narrower
/// than the bins they hold none: each is read off a parabola through the levels of the louder of the two bins either
/// side of it and that bin's neighbours, which puts a low sine's peak in the right row. Rows above half the sample rate
/// are left silent.
/// </remarks>
internal sealed class SpectrogramRowMap
{
    /// <summary>The level of a row with nothing in it.</summary>
    public const float SilentDb = -240;

    // Per row: the first and last bin it takes the loudest of, or, for an interpolated row (First = -1), the bin just
    // below the row's middle and how far above it the middle is, in bins. A silent row has First = Last = -2.
    private readonly int[] _first = new int[SpectrogramRows.Count];
    private readonly int[] _last = new int[SpectrogramRows.Count];
    private readonly float[] _offset = new float[SpectrogramRows.Count];

    public SpectrogramRowMap(double sampleRate, int fftSize)
    {
        int bins = (fftSize / 2) + 1;
        double binHz = sampleRate / fftSize;
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
            last = Math.Min(last, bins - 1);
            if (first <= last)
            {
                _first[row] = first;
                _last[row] = last;
            }
            else
            {
                double x = centre / binHz;
                int below = Math.Clamp((int)Math.Floor(x), 1, bins - 3);
                _first[row] = -1;
                _last[row] = below;
                _offset[row] = (float)(x - below);
            }
        }
    }

    /// <summary>The levels of rows <paramref name="firstRow"/> to <paramref name="lastRow"/>, into <paramref name="levels"/> at each row's place.</summary>
    /// <param name="power">Bins 0 to half the FFT's size.</param>
    /// <param name="decibelOffset">Added to <c>10 log10(power)</c> so that a full-scale sine reads 0 dB.</param>
    public void Map(ReadOnlySpan<float> power, Span<float> levels, int firstRow, int lastRow, double decibelOffset)
    {
        for (int row = firstRow; row <= lastRow; row++)
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
                levels[row] = SilentDb;
                continue;
            }

            levels[row] = (float)Math.Max(SilentDb, db + decibelOffset);
        }
    }

    private static double Decibels(float power) => power <= 1e-24f ? SilentDb : 10 * Math.Log10(power);
}
