using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;

namespace ChartShared.UI.Controls.Highway
{
    /// <summary>
    /// The camera behind a perspective highway, as in YARG and Clone Hero: the flat highway (the <i>plane</i>, drawn as it
    /// always is) is tilted back about its bottom edge, the strike end, so its far end shrinks towards a vanishing line.
    /// Notes are drawn upright on top of it, at the places <see cref="Project"/> gives them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The plane is the host's width and <see cref="PlaneHeight"/> tall, bottom-aligned in the host, and it is tilted by
    /// <see cref="Transform"/>, a <see cref="Rotate3DTransform"/> about its bottom centre. A point <c>h</c> plane pixels above
    /// that pivot lands <c>D·h·cos(θ) / (D + h·sin(θ))</c> above it on screen, and is as wide as <c>D / (D + h·sin(θ))</c> of
    /// itself, where <c>D</c> is <see cref="Depth"/> and <c>θ</c> the tilt. The far end of any plane converges on a vanishing
    /// line at <c>D / tan(θ)</c>; a plane much taller than that collapses into it.
    /// </para>
    /// <para>
    /// A camera is described by the tilt, how wide the far end is of the near end (<see cref="FarWidth"/>) and how much of the
    /// host's height the far end reaches (<see cref="Fill"/>); the plane's height and the depth follow from those and the host.
    /// The maths here is analytic, and a test holds it to what Avalonia's transform does.
    /// </para>
    /// </remarks>
    public sealed class PerspectiveCamera
    {
        /// <summary>The tilt the highway is shown at by default, in degrees back from facing the viewer.</summary>
        public const double DefaultTilt = 62;

        /// <summary>By default the far end of the plane is half as wide as its near end.</summary>
        public const double DefaultFarWidth = 0.5;

        /// <summary>By default the far end of the plane reaches nine tenths of the way up the host.</summary>
        public const double DefaultFill = 0.9;

        /// <summary>By default the last 15% of the plane's length, by distance along it, fades out.</summary>
        public const double DefaultFadeStart = 0.85;

        /// <summary>
        /// The widest a perspective highway's columns get together, in pixels of the plane, in the editor and the preview alike: as
        /// wide as the room allows, to this, for the plane narrows towards its far end and the notes are best large near the strike line.
        /// </summary>
        public const double MaxHighwayWidth = 480;

        private readonly double _sin;
        private readonly double _cos;

        /// <param name="hostWidth">The width of the host, and of the plane.</param>
        /// <param name="hostHeight">The height of the host, which the plane is bottom-aligned in.</param>
        /// <param name="tilt">How far the plane is tilted back, in degrees; kept between 5 and 85.</param>
        /// <param name="farWidth">The width of the far end of the plane as a fraction of its near end; kept between 0.05 and 0.95.</param>
        /// <param name="fill">The fraction of the host's height the far end of the plane reaches; kept between 0.2 and 1.</param>
        public PerspectiveCamera(double hostWidth, double hostHeight, double tilt = DefaultTilt, double farWidth = DefaultFarWidth, double fill = DefaultFill)
        {
            HostWidth = Math.Max(1, hostWidth);
            HostHeight = Math.Max(1, hostHeight);
            Tilt = Math.Clamp(tilt, 5, 85);
            FarWidth = Math.Clamp(farWidth, 0.05, 0.95);
            Fill = Math.Clamp(fill, 0.2, 1);

            double radians = Tilt * Math.PI / 180;
            _sin = Math.Sin(radians);
            _cos = Math.Cos(radians);

            // The far end, h = PlaneHeight, is FarWidth · PlaneHeight · cos(θ) up the host, and Fill of the way.
            PlaneHeight = Fill * HostHeight / (FarWidth * _cos);
            Depth = PlaneHeight * _sin * FarWidth / (1 - FarWidth);
            Transform = new Rotate3DTransform { AngleX = Tilt, Depth = Depth };
        }

        public double HostWidth { get; }

        public double HostHeight { get; }

        /// <summary>How far the plane is tilted back, in degrees.</summary>
        public double Tilt { get; }

        /// <summary>The width of the far end of the plane as a fraction of its near end.</summary>
        public double FarWidth { get; }

        /// <summary>The fraction of the host's height the far end of the plane reaches.</summary>
        public double Fill { get; }

        /// <summary>The height of the plane, in its own (untilted) pixels. It is taller than the host.</summary>
        public double PlaneHeight { get; }

        /// <summary>The distance from the eye to the plane's pivot, in plane pixels.</summary>
        public double Depth { get; }

        /// <summary>
        /// The tilt as Avalonia draws it. Put it on the plane's control, which is <see cref="HostWidth"/> by
        /// <see cref="PlaneHeight"/> and bottom-aligned in the host, with its <c>RenderTransformOrigin</c> at the bottom centre;
        /// Avalonia then maps pointers through the tilt itself.
        /// </summary>
        public Rotate3DTransform Transform { get; }

        /// <summary>Where the top of the plane is in the host: it is higher than the host's own top, for the plane is taller.</summary>
        public double PlaneTop => HostHeight - PlaneHeight;

        /// <summary>The host position of the plane position <paramref name="plane"/>, relative to the plane's top left.</summary>
        public Point Project(Point plane)
        {
            double h = PlaneHeight - plane.Y;
            double denominator = Depth + (h * _sin);
            double scale = Depth / denominator;
            return new Point(
                (HostWidth / 2) + ((plane.X - (HostWidth / 2)) * scale),
                HostHeight - (Depth * h * _cos / denominator));
        }

        /// <summary>
        /// The plane position under the host position <paramref name="host"/>, or null when it is at or above the vanishing
        /// line, where the plane is no longer seen.
        /// </summary>
        public Point? Unproject(Point host)
        {
            double up = HostHeight - host.Y;
            double denominator = (Depth * _cos) - (up * _sin);
            if (denominator <= 1e-9)
                return null;

            double h = up * Depth / denominator;
            double scale = Depth / (Depth + (h * _sin));
            return new Point((HostWidth / 2) + ((host.X - (HostWidth / 2)) / scale), PlaneHeight - h);
        }

        /// <summary>How wide the plane is drawn at <paramref name="planeY"/>, as a fraction of what it is across: 1 at its near end.</summary>
        public double ScaleAt(double planeY) => Depth / (Depth + (Math.Max(0, PlaneHeight - planeY) * _sin));

        /// <summary>
        /// How tall the plane is drawn at <paramref name="planeY"/>, as a fraction of what it is along. It is foreshortened,
        /// and more so the further it is: <see cref="ScaleAt"/> squared, by the cosine of the tilt.
        /// </summary>
        public double DepthScaleAt(double planeY)
        {
            double scale = ScaleAt(planeY);
            return scale * scale * _cos;
        }

        /// <summary>
        /// How much of the plane to show at <paramref name="planeY"/>: 1 until <paramref name="fadeStart"/> of the way to its far
        /// end, by distance along the plane, then easing to 0 at the far end.
        /// </summary>
        public double OpacityAt(double planeY, double fadeStart = DefaultFadeStart)
        {
            double distance = Math.Clamp(1 - (planeY / PlaneHeight), 0, 1);
            double start = Math.Clamp(fadeStart, 0, 0.999);
            double t = Math.Clamp((distance - start) / (1 - start), 0, 1);
            return 1 - (t * t * (3 - (2 * t)));
        }

        /// <summary>
        /// The stops of a gradient down the host that hides the far end of the plane: its offsets are fractions of the host's
        /// height from its top, and its alphas how much of a covering colour to lay there, 1 at the far end and 0 from
        /// <paramref name="fadeStart"/>. Sampled where the plane's own distances land on screen, so the fade follows the perspective.
        /// </summary>
        public IReadOnlyList<(double Offset, double Alpha)> FadeStops(double fadeStart = DefaultFadeStart, int samples = 12)
        {
            var stops = new List<(double Offset, double Alpha)>();
            double top = Math.Clamp(Project(new Point(HostWidth / 2, 0)).Y / HostHeight, 0, 1);
            if (top > 0)
                stops.Add((0, 1));

            for (int i = 0; i <= samples; i++)
            {
                // The far end first, then back towards the strike end, so the offsets rise.
                double planeY = PlaneHeight * i / samples;
                double offset = Math.Clamp(Project(new Point(HostWidth / 2, planeY)).Y / HostHeight, 0, 1);
                if (stops.Count == 0 || offset > stops[^1].Offset)
                    stops.Add((offset, 1 - OpacityAt(planeY, fadeStart)));
            }

            if (stops[^1].Offset < 1)
                stops.Add((1, 0));
            return stops;
        }
    }
}
