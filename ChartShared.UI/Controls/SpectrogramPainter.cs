using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ChartShared.Audio.Analysis;
using ChartShared.Core.Charting.Layout;

namespace ChartShared.UI.Controls
{
    /// <summary>
    /// Draws a spectrogram (charter-spec C13) along a lane's time axis: low frequencies on the left of a highway, or at the
    /// bottom of a timeline, its levels through a contrast floor and ceiling to <see cref="SpectrogramColors"/>, and rows
    /// outside the band chosen dimmed. Columns not yet computed are left blank, and fill in as they are.
    /// </summary>
    /// <remarks>
    /// <para>As <see cref="LaneWaveformPainter"/> does, it draws stretches of the spectrogram once into pictures that are
    /// moved and stretched into the lane: 256 of the spectrogram's columns across a picture, or, zoomed out, 256 groups of
    /// a power of two of them, each group's loudest. The pictures are drawn within the <see cref="PaintBudget"/>, and one
    /// that was drawn before all of its columns were ready is drawn again once more are, at most every
    /// <see cref="RefreshInterval"/>.</para>
    /// <para>For the UI thread only.</para>
    /// </remarks>
    public static class SpectrogramPainter
    {
        private const int TileColumns = 256;
        private const int Rows = Spectrogram.Rows;
        private const int MaxTiles = 128;
        private const int MaxLevel = 14;

        /// <summary>A dimmed row's colour is taken from this far up the levels, as a fraction.</summary>
        private const double Dimmed = 0.3;

        // How far under a power of two the columns to a pixel may be and still be taken as it.
        private const double LevelSlack = 1e-6;

        private static readonly long RefreshInterval = Stopwatch.Frequency / 10;
        private static readonly RenderOptions Smooth = new() { BitmapInterpolationMode = BitmapInterpolationMode.MediumQuality };
        private static readonly Dictionary<TileKey, Tile> Tiles = [];
        private static readonly int[] Pixels = new int[TileColumns * Rows];
        private static long s_drawn;

        /// <summary>Drops every picture kept, as when the audio they're of is closed.</summary>
        public static void Clear()
        {
            foreach (var tile in Tiles.Values)
                tile.Bitmap.Dispose();
            Tiles.Clear();
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
        /// <returns>Whether all of it was drawn as it is now: <see langword="false"/> when a picture had to wait for the budget.</returns>
        public static bool Draw(DrawingContext context, Spectrogram spectrogram, Rect bounds, bool isVertical, double from, double to, Func<double, double> position,
            double floorDb, double ceilingDb, SpectrogramBand band, double pixel = 1)
        {
            double origin = position(0);
            double perColumn = (position(1) - origin) * spectrogram.SecondsPerColumn;
            if (spectrogram.IsReleased || perColumn == 0 || !double.IsFinite(perColumn) || bounds.Width <= 0 || bounds.Height <= 0)
                return true;

            // A power of two of columns to a picture's pixel, as many as fit in a screen pixel; a hair under a power of two
            // is taken as it, so the level doesn't flip between frames as the lane moves.
            int level = Math.Clamp((int)Math.Ceiling(Math.Log2(Math.Max(1, pixel / Math.Abs(perColumn))) - LevelSlack), 0, MaxLevel);
            int span = TileColumns << level;
            int first = Math.Max(0, spectrogram.ColumnAt(Math.Min(from, to)) - 1);
            int last = Math.Min(spectrogram.ColumnCount - 1, spectrogram.ColumnAt(Math.Max(from, to)) + 1);
            if (last < first)
                return true;

            var levels = (Spectrogram.LevelOf(floorDb), Spectrogram.LevelOf(ceilingDb));
            bool complete = true;
            using (context.PushClip(bounds))
            using (context.PushRenderOptions(Smooth))
            {
                for (int index = first / span; index <= last / span; index++)
                {
                    var key = new TileKey(spectrogram, level, index, levels.Item1, levels.Item2, band);
                    if (TileOf(key, out bool current) is { } bitmap)
                    {
                        complete &= current;
                        DrawTile(bitmap, level, index);
                        continue;
                    }

                    // Not drawn yet: a neighbouring level's pictures of the same columns stand in, rather than a gap.
                    complete = false;
                    long start = (long)index * span;
                    foreach (int other in (ReadOnlySpan<int>)[level + 1, level - 1])
                    {
                        if (other < 0 || other > MaxLevel)
                            continue;
                        int otherSpan = TileColumns << other;
                        bool any = false;
                        for (long at = start / otherSpan; at * otherSpan < start + span; at++)
                        {
                            if (!Tiles.TryGetValue(key with { Level = other, Index = (int)at }, out var stand))
                                continue;
                            double near = origin + (perColumn * (start - 0.5)), far = origin + (perColumn * (start + span - 0.5));
                            var clip = isVertical
                                ? new Rect(bounds.X, Math.Min(near, far), bounds.Width, Math.Abs(far - near))
                                : new Rect(Math.Min(near, far), bounds.Y, Math.Abs(far - near), bounds.Height);
                            using (context.PushClip(clip))
                                DrawTile(stand.Bitmap, other, (int)at);
                            any = true;
                        }

                        if (any)
                            break;
                    }
                }
            }

            return complete;

            void DrawTile(WriteableBitmap bitmap, int tileLevel, int tileIndex)
            {
                // A picture's pixel x is columns start + x·2^level, centred half a column earlier.
                double along = origin + (perColumn * ((tileIndex * (double)(TileColumns << tileLevel)) - 0.5));
                double step = perColumn * (1 << tileLevel);
                var matrix = isVertical
                    ? new Matrix(0, step, -bounds.Width / Rows, 0, bounds.Right, along)
                    : new Matrix(step, 0, 0, bounds.Height / Rows, along, bounds.Top);
                using (context.PushTransform(matrix))
                    context.DrawImage(bitmap, new Rect(0, 0, TileColumns, Rows), new Rect(0, 0, TileColumns, Rows));
            }
        }

        /// <summary>
        /// The picture for <paramref name="key"/>, drawn or drawn again within the budget; <see langword="null"/> when
        /// there's none yet. <paramref name="current"/> says whether it shows every column ready now.
        /// </summary>
        private static WriteableBitmap? TileOf(TileKey key, out bool current)
        {
            int ready = ReadyIn(key);
            long now = Stopwatch.GetTimestamp();
            if (Tiles.TryGetValue(key, out var tile))
            {
                tile.Drawn = ++s_drawn;
                current = tile.Ready == ready;
                if (current || now - tile.Built < RefreshInterval || !PaintBudget.CanBuild())
                    return tile.Bitmap;

                Fill(tile.Bitmap, key);
                tile.Ready = ready;
                tile.Built = now;
                PaintBudget.Spend(now);
                current = true;
                return tile.Bitmap;
            }

            current = false;
            if (ready == 0 || !PaintBudget.CanBuild())
                return null;

            if (Tiles.Count >= MaxTiles)
                DropLeastRecent();
            var bitmap = new WriteableBitmap(new PixelSize(TileColumns, Rows), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            Fill(bitmap, key);
            PaintBudget.Spend(now);
            Tiles[key] = new Tile(bitmap) { Ready = ready, Built = now, Drawn = ++s_drawn };
            current = true;
            return bitmap;
        }

        /// <summary>How many of the picture's columns are ready, counted by chunk.</summary>
        private static int ReadyIn(TileKey key)
        {
            var spectrogram = key.Spectrogram;
            long start = (long)key.Index * (TileColumns << key.Level);
            long end = Math.Min(spectrogram.ColumnCount, start + (TileColumns << key.Level));
            int ready = 0;
            for (long chunk = start / spectrogram.ChunkColumns; chunk * spectrogram.ChunkColumns < end; chunk++)
                ready += spectrogram.ReadyIn((int)chunk);
            return ready;
        }

        /// <summary>Draws the picture's columns that are ready; the rest are left clear.</summary>
        private static void Fill(WriteableBitmap bitmap, TileKey key)
        {
            var spectrogram = key.Spectrogram;
            var ramp = SpectrogramColors.Ramp;
            var emphasised = SpectrogramBands.Emphasised(key.Band);
            int floor = key.Floor, range = Math.Max(1, key.Ceiling - key.Floor);
            int group = 1 << key.Level;
            long start = (long)key.Index * TileColumns * group;
            Span<byte> loudest = stackalloc byte[Rows];
            var pixels = Pixels;

            for (int x = 0; x < TileColumns; x++)
            {
                loudest.Clear();
                bool any = false;
                for (int c = 0; c < group; c++)
                {
                    long column = start + ((long)x * group) + c;
                    if (column >= spectrogram.ColumnCount)
                        break;
                    var rows = spectrogram.Column((int)column);
                    if (rows.IsEmpty)
                        continue;
                    any = true;
                    for (int r = 0; r < Rows; r++)
                    {
                        if (rows[r] > loudest[r])
                            loudest[r] = rows[r];
                    }
                }

                // Row r is drawn at the picture's y = 255 - r, so the lowest is at the bottom.
                for (int r = 0; r < Rows; r++)
                {
                    uint colour = 0;
                    if (any)
                    {
                        int shade = Math.Clamp((loudest[r] - floor) * 255 / range, 0, 255);
                        if (!emphasised[r])
                            shade = (int)(shade * Dimmed);
                        colour = ramp[shade];
                    }

                    pixels[((Rows - 1 - r) * TileColumns) + x] = (int)colour;
                }
            }

            using var buffer = bitmap.Lock();
            for (int y = 0; y < Rows; y++)
                Marshal.Copy(pixels, y * TileColumns, buffer.Address + (y * buffer.RowBytes), TileColumns);
        }

        private static void DropLeastRecent()
        {
            var oldest = default(KeyValuePair<TileKey, Tile>);
            foreach (var entry in Tiles)
            {
                if (oldest.Value is null || entry.Value.Drawn < oldest.Value.Drawn)
                    oldest = entry;
            }

            Tiles.Remove(oldest.Key);
            oldest.Value?.Bitmap.Dispose();
        }

        /// <summary>Which picture: of which spectrogram, at which level, which stretch, and through which contrast and band.</summary>
        private readonly record struct TileKey(Spectrogram Spectrogram, int Level, int Index, byte Floor, byte Ceiling, SpectrogramBand Band);

        /// <summary>A picture, how many of its columns were ready when it was drawn, and when it was drawn and last used.</summary>
        private sealed class Tile(WriteableBitmap bitmap)
        {
            public WriteableBitmap Bitmap { get; } = bitmap;

            public int Ready { get; set; }

            public long Built { get; set; }

            public long Drawn { get; set; }
        }
    }
}
