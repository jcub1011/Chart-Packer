using System;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace ChartShared.UI.Controls.Skia
{
    /// <summary>
    /// What a highway is drawn on: the calls of Avalonia's <see cref="DrawingContext"/> the highways use, with the same
    /// meaning, kept to be drawn by SkiaSharp on the render thread (<see cref="DrawList"/>).
    /// </summary>
    /// <remarks>
    /// Shapes are <see cref="HighwayPath"/>s and pictures <see cref="HighwayImage"/>s rather than Avalonia's geometries and
    /// bitmaps, which SkiaSharp can't be handed. Every push returns a <see cref="CanvasState"/>, a struct, so a <c>using</c>
    /// over one doesn't box it.
    /// </remarks>
    public interface IHighwayCanvas
    {
        /// <summary>A rectangle, filled with <paramref name="brush"/> and edged with <paramref name="pen"/>, either of which may be left out.</summary>
        void DrawRectangle(IBrush? brush, IPen? pen, Rect rect, double radiusX = 0, double radiusY = 0);

        /// <summary>The edge of a rectangle.</summary>
        void DrawRectangle(IPen pen, Rect rect, float cornerRadius = 0);

        /// <summary>A rectangle filled with <paramref name="brush"/>.</summary>
        void FillRectangle(IBrush brush, Rect rect, float cornerRadius = 0);

        void DrawLine(IPen pen, Point p1, Point p2);

        void DrawGeometry(IBrush? brush, IPen? pen, HighwayPath path);

        /// <summary>Draws the part <paramref name="sourceRect"/> of <paramref name="image"/>, in its pixels, into <paramref name="destRect"/>.</summary>
        void DrawImage(HighwayImage image, Rect sourceRect, Rect destRect);

        /// <summary>Draws <paramref name="text"/> with its top left at <paramref name="origin"/>, as <see cref="TextLayout.Draw"/> does.</summary>
        void DrawText(TextLayout text, Point origin);

        CanvasState PushOpacity(double opacity);

        CanvasState PushClip(Rect clip);

        CanvasState PushGeometryClip(HighwayPath clip);

        CanvasState PushTransform(Matrix matrix);

        /// <summary>Has what's drawn until it's popped shown only as much as <paramref name="mask"/> is opaque, over <paramref name="bounds"/>.</summary>
        CanvasState PushOpacityMask(IBrush mask, Rect bounds);

        CanvasState PushRenderOptions(RenderOptions options);

        /// <summary>Undoes the last push, which was of <paramref name="kind"/>: what <see cref="CanvasState.Dispose"/> calls.</summary>
        void Pop(CanvasStateKind kind);
    }

    /// <summary>Which push a <see cref="CanvasState"/> undoes.</summary>
    public enum CanvasStateKind : byte
    {
        Opacity,
        Clip,
        GeometryClip,
        Transform,
        OpacityMask,
        RenderOptions,
    }

    /// <summary>
    /// A push on an <see cref="IHighwayCanvas"/>, undone when disposed. A default one undoes nothing, so a push that's only
    /// sometimes wanted can be <c>condition ? canvas.PushClip(rect) : default</c>.
    /// </summary>
    public readonly struct CanvasState : IDisposable
    {
        private readonly IHighwayCanvas? _canvas;
        private readonly CanvasStateKind _kind;

        public CanvasState(IHighwayCanvas canvas, CanvasStateKind kind)
        {
            _canvas = canvas;
            _kind = kind;
        }

        public void Dispose() => _canvas?.Pop(_kind);
    }
}
