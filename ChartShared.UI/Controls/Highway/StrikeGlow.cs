using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;

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

        // How opaque a beam is at the strike line, at full brightness, before BeamOpacity, out of 255; and how many layers it's
        // built of at most (see DrawBeam). Each layer's blend is rounded to a 255th, and over many layers that adds up: 48 came out
        // 16/255 off the fade they were laid for, 16 within about 9/255, their steps a little coarser than 8-bit colour's own.
        private const int BeamAlpha = 0x70;
        private const int BeamLayers = 16;

        // The brushes beams are filled with, for each colour and opacity: a few opacities, all faint (see DrawBeam).
        private static readonly Dictionary<(Color, byte), IBrush> Beams = [];

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
        /// <para>The fade is up to <see cref="BeamLayers"/> rectangles laid over each other from the strike line, each a little
        /// shorter, all in one faint brush of the column's colour: where more of them overlap the beam is brighter.</para>
        /// <para>The opacity is in the brush, the faintest that reaches it in so many layers, not pushed on the drawing:
        /// pushed, Avalonia lays it on each fill, whose opacity is kept to a 255th, and a dim beam's layers came to nothing.
        /// A dim beam needs a fainter brush or fewer layers, a handful of brushes for each colour.</para>
        /// <para>Not a picture stretched over the column, as it was, nor a gradient brush: a beam is drawn every frame for
        /// every lit column, and Avalonia allocates a finalizable reference each time a picture is drawn, and makes a
        /// gradient's shader again, with garbage, each time it's drawn. A fill is a few bytes of the drawing's own, and the
        /// brush is only one more thing the drawing keeps.</para>
        /// </remarks>
        public static void DrawBeam(DrawingContext context, Color color, bool isVertical, Rect rect, double opacity)
        {
            // How opaque the beam is at the strike line, and the faintest layer that, BeamLayers over each other, is that opaque.
            double full = BeamAlpha / 255.0 * Math.Clamp(opacity, 0, 1);
            int alpha = (int)Math.Ceiling(255 * (1 - Math.Pow(1 - full, 1.0 / BeamLayers)) - 1e-9);
            if (full <= 0 || alpha <= 0)
                return;

            var brush = BeamOf(color, (byte)alpha);
            double layer = alpha / 255.0;
            double clear = 1, under = 0;
            for (int n = 1; n <= BeamLayers; n++)
            {
                // How opaque n layers are over each other, and n - 1: the n-th shortest layer ends where the straight fade,
                // from full at the strike line to clear at the far end, is halfway between them. The longest comes first.
                clear *= 1 - layer;
                double over = 1 - clear;
                double reach = Math.Min(1, 1 - ((over + under) / 2 / full));
                if (reach <= 0)
                    break;

                context.FillRectangle(brush, isVertical
                    ? new Rect(rect.X, rect.Bottom - (rect.Height * reach), rect.Width, rect.Height * reach)
                    : new Rect(rect.X, rect.Y, rect.Width * reach, rect.Height));
                under = over;
            }
        }

        /// <summary>The brush a beam's layers are filled with: the column's colour, <paramref name="alpha"/> 255ths opaque.</summary>
        private static IBrush BeamOf(Color color, byte alpha)
        {
            if (!Beams.TryGetValue((color, alpha), out var brush))
                Beams[(color, alpha)] = brush = new ImmutableSolidColorBrush(HighwayPalette.WithAlpha(color, alpha));
            return brush;
        }
    }
}
