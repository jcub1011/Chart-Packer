using System;
using System.Collections.Generic;
using System.Diagnostics;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ChartShared.Audio.Waveforms;
using ChartShared.UI.Controls.Skia;

namespace ChartShared.UI.Controls
{
    /// <summary>
    /// Draws a waveform behind a lane's notes: along the lane's time axis, centred across it, as
    /// <see cref="WaveformView"/> draws a column, its peaks faintly behind how loud it is.
    /// </summary>
    /// <remarks>
    /// <para>A bar is a pixel of the screen long at any zoom (issue #296). Zoomed out, each holds the levels of the buckets it
    /// covers; zoomed in, past a bucket a pixel, the bars follow a curve through the buckets' middles, which never goes
    /// past either bucket it's between, so it draws no peak the audio doesn't have.</para>
    /// <para>The bars are drawn once for each stretch of a waveform into a picture, which is moved into the lane as it's
    /// drawn: a lane scrolling every frame only draws a picture or two, rather than every bar, which is as cheap as drawing
    /// gets. Shapes of many bars would be drawn again, point by point, every frame.</para>
    /// <para>Drawn within the <see cref="PaintBudget"/>, a picture there's no time left to draw this frame is stood in for by
    /// those of the same stretch already drawn at other zooms, the nearest first, if there are any, and the draw says it isn't
    /// complete. So is one of a zoom asked for only just now, while others cover it: zooming asks for a new zoom every frame,
    /// and its pictures are drawn once it stops, not a set each frame, each shown once.</para>
    /// </remarks>
    public static class LaneWaveformPainter
    {
        // A track whose loudest peak is below this (-60 dBFS) is silent: scaling it up would only draw its noise.
        private const double SilentPeak = 0.001;

        // How many bars each picture holds, a pixel each. A picture has as many pixels as it covers on the screen, both ways, so
        // it's drawn pixel for pixel, without smoothing, and its bars stay sharp.
        private const int BarsPerTile = 256;

        // How many pixels a picture may have across the lane.
        private const int MaxTilePixels = 4096;

        // The decimal places the lane's scale is rounded to, which a frame's movement can't change.
        private const int ScaleDigits = 6;

        // Pictures drawn pixel for pixel, not smoothed.
        private static readonly RenderOptions Sharp = new() { BitmapInterpolationMode = BitmapInterpolationMode.None, EdgeMode = EdgeMode.Aliased };

        // Pictures drawn at a slant, as a lane tilted back is, bilinearly: the bars shrink smoothly, and don't jump from one to the next
        // as they move. Not mipmapped, which at a slant picks the picture's smaller copy for the squeezed axis and blurs the other too.
        private static readonly RenderOptions Smooth = new() { BitmapInterpolationMode = BitmapInterpolationMode.LowQuality, EdgeMode = EdgeMode.Antialias };

        // More pictures than this kept, or more pixels than this in them, and the least recently drawn go: several lanes,
        // each drawing its waveform behind and beside, by channel, show many at once.
        private const int MaxTiles = 256;
        private const long MaxPixels = 24L * 1024 * 1024;

        private static readonly Dictionary<TileKey, Tile> Tiles = [];
        private static long s_drawn;

        // How long a zoom must have been asked for, unbroken, before its pictures are drawn where others can stand in.
        private static readonly long SettleTicks = Stopwatch.Frequency / 10;

        // A zoom not asked for in this long is forgotten, and has to settle again.
        private static readonly long ForgetTicks = Stopwatch.Frequency / 2;

        // When each zoom of each waveform was first asked for, and last.
        private static readonly Dictionary<(Waveform Waveform, double BucketsPerBar), (long First, long Last)> Zooms = [];

        // The pictures chosen to stand in for one not drawn, each cut to the buckets it covers, and the buckets still uncovered.
        // On the UI thread, as drawing is.
        private static readonly List<(TileKey Key, Tile Tile, double Distance)> s_candidates = [];
        private static readonly List<(TileKey Key, Tile Tile, double From, double To)> s_pieces = [];
        private static readonly List<(double From, double To)> s_gaps = [];

        // Gaps narrower than this, in buckets, are rounding, not gaps.
        private const double GapTolerance = 1e-6;

        // A picture's bars' levels as it's drawn, worked out once for both its passes. On the UI thread, as drawing is.
        private static readonly double[] s_lows = new double[BarsPerTile];
        private static readonly double[] s_highs = new double[BarsPerTile];
        private static readonly double[] s_louds = new double[BarsPerTile];

        /// <summary>How many pictures have been drawn since the program began, and how many are kept now: for a readout of what a lane costs.</summary>
        public static long TilesBuilt { get; private set; }

        public static int TileCount => Tiles.Count;
        private static long s_pixels;

        /// <summary>Drops every picture kept, as when the audio they're of is closed.</summary>
        public static void Clear()
        {
            foreach (var tile in Tiles.Values)
                tile.Image?.Dispose();
            Tiles.Clear();
            Zooms.Clear();
            s_pixels = 0;
        }

        /// <summary>
        /// Draws what's been read of <paramref name="waveform"/> between <paramref name="from"/> and <paramref name="to"/>
        /// seconds, across <paramref name="bounds"/>.
        /// </summary>
        /// <param name="isVertical">Whether time runs up or down the lane, rather than across it.</param>
        /// <param name="position">Where along the lane a time is drawn, in the lane's coordinates; linear.</param>
        /// <param name="gain">How much louder than it is to draw it, e.g. <see cref="FullScaleGain"/>.</param>
        /// <param name="pixel">How long a pixel of the screen is, in the lane's coordinates: each bar is that long, so a slow lane
        /// isn't drawn as thousands of slivers, and a fast one shows as much of the waveform's detail as the screen can.</param>
        /// <param name="budgeted">Draw new pictures only within the <see cref="PaintBudget"/>.</param>
        /// <param name="smooth">Smooth the pictures as they are drawn, rather than pixel for pixel: for a lane a transform shrinks, as a tilt does.</param>
        /// <returns>Whether all of it was drawn: <see langword="false"/> when the budget ran out, so it should be drawn again soon.</returns>
        public static bool Draw(IHighwayCanvas context, Waveform waveform, Rect bounds, bool isVertical, double from, double to, Func<double, double> position, IBrush peakBrush, IBrush loudBrush, double gain = 1, double pixel = 1, bool budgeted = false, bool smooth = false, bool clip = true)
        {
            int perSecond = waveform.BucketsPerSecond;
            double origin = position(0);
            double perBucket = (position(1) - origin) / perSecond;
            if (waveform.ReadCount == 0 || perBucket == 0 || !double.IsFinite(perBucket) || !(pixel > 0) || bounds.Width <= 0 || bounds.Height <= 0)
                return true;

            // A bar to a pixel, on a grid from the song's start, so the bars keep their places as the lane moves. The lane's scale
            // is rounded first: it's worked out from where it is, which changes every frame it moves, and the noise in that mustn't
            // move the bars or change the pictures between frames.
            double bucketsPerBar = Math.Round(pixel / Math.Abs(perBucket), ScaleDigits);
            if (!(bucketsPerBar > 0) || !double.IsFinite(bucketsPerBar))
                return true;

            double bucketsPerTile = bucketsPerBar * BarsPerTile;
            double first = Math.Max(0, Math.Min(from, to) * perSecond);
            double end = Math.Min(waveform.ReadCount, Math.Max(from, to) * perSecond);
            if (end <= first)
                return true;
            long firstTile = (long)Math.Floor(first / bucketsPerTile);
            long lastTile = (long)Math.Ceiling(end / bucketsPerTile) - 1;
            // The tiles are counted in ints, and the loop must be able to count past the last.
            if (lastTile >= int.MaxValue)
                return true;

            double centre = isVertical ? bounds.Center.X : bounds.Center.Y;
            double half = (isVertical ? bounds.Width : bounds.Height) / 2;

            // The screen's pixels a picture covers across the lane; along it, it has a pixel for each bar.
            int across = Math.Clamp((int)Math.Round(2 * half / pixel), 1, MaxTilePixels);

            // A picture spans its buckets along the lane, from its start, and levels from -1 to 1 across it: to the
            // right, or upwards, from the centre.
            bool complete = true;
            bool settled = !budgeted || IsSettled(waveform, bucketsPerBar);
            using (new OptionalClip(context, bounds, clip))
            using (context.PushRenderOptions(smooth ? Smooth : Sharp))
            {
                for (int index = (int)firstTile; index <= (int)lastTile; index++)
                {
                    var key = new TileKey(waveform, peakBrush, loudBrush, gain, bucketsPerBar, index, across);
                    if (Tiles.TryGetValue(key, out var kept))
                    {
                        kept.Drawn = ++s_drawn;
                        if (kept.Image is { } image)
                            DrawTile(image, key);
                        continue; // Else drawn, with nothing in it.
                    }

                    // A zoom still changing is drawn from other zooms' pictures while they cover it all, and its own once it stops.
                    double start = index * bucketsPerTile;
                    bool canBuild = !budgeted || PaintBudget.CanBuild();
                    bool covered = !(canBuild && settled) && PlanStandIn(key, start, start + bucketsPerTile);
                    if (canBuild && !covered)
                    {
                        if (TileOf(key, build: true) is { } built)
                            DrawTile(built, key);
                        continue;
                    }

                    // No time to draw it this frame, or its zoom hasn't settled: the same buckets drawn at other zooms stand in.
                    complete = false;
                    DrawStandIns();
                }
            }

            return complete;

            void DrawTile(HighwayImage tile, TileKey key)
            {
                double tileBuckets = key.BucketsPerBar * BarsPerTile;
                double along = origin + (perBucket * key.Index * tileBuckets);
                var matrix = isVertical
                    ? new Matrix(0, perBucket, half, 0, centre, along)
                    : new Matrix(perBucket, 0, 0, -half, along, centre);
                using (context.PushTransform(matrix))
                    context.DrawImage(tile, new Rect(0, 0, BarsPerTile, key.Across), new Rect(0, -1, tileBuckets, 2));
            }

            // The pictures PlanStandIn chose, each cut to the buckets it stands in for, and kept as recently drawn.
            void DrawStandIns()
            {
                foreach (var (other, tile, start, finish) in s_pieces)
                {
                    tile.Drawn = ++s_drawn;
                    double near = origin + (perBucket * start), far = origin + (perBucket * finish);
                    var span = isVertical
                        ? new Rect(bounds.X, Math.Min(near, far), bounds.Width, Math.Abs(far - near))
                        : new Rect(Math.Min(near, far), bounds.Y, Math.Abs(far - near), bounds.Height);
                    using (context.PushClip(span))
                        DrawTile(tile.Image!, other);
                }

                s_pieces.Clear();
            }
        }

        /// <summary>
        /// Whether <paramref name="bucketsPerBar"/> has been asked for of <paramref name="waveform"/> long enough, unbroken, that
        /// it isn't one a zoom is passing through; it's noted as asked for now.
        /// </summary>
        private static bool IsSettled(Waveform waveform, double bucketsPerBar)
        {
            long now = Stopwatch.GetTimestamp();
            var zoom = (waveform, bucketsPerBar);
            if (!Zooms.TryGetValue(zoom, out var seen) || now - seen.Last > ForgetTicks)
            {
                seen.First = now;

                // Those not asked for lately are forgotten as new ones come, so a zoom leaves only the last moment's behind.
                List<(Waveform, double)>? stale = null;
                foreach (var (other, times) in Zooms)
                {
                    if (now - times.Last > ForgetTicks)
                        (stale ??= []).Add(other);
                }

                foreach (var other in stale ?? [])
                    Zooms.Remove(other);
            }

            Zooms[zoom] = (seen.First, now);
            return now - seen.First >= SettleTicks;
        }

        /// <summary>
        /// Chooses, into <see cref="s_pieces"/>, the pictures kept at other zooms that stand in for <paramref name="key"/>, from
        /// <paramref name="start"/> to <paramref name="finish"/> buckets: the nearest zoom's first, then the next nearest's for the
        /// buckets still uncovered, and so on, each cut to the buckets it covers that no nearer one does.
        /// </summary>
        /// <returns>Whether they cover all of its buckets that have been read.</returns>
        private static bool PlanStandIn(TileKey key, double start, double finish)
        {
            s_pieces.Clear();
            s_candidates.Clear();
            foreach (var (other, kept) in Tiles)
            {
                if (kept.Image is not null && IsStandInFor(other, key, start, finish))
                    s_candidates.Add((other, kept, Math.Abs(Math.Log(other.BucketsPerBar / key.BucketsPerBar))));
            }

            // Past what's been read there's nothing to cover.
            finish = Math.Min(finish, key.Waveform.ReadCount);
            s_gaps.Clear();
            if (finish - start > GapTolerance)
                s_gaps.Add((start, finish));

            // The nearest zoom first; of two as near, the one drawn more recently.
            s_candidates.Sort((a, b) => a.Distance != b.Distance ? a.Distance.CompareTo(b.Distance) : b.Tile.Drawn.CompareTo(a.Tile.Drawn));
            foreach (var (other, kept, _) in s_candidates)
            {
                if (s_gaps.Count == 0)
                    break;

                double otherBuckets = other.BucketsPerBar * BarsPerTile;
                double otherStart = other.Index * otherBuckets, otherEnd = otherStart + otherBuckets;

                // From the end, as the gaps it leaves are added there.
                for (int g = s_gaps.Count - 1; g >= 0; g--)
                {
                    var (gapStart, gapEnd) = s_gaps[g];
                    double from = Math.Max(gapStart, otherStart), to = Math.Min(gapEnd, otherEnd);
                    if (to <= from)
                        continue;

                    s_pieces.Add((other, kept, from, to));
                    s_gaps.RemoveAt(g);
                    if (from - gapStart > GapTolerance)
                        s_gaps.Add((gapStart, from));
                    if (gapEnd - to > GapTolerance)
                        s_gaps.Add((to, gapEnd));
                }
            }

            s_candidates.Clear();
            return s_gaps.Count == 0;
        }

        /// <summary>Whether <paramref name="other"/> is of the same waveform, looks and size across as <paramref name="key"/>, and covers some of its buckets from <paramref name="start"/> to <paramref name="finish"/>.</summary>
        private static bool IsStandInFor(TileKey other, TileKey key, double start, double finish)
        {
            if (other.BucketsPerBar == key.BucketsPerBar || !ReferenceEquals(other.Waveform, key.Waveform) || !ReferenceEquals(other.PeakBrush, key.PeakBrush)
                || !ReferenceEquals(other.LoudBrush, key.LoudBrush) || other.Gain != key.Gain || other.Across != key.Across)
                return false;

            double otherBuckets = other.BucketsPerBar * BarsPerTile;
            double otherStart = other.Index * otherBuckets;
            return otherStart < finish && otherStart + otherBuckets > start;
        }

        /// <summary>
        /// The gain that brings the loudest peak read so far of <paramref name="waveform"/> to full scale, so a quiet
        /// track's beats stand out as clearly as a loud one's; 1 for a silent track.
        /// </summary>
        /// <remarks>While a track is read the gain can drop, as a louder part is read, until the whole track is.</remarks>
        public static double FullScaleGain(Waveform waveform) => waveform.Peak > SilentPeak ? 1 / waveform.Peak : 1;

        /// <summary>
        /// The levels bar <paramref name="bar"/> is drawn at, of bars <paramref name="bucketsPerBar"/> buckets long from the
        /// waveform's start: its lowest and highest, and how loud it is (<see cref="WaveformLevels.Loudness"/>). <see langword="null"/>
        /// for a bar that starts past what's been read.
        /// </summary>
        /// <remarks>
        /// A bar a bucket long or more has the levels of every bucket it touches, so neighbouring bars share the buckets at their
        /// edges and none jumps out for having fewer. A shorter one is on a curve through the buckets' middles, a monotone cubic
        /// one: smooth, and never past either bucket it's between, so it draws no peak the audio doesn't have.
        /// </remarks>
        internal static (double Low, double High, double Loud)? BarLevels(Waveform waveform, double bucketsPerBar, long bar)
        {
            int read = waveform.ReadCount;
            double start = bar * bucketsPerBar;
            if (read == 0 || bar < 0 || start >= read)
                return null;

            if (bucketsPerBar >= 1)
            {
                int first = (int)Math.Floor(start);
                int last = (int)Math.Min(read, Math.Ceiling(start + bucketsPerBar));
                var (low, high, rms) = waveform.LevelsOf(first, last);
                return (low, high, WaveformLevels.Loudness(low, high, rms));
            }

            // Where the bar's middle is between the middles of buckets i and i + 1.
            double at = start + (bucketsPerBar / 2) - 0.5;
            int i = (int)Math.Floor(at);
            double t = at - i;
            double curvedLow = Math.Min(0, Curve(waveform.Min, i, t, read));
            double curvedHigh = Math.Max(0, Curve(waveform.Max, i, t, read));
            double curvedRms = Math.Max(0, Curve(waveform.Rms, i, t, read));
            return (curvedLow, curvedHigh, WaveformLevels.Loudness(curvedLow, curvedHigh, curvedRms));
        }

        /// <summary>
        /// The value <paramref name="t"/> of the way from the middle of bucket <paramref name="i"/> of <paramref name="values"/> to the
        /// next's, on a monotone cubic curve through them; the buckets past either end of the first <paramref name="read"/> are
        /// the end ones.
        /// </summary>
        private static double Curve(IReadOnlyList<float> values, int i, double t, int read)
        {
            return MonotoneCubic(At(i - 1), At(i), At(i + 1), At(i + 2), t);

            double At(int index) => values[Math.Clamp(index, 0, read - 1)];
        }

        /// <summary>
        /// The value <paramref name="t"/> of the way from <paramref name="p1"/> to <paramref name="p2"/>, from 0 to 1, on a cubic
        /// curve through <paramref name="p0"/> to <paramref name="p3"/> that never leaves the range of <paramref name="p1"/> and
        /// <paramref name="p2"/>: its slopes at them are those of Fritsch and Butland, so it rises where they rise and is flat at a peak.
        /// </summary>
        internal static double MonotoneCubic(double p0, double p1, double p2, double p3, double t)
        {
            double before = p1 - p0, between = p2 - p1, after = p3 - p2;
            double m1 = Slope(before, between), m2 = Slope(between, after);
            double t2 = t * t, t3 = t2 * t;
            return ((2 * t3 - 3 * t2 + 1) * p1) + ((t3 - 2 * t2 + t) * m1) + ((-2 * t3 + 3 * t2) * p2) + ((t3 - t2) * m2);

            // None where the curve turns, or either side is flat; else the harmonic mean of the slopes either side, which is
            // never more than twice the shallower, and keeps the curve between its ends.
            static double Slope(double a, double b) => a * b <= 0 ? 0 : 2 * a * b / (a + b);
        }

        /// <summary>
        /// The picture for <paramref name="key"/>, drawn if it isn't kept and <paramref name="build"/> allows; <see langword="null"/>
        /// when there's nothing in it, or it wasn't drawn.
        /// </summary>
        private static HighwayImage? TileOf(TileKey key, bool build)
        {
            if (!Tiles.TryGetValue(key, out var tile))
            {
                if (!build)
                    return null;

                long pixels = (long)BarsPerTile * key.Across;
                while (Tiles.Count > 0 && (Tiles.Count >= MaxTiles || s_pixels + pixels > MaxPixels))
                    DropLeastRecent();

                long started = Stopwatch.GetTimestamp();
                tile = new Tile(Build(key), pixels);
                PaintBudget.Spend(started);
                Tiles[key] = tile;
                TilesBuilt++;
                s_pixels += pixels;
            }

            tile.Drawn = ++s_drawn;
            return tile.Image;
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
            s_pixels -= oldest.Value?.Pixels ?? 0;

            // A frame already drawn with it keeps what it needs of it.
            oldest.Value?.Image?.Dispose();
        }

        /// <summary>
        /// The bars of picture <paramref name="key"/>, a pixel each from its own start, at its gain and as far as -1 and 1; they end
        /// at the edge of what's been read.
        /// </summary>
        private static HighwayImage? Build(TileKey key)
        {
            var waveform = key.Waveform;
            long firstBar = (long)key.Index * BarsPerTile;
            int bars = 0;
            while (bars < BarsPerTile && BarLevels(waveform, key.BucketsPerBar, firstBar + bars) is (double low, double high, double loud))
            {
                s_lows[bars] = low;
                s_highs[bars] = high;
                s_louds[bars] = loud;
                bars++;
            }

            if (bars == 0)
                return null;

            // Drawn on a picture in memory, a pixel to a unit.
            var context = SkiaRaster.Begin();
            using (context.PushRenderOptions(Sharp))
            {
                for (int bar = 0; bar < bars; bar++)
                    Bar(context, key.PeakBrush, key.Across, bar, s_lows[bar] * key.Gain, s_highs[bar] * key.Gain);
                for (int bar = 0; bar < bars; bar++)
                    Bar(context, key.LoudBrush, key.Across, bar, -s_louds[bar] * key.Gain, s_louds[bar] * key.Gain);
            }

            return HighwayImage.Of(SkiaRaster.End(BarsPerTile, key.Across));
        }

        /// <summary>Bar <paramref name="at"/>, a pixel wide, from <paramref name="low"/> to <paramref name="high"/>, from -1 at the picture's top to 1 at its bottom.</summary>
        private static void Bar(IHighwayCanvas context, IBrush brush, int height, int at, double low, double high)
        {
            double top = Math.Round((Math.Clamp(low, -1, 1) + 1) / 2 * height);
            double bottom = Math.Round((Math.Clamp(high, -1, 1) + 1) / 2 * height);
            if (bottom > top)
                context.FillRectangle(brush, new Rect(at, top, 1, bottom - top));
        }

        /// <summary>
        /// Which picture: of which waveform, in which brushes and at which gain, how many buckets to a bar, which stretch, and how many
        /// pixels it has across the lane.
        /// </summary>
        private readonly record struct TileKey(Waveform Waveform, IBrush PeakBrush, IBrush LoudBrush, double Gain, double BucketsPerBar, int Index, int Across);

        /// <summary>A picture of a stretch of bars, or none when there's nothing in it, its size, and when it was last drawn.</summary>
        private sealed class Tile(HighwayImage? image, long pixels)
        {
            public HighwayImage? Image { get; } = image;

            public long Pixels { get; } = pixels;

            public long Drawn { get; set; }
        }
    }
}
