using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.TextFormatting;
using Serilog;
using SkiaSharp;

namespace ChartShared.UI.Controls.Skia
{
    /// <summary>
    /// A drawing kept to be drawn by SkiaSharp on the render thread: what a highway draws in a frame, recorded on the UI
    /// thread into arrays it keeps from frame to frame, so recording a frame makes no garbage once they're big enough.
    /// <see cref="DrawListPlayer"/> draws it.
    /// </summary>
    /// <remarks>
    /// <para>Whatever only the UI thread may read is read as it's recorded: a brush's colour, and the opacity pushed, are worked
    /// into each shape's colour, as Avalonia's Skia backend works them in (it doesn't draw an opacity on a layer of its
    /// own), so the render thread is only handed numbers and SkiaSharp objects that never change.</para>
    /// <para>Its <see cref="Bounds"/> are worked out as Avalonia works out a recorded drawing's: a layer kept as a picture is a
    /// picture of exactly that much, so the same bounds put its pixels in the same places.</para>
    /// </remarks>
    public sealed class DrawList : IHighwayCanvas
    {
        private static bool s_warnedBrush;

        private Op[] _ops = new Op[256];
        private object?[] _resources = new object?[64];
        private SKMatrix[] _matrices = new SKMatrix[16];
        private int _matrixCount;

        // What's pushed, as it's recorded.
        private double _opacity = 1;
        private RenderOptions _options;
        private readonly Stack<double> _opacities = new();
        private readonly Stack<RenderOptions> _optionsPushed = new();

        // Where what's drawn reaches, in the coordinates of the transform pushed last, and, for each transform pushed, what
        // was reached before it and the transform, which takes its own when it's popped.
        private Rect? _bounds;
        private readonly Stack<(Rect? Before, Matrix Matrix)> _transforms = new();

        internal int Count { get; private set; }

        internal int ResourceCount { get; private set; }

        internal ReadOnlySpan<Op> Ops => _ops.AsSpan(0, Count);

        internal object? Resource(int index) => _resources[index];

        internal ref readonly SKMatrix MatrixAt(int index) => ref _matrices[index];

        /// <summary>Where anything was drawn, in the host's coordinates: what it's culled, invalidated and kept as a picture by.</summary>
        public Rect Bounds => _bounds ?? default;

        /// <summary>
        /// Whether text is drawn in greyscale rather than LCD: as Avalonia draws it on a picture it keeps, such as a layer cached
        /// as a bitmap, whose pixels may land anywhere when it's drawn. Set by the host as it records.
        /// </summary>
        public bool GreyscaleText { get; set; }

        /// <summary>Where the host's visual is placed for this drawing, which is drawn moved back by as much: set by the host as it records.</summary>
        internal Point Origin { get; set; }

        /// <summary>Empties this to record a new drawing, letting go of the pictures it held.</summary>
        public void Reset()
        {
            for (int i = 0; i < ResourceCount; i++)
            {
                if (_resources[i] is HighwayImage image)
                    image.Unhold();
                _resources[i] = null;
            }

            Count = 0;
            ResourceCount = 0;
            _matrixCount = 0;
            _opacity = 1;
            _options = default;
            _opacities.Clear();
            _optionsPushed.Clear();
            _bounds = null;
            _transforms.Clear();
        }

        public void DrawRectangle(IBrush? brush, IPen? pen, Rect rect, double radiusX = 0, double radiusY = 0)
        {
            bool penShows = PenShows(pen);
            if (brush is null && !penShows)
                return;

            // What Avalonia records, it counts in its bounds, the pen's half width round it, even if it draws nothing.
            if (!IsEmpty(rect))
                Include(rect.Inflate((pen?.Thickness ?? 0) / 2));

            if (radiusX != 0)
                radiusX = Math.Min(radiusX, rect.Width / 2);
            if (radiusY != 0)
                radiusY = Math.Min(radiusY, rect.Height / 2);
            if (rect.Width <= 0 || rect.Height <= 0)
                return;

            bool rounded = radiusX != 0 || radiusY != 0;
            var radii = new SKRect((float)radiusX, (float)radiusY, 0, 0);
            if (brush is not null)
            {
                ref var op = ref Add(rounded ? OpKind.RoundRect : OpKind.Rect);
                op.Rect = ToSK(rect);
                op.Src = radii;
                Fill(ref op, brush, rect);
            }

            if (penShows)
            {
                ref var op = ref Add(rounded ? OpKind.RoundRect : OpKind.Rect);
                op.Rect = ToSK(rect);
                op.Src = radii;
                Stroke(ref op, pen!, rect.Inflate(pen!.Thickness / 2));
            }
        }

        public void DrawRectangle(IPen pen, Rect rect, float cornerRadius = 0) => DrawRectangle(null, pen, rect, cornerRadius, cornerRadius);

        public void FillRectangle(IBrush brush, Rect rect, float cornerRadius = 0) => DrawRectangle(brush, null, rect, cornerRadius, cornerRadius);

        public void DrawLine(IPen pen, Point p1, Point p2)
        {
            if (!PenShows(pen))
                return;
            Include(LineBounds(p1, p2, pen));
            ref var op = ref Add(OpKind.Line);
            op.Rect = new SKRect((float)p1.X, (float)p1.Y, (float)p2.X, (float)p2.Y);
            Stroke(ref op, pen, new Rect(p1, p2).Normalize());
        }

        public void DrawGeometry(IBrush? brush, IPen? pen, HighwayPath path)
        {
            bool penShows = PenShows(pen);
            if (brush is null && !penShows)
                return;
            var bounds = path.Bounds;
            Include(path.ReachWith(pen));
            if (brush is not null && path.FillPath is { } fill)
            {
                ref var op = ref Add(OpKind.Path);
                op.Res = AddResource(fill);
                Fill(ref op, brush, bounds);
            }

            if (penShows)
            {
                ref var op = ref Add(OpKind.Path);
                op.Res = AddResource(path.StrokePath);
                Stroke(ref op, pen!, bounds.Inflate(pen!.Thickness / 2));
            }
        }

        public void DrawImage(HighwayImage image, Rect sourceRect, Rect destRect)
        {
            if (IsEmpty(sourceRect) || IsEmpty(destRect))
                return;
            image.Hold();
            Include(destRect);
            ref var op = ref Add(OpKind.Image);
            op.Res = AddResource(image);
            op.Rect = ToSK(destRect);
            op.Src = ToSK(sourceRect);
            op.Color = Argb((byte)(255.0 * _opacity), 255, 255, 255);
            op.Antialias = _options.EdgeMode != EdgeMode.Aliased;
            bool upscaling = destRect.Width > sourceRect.Width || destRect.Height > sourceRect.Height;
            op.Sampling = _options.BitmapInterpolationMode switch
            {
                BitmapInterpolationMode.None => Sampling.Nearest,
                BitmapInterpolationMode.MediumQuality => Sampling.Mipmapped,
                BitmapInterpolationMode.HighQuality => upscaling ? Sampling.Cubic : Sampling.Mipmapped,
                _ => Sampling.Linear,
            };
        }

        public void DrawText(TextLayout text, Point origin)
        {
            foreach (var run in SkiaText.Of(text).Items)
            {
                var bounds = run.Bounds.Translate(new Vector(origin.X, origin.Y));
                Include(bounds);
                ref var op = ref Add(OpKind.Text);
                op.Res = AddResource(run.Blob);
                op.Res3 = AddResource(run.GreyBlob);
                op.Rect = new SKRect((float)(origin.X + run.X), (float)(origin.Y + run.Y), 0, 0);
                Fill(ref op, run.Brush, bounds);
            }
        }

        public CanvasState PushOpacity(double opacity)
        {
            _opacities.Push(_opacity);
            _opacity *= opacity;
            return new CanvasState(this, CanvasStateKind.Opacity);
        }

        public CanvasState PushClip(Rect clip)
        {
            Add(OpKind.Save);
            ref var op = ref Add(OpKind.ClipRect);
            op.Rect = ToSK(clip);
            return new CanvasState(this, CanvasStateKind.Clip);
        }

        public CanvasState PushGeometryClip(HighwayPath clip)
        {
            Add(OpKind.Save);
            if (clip.FillPath is { } fill)
            {
                ref var op = ref Add(OpKind.ClipPath);
                op.Res = AddResource(fill);
            }
            else
            {
                // Nothing filled: nothing shows through it.
                ref var op = ref Add(OpKind.ClipRect);
                op.Rect = SKRect.Empty;
            }

            return new CanvasState(this, CanvasStateKind.GeometryClip);
        }

        public CanvasState PushTransform(Matrix matrix)
        {
            Add(OpKind.Save);
            if (_matrixCount == _matrices.Length)
                Array.Resize(ref _matrices, _matrices.Length * 2);
            _matrices[_matrixCount] = ToSK(matrix);
            ref var op = ref Add(OpKind.Concat);
            op.Res = _matrixCount++;
            _transforms.Push((_bounds, matrix));
            _bounds = null;
            return new CanvasState(this, CanvasStateKind.Transform);
        }

        public CanvasState PushOpacityMask(IBrush mask, Rect bounds)
        {
            ref var op = ref Add(OpKind.MaskBegin);
            op.Rect = ToSK(bounds);
            Fill(ref op, mask, bounds);
            return new CanvasState(this, CanvasStateKind.OpacityMask);
        }

        public CanvasState PushRenderOptions(RenderOptions options)
        {
            _optionsPushed.Push(_options);
            _options = _options.MergeWith(options);
            return new CanvasState(this, CanvasStateKind.RenderOptions);
        }

        public void Pop(CanvasStateKind kind)
        {
            switch (kind)
            {
                case CanvasStateKind.Opacity:
                    _opacity = _opacities.Pop();
                    break;
                case CanvasStateKind.RenderOptions:
                    _options = _optionsPushed.Pop();
                    break;
                case CanvasStateKind.OpacityMask:
                    Add(OpKind.MaskEnd);
                    break;
                case CanvasStateKind.Transform:
                    Add(OpKind.Restore);
                    var (before, matrix) = _transforms.Pop();
                    _bounds = Union(before, _bounds?.TransformToAABB(matrix));
                    break;
                default:
                    Add(OpKind.Restore);
                    break;
            }
        }

        private ref Op Add(OpKind kind)
        {
            if (Count == _ops.Length)
                Array.Resize(ref _ops, _ops.Length * 2);
            ref var op = ref _ops[Count++];
            op = default;
            op.Kind = kind;
            op.Res = -1;
            op.Res2 = -1;
            op.Res3 = -1;
            op.Antialias = _options.EdgeMode != EdgeMode.Aliased;
            return ref op;
        }

        private int AddResource(object resource)
        {
            if (ResourceCount == _resources.Length)
                Array.Resize(ref _resources, _resources.Length * 2);
            _resources[ResourceCount] = resource;
            return ResourceCount++;
        }

        /// <summary>Widens <see cref="Bounds"/> to take in <paramref name="rect"/>, as Avalonia's bounds take in each thing recorded.</summary>
        private void Include(Rect rect) => _bounds = Union(_bounds, rect);

        /// <summary>Two bounds together, as Avalonia puts them together: one with neither width nor height is no bounds at all.</summary>
        private static Rect? Union(Rect? left, Rect? right) =>
            left is not { } a ? right : right is not { } b ? a : a.Union(b);

        private static bool IsEmpty(Rect rect) => rect.Width == 0 && rect.Height == 0;

        /// <summary>
        /// Where a line in <paramref name="pen"/> reaches, as Avalonia works it out: its ends moved out across it by half the pen's
        /// width, and, with a cap, along it too.
        /// </summary>
        private static Rect LineBounds(Point p1, Point p2, IPen pen)
        {
            double angle = Math.Atan2(p2.Y - p1.Y, p2.X - p1.X);
            double half = pen.Thickness / 2;
            if (pen.LineCap != PenLineCap.Flat)
                (p1, p2) = Spread(p1, p2, angle - (Math.PI / 2), half);
            (p1, p2) = Spread(p1, p2, angle, half);
            return new Rect(p1, p2);

            static (Point TopLeft, Point BottomRight) Spread(Point p1, Point p2, double angle, double distance)
            {
                double across = Math.Sin(angle) * distance, along = Math.Cos(angle) * distance;
                var a = new Point(p1.X + across, p1.Y - along);
                var b = new Point(p1.X - across, p1.Y + along);
                var c = new Point(p2.X + across, p2.Y - along);
                var d = new Point(p2.X - across, p2.Y + along);
                return (new Point(Math.Min(a.X, Math.Min(b.X, Math.Min(c.X, d.X))), Math.Min(a.Y, Math.Min(b.Y, Math.Min(c.Y, d.Y)))),
                    new Point(Math.Max(a.X, Math.Max(b.X, Math.Max(c.X, d.X))), Math.Max(a.Y, Math.Max(b.Y, Math.Max(c.Y, d.Y)))));
            }
        }

        private static bool PenShows(IPen? pen) => pen is { Brush: not null } && pen.Thickness > 0;

        /// <summary>Fills <paramref name="op"/> with <paramref name="brush"/>, as Avalonia's Skia backend makes a brush's paint.</summary>
        private void Fill(ref Op op, IBrush brush, Rect target)
        {
            double opacity = brush.Opacity * _opacity;
            if (brush is ISolidColorBrush solid)
            {
                var color = solid.Color;
                op.Color = Argb((byte)(color.A * opacity), color.R, color.G, color.B);
                return;
            }

            op.Color = Argb((byte)(255.0 * opacity), 255, 255, 255);
            if (brush is IGradientBrush gradient && SkiaShaders.Of(gradient, target) is { } shader)
            {
                op.Res2 = AddResource(shader);
                return;
            }

            op.Color = Argb(0, 255, 255, 255);
            if (!s_warnedBrush)
            {
                s_warnedBrush = true;
                Log.Warning("A highway drew with a {Brush}, which SkiaSharp drawing leaves out", brush.GetType().Name);
            }
        }

        private void Stroke(ref Op op, IPen pen, Rect target)
        {
            Fill(ref op, pen.Brush!, target);
            op.Stroke = true;
            op.Width = (float)pen.Thickness;
            op.Miter = (float)pen.MiterLimit;
            op.Cap = pen.LineCap switch
            {
                PenLineCap.Round => SKStrokeCap.Round,
                PenLineCap.Square => SKStrokeCap.Square,
                _ => SKStrokeCap.Butt,
            };
            op.Join = pen.LineJoin switch
            {
                PenLineJoin.Bevel => SKStrokeJoin.Bevel,
                PenLineJoin.Round => SKStrokeJoin.Round,
                _ => SKStrokeJoin.Miter,
            };
            if (SkiaShaders.DashOf(pen) is { } dash)
                op.Res3 = AddResource(dash);
        }

        private static uint Argb(byte a, byte r, byte g, byte b) => ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | b;

        internal static SKRect ToSK(Rect rect) => new((float)rect.Left, (float)rect.Top, (float)rect.Right, (float)rect.Bottom);

        internal static SKMatrix ToSK(Matrix m) => new()
        {
            ScaleX = (float)m.M11,
            SkewX = (float)m.M21,
            TransX = (float)m.M31,
            SkewY = (float)m.M12,
            ScaleY = (float)m.M22,
            TransY = (float)m.M32,
            Persp0 = (float)m.M13,
            Persp1 = (float)m.M23,
            Persp2 = (float)m.M33,
        };

        internal enum OpKind : byte
        {
            Rect,
            RoundRect,
            Line,
            Path,
            Image,
            Text,
            Save,
            Restore,
            ClipRect,
            ClipPath,
            Concat,
            MaskBegin,
            MaskEnd,
        }

        internal enum Sampling : byte
        {
            Linear,
            Nearest,
            Mipmapped,
            Cubic,
        }

        /// <summary>
        /// One thing drawn. <see cref="Rect"/> is the shape's rectangle, a line's ends, a picture's destination or a text's
        /// origin; <see cref="Src"/> a picture's source, or a rounded rectangle's radii in its left and top.
        /// </summary>
        internal struct Op
        {
            public OpKind Kind;
            public bool Stroke;
            public bool Antialias;
            public Sampling Sampling;
            public SKStrokeCap Cap;
            public SKStrokeJoin Join;
            public SKRect Rect;
            public SKRect Src;
            public uint Color;
            public float Width;
            public float Miter;

            /// <summary>The shape's path, picture, text or matrix; -1 for none.</summary>
            public int Res;

            /// <summary>The paint's shader; -1 for none.</summary>
            public int Res2;

            /// <summary>The paint's dashes, or a text's greyscale blob; -1 for none.</summary>
            public int Res3;
        }
    }
}
