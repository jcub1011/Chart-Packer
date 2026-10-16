using System;
using ChartShared.Core.Theming;

namespace ChartShared.UI.Controls
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
            var labs = Array.ConvertAll(Anchors, a => Oklab.FromSrgb(a.R, a.G, a.B));
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
                ramp[i] = new Oklab(a.L + ((b.L - a.L) * t), a.A + ((b.A - a.A) * t), a.B + ((b.B - a.B) * t)).ToSrgb().ToArgb();
            }

            return ramp;
        }
    }
}
