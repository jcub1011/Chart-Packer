namespace ChartShared.Audio.Dsp;

/// <summary>
/// Halves a signal's rate: a 31-tap half-band low-pass (a Kaiser-windowed sinc), then every other sample. Up to 0.13 of
/// the rate going in passes untouched, and what's above 0.37 of it, which would fold back under that, is taken out by
/// more than 80 dB.
/// </summary>
/// <remarks>
/// It keeps nothing between calls, and samples are counted from the signal's start: sample <c>m</c> out is the filter
/// centred on sample <c>2m</c> in, whichever stretch is asked for. So a stretch comes out the same however the signal
/// is cut into stretches, and stays in time with the signal it was made from.
/// </remarks>
internal static class HalfBandDecimator
{
    /// <summary>How many samples either side of its centre the filter reads.</summary>
    public const int Reach = 15;

    private const double Beta = 9;

    // The taps 1, 3, ... 15 samples from the centre, which are the same either side. The centre's is a half, and the
    // even ones between are zero.
    private static readonly float[] Odd = Taps();

    /// <summary>The samples going in that <paramref name="count"/> samples out from <paramref name="start"/> are made from.</summary>
    public static (long Start, int Count) InputFor(long start, int count) => ((2 * start) - Reach, count <= 0 ? 0 : (2 * (count - 1)) + (2 * Reach) + 1);

    /// <summary>
    /// Fills <paramref name="output"/> with the halved signal's samples from <paramref name="outputStart"/> on.
    /// </summary>
    /// <param name="input">The signal, from its sample <paramref name="inputStart"/>; it must hold all of <see cref="InputFor"/>.</param>
    public static void Decimate(ReadOnlySpan<float> input, long inputStart, Span<float> output, long outputStart)
    {
        var odd = Odd;
        int centre = checked((int)((2 * outputStart) - inputStart));
        if (output.Length > 0 && (centre < Reach || centre + (2 * (output.Length - 1)) + Reach >= input.Length))
            throw new ArgumentException("The input doesn't hold every sample the output is made from.", nameof(input));

        for (int i = 0; i < output.Length; i++, centre += 2)
        {
            float sum = 0.5f * input[centre];
            for (int j = 0; j < odd.Length; j++)
            {
                int offset = (2 * j) + 1;
                sum += odd[j] * (input[centre - offset] + input[centre + offset]);
            }

            output[i] = sum;
        }
    }

    private static float[] Taps()
    {
        var taps = new double[(Reach + 1) / 2];
        double total = 0;
        for (int j = 0; j < taps.Length; j++)
        {
            int n = (2 * j) + 1;
            double ideal = Math.Sin(Math.PI * n / 2) / (Math.PI * n);
            double position = (double)n / Reach;
            taps[j] = ideal * Bessel(Beta * Math.Sqrt(Math.Max(0, 1 - (position * position)))) / Bessel(Beta);
            total += taps[j];
        }

        // A steady level passes at exactly its own: the centre's half and these, twice over, add up to 1.
        var scaled = new float[taps.Length];
        for (int j = 0; j < taps.Length; j++)
            scaled[j] = (float)(taps[j] * 0.25 / total);
        return scaled;
    }

    /// <summary>The modified Bessel function of the first kind, order 0.</summary>
    private static double Bessel(double x)
    {
        double sum = 1, term = 1;
        for (int k = 1; k < 64; k++)
        {
            term *= (x / (2 * k)) * (x / (2 * k));
            sum += term;
            if (term < sum * 1e-16)
                break;
        }

        return sum;
    }
}
