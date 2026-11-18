using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ChartShared.Audio.Analysis;
using ChartShared.Core.Charting.Layout;
using ChartShared.UI.Controls.Skia;
using SkiaSharp;

namespace ChartShared.UI.Controls
{
    /// <summary>
    /// Draws a spectrogram (charter-spec C13) along a lane's time axis: low frequencies on the left of a highway, or at the
    /// bottom of a timeline, its levels through a contrast floor and ceiling to <see cref="SpectrogramColors"/>, and rows
    /// outside the band chosen dimmed. Columns not yet computed are left blank, and fill in as they are.
    /// </summary>
    /// <remarks>
    /// <para>As <see cref="LaneWaveformPainter"/> does, it draws stretches of the spectrogram once into pictures that are
    /// moved into the lane. A picture is drawn at the size it's shown at, so that it's as sharp as the screen: 256 pixels
    /// along the time axis, each a column of the spectrogram, or, zoomed out, a power of two of them, each group's loudest,
    /// or, zoomed in, a half or a quarter of one; and across it, as many pixels as the rows shown take on the screen, each
    /// the loudest of the rows it covers, or between the two it's between. The levels are what's worked out between,
    /// before they're coloured, which keeps a thin line thin.</para>
    /// <para>The pictures are drawn within the <see cref="PaintBudget"/>, and one that was drawn before all of its columns
    /// were ready is drawn again once more are, at most every <see cref="RefreshInterval"/>. Until a picture is drawn, one
    /// of the same stretch drawn for other rows or another zoom stands in, stretched.</para>
    /// <para>For the UI thread only.</para>
    /// </remarks>
    public static class SpectrogramPainter
    {
        private const int TileColumns = 256;
        private const int Rows = Spectrogram.Rows;

        /// <summary>The fewest columns to a picture's pixel is 2 to this power: a quarter of a column.</summary>
        internal const int MinLevel = -2;

        internal const int MaxLevel = 14;

        /// <summary>The most pixels a picture is across the rows, and what their number is rounded up to a multiple of.</summary>
        internal const int MaxHeight = 1024;

        internal const int HeightStep = 32;

        /// <summary>A picture's rows are kept to this fraction of a row.</summary>
        private const int RowParts = 16;

        /// <summary>How many bytes of pictures are kept.</summary>
        private const long MaxBytes = 96L * 1024 * 1024;

        /// <summary>A dimmed row's colour is taken from this far up the levels, as a fraction.</summary>
        private const double Dimmed = 0.3;

        // How far under a power of two the columns to a pixel may be and still be taken as it.
        private const double LevelSlack = 1e-6;

        private static readonly long RefreshInterval = Stopwatch.Frequency / 10;
        private static readonly RenderOptions Smooth = new() { BitmapInterpolationMode = BitmapInterpolationMode.MediumQuality };
        private static readonly Dictionary<TileKey, Tile> Tiles = [];

        // The picture last drawn of each stretch, whatever rows it's of: what stands in for one not drawn yet.
        private static readonly Dictionary<StretchKey, Tile> Latest = [];
        private static readonly int[] Pixels = new int[TileColumns * MaxHeight];
        private static readonly RowSample[] Samples = new RowSample[MaxHeight];
        private static readonly float[][] Strips = [new float[MaxHeight], new float[MaxHeight]];
        private static long s_drawn;
        private static long s_bytes;

        /// <summary>
        /// Where the picture's rows go across a timeline: a picture's y, counted in rows from its top, is drawn at
        /// <see cref="Offset"/> plus <see cref="Scale"/> times it.
        /// </summary>
        /// <param name="Scale">How many pixels a row of the picture is.</param>
        /// <param name="Offset">Where the picture's top edge is across the lane.</param>
        public readonly record struct PitchFrame(double Scale, double Offset)
        {
            /// <summary>
            /// The frame that puts each note's frequency on its row of a piano roll: the row of <paramref name="topPitch"/> is
            /// the first, starting <paramref name="firstRowTop"/> across the lane, each <paramref name="rowSize"/> pixels, one
            /// to a semitone, with the pitches below it following.
            /// </summary>
            public static PitchFrame Of(int topPitch, double firstRowTop, double rowSize)
            {
                double scale = rowSize / SpectrogramRows.RowsPerSemitone;
                return new PitchFrame(scale, firstRowTop + (rowSize / 2) - (scale * SpectrogramRows.PictureYOfPitch(topPitch)));
            }
        }

        /// <summary>Drops every picture kept, as when the audio they're of is closed.</summary>
        public static void Clear()
        {
            foreach (var tile in Tiles.Values)
                tile.Image?.Dispose();
            Tiles.Clear();
            Latest.Clear();
            s_bytes = 0;
        }

        /// <summary>
        /// Draws <paramref name="spectrogram"/> between <paramref name="from"/> and <paramref name="to"/> seconds, across
        /// <paramref name="bounds"/>.
        /// </summary>
        /// <param name="isVertical">Whether time runs up or down the lane, rather than across it.</param>
        /// <param name="position">Where along the lane a time is drawn, in the lane's coordinates; linear.</param>
        /// <param name="floorDb">Levels at or below this are black.</param>
        /// <param name="ceilingDb">Levels at or above this are the ramp's lightest.</param>
        /// <param name="pixel">How long a pixel of the screen is, in the lane's coordinates.</param>
        /// <param name="frame">
        /// Where the frequency rows go across a timeline, when they're not stretched over all of <paramref name="bounds"/>: as
        /// a piano roll's rows line up with the notes' frequencies, which crops the picture to <paramref name="bounds"/>. Ignored down a lane.
        /// </param>
        /// <returns>Whether all of it was drawn as it is now: <see langword="false"/> when a picture had to wait for the budget.</returns>
        public static bool Draw(IHighwayCanvas context, Spectrogram spectrogram, Rect bounds, bool isVertical, double from, double to, Func<double, double> position,
            double floorDb, double ceilingDb, SpectrogramBand band, double pixel = 1, PitchFrame? frame = null)
        {
            double origin = position(0);
            double perColumn = (position(1) - origin) * spectrogram.SecondsPerColumn;
            if (spectrogram.IsReleased || perColumn == 0 || !double.IsFinite(perColumn) || bounds.Width <= 0 || bounds.Height <= 0 || !(pixel > 0))
                return true;

            // Where the picture's rows are across the lane: a row is perRow long, and the picture's top edge is at rowsFrom.
            // Then the rows that show, and how many pixels of the screen they take.
            double perRow, rowsFrom;
            if (isVertical)
                (perRow, rowsFrom) = (-bounds.Width / Rows, bounds.Right);
            else if (frame is { } pitched)
                (perRow, rowsFrom) = (pitched.Scale, pitched.Offset);
            else
                (perRow, rowsFrom) = (bounds.Height / Rows, bounds.Top);
            if (perRow == 0 || !double.IsFinite(perRow) || !double.IsFinite(rowsFrom))
                return true;

            var (near, far) = isVertical ? (bounds.Left, bounds.Right) : (bounds.Top, bounds.Bottom);
            if (!RowsShown((near - rowsFrom) / perRow, (far - rowsFrom) / perRow, Math.Abs(perRow) / pixel, out int top, out int bottom, out int height))
                return true;

            int level = LevelFor(pixel / Math.Abs(perColumn));
            double span = ColumnsIn(level);
            int first = Math.Max(0, spectrogram.ColumnAt(Math.Min(from, to)) - 1);
            int last = Math.Min(spectrogram.ColumnCount - 1, spectrogram.ColumnAt(Math.Max(from, to)) + 1);
            if (last < first)
                return true;

            var levels = (Spectrogram.LevelOf(floorDb), Spectrogram.LevelOf(ceilingDb));
            bool complete = true;
            using (context.PushClip(bounds))
            using (context.PushRenderOptions(Smooth))
            {
                for (int index = (int)(first / span); index <= (int)(last / span); index++)
                {
                    var key = new TileKey(new StretchKey(spectrogram, level, index, levels.Item1, levels.Item2, band), top, bottom, height);
                    if (TileOf(key, out bool current) is { } tile)
                    {
                        complete &= current;
                        DrawTile(tile);
                        continue;
                    }

                    // Not drawn yet: one of the same stretch for other rows stands in, or a neighbouring level's pictures
                    // of the same columns, rather than a gap.
                    complete = false;
                    if (Latest.TryGetValue(key.Stretch, out var other))
                    {
                        DrawTile(other);
                        continue;
                    }

                    double start = index * span;
                    foreach (int otherLevel in (ReadOnlySpan<int>)[level + 1, level - 1])
                    {
                        if (otherLevel < MinLevel || otherLevel > MaxLevel)
                            continue;
                        double otherSpan = ColumnsIn(otherLevel);
                        bool any = false;
                        for (long at = (long)(start / otherSpan); at * otherSpan < start + span; at++)
                        {
                            if (!Latest.TryGetValue(key.Stretch with { Level = otherLevel, Index = (int)at }, out var stand))
                                continue;
                            double early = origin + (perColumn * (start - 0.5)), late = origin + (perColumn * (start + span - 0.5));
                            var clip = isVertical
                                ? new Rect(bounds.X, Math.Min(early, late), bounds.Width, Math.Abs(late - early))
                                : new Rect(Math.Min(early, late), bounds.Y, Math.Abs(late - early), bounds.Height);
                            using (context.PushClip(clip))
                                DrawTile(stand);
                            any = true;
                        }

                        if (any)
                            break;
                    }
                }
            }

            return complete;

            void DrawTile(Tile tile)
            {
                // A picture's pixel x is columns start + x·2^level, centred half a column earlier; its pixel y is rows
                // top + y·(bottom - top) / height down the whole picture.
                var stretch = tile.Key.Stretch;
                double along = origin + (perColumn * ((stretch.Index * ColumnsIn(stretch.Level)) - 0.5));
                double step = perColumn * Math.Pow(2, stretch.Level);
                double rowsStart = rowsFrom + (perRow * tile.Key.Top / RowParts);
                double rowStep = perRow * (tile.Key.Bottom - tile.Key.Top) / RowParts / tile.Key.Height;
                var matrix = isVertical
                    ? new Matrix(0, step, rowStep, 0, rowsStart, along)
                    : new Matrix(step, 0, 0, rowStep, along, rowsStart);
                var size = new Rect(0, 0, TileColumns, tile.Key.Height);
                using (context.PushTransform(matrix))
                    context.DrawImage(tile.Image!, size, size);
            }
        }

        /// <summary>
        /// The power of two of columns to a picture's pixel when <paramref name="columnsPerPixel"/> columns fit in a pixel
        /// of the screen: as many as fit, or, when a column is wider than a pixel, a half or a quarter of one. A hair
        /// under a power of two is taken as it, so the level doesn't flip between frames as the lane moves.
        /// </summary>
        internal static int LevelFor(double columnsPerPixel) =>
            !(columnsPerPixel > 0) ? MinLevel : Math.Clamp((int)Math.Ceiling(Math.Log2(columnsPerPixel) - LevelSlack), MinLevel, MaxLevel);

        /// <summary>How many columns a picture at <paramref name="level"/> is of.</summary>
        internal static double ColumnsIn(int level) => TileColumns * Math.Pow(2, level);

        /// <summary>
        /// The rows of the picture that show between <paramref name="near"/> and <paramref name="far"/>, counted in rows
        /// from the whole picture's top edge, in <see cref="RowParts"/>ths of a row, and how many pixels a picture of
        /// them is across them: as many as they take on the screen, rounded up to a multiple of <see cref="HeightStep"/>
        /// so that a lane being resized doesn't draw a new picture at every size, and no more than <see cref="MaxHeight"/>.
        /// </summary>
        /// <param name="pixelsPerRow">How many pixels of the screen a row is.</param>
        /// <returns>Whether any show.</returns>
        internal static bool RowsShown(double near, double far, double pixelsPerRow, out int top, out int bottom, out int height)
        {
            top = (int)Math.Floor(Math.Clamp(Math.Min(near, far), 0, Rows) * RowParts);
            bottom = (int)Math.Ceiling(Math.Clamp(Math.Max(near, far), 0, Rows) * RowParts);
            height = 0;
            if (bottom <= top || !(pixelsPerRow > 0))
                return false;

            double pixels = (double)(bottom - top) / RowParts * pixelsPerRow;
            height = Math.Clamp((int)Math.Ceiling(pixels / HeightStep) * HeightStep, HeightStep, MaxHeight);
            return true;
        }

        /// <summary>
        /// What each of a picture's pixels across the rows is made from, when the picture is of the rows from
        /// <paramref name="top"/> to <paramref name="bottom"/>, counted from the whole picture's top edge: the loudest of
        /// the rows it covers, or, where a row is wider than a pixel, between the two rows whose middles it's between.
        /// </summary>
        internal static void SampleRows(double top, double bottom, Span<RowSample> samples)
        {
            double perPixel = (bottom - top) / samples.Length;
            for (int y = 0; y < samples.Length; y++)
            {
                // Row r, counted from the lowest, is from Rows - 1 - r to Rows - r down the picture.
                if (perPixel > 1)
                {
                    double from = top + (y * perPixel), to = from + perPixel;
                    int high = Math.Clamp(Rows - 1 - (int)Math.Floor(from), 0, Rows - 1);
                    int low = Math.Clamp(Rows - (int)Math.Ceiling(to), 0, high);
                    samples[y] = new RowSample(low, high, -1);
                    continue;
                }

                double row = Rows - (top + ((y + 0.5) * perPixel)) - 0.5;
                int under = Math.Clamp((int)Math.Floor(row), 0, Rows - 1);
                samples[y] = new RowSample(under, Math.Min(Rows - 1, under + 1), (float)Math.Clamp(row - under, 0, 1));
            }
        }

        /// <summary>
        /// The picture for <paramref name="key"/>, drawn or drawn again within the budget; <see langword="null"/> when
        /// there's none yet. <paramref name="current"/> says whether it shows every column ready now.
        /// </summary>
        private static Tile? TileOf(TileKey key, out bool current)
        {
            int ready = ReadyIn(key.Stretch);
            long now = Stopwatch.GetTimestamp();
            if (Tiles.TryGetValue(key, out var tile))
            {
                tile.Drawn = ++s_drawn;
                current = tile.Ready == ready;
                if (current || now - tile.Built < RefreshInterval || !PaintBudget.CanBuild())
                    return tile;

                Fill(tile, key);
                tile.Ready = ready;
                tile.Built = now;
                PaintBudget.Spend(now);
                Latest[key.Stretch] = tile;
                current = true;
                return tile;
            }

            current = false;
            if (ready == 0 || !PaintBudget.CanBuild())
                return null;

            long bytes = (long)TileColumns * key.Height * 4;
            while (s_bytes + bytes > MaxBytes && Tiles.Count > 0)
                DropLeastRecent();
            tile = new Tile(key) { Ready = ready, Built = now, Drawn = ++s_drawn };
            Fill(tile, key);
            PaintBudget.Spend(now);
            Tiles[key] = tile;
            Latest[key.Stretch] = tile;
            s_bytes += bytes;
            current = true;
            return tile;
        }

        /// <summary>How many of the stretch's columns are ready, counted by chunk.</summary>
        private static int ReadyIn(StretchKey key)
        {
            var spectrogram = key.Spectrogram;
            long span = (long)ColumnsIn(key.Level);
            long start = key.Index * span;
            long end = Math.Min(spectrogram.ColumnCount, start + span);
            int ready = 0;
            for (long chunk = start / spectrogram.ChunkColumns; chunk * spectrogram.ChunkColumns < end; chunk++)
                ready += spectrogram.ReadyIn((int)chunk);
            return ready;
        }

        /// <summary>
        /// Draws the picture's columns that are ready; the rest are left clear. A picture can't be drawn in once made, and the render
        /// thread may still be drawing it: it's made again, and the old one let go once no drawing holds it.
        /// </summary>
        private static void Fill(Tile tile, TileKey key)
        {
            var stretch = key.Stretch;
            var spectrogram = stretch.Spectrogram;
            var ramp = SpectrogramColors.Ramp;
            var emphasised = SpectrogramBands.Emphasised(stretch.Band);
            float floor = stretch.Floor, perLevel = 255f / Math.Max(1, stretch.Ceiling - stretch.Floor);
            int height = key.Height;
            var samples = Samples.AsSpan(0, height);
            SampleRows((double)key.Top / RowParts, (double)key.Bottom / RowParts, samples);

            long start = (long)(stretch.Index * ColumnsIn(stretch.Level));
            int group = stretch.Level > 0 ? 1 << stretch.Level : 1;
            double perPixel = Math.Pow(2, stretch.Level);
            Span<byte> loudest = stackalloc byte[Rows];
            var pixels = Pixels;

            // Zoomed in, a pixel is between two columns: each column's levels down the picture are kept for the next pixel.
            float[] earlier = Strips[0], later = Strips[1];
            long earlierOf = -1, laterOf = -1;
            bool earlierAny = false, laterAny = false;

            for (int x = 0; x < TileColumns; x++)
            {
                float[] strip;
                bool any;
                float blend = 0;
                if (stretch.Level >= 0)
                {
                    long column = start + ((long)x * group);
                    any = stretch.Level == 0 ? Levels(spectrogram, column, samples, earlier) : Loudest(spectrogram, column, group, loudest, samples, earlier);
                    strip = earlier;
                }
                else
                {
                    // The pixel's middle, in columns: column c is from c - 0.5 to c + 0.5.
                    double at = start + ((x + 0.5) * perPixel) - 0.5;
                    long under = (long)Math.Floor(at);
                    if (under == laterOf)
                    {
                        (earlier, later) = (later, earlier);
                        (earlierOf, laterOf) = (laterOf, -1);
                        earlierAny = laterAny;
                    }

                    if (under != earlierOf)
                    {
                        earlierAny = Levels(spectrogram, under, samples, earlier);
                        earlierOf = under;
                    }

                    if (under + 1 != laterOf)
                    {
                        laterAny = Levels(spectrogram, under + 1, samples, later);
                        laterOf = under + 1;
                    }

                    any = earlierAny || laterAny;
                    strip = earlierAny ? earlier : later;
                    blend = earlierAny && laterAny ? (float)(at - under) : 0;
                }

                for (int y = 0; y < height; y++)
                {
                    uint colour = 0;
                    if (any)
                    {
                        float level = blend > 0 ? strip[y] + ((later[y] - strip[y]) * blend) : strip[y];
                        int shade = Math.Clamp((int)((level - floor) * perLevel), 0, 255);
                        if (!emphasised[samples[y].Nearest])
                            shade = (int)(shade * Dimmed);
                        colour = ramp[shade];
                    }

                    pixels[(y * TileColumns) + x] = (int)colour;
                }
            }

            var info = new SKImageInfo(TileColumns, height, SKColorType.Bgra8888, SKAlphaType.Premul);
            var image = SKImage.FromPixelCopy(info, MemoryMarshal.AsBytes(pixels.AsSpan(0, TileColumns * height)), TileColumns * 4);
            tile.Replace(HighwayImage.Of(image));
        }

        /// <summary>A column's levels down the picture, into <paramref name="strip"/>; <see langword="false"/> when it isn't ready, or there's no such column.</summary>
        private static bool Levels(Spectrogram spectrogram, long column, ReadOnlySpan<RowSample> samples, float[] strip)
        {
            if (column < 0 || column >= spectrogram.ColumnCount)
                return false;
            var rows = spectrogram.Column((int)column);
            if (rows.IsEmpty)
                return false;

            Sample(rows, samples, strip);
            return true;
        }

        /// <summary>The loudest levels of <paramref name="group"/> columns from <paramref name="column"/>, down the picture; <see langword="false"/> when none is ready.</summary>
        private static bool Loudest(Spectrogram spectrogram, long column, int group, Span<byte> loudest, ReadOnlySpan<RowSample> samples, float[] strip)
        {
            loudest.Clear();
            bool any = false;
            for (int c = 0; c < group && column + c < spectrogram.ColumnCount; c++)
            {
                var rows = spectrogram.Column((int)(column + c));
                if (rows.IsEmpty)
                    continue;
                any = true;
                for (int r = 0; r < Rows; r++)
                {
                    if (rows[r] > loudest[r])
                        loudest[r] = rows[r];
                }
            }

            if (any)
                Sample(loudest, samples, strip);
            return any;
        }

        private static void Sample(ReadOnlySpan<byte> rows, ReadOnlySpan<RowSample> samples, float[] strip)
        {
            for (int y = 0; y < samples.Length; y++)
            {
                var sample = samples[y];
                if (sample.Blend < 0)
                {
                    byte most = 0;
                    for (int r = sample.Low; r <= sample.High; r++)
                    {
                        if (rows[r] > most)
                            most = rows[r];
                    }

                    strip[y] = most;
                }
                else
                {
                    strip[y] = rows[sample.Low] + ((rows[sample.High] - rows[sample.Low]) * sample.Blend);
                }
            }
        }

        private static void DropLeastRecent()
        {
            Tile? oldest = null;
            foreach (var tile in Tiles.Values)
            {
                if (oldest is null || tile.Drawn < oldest.Drawn)
                    oldest = tile;
            }

            if (oldest is null)
                return;
            Tiles.Remove(oldest.Key);
            if (Latest.TryGetValue(oldest.Key.Stretch, out var latest) && ReferenceEquals(latest, oldest))
                Latest.Remove(oldest.Key.Stretch);
            s_bytes -= (long)TileColumns * oldest.Key.Height * 4;
            oldest.Image?.Dispose();
        }

        /// <summary>
        /// What a pixel across the rows is made from: the loudest of rows <see cref="Low"/> to <see cref="High"/> when
        /// <see cref="Blend"/> is negative, or else <see cref="Blend"/> of the way from <see cref="Low"/>'s level to
        /// <see cref="High"/>'s. Rows are counted from the lowest.
        /// </summary>
        internal readonly record struct RowSample(int Low, int High, float Blend)
        {
            /// <summary>The row the pixel is most of, for whether it's dimmed.</summary>
            public int Nearest => Blend < 0 ? (Low + High) / 2 : Blend < 0.5f ? Low : High;
        }

        /// <summary>Which stretch of which spectrogram, at which level, through which contrast and band.</summary>
        private readonly record struct StretchKey(Spectrogram Spectrogram, int Level, int Index, byte Floor, byte Ceiling, SpectrogramBand Band);

        /// <summary>Which picture: of which stretch, of which of its rows (in <see cref="RowParts"/>ths, from the whole picture's top), at how many pixels across them.</summary>
        private readonly record struct TileKey(StretchKey Stretch, int Top, int Bottom, int Height);

        /// <summary>A picture, how many of its columns were ready when it was drawn, and when it was drawn and last used.</summary>
        private sealed class Tile(TileKey key)
        {
            public TileKey Key { get; } = key;

            /// <summary>The picture, as last made; none until it's first filled.</summary>
            public HighwayImage? Image { get; private set; }

            /// <summary>Puts the newest picture in place of the last, which is let go.</summary>
            public void Replace(HighwayImage image)
            {
                Image?.Dispose();
                Image = image;
            }

            public int Ready { get; set; }

            public long Built { get; set; }

            public long Drawn { get; set; }
        }
    }
}
