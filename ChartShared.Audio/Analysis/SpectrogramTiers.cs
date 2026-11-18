using System.Collections.Concurrent;

namespace ChartShared.Audio.Analysis;

/// <summary>
/// The three analyses a spectrogram is put together from (charter-spec C13 item 1), each of a band of the rows: a long
/// window for the low rows, where notes are a few hertz apart, and shorter ones higher up, where they're far apart and
/// what matters is when a sound starts. The low and middle bands are analysed at a sixteenth and a quarter of the
/// sample rate, which makes their long windows cheap.
/// </summary>
/// <remarks>
/// At 44.1 kHz the bands meet at 500 Hz and 2.2 kHz, about where a semitone is three bins of the band above. A quarter
/// of an octave either side of where two meet, the rows are a blend of both, so there's no seam.
/// </remarks>
internal sealed class SpectrogramTiers
{
    /// <summary>Where the low and middle bands meet at 44.1 kHz and above, in Hz.</summary>
    public const double LowMidHz = 500;

    /// <summary>Where the middle and high bands meet at 44.1 kHz and above, in Hz.</summary>
    public const double MidHighHz = 2205;

    /// <summary>How far either side of where two bands meet the rows are a blend of both, in octaves.</summary>
    public const double BlendOctaves = 0.25;

    /// <summary>How many times the rate is halved for the lowest band: the most any band is.</summary>
    public const int MostHalvings = 4;

    private static readonly ConcurrentDictionary<int, SpectrogramTiers> ByRate = new();

    private SpectrogramTiers(int sampleRate)
    {
        // The windows are as long in time at any rate, to the nearest power of two.
        int scale = Math.Clamp((int)Math.Round(Math.Log2(sampleRate / 44100.0)), -2, 3);
        double lowMid = Math.Min(LowMidHz, sampleRate * LowMidHz / 44100);
        double midHigh = Math.Min(MidHighHz, sampleRate * MidHighHz / 44100);

        var low = new float[SpectrogramRows.Count];
        var mid = new float[SpectrogramRows.Count];
        var high = new float[SpectrogramRows.Count];
        double nyquist = sampleRate / 2.0;
        FirstSilentRow = SpectrogramRows.Count;
        for (int row = 0; row < SpectrogramRows.Count; row++)
        {
            double hz = SpectrogramRows.HzOf(row);
            if (hz >= nyquist)
            {
                FirstSilentRow = Math.Min(FirstSilentRow, row);
                continue;
            }

            double overLow = Math.Clamp((Math.Log2(hz / lowMid) + BlendOctaves) / (2 * BlendOctaves), 0, 1);
            double overMid = Math.Clamp((Math.Log2(hz / midHigh) + BlendOctaves) / (2 * BlendOctaves), 0, 1);
            low[row] = (float)(1 - overLow);
            mid[row] = (float)(overLow * (1 - overMid));
            high[row] = (float)overMid;
        }

        All =
        [
            new SpectrogramTier(sampleRate, halvings: 4, Shift(512, scale), low),
            new SpectrogramTier(sampleRate, halvings: 2, Shift(1024, scale), mid),
            new SpectrogramTier(sampleRate, halvings: 0, Shift(1024, scale), high),
        ];
    }

    /// <summary>The bands, lowest first.</summary>
    public IReadOnlyList<SpectrogramTier> All { get; }

    /// <summary>The first row whose middle is at or above half the sample rate; it and those above are silent.</summary>
    public int FirstSilentRow { get; }

    public static SpectrogramTiers For(int sampleRate) => ByRate.GetOrAdd(sampleRate, rate => new SpectrogramTiers(rate));

    private static int Shift(int size, int scale) => scale >= 0 ? size << scale : size >> -scale;
}

/// <summary>One of a spectrogram's analyses: its window, the rate it's at, and how much of each row is its.</summary>
internal sealed class SpectrogramTier
{
    public SpectrogramTier(int sampleRate, int halvings, int window, float[] rowWeight)
    {
        Halvings = halvings;
        Window = window;
        Hop = Spectrogram.Hop >> halvings;
        RowWeight = rowWeight;
        MarginColumns = (((window / 2) + Hop - 1) / Hop) + 1;

        double rate = (double)sampleRate / (1 << halvings);
        BinHz = rate / window;
        RowOfBinOne = (float)SpectrogramRows.EdgeOf(BinHz);
        Map = new SpectrogramRowMap(rate, window);

        BinLow = new float[(window / 2) + 1];
        BinHigh = new float[(window / 2) + 1];
        BinWeight = new float[(window / 2) + 1];
        FirstRow = Array.FindIndex(rowWeight, w => w > 0);
        LastRow = Array.FindLastIndex(rowWeight, w => w > 0);
        if (FirstRow < 0)
        {
            FirstBin = 1;
            LastBin = 0;
            return;
        }

        // The bins that may point into its rows: those within them, and as far outside as a bin may point.
        int reach = (int)ReassignedPair.MaxBinShift + 1;
        FirstBin = Math.Max(1, (int)Math.Floor(SpectrogramRows.EdgeHz(FirstRow) / BinHz) - reach);
        LastBin = Math.Min(window / 2, (int)Math.Ceiling(SpectrogramRows.EdgeHz(LastRow + 1) / BinHz) + reach);
        for (int k = FirstBin; k <= LastBin; k++)
        {
            double low = SpectrogramRows.EdgeOf((k - 0.5) * BinHz), high = SpectrogramRows.EdgeOf(Math.Min(k + 0.5, window / 2.0) * BinHz);
            double middle = SpectrogramRows.EdgeOf(k * BinHz);
            if (high - low < 1)
                (low, high) = (middle - 0.5, middle + 0.5);
            BinLow[k] = (float)low;
            BinHigh[k] = (float)high;
            BinWeight[k] = high <= 0 || low >= SpectrogramRows.Count ? 0 : rowWeight[Math.Clamp((int)Math.Floor(middle), 0, SpectrogramRows.Count - 1)];
        }
    }

    /// <summary>How many times the audio's rate is halved before it's analysed.</summary>
    public int Halvings { get; }

    /// <summary>The window's length, in samples at its rate.</summary>
    public int Window { get; }

    /// <summary>How many samples at its rate there are between columns.</summary>
    public int Hop { get; }

    /// <summary>How many columns either side of a frame's own its energy can be put in when sharpened.</summary>
    public int MarginColumns { get; }

    /// <summary>How far apart its bins are, in Hz.</summary>
    public double BinHz { get; }

    /// <summary>Where its bin 1 is up the rows (<see cref="SpectrogramRows.EdgeOf"/>); bin <c>x</c> is the log of <c>x</c> times <see cref="SpectrogramRows.RowsPerLog"/> above it.</summary>
    public float RowOfBinOne { get; }

    /// <summary>How much of each row is this band's, 0 to 1; a row's weights over the bands add up to 1.</summary>
    public float[] RowWeight { get; }

    /// <summary>The first and last rows any of which is its; none when <see cref="FirstRow"/> is negative.</summary>
    public int FirstRow { get; }

    public int LastRow { get; }

    /// <summary>The first and last bins that can put energy in its rows.</summary>
    public int FirstBin { get; }

    public int LastBin { get; }

    /// <summary>
    /// The rows each bin's own frequencies span, from <see cref="BinLow"/> up to <see cref="BinHigh"/>
    /// (<see cref="SpectrogramRows.EdgeOf"/>) and at least a row, and how much of the row at its middle is this band's.
    /// </summary>
    public float[] BinLow { get; }

    public float[] BinHigh { get; }

    public float[] BinWeight { get; }

    /// <summary>Its bins to rows, without sharpening.</summary>
    public SpectrogramRowMap Map { get; }
}
