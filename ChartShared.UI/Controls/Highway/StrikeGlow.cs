using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using ChartShared.UI.Controls.Skia;

namespace ChartShared.UI.Controls.Highway
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

        // How opaque a beam is at the strike line, at full brightness, before BeamOpacity, out of 255.
        private const byte BeamAlpha = 0x70;

        // The unit square a beam's gradient is drawn over, stretched onto the column (see DrawBeam).
        private static readonly Rect Unit = new(0, 0, 1, 1);

        // The gradients beams are filled with, for each colour, up a highway or across a timeline (see DrawBeam).
        private static readonly Dictionary<(Color, bool), IBrush> Beams = [];

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

        /// <summary>
        /// Lights <paramref name="column"/> of the first <paramref name="count"/> in <paramref name="hits"/> as brightly as
        /// <paramref name="glow"/> says, unless it's already lit more. A note with no column of its own (an open note, a
        /// kick: <paramref name="column"/> below zero) lights every one, by <see cref="SpreadLevel"/> of its glow.
        /// </summary>
        public static void Light(double[] hits, int count, int column, double glow)
        {
            if (column < 0)
            {
                for (int c = 0; c < count; c++)
                    hits[c] = Math.Max(hits[c], glow * SpreadLevel);
            }
            else
            {
                hits[column] = Math.Max(hits[column], glow);
            }
        }

        /// <summary>The fill of a lit column's target, drawn as opaque as the column is lit.</summary>
        public static Color TargetFill(Color color) => HighwayPalette.WithAlpha(color, 0xFF);

        /// <summary>The edge of a lit column's target.</summary>
        public static Color TargetEdge(Color color) => HighwayPalette.Lighten(color, 0.4);

        /// <summary>
        /// Draws a struck column's beam over <paramref name="rect"/>, <paramref name="opacity"/> as opaque as it is at its
        /// brightest: the column's colour at the strike line, clear at the far end. Vertical for a highway, with the strike line
        /// at the bottom; across for a timeline, with the strike line on the left.
        /// </summary>
        /// <remarks>
        /// <para>One rectangle filled with a straight gradient, from the column's colour at the strike line to clear at the far
        /// end, faded as a whole by <paramref name="opacity"/>. It was once rectangles laid over each other, each a little
        /// shorter, which showed as steps up the beam, plainest over a sustain's long glow.</para>
        /// <para>The gradient is drawn over the unit square, stretched onto <paramref name="rect"/> by a transform, so the
        /// shader SkiaSharp draws it with is made once for each colour and kept (see <see cref="SkiaShaders"/>), however the
        /// beam moves or grows from frame to frame: a gradient over the beam's own rectangle would be made again each time
        /// it moved.</para>
        /// </remarks>
        public static void DrawBeam(IHighwayCanvas context, Color color, bool isVertical, Rect rect, double opacity)
        {
            double alpha = Math.Clamp(opacity, 0, 1);
            if (alpha <= 0 || rect.Width <= 0 || rect.Height <= 0)
                return;

            using (context.PushTransform(new Matrix(rect.Width, 0, 0, rect.Height, rect.X, rect.Y)))
            using (context.PushOpacity(alpha))
                context.FillRectangle(BeamOf(color, isVertical), Unit);
        }

        /// <summary>
        /// The gradient a beam is filled with over the unit square: the column's colour, <see cref="BeamAlpha"/> 255ths opaque,
        /// at the strike line (the bottom up a highway, the left across a timeline), clear at the far end.
        /// </summary>
        private static IBrush BeamOf(Color color, bool isVertical)
        {
            if (Beams.TryGetValue((color, isVertical), out var brush))
                return brush;

            brush = new ImmutableLinearGradientBrush(
                [
                    new ImmutableGradientStop(0, HighwayPalette.WithAlpha(color, BeamAlpha)),
                    new ImmutableGradientStop(1, HighwayPalette.WithAlpha(color, 0)),
                ],
                startPoint: new RelativePoint(0, isVertical ? 1 : 0, RelativeUnit.Relative),
                endPoint: new RelativePoint(isVertical ? 0 : 1, 0, RelativeUnit.Relative));
            Beams[(color, isVertical)] = brush;
            return brush;
        }
    }
}
