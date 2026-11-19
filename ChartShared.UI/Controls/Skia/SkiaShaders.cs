using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using Serilog;
using SkiaSharp;

namespace ChartShared.UI.Controls.Skia
{
    /// <summary>
    /// SkiaSharp's shaders for Avalonia's gradient brushes, and path effects for its dashed pens, made as Avalonia's Skia
    /// backend makes them and kept: a highway draws with a few brushes and pens, over and over. For the UI thread.
    /// </summary>
    /// <remarks>
    /// Nothing made is disposed: a drawing still waiting for the render thread may hold it, and SkiaSharp lets each go
    /// when nothing does. Too many kept, as a gradient over a rectangle that keeps changing size would make, and the lot
    /// are let go.
    /// </remarks>
    internal static class SkiaShaders
    {
        private const int MaxShaders = 256;
        private const int MaxDashes = 256;

        private static readonly Dictionary<(IGradientBrush Brush, Rect Target), SKShader?> s_shaders = [];
        private static readonly Dictionary<(IDashStyle Style, double Thickness), SKPathEffect?> s_dashes = [];
        private static bool s_warned;

        /// <summary>A gradient brush's shader over <paramref name="target"/>, or <see langword="null"/> for one SkiaSharp drawing doesn't draw.</summary>
        public static SKShader? Of(IGradientBrush brush, Rect target)
        {
            if (s_shaders.TryGetValue((brush, target), out var known))
                return known;
            if (s_shaders.Count >= MaxShaders)
                s_shaders.Clear();

            var shader = Make(brush, target);
            s_shaders[(brush, target)] = shader;
            return shader;
        }

        /// <summary>A dashed pen's dashes, or <see langword="null"/> for a solid one.</summary>
        public static SKPathEffect? DashOf(IPen pen)
        {
            if (pen.DashStyle is not { Dashes: { Count: > 0 } dashes } style)
                return null;
            if (s_dashes.TryGetValue((style, pen.Thickness), out var known))
                return known;
            if (s_dashes.Count >= MaxDashes)
                s_dashes.Clear();

            // An odd number of dashes is gone round twice, as Avalonia's Skia backend does.
            int count = dashes.Count % 2 == 0 ? dashes.Count : dashes.Count * 2;
            var intervals = new float[count];
            for (int i = 0; i < count; i++)
                intervals[i] = (float)dashes[i % dashes.Count] * (float)pen.Thickness;
            var effect = SKPathEffect.CreateDash(intervals, (float)(style.Offset * pen.Thickness));
            s_dashes[(style, pen.Thickness)] = effect;
            return effect;
        }

        private static SKShader? Make(IGradientBrush brush, Rect target)
        {
            if (brush is not ILinearGradientBrush linear)
            {
                if (!s_warned)
                {
                    s_warned = true;
                    Log.Warning("A highway drew with a {Brush}, which SkiaSharp drawing leaves out", brush.GetType().Name);
                }

                return null;
            }

            var stops = brush.GradientStops;
            var colors = new SKColor[stops.Count];
            var offsets = new float[stops.Count];
            for (int i = 0; i < stops.Count; i++)
            {
                var color = stops[i].Color;
                colors[i] = new SKColor(color.R, color.G, color.B, color.A);
                offsets[i] = (float)stops[i].Offset;
            }

            var mode = brush.SpreadMethod switch
            {
                GradientSpreadMethod.Reflect => SKShaderTileMode.Mirror,
                GradientSpreadMethod.Repeat => SKShaderTileMode.Repeat,
                _ => SKShaderTileMode.Clamp,
            };
            var start = linear.StartPoint.ToPixels(target);
            var end = linear.EndPoint.ToPixels(target);
            var from = new SKPoint((float)start.X, (float)start.Y);
            var to = new SKPoint((float)end.X, (float)end.Y);
            if (brush.Transform is null)
                return SKShader.CreateLinearGradient(from, to, colors, offsets, mode);

            var origin = Matrix.CreateTranslation(brush.TransformOrigin.ToPixels(target));
            var local = -origin * brush.Transform.Value * origin;
            return SKShader.CreateLinearGradient(from, to, colors, offsets, mode, DrawList.ToSK(local));
        }
    }
}
