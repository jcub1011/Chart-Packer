using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Serilog;
using SkiaSharp;

namespace ChartShared.UI.Controls.Skia
{
    /// <summary>
    /// Text for SkiaSharp: a <see cref="TextLayout"/>'s glyphs, as Avalonia shaped them (its fonts, its fallback, its
    /// trimming), turned into <see cref="SKTextBlob"/>s once and kept for as long as the layout is, so drawing it again is
    /// one call per run, as Avalonia's own drawing of it is.
    /// </summary>
    /// <remarks>
    /// The blobs are built as Avalonia's Skia backend builds a glyph run's: each glyph at its advance plus its offset, in
    /// a font of the run's size with linear metrics, full hinting and subpixel positioning; twice, with LCD edging, as
    /// Avalonia draws text on the window, and with greyscale, as it does on a picture it keeps (see
    /// <see cref="DrawList.GreyscaleText"/>). For the UI thread.
    /// </remarks>
    internal static class SkiaText
    {
        private static readonly ConditionalWeakTable<TextLayout, Runs> s_layouts = new();
        private static readonly Dictionary<GlyphTypeface, SKTypeface?> s_typefaces = new(ReferenceEqualityComparer.Instance);
        private static readonly SKTextBlobBuilder s_builder = new();
        private static bool s_warnedRun;

        /// <summary>The runs <paramref name="layout"/> draws, with their places from its top left.</summary>
        public static Runs Of(TextLayout layout)
        {
            if (s_layouts.TryGetValue(layout, out var runs))
                return runs;

            runs = Build(layout);
            s_layouts.AddOrUpdate(layout, runs);
            return runs;
        }

        private static Runs Build(TextLayout layout)
        {
            var built = new List<Run>();
            double y = 0;
            foreach (var line in layout.TextLines)
            {
                double x = line.Start;
                foreach (var textRun in line.TextRuns)
                {
                    if (textRun is not DrawableTextRun drawable)
                        continue;

                    if (drawable is ShapedTextRun shaped)
                        AddRun(built, line, shaped, x, y);
                    else if (!s_warnedRun)
                    {
                        s_warnedRun = true;
                        Log.Warning("Highway text has a {Run} run, which SkiaSharp drawing leaves out", drawable.GetType().Name);
                    }

                    x += drawable.Size.Width;
                }

                y += line.Height;
            }

            return new Runs([.. built]);
        }

        private static void AddRun(List<Run> runs, TextLine line, ShapedTextRun run, double x, double y)
        {
            var properties = run.Properties;
            if (properties.ForegroundBrush is not { } brush || properties.Typeface == default)
                return;

            var glyphRun = run.GlyphRun;
            var infos = glyphRun.GlyphInfos;
            if (infos.Count == 0 || TypefaceOf(glyphRun.GlyphTypeface) is not { } typeface)
                return;

            if (BlobOf(glyphRun, typeface, SKFontEdging.SubpixelAntialias) is not { } blob || BlobOf(glyphRun, typeface, SKFontEdging.Antialias) is not { } grey)
                return;

            // As the line draws the run: moved down to its baseline, and its glyph run drawn from its baseline origin.
            double runY = y + BaselineOffset(line, run);
            var baseline = glyphRun.BaselineOrigin;
            var ink = InkOf(glyphRun, typeface).Translate(new Vector(x + baseline.X, runY + baseline.Y));
            runs.Add(new Run(blob, grey, (float)(x + baseline.X), (float)(runY + baseline.Y), brush, ink));
        }

        /// <summary>
        /// Where a glyph run's ink reaches from its baseline origin, as Avalonia's Skia backend measures a run for the bounds of a
        /// drawing: each glyph's ink, from where it's advanced to.
        /// </summary>
        private static Rect InkOf(GlyphRun glyphRun, SKTypeface typeface)
        {
            var infos = glyphRun.GlyphInfos;
            using var font = FontOf(glyphRun, typeface, SKFontEdging.SubpixelAntialias, baselineSnap: false);
            Span<ushort> glyphs = infos.Count <= 256 ? stackalloc ushort[infos.Count] : new ushort[infos.Count];
            for (int i = 0; i < infos.Count; i++)
                glyphs[i] = infos[i].GlyphIndex;
            var inks = new SKRect[infos.Count];
            font.GetGlyphWidths(glyphs, Span<float>.Empty, inks.AsSpan(), null);

            var bounds = default(Rect);
            double advance = 0;
            for (int i = 0; i < infos.Count; i++)
            {
                var glyph = inks[i];
                bounds = bounds.Union(new Rect(advance + glyph.Left, glyph.Top, glyph.Width, glyph.Height));
                advance += infos[i].GlyphAdvance;
            }

            return bounds;
        }

        private static SKFont FontOf(GlyphRun glyphRun, SKTypeface typeface, SKFontEdging edging, bool baselineSnap)
        {
            var simulations = glyphRun.GlyphTypeface.FontSimulations;
            return new SKFont(typeface, (float)glyphRun.FontRenderingEmSize, 1, (simulations & FontSimulations.Oblique) != 0 ? -0.3f : 0)
            {
                LinearMetrics = true,
                Embolden = (simulations & FontSimulations.Bold) != 0,
                ForceAutoHinting = false,
                Hinting = SKFontHinting.Full,
                Subpixel = true,
                Edging = edging,
                BaselineSnap = baselineSnap,
            };
        }

        private static SKTextBlob? BlobOf(GlyphRun glyphRun, SKTypeface typeface, SKFontEdging edging)
        {
            var infos = glyphRun.GlyphInfos;
            using var font = FontOf(glyphRun, typeface, edging, baselineSnap: true);

            var buffer = s_builder.AllocatePositionedRun(font, infos.Count);
            var glyphs = buffer.Glyphs;
            var positions = buffer.Positions;
            double advance = 0;
            for (int i = 0; i < infos.Count; i++)
            {
                var info = infos[i];
                glyphs[i] = info.GlyphIndex;
                positions[i] = new SKPoint((float)(advance + info.GlyphOffset.X), (float)info.GlyphOffset.Y);
                advance += info.GlyphAdvance;
            }

            return s_builder.Build();
        }

        /// <summary>How far down the line a run's drawn, as Avalonia's text line places it.</summary>
        private static double BaselineOffset(TextLine line, DrawableTextRun run)
        {
            double offset = -run.Baseline;
            return run.Properties?.BaselineAlignment switch
            {
                BaselineAlignment.Top or BaselineAlignment.TextTop => offset + (line.Height - line.Extent + (run.Size.Height / 2)),
                BaselineAlignment.Center => offset + ((line.Height / 2) + run.Baseline - (run.Size.Height / 2)),
                BaselineAlignment.Bottom or BaselineAlignment.TextBottom or BaselineAlignment.Subscript => offset + (line.Height - run.Size.Height + run.Baseline),
                BaselineAlignment.Superscript => offset + run.Baseline,
                _ => offset + line.Baseline,
            };
        }

        /// <summary>
        /// SkiaSharp's typeface for Avalonia's: the installed font of its family and style, when that's the same font, or else
        /// one made from the font's own data, as for a font the program carries.
        /// </summary>
        private static SKTypeface? TypefaceOf(GlyphTypeface glyphTypeface)
        {
            if (s_typefaces.TryGetValue(glyphTypeface, out var known))
                return known;

            SKTypeface? typeface = null;
            try
            {
                var style = new SKFontStyle((int)glyphTypeface.Weight, (int)glyphTypeface.Stretch, glyphTypeface.Style switch
                {
                    FontStyle.Italic => SKFontStyleSlant.Italic,
                    FontStyle.Oblique => SKFontStyleSlant.Oblique,
                    _ => SKFontStyleSlant.Upright,
                });
                var installed = SKFontManager.Default.MatchFamily(glyphTypeface.FamilyName, style);
                if (installed is not null && Matches(installed, glyphTypeface))
                {
                    typeface = installed;
                }
                else
                {
                    installed?.Dispose();
                    typeface = FromData(glyphTypeface);
                }
            }
            catch (Exception exception)
            {
                Log.Warning(exception, "No SkiaSharp typeface for {Family}; its text is left out of the highway", glyphTypeface.FamilyName);
            }

            if (typeface is null)
                Log.Warning("No SkiaSharp typeface for {Family}; its text is left out of the highway", glyphTypeface.FamilyName);
            s_typefaces[glyphTypeface] = typeface;
            return typeface;
        }

        private static bool Matches(SKTypeface typeface, GlyphTypeface glyphTypeface) =>
            typeface.GlyphCount == glyphTypeface.GlyphCount &&
            string.Equals(typeface.FamilyName, glyphTypeface.FamilyName, StringComparison.OrdinalIgnoreCase);

        private static SKTypeface? FromData(GlyphTypeface glyphTypeface)
        {
            if (!glyphTypeface.PlatformTypeface.TryGetStream(out var stream))
                return null;

            using (stream)
            {
                using var copy = new MemoryStream();
                stream.CopyTo(copy);
                using var data = SKData.CreateCopy(copy.GetBuffer().AsSpan(0, (int)copy.Length));

                // A collection holds several fonts: the one with as many glyphs as Avalonia's is the one it shaped with.
                SKTypeface? first = null;
                for (int index = 0; index < 64; index++)
                {
                    var typeface = SKFontManager.Default.CreateTypeface(data, index);
                    if (typeface is null)
                        break;
                    if (typeface.GlyphCount == glyphTypeface.GlyphCount)
                    {
                        if (first is not null && !ReferenceEquals(first, typeface))
                            first.Dispose();
                        return typeface;
                    }

                    if (first is null)
                        first = typeface;
                    else
                        typeface.Dispose();
                }

                return first;
            }
        }

        /// <summary>A layout's runs, in the order it draws them.</summary>
        internal sealed class Runs(Run[] items)
        {
            public Run[] Items { get; } = items;
        }

        /// <summary>
        /// A run of glyphs, with LCD and greyscale edging, where its baseline origin is from the layout's top left, its brush, and
        /// the area it covers.
        /// </summary>
        internal readonly record struct Run(SKTextBlob Blob, SKTextBlob GreyBlob, float X, float Y, IBrush Brush, Rect Bounds);
    }
}
