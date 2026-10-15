namespace ChartShared.Core.Theming;

/// <summary>
/// A colour in Oklab, a colour space where distances match how different colours look: perceived lightness
/// <see cref="L"/> from 0 (black) to 1 (white), and the green–red <see cref="A"/> and blue–yellow <see cref="B"/> axes.
/// </summary>
/// <remarks>
/// Oklab is Björn Ottosson's, from "A perceptual color space for image processing" (2020,
/// https://bottosson.github.io/posts/oklab/). The conversions are written from the matrices published there.
/// </remarks>
public readonly record struct Oklab(double L, double A, double B)
{
    // How far outside 0–1 a linear channel may stray and still count as in gamut: rounding, not colour.
    private const double GamutTolerance = 1e-4;

    private static readonly double[] LinearTable = BuildLinearTable();

    /// <summary>How colourful: the distance from grey at the same lightness. sRGB tops out near 0.32.</summary>
    public double Chroma => Math.Sqrt((A * A) + (B * B));

    /// <summary>The hue angle in degrees, 0 to 360. Meaningless for greys.</summary>
    public double Hue
    {
        get
        {
            double hue = Math.Atan2(B, A) * 180 / Math.PI;
            return hue < 0 ? hue + 360 : hue;
        }
    }

    /// <summary>Whether this colour can be shown in sRGB without clipping a channel.</summary>
    public bool IsInGamut
    {
        get
        {
            var (r, g, b) = ToLinearRgb();
            return InUnit(r) && InUnit(g) && InUnit(b);
        }
    }

    /// <summary>The colour of lightness <paramref name="lightness"/>, chroma <paramref name="chroma"/> and hue <paramref name="hue"/> (degrees).</summary>
    public static Oklab FromLch(double lightness, double chroma, double hue)
    {
        double radians = hue * Math.PI / 180;
        return new(lightness, chroma * Math.Cos(radians), chroma * Math.Sin(radians));
    }

    /// <summary>
    /// <see cref="FromLch"/>, with the chroma lowered as little as needed to fit in sRGB, so the colour keeps its
    /// lightness and hue instead of shifting when a channel clips.
    /// </summary>
    public static Oklab FromLchInGamut(double lightness, double chroma, double hue)
    {
        lightness = Math.Clamp(lightness, 0, 1);
        var colour = FromLch(lightness, chroma, hue);
        if (colour.IsInGamut)
            return colour;

        double low = 0, high = chroma;
        for (int i = 0; i < 24; i++)
        {
            double middle = (low + high) / 2;
            if (FromLch(lightness, middle, hue).IsInGamut)
                low = middle;
            else
                high = middle;
        }

        return FromLch(lightness, low, hue);
    }

    /// <summary>The Oklab colour of an sRGB colour, ignoring its alpha.</summary>
    public static Oklab FromSrgb(ThemeColor colour) => FromSrgb(colour.R, colour.G, colour.B);

    /// <summary>The Oklab colour of an 8-bit sRGB colour.</summary>
    public static Oklab FromSrgb(byte red, byte green, byte blue)
    {
        double r = ToLinear(red), g = ToLinear(green), b = ToLinear(blue);
        double l = Math.Cbrt((0.4122214708 * r) + (0.5363325363 * g) + (0.0514459929 * b));
        double m = Math.Cbrt((0.2119034982 * r) + (0.6806995451 * g) + (0.1073969566 * b));
        double s = Math.Cbrt((0.0883024619 * r) + (0.2817188376 * g) + (0.6299787005 * b));
        return new(
            (0.2104542553 * l) + (0.7936177850 * m) - (0.0040720468 * s),
            (1.9779984951 * l) - (2.4285922050 * m) + (0.4505937099 * s),
            (0.0259040371 * l) + (0.7827717662 * m) - (0.8086757660 * s));
    }

    /// <summary>This colour in 8-bit sRGB, each channel clipped to range, with alpha <paramref name="alpha"/>.</summary>
    public ThemeColor ToSrgb(byte alpha = 255)
    {
        var (r, g, b) = ToLinearRgb();
        return new(alpha, ToGamma(r), ToGamma(g), ToGamma(b));
    }

    /// <summary>An 8-bit sRGB channel as linear light, 0 to 1.</summary>
    public static double ToLinear(byte channel) => LinearTable[channel];

    private (double R, double G, double B) ToLinearRgb()
    {
        double l = L + (0.3963377774 * A) + (0.2158037573 * B);
        double m = L - (0.1055613458 * A) - (0.0638541728 * B);
        double s = L - (0.0894841775 * A) - (1.2914855480 * B);
        l *= l * l;
        m *= m * m;
        s *= s * s;
        return (
            (4.0767416621 * l) - (3.3077115913 * m) + (0.2309699292 * s),
            (-1.2684380046 * l) + (2.6097574011 * m) - (0.3413193965 * s),
            (-0.0041960863 * l) - (0.7034186147 * m) + (1.7076147010 * s));
    }

    private static bool InUnit(double channel) => channel >= -GamutTolerance && channel <= 1 + GamutTolerance;

    private static byte ToGamma(double linear)
    {
        linear = Math.Clamp(linear, 0, 1);
        double c = linear <= 0.0031308 ? linear * 12.92 : (1.055 * Math.Pow(linear, 1 / 2.4)) - 0.055;
        return (byte)Math.Round(c * 255);
    }

    private static double[] BuildLinearTable()
    {
        var table = new double[256];
        for (int i = 0; i < table.Length; i++)
        {
            double c = i / 255.0;
            table[i] = c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return table;
    }
}
