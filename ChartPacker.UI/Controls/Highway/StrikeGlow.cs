using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace ChartPacker.UI.Controls.Highway
{
    /// <summary>
    /// How a highway's column lights up as a note reaches its strike line, for every view that plays notes: Chart
    /// Packer's highway and Chart Builder's editor. A beam rises from the strike line and the column's target fills in,
    /// then fades: at once for a plain note, and over a sustain's length to <see cref="SustainEndLevel"/> at its end, then
    /// quickly out.
    /// </summary>
    public static class StrikeGlow
    {
        /// <summary>How long a column stays lit after a plain note reaches it, in seconds.</summary>
        public const double Seconds = 0.16;

        /// <summary>How brightly a column is still lit as a sustain ends.</summary>
        public const double SustainEndLevel = 0.3;

        /// <summary>How long a column takes to go out after a sustain ends, in seconds.</summary>
        public const double ReleaseSeconds = 0.1;

        /// <summary>How long after a note reaches the strike line its column can still be lit, beyond its sustain.</summary>
        public const double Lingers = Seconds > ReleaseSeconds ? Seconds : ReleaseSeconds;

        /// <summary>How brightly an open note or a kick lights every column, as a share of its own glow.</summary>
        public const double SpreadLevel = 0.6;

        /// <summary>How far a beam rises from the strike line, as a share of the way to the far end of the highway.</summary>
        public const double BeamReach = 0.55;

        /// <summary>How opaque a column's beam is at full brightness.</summary>
        public const double BeamOpacity = 0.8;

        // How opaque a beam is at the strike line, at full brightness, before BeamOpacity, out of 255; and how many steps its
        // picture fades over, which smoothing stretches over any column without banding.
        private const int BeamAlpha = 0x70;
        private const int BeamSteps = 256;

        // A gradient's pixels, smoothed between when stretched.
        private static readonly RenderOptions Smooth = new() { BitmapInterpolationMode = BitmapInterpolationMode.LowQuality };

        private static readonly Dictionary<(Color, bool), Bitmap> Beams = [];

        /// <summary>
        /// How brightly a note lights its column, from 0 to 1, <paramref name="since"/> seconds after it reached the strike
        /// line: full as it's struck, then fading. A plain note fades out over <see cref="Seconds"/>; a sustain
        /// <paramref name="sustainSeconds"/> long fades steadily to <see cref="SustainEndLevel"/> as it ends, then out over
        /// <see cref="ReleaseSeconds"/>.
        /// </summary>
        /// <param name="sustainSeconds">How long the note is held, as the games read it; 0 for a plain note.</param>
        public static double Brightness(double since, double sustainSeconds)
        {
            if (since < 0)
                return 0;
            if (sustainSeconds <= 0)
                return since < Seconds ? 1 - (since / Seconds) : 0;
            if (since < sustainSeconds)
                return 1 - ((1 - SustainEndLevel) * since / sustainSeconds);
            double released = since - sustainSeconds;
            return released < ReleaseSeconds ? SustainEndLevel * (1 - (released / ReleaseSeconds)) : 0;
        }

        /// <summary>The fill of a lit column's target, drawn as opaque as the column is lit.</summary>
        public static Color TargetFill(Color color) => HighwayPalette.WithAlpha(color, 0xFF);

        /// <summary>The edge of a lit column's target.</summary>
        public static Color TargetEdge(Color color) => HighwayPalette.Lighten(color, 0.4);

        /// <summary>
        /// Draws a struck column's beam over <paramref name="rect"/>: the column's colour at the strike line, clear at the
        /// far end. Vertical for a highway, with the strike line at the bottom; across for a timeline, with the strike line
        /// on the left.
        /// </summary>
        /// <remarks>
        /// A picture of the beam, made once and stretched over the column, rather than a gradient brush: a gradient has
        /// its shader made again each time it's drawn, and a beam is drawn every frame for every lit column.
        /// </remarks>
        public static void DrawBeam(DrawingContext context, Color color, bool isVertical, Rect rect)
        {
            using (context.PushRenderOptions(Smooth))
                context.DrawImage(BeamOf(color, isVertical), rect);
        }

        /// <summary>The beam's picture: <see cref="BeamSteps"/> pixels from clear to the strike line's end, one across.</summary>
        private static Bitmap BeamOf(Color color, bool isVertical)
        {
            if (Beams.TryGetValue((color, isVertical), out var beam))
                return beam;

            // Premultiplied BGRA, from the top of a highway down, or from a timeline's left across.
            var pixels = new uint[BeamSteps];
            for (int i = 0; i < BeamSteps; i++)
            {
                double along = (double)i / (BeamSteps - 1);
                double share = (isVertical ? along : 1 - along) * BeamAlpha / 255;
                uint alpha = (uint)Math.Round(share * 255);
                uint red = (uint)Math.Round(color.R * share);
                uint green = (uint)Math.Round(color.G * share);
                uint blue = (uint)Math.Round(color.B * share);
                pixels[i] = (alpha << 24) | (red << 16) | (green << 8) | blue;
            }

            var size = isVertical ? new PixelSize(1, BeamSteps) : new PixelSize(BeamSteps, 1);
            var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                beam = new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Premul, handle.AddrOfPinnedObject(), size, new Vector(96, 96), size.Width * sizeof(uint));
            }
            finally
            {
                handle.Free();
            }

            Beams[(color, isVertical)] = beam;
            return beam;
        }
    }
}
