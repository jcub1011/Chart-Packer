using System;

namespace ChartPacker.UI.Controls
{
    /// <summary>
    /// The spectrogram's colours (charter-spec C13 item 1): from black through violet, magenta and orange to pale yellow,
    /// with lightness rising evenly, so equal steps of level look like equal steps of brightness.
    /// </summary>
    /// <remarks>
    /// The anchor colours are blended in OKLab, a colour space where distances match how different colours look, rather
    /// than in RGB, whose blends dip in brightness between hues.
    /// </remarks>
    public static class SpectrogramColors
    {
        // Darkest to lightest; each step a little lighter than the one before.
        private static readonly (byte R, byte G, byte B)[] Anchors =
        [
            (0, 0, 4),
            (40, 11, 84),
            (101, 21, 110),
            (159, 42, 99),
            (212, 72, 66),
            (245, 125, 21),
            (250, 193, 39),
            (252, 255, 164),
        ];

        /// <summary>256 colours, from silence to full scale, as premultiplied BGRA, opaque.</summary>
        public static uint[] Ramp { get; } = BuildRamp();

        private static uint[] BuildRamp()
        {
            var labs = Array.ConvertAll(Anchors, a => ToOklab(a.R, a.G, a.B));
            var ramp = new uint[256];
            double darkest = labs[0].L, lightest = labs[^1].L;
            for (int i = 0; i < 256; i++)
            {
                // The anchors either side of this step's lightness, and how far between them it is.
                double lightness = darkest + ((lightest - darkest) * i / 255.0);
                int from = 0;
                while (from < labs.Length - 2 && labs[from + 1].L < lightness)
                    from++;
                var a = labs[from];
                var b = labs[from + 1];
                double t = b.L > a.L ? Math.Clamp((lightness - a.L) / (b.L - a.L), 0, 1) : 0;
                var (r, g, bl) = FromOklab(a.L + ((b.L - a.L) * t), a.A + ((b.A - a.A) * t), a.B + ((b.B - a.B) * t));
                ramp[i] = 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | bl;
            }

            return ramp;
        }

        private static (double L, double A, double B) ToOklab(byte r8, byte g8, byte b8)
        {
            double r = Linear(r8), g = Linear(g8), b = Linear(b8);
            double l = Math.Cbrt((0.4122214708 * r) + (0.5363325363 * g) + (0.0514459929 * b));
            double m = Math.Cbrt((0.2119034982 * r) + (0.6806995451 * g) + (0.1073969566 * b));
            double s = Math.Cbrt((0.0883024619 * r) + (0.2817188376 * g) + (0.6299787005 * b));
            return (
                (0.2104542553 * l) + (0.7936177850 * m) - (0.0040720468 * s),
                (1.9779984951 * l) - (2.4285922050 * m) + (0.4505937099 * s),
                (0.0259040371 * l) + (0.7827717662 * m) - (0.8086757660 * s));
        }

        private static (byte R, byte G, byte B) FromOklab(double lightness, double a, double b)
        {
            double l = lightness + (0.3963377774 * a) + (0.2158037573 * b);
            double m = lightness - (0.1055613458 * a) - (0.0638541728 * b);
            double s = lightness - (0.0894841775 * a) - (1.2914855480 * b);
            l *= l * l;
            m *= m * m;
            s *= s * s;
            return (
                Gamma((4.0767416621 * l) - (3.3077115913 * m) + (0.2309699292 * s)),
                Gamma((-1.2684380046 * l) + (2.6097574011 * m) - (0.3413193965 * s)),
                Gamma((-0.0041960863 * l) - (0.7034186147 * m) + (1.7076147010 * s)));
        }

        private static double Linear(byte channel)
        {
            double c = channel / 255.0;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        private static byte Gamma(double linear)
        {
            linear = Math.Clamp(linear, 0, 1);
            double c = linear <= 0.0031308 ? linear * 12.92 : (1.055 * Math.Pow(linear, 1 / 2.4)) - 0.055;
            return (byte)Math.Round(c * 255);
        }
    }
}
