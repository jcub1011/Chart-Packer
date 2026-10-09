using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;

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

        /// <summary>The fill of a lit column's target, drawn as opaque as the column is lit.</summary>
        public static Color TargetFill(Color color) => HighwayPalette.WithAlpha(color, 0xFF);

        /// <summary>The edge of a lit column's target.</summary>
        public static Color TargetEdge(Color color) => HighwayPalette.Lighten(color, 0.4);

        /// <summary>
        /// A struck column's beam, made once: the column's colour at the strike line, clear at the far end. Vertical for a
        /// highway, with the strike line at the bottom; across for a timeline, with the strike line on the left.
        /// </summary>
        public static IBrush Beam(Color color, bool isVertical)
        {
            if (!Beams.TryGetValue((color, isVertical), out var brush))
            {
                var stops = new[]
                {
                    new ImmutableGradientStop(0, HighwayPalette.WithAlpha(color, 0)),
                    new ImmutableGradientStop(1, HighwayPalette.WithAlpha(color, 0x70)),
                };
                Beams[(color, isVertical)] = brush = isVertical
                    ? new ImmutableLinearGradientBrush(stops, startPoint: new RelativePoint(0.5, 0, RelativeUnit.Relative), endPoint: new RelativePoint(0.5, 1, RelativeUnit.Relative))
                    : new ImmutableLinearGradientBrush(stops, startPoint: new RelativePoint(1, 0.5, RelativeUnit.Relative), endPoint: new RelativePoint(0, 0.5, RelativeUnit.Relative));
            }

            return brush;
        }
    }
}
