using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ChartPacker.Audio.Waveforms;

namespace ChartPacker.Controls
{
    /// <summary>
    /// Draws a waveform behind a lane's notes: along the lane's time axis, centred across it, as
    /// <see cref="WaveformView"/> draws a column, its peaks faintly behind how loud it is.
    /// </summary>
    /// <remarks>
    /// The bars are drawn once for each stretch of a waveform into a picture, which is moved and stretched into the
    /// lane as it's drawn: a lane scrolling every frame only draws a picture or two, rather than every bar, which is
    /// as cheap as drawing gets. Shapes of many bars would be drawn again, point by point, every frame.
    /// </remarks>
    public static class LaneWaveformPainter
    {
        // A track whose loudest peak is below this (-60 dBFS) is silent: scaling it up would only draw its noise.
        private const double SilentPeak = 0.001;

        // How many bars each picture holds. A picture has as many pixels as it covers on the screen, both ways, so it's
        // drawn pixel for pixel, without smoothing, and its bars stay sharp.
        private const int BarsPerTile = 256;

        // How many pixels a picture may have across the lane, or along it.
        private const int MaxTilePixels = 4096;

        // Pictures drawn pixel for pixel, not smoothed.
        private static readonly RenderOptions Sharp = new() { BitmapInterpolationMode = BitmapInterpolationMode.None, EdgeMode = EdgeMode.Aliased };

        // More pictures than this kept, and the least recently drawn go: the lanes show a couple each at once.
        private const int MaxTiles = 48;

        private static readonly Dictionary<TileKey, Tile> Tiles = [];
        private static long s_drawn;

        /// <summary>
        /// Draws what's been read of <paramref name="waveform"/> between <paramref name="from"/> and <paramref name="to"/>
        /// seconds, across <paramref name="bounds"/>.
        /// </summary>
        /// <param name="isVertical">Whether time runs up or down the lane, rather than across it.</param>
        /// <param name="position">Where along the lane a time is drawn, in the lane's coordinates; linear.</param>
        /// <param name="gain">How much louder than it is to draw it, e.g. <see cref="FullScaleGain"/>.</param>
        /// <param name="pixel">How long a pixel of the screen is, in the lane's coordinates: each bar is at least that long, so a slow lane
        /// isn't drawn as thousands of slivers, and a fast one shows as much of the waveform's detail as the screen can.</param>
        public static void Draw(DrawingContext context, Waveform waveform, Rect bounds, bool isVertical, double from, double to, Func<double, double> position, IBrush peakBrush, IBrush loudBrush, double gain = 1, double pixel = 1)
        {
            int perSecond = waveform.BucketsPerSecond;
            double origin = position(0);
            double perBucket = (position(1) - origin) / perSecond;
            if (waveform.ReadCount == 0 || perBucket == 0 || !double.IsFinite(perBucket) || bounds.Width <= 0 || bounds.Height <= 0)
                return;

            // Whole buckets to a bar, a power of two of them, on a grid from the song's start, so the bars keep their
            // places as the lane moves and the same pictures do for any speed near this one.
            int bucketsPerBar = (int)Math.Min(1 << 20, System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Max(1, Math.Ceiling(pixel / Math.Abs(perBucket)))));
            int bucketsPerTile = bucketsPerBar * BarsPerTile;
            int first = Math.Max(0, (int)Math.Floor(Math.Min(from, to) * perSecond));
            int end = Math.Min(waveform.ReadCount, (int)Math.Ceiling(Math.Max(from, to) * perSecond));
            if (end <= first)
                return;

            double centre = isVertical ? bounds.Center.X : bounds.Center.Y;
            double half = (isVertical ? bounds.Width : bounds.Height) / 2;

            // The screen's pixels a picture covers: along the lane, for its bars, and across it.
            int length = Math.Clamp((int)Math.Round(Math.Abs(perBucket) * bucketsPerTile / pixel), 1, MaxTilePixels);
            int across = Math.Clamp((int)Math.Round(2 * half / pixel), 1, MaxTilePixels);

            // A picture spans its buckets along the lane, from its start, and levels from -1 to 1 across it: to the
            // right, or upwards, from the centre.
            using (context.PushClip(bounds))
            using (context.PushRenderOptions(Sharp))
            {
                for (int index = first / bucketsPerTile; index <= (end - 1) / bucketsPerTile; index++)
                {
                    var tile = TileOf(new TileKey(waveform, peakBrush, loudBrush, gain, bucketsPerBar, index, length, across));
                    if (tile is null)
                        continue;

                    double along = origin + (perBucket * index * bucketsPerTile);
                    var matrix = isVertical
                        ? new Matrix(0, perBucket, half, 0, centre, along)
                        : new Matrix(perBucket, 0, 0, -half, along, centre);
                    using (context.PushTransform(matrix))
                        context.DrawImage(tile, new Rect(0, 0, length, across), new Rect(0, -1, bucketsPerTile, 2));
                }
            }
        }

        /// <summary>
        /// The gain that brings the loudest peak read so far of <paramref name="waveform"/> to full scale, so a quiet
        /// track's beats stand out as clearly as a loud one's; 1 for a silent track.
        /// </summary>
        /// <remarks>While a track is read the gain can drop, as a louder part is read, until the whole track is.</remarks>
        public static double FullScaleGain(Waveform waveform) => waveform.Peak > SilentPeak ? 1 / waveform.Peak : 1;

        /// <summary>The picture for <paramref name="key"/>, drawn if it isn't kept; <see langword="null"/> when there's nothing in it.</summary>
        private static RenderTargetBitmap? TileOf(TileKey key)
        {
            if (!Tiles.TryGetValue(key, out var tile))
            {
                if (Tiles.Count >= MaxTiles)
                    DropLeastRecent();

                tile = new Tile(Build(key));
                Tiles[key] = tile;
            }

            tile.Drawn = ++s_drawn;
            return tile.Bitmap;
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

            // A frame already drawn with it keeps what it needs of it.
            oldest.Value?.Bitmap?.Dispose();
        }

        /// <summary>
        /// The bars of picture <paramref name="key"/>, from its own start, at its gain and as far as -1 and 1; the bar at
        /// the edge of what's been read ends there.
        /// </summary>
        private static RenderTargetBitmap? Build(TileKey key)
        {
            var waveform = key.Waveform;
            int bucketsPerBar = key.BucketsPerBar;
            int start = key.Index * bucketsPerBar * BarsPerTile;
            int end = Math.Min(waveform.ReadCount, start + (bucketsPerBar * BarsPerTile));
            if (end <= start)
                return null;

            var bitmap = new RenderTargetBitmap(new PixelSize(key.Length, key.Across), new Vector(96, 96));
            double perBar = (double)key.Length / BarsPerTile;
            using (var context = bitmap.CreateDrawingContext())
            using (context.PushRenderOptions(Sharp))
            {
                for (int pass = 0; pass < 2; pass++)
                {
                    var brush = pass == 0 ? key.PeakBrush : key.LoudBrush;
                    for (int bucket = start; bucket < end; bucket += bucketsPerBar)
                    {
                        int last = Math.Min(bucket + bucketsPerBar, end);
                        var (low, high, rms) = waveform.LevelsOf(bucket, last);
                        double loud = WaveformLevels.Loudness(low, high, rms);
                        // Whole pixels, so neighbouring bars meet without a seam or an overlap.
                        double near = Math.Round((double)(bucket - start) / bucketsPerBar * perBar);
                        double far = Math.Round((double)(last - start) / bucketsPerBar * perBar);
                        if (pass == 0)
                            Bar(context, brush, key.Across, near, far, low * key.Gain, high * key.Gain);
                        else
                            Bar(context, brush, key.Across, near, far, -loud * key.Gain, loud * key.Gain);
                    }
                }
            }

            return bitmap;
        }

        /// <summary>A bar from <paramref name="low"/> to <paramref name="high"/>, from -1 at the picture's top to 1 at its bottom.</summary>
        private static void Bar(DrawingContext context, IBrush brush, int height, double near, double far, double low, double high)
        {
            double top = Math.Round((Math.Clamp(low, -1, 1) + 1) / 2 * height);
            double bottom = Math.Round((Math.Clamp(high, -1, 1) + 1) / 2 * height);
            if (bottom > top && far > near)
                context.FillRectangle(brush, new Rect(near, top, far - near, bottom - top));
        }

        /// <summary>
        /// Which picture: of which waveform, in which brushes and at which gain, how many buckets to a bar, which stretch, and how many
        /// pixels it has along the lane and across it.
        /// </summary>
        private readonly record struct TileKey(Waveform Waveform, IBrush PeakBrush, IBrush LoudBrush, double Gain, int BucketsPerBar, int Index, int Length, int Across);

        /// <summary>A picture of a stretch of bars, or none when there's nothing in it, and when it was last drawn.</summary>
        private sealed class Tile(RenderTargetBitmap? bitmap)
        {
            public RenderTargetBitmap? Bitmap { get; } = bitmap;

            public long Drawn { get; set; }
        }
    }
}
