using System;
using System.Collections.Generic;
using SkiaSharp;

namespace ChartShared.UI.Controls.Skia
{
    /// <summary>
    /// Draws a <see cref="DrawList"/> on a SkiaSharp canvas, on the render thread, with paints it keeps, so drawing a frame
    /// makes no garbage. Each host's handler keeps one.
    /// </summary>
    internal sealed class DrawListPlayer : IDisposable
    {
        private static readonly SKSamplingOptions Nearest = new(SKFilterMode.Nearest, SKMipmapMode.None);
        private static readonly SKSamplingOptions Linear = new(SKFilterMode.Linear, SKMipmapMode.None);
        private static readonly SKSamplingOptions Mipmapped = new(SKFilterMode.Linear, SKMipmapMode.Linear);
        private static readonly SKSamplingOptions Cubic = new(SKCubicResampler.Mitchell);

        private readonly SKPaint _paint = new();
        private readonly SKPaint _layer = new();
        private readonly SKPaint _dstIn = new() { BlendMode = SKBlendMode.DstIn };
        private readonly Stack<(SKMatrix44 Matrix, int Op)> _masks = new();

        /// <summary>Draws <paramref name="list"/>, faded to <paramref name="opacity"/> as a whole, moved by <paramref name="dx"/> and <paramref name="dy"/>.</summary>
        public void Play(DrawList list, SKCanvas canvas, double opacity, float dx = 0, float dy = 0)
        {
            int saved = canvas.Save();
            if (dx != 0 || dy != 0)
                canvas.Translate(dx, dy);
            if (opacity < 1)
            {
                // What Avalonia pushes over the host itself: kept by the canvas it lends, rather than applied by it.
                _layer.Color = new SKColor(0, 0, 0, (byte)(255 * Math.Max(0, opacity)));
                canvas.SaveLayer(_layer);
            }

            _masks.Clear();
            var ops = list.Ops;
            for (int i = 0; i < ops.Length; i++)
            {
                ref readonly var op = ref ops[i];
                switch (op.Kind)
                {
                    case DrawList.OpKind.Rect:
                        canvas.DrawRect(op.Rect, Paint(list, op));
                        break;
                    case DrawList.OpKind.RoundRect:
                        canvas.DrawRoundRect(op.Rect, op.Src.Left, op.Src.Top, Paint(list, op));
                        break;
                    case DrawList.OpKind.Line:
                        canvas.DrawLine(op.Rect.Left, op.Rect.Top, op.Rect.Right, op.Rect.Bottom, Paint(list, op));
                        break;
                    case DrawList.OpKind.Path:
                        canvas.DrawPath((SKPath)list.Resource(op.Res)!, Paint(list, op));
                        break;
                    case DrawList.OpKind.Text:
                        canvas.DrawText((SKTextBlob)list.Resource(list.GreyscaleText ? op.Res3 : op.Res)!, op.Rect.Left, op.Rect.Top, TextPaint(list, op));
                        break;
                    case DrawList.OpKind.Image:
                        DrawImage(canvas, list, op);
                        break;
                    case DrawList.OpKind.Save:
                        canvas.Save();
                        break;
                    case DrawList.OpKind.Restore:
                        canvas.Restore();
                        break;
                    case DrawList.OpKind.ClipRect:
                        // Antialiased: Avalonia replays a recorded clip as a rounded rectangle's, which it antialiases.
                        canvas.ClipRect(op.Rect, SKClipOperation.Intersect, true);
                        break;
                    case DrawList.OpKind.ClipPath:
                        canvas.ClipPath((SKPath)list.Resource(op.Res)!, SKClipOperation.Intersect, true);
                        break;
                    case DrawList.OpKind.Concat:
                        canvas.Concat(in list.MatrixAt(op.Res));
                        break;
                    case DrawList.OpKind.MaskBegin:
                        // As Avalonia draws an opacity mask: what's under it on a layer, then the mask over it, keeping
                        // only as much of it as the mask is opaque.
                        canvas.SaveLayer(op.Rect, null);
                        _masks.Push((canvas.TotalMatrix44, i));
                        break;
                    case DrawList.OpKind.MaskEnd:
                        if (_masks.Count == 0)
                            break;
                        var (matrix, maskAt) = _masks.Pop();
                        canvas.SaveLayer(_dstIn);
                        canvas.SetMatrix(in matrix);
                        canvas.DrawPaint(Paint(list, ops[maskAt]));
                        canvas.Restore();
                        canvas.Restore();
                        break;
                }
            }

            canvas.RestoreToCount(saved);
        }

        private SKPaint Paint(DrawList list, in DrawList.Op op)
        {
            var paint = _paint;
            paint.Color = op.Color;
            paint.IsAntialias = op.Antialias;
            paint.Shader = op.Res2 >= 0 ? (SKShader)list.Resource(op.Res2)! : null;
            if (op.Stroke)
            {
                paint.Style = SKPaintStyle.Stroke;
                paint.StrokeWidth = op.Width;
                paint.StrokeCap = op.Cap;
                paint.StrokeJoin = op.Join;
                paint.StrokeMiter = op.Miter;
                paint.PathEffect = op.Res3 >= 0 ? (SKPathEffect)list.Resource(op.Res3)! : null;
            }
            else
            {
                paint.Style = SKPaintStyle.Fill;
                paint.PathEffect = null;
            }

            return paint;
        }

        private SKPaint TextPaint(DrawList list, in DrawList.Op op)
        {
            var paint = _paint;
            paint.Color = op.Color;
            paint.IsAntialias = op.Antialias;
            paint.Shader = op.Res2 >= 0 ? (SKShader)list.Resource(op.Res2)! : null;
            paint.Style = SKPaintStyle.Fill;
            paint.PathEffect = null;
            return paint;
        }

        private void DrawImage(SKCanvas canvas, DrawList list, in DrawList.Op op)
        {
            if (list.Resource(op.Res) is not HighwayImage { Image: var image })
                return;
            var paint = _paint;
            paint.Color = op.Color;
            paint.IsAntialias = op.Antialias;
            paint.Shader = null;
            paint.PathEffect = null;
            paint.Style = SKPaintStyle.Fill;
            var sampling = op.Sampling switch
            {
                DrawList.Sampling.Nearest => Nearest,
                DrawList.Sampling.Mipmapped => Mipmapped,
                DrawList.Sampling.Cubic => Cubic,
                _ => Linear,
            };
            canvas.DrawImage(image, op.Src, op.Rect, sampling, paint);
        }

        public void Dispose()
        {
            _paint.Dispose();
            _layer.Dispose();
            _dstIn.Dispose();
        }
    }
}
