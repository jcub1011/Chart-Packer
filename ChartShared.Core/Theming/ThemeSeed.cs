namespace ChartShared.Core.Theming;

/// <summary>
/// The colour an image's theme grows from: the hue that stands out most in it, and how colourful it is there.
/// <see cref="ThemePalette.From"/> turns it into a palette.
/// </summary>
/// <param name="Hue">The Oklab hue angle, in degrees from 0 to 360.</param>
/// <param name="Chroma">The Oklab chroma: how colourful the image is at that hue.</param>
public sealed record ThemeSeed(double Hue, double Chroma)
{
    // Pixels more transparent than this aren't part of the picture.
    private const byte MinAlpha = 128;

    // Near-black and near-white pixels are backdrop, whatever faint hue they carry.
    private const double MinLightness = 0.15;
    private const double MaxLightness = 0.95;

    // Below this a pixel reads as grey.
    private const double MinChroma = 0.04;

    // The share of the picture that must be colourful before it has a seed at all.
    private const double MinColourfulShare = 0.03;

    private const int HueBins = 36;

    // The lightness where a colour counts most: mid-tones carry the hue, shadows and highlights less.
    private const double PeakLightness = 0.65;

    /// <summary>
    /// Finds the seed of an image: the hue that stands out, weighted by how colourful each pixel is rather than how
    /// common, so a vivid title on a black cover wins over the black.
    /// </summary>
    /// <param name="bgra">The pixels, 4 bytes each in blue, green, red, alpha order, alpha not premultiplied.</param>
    /// <param name="width">Pixels in a row.</param>
    /// <param name="height">Rows.</param>
    /// <param name="stride">Bytes from the start of one row to the next, at least <c>4 × <paramref name="width"/></c>.</param>
    /// <returns>The seed, or <see langword="null"/> when the image is too grey, dark or light to have one.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A size is negative, or the stride is too short for a row.</exception>
    /// <exception cref="ArgumentException"><paramref name="bgra"/> is too short for the image.</exception>
    public static ThemeSeed? Extract(ReadOnlySpan<byte> bgra, int width, int height, int stride)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, width * 4);
        if (width == 0 || height == 0)
            return null;
        if (bgra.Length < ((long)stride * (height - 1)) + (width * 4))
            throw new ArgumentException("The pixels are too short for the image.", nameof(bgra));

        // Per hue bin: total weight, the weighted hue as a vector (so 359° and 1° average to 0°), and weighted chroma.
        Span<double> weights = stackalloc double[HueBins];
        Span<double> cosines = stackalloc double[HueBins];
        Span<double> sines = stackalloc double[HueBins];
        Span<double> chromas = stackalloc double[HueBins];
        weights.Clear();
        cosines.Clear();
        sines.Clear();
        chromas.Clear();

        int opaque = 0, colourful = 0;
        for (int y = 0; y < height; y++)
        {
            var row = bgra.Slice(y * stride, width * 4);
            for (int x = 0; x < row.Length; x += 4)
            {
                if (row[x + 3] < MinAlpha)
                    continue;
                opaque++;

                var colour = Oklab.FromSrgb(row[x + 2], row[x + 1], row[x]);
                double chroma = colour.Chroma;
                if (colour.L < MinLightness || colour.L > MaxLightness || chroma < MinChroma)
                    continue;
                colourful++;

                double weight = chroma * Math.Max(0.1, 1 - (Math.Abs(colour.L - PeakLightness) / PeakLightness));
                double hue = colour.Hue;
                int bin = Math.Min(HueBins - 1, (int)(hue / (360.0 / HueBins)));
                double radians = hue * Math.PI / 180;
                weights[bin] += weight;
                cosines[bin] += weight * Math.Cos(radians);
                sines[bin] += weight * Math.Sin(radians);
                chromas[bin] += weight * chroma;
            }
        }

        if (opaque == 0 || colourful < opaque * MinColourfulShare)
            return null;

        // The peak of the histogram smoothed over neighbouring bins, so a hue split across a bin edge isn't halved.
        int peak = 0;
        double best = -1;
        for (int i = 0; i < HueBins; i++)
        {
            double smoothed = weights[Wrap(i - 1)] + (2 * weights[i]) + weights[Wrap(i + 1)];
            if (smoothed > best)
            {
                best = smoothed;
                peak = i;
            }
        }

        double totalWeight = 0, cosine = 0, sine = 0, totalChroma = 0;
        for (int offset = -1; offset <= 1; offset++)
        {
            int bin = Wrap(peak + offset);
            totalWeight += weights[bin];
            cosine += cosines[bin];
            sine += sines[bin];
            totalChroma += chromas[bin];
        }

        double seedHue = Math.Atan2(sine, cosine) * 180 / Math.PI;
        return new(seedHue < 0 ? seedHue + 360 : seedHue, totalChroma / totalWeight);
    }

    private static int Wrap(int bin) => (bin + HueBins) % HueBins;
}
