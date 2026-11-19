using System;
using System.Collections.Generic;
using System.Diagnostics;
using Avalonia;
using Avalonia.Media;
using ChartShared.Audio.Waveforms;
using ChartShared.UI.Controls.Skia;

namespace ChartShared.UI.Controls
{
    /// <summary>
    /// Draws a waveform behind a lane's notes: along the lane's time axis, centred across it, as
    /// <see cref="WaveformView"/> draws a column, its peaks faintly behind how loud it is.
    /// </summary>
    /// <remarks>
    /// <para>The waveform is a shape, not a picture: a filled outline of its peaks and another of how loud it is, anti-aliased as the
    /// screen draws them (issue #300). A picture of bars has to be resampled when a lane is tilted back, which blurs or aliases it, and
    /// pictures of different detail meet in a visible edge; an outline goes through the tilt as it is, with nothing to sample.</para>
    /// <para>A column is a pixel of the screen long, or less, at any zoom (issue #296). Zoomed out, each holds the levels of the buckets it
    /// covers; zoomed in, past a bucket a pixel, the columns follow a curve through the buckets' middles, which never goes
    /// past either bucket it's between, so it draws no peak the audio doesn't have. A tilted lane's far end, which the screen shows
    /// smaller, has fewer, longer columns, in the same outline, so there's no join in it.</para>
    /// <para>The outline is made once for the stretch of the waveform on show, and kept: a lane scrolling every frame only moves it. It's
    /// made again when a chunk of columns comes into view or changes its detail, from the levels of each chunk, which are kept too.</para>
    /// <para>Drawn within the <see cref="PaintBudget"/>, a chunk there's no time left to make this frame is stood in for by the same
    /// buckets' levels at another detail, or as they were before more was read, or left out, and the lane is drawn again soon to carry on.</para>
    /// </remarks>
    public static class LaneWaveformPainter
    {
        // A track whose loudest peak is below this (-60 dBFS) is silent: scaling it up would only draw its noise.
        private const double SilentPeak = 0.001;

        // How many columns each chunk holds. A chunk is a stretch of 2^level buckets a column from the waveform's start, so those of
        // different detail nest, and each keeps its places as the lane moves.
        private const int ColumnsPerChunk = 256;

        // The finest and coarsest detail a column has, as powers of two of a bucket: past the finest the lane is zoomed in further than
        // there's anything to show; past the coarsest, out further than a lane's worth is anything but a line.
        private const int FinestLevel = -8;
        private const int CoarsestLevel = 20;

        // The most chunks of levels kept: several lanes, each drawing its waveform by channel, show many at once.
        private const int MaxChunks = 512;

        // The most outlines kept. One frame can draw one for each lane, by channel, behind it and beside it, at each lane's boost,
        // and the preview's too: a dozen stereo lanes behind and beside is 48. Fewer than a frame draws would make every one again
        // every frame, and each is only a path or two, so there's room to spare.
        private const int MaxOutlines = 128;

        // The most chunks one outline is made of; a lane's worth is a handful. Past it, the rest is one chunk as coarse as it takes.
        private const int MaxCover = 256;

        // How much coarser a kept chunk may be to stand in for one there's no time to make.
        private const int StandInLevels = 4;

        // The decimal places the lane's scale is rounded to, which a frame's movement can't change.
        private const int ScaleDigits = 6;

        // Outlines are drawn antialiased: their edges are shaded by how much of a pixel they cover, at any tilt.
        private static readonly RenderOptions Smooth = new() { EdgeMode = EdgeMode.Antialias };

        private static readonly Dictionary<ChunkKey, Chunk> Chunks = [];
        private static readonly Dictionary<OutlineKey, Outline> Outlines = [];
        private static long s_used;

        // The chunks chosen to cover what's drawn, and the points of the outlines made of them. On the UI thread, as drawing is.
        private static readonly List<(int Level, int Index)> s_cover = [];
        private static readonly List<Point> s_peakUp = [];
        private static readonly List<Point> s_peakDown = [];
        private static readonly List<Point> s_loudUp = [];
        private static readonly List<Point> s_loudDown = [];

        // Where each unbroken run of the points starts, and how long its first column is: a chunk left out breaks the outline in two.
        private static readonly List<int> s_runs = [];
        private static readonly List<double> s_runColumns = [];

        /// <summary>How many chunks of levels have been made since the program began, and how many are kept now: for a readout of what a lane costs.</summary>
        public static long ChunksBuilt { get; private set; }

        public static int ChunkCount => Chunks.Count;

        /// <summary>Drops everything kept, as when the audio it's of is closed.</summary>
        public static void Clear()
        {
            Chunks.Clear();
            Outlines.Clear();
        }

        /// <summary>
        /// Draws what's been read of <paramref name="waveform"/> between <paramref name="from"/> and <paramref name="to"/>
        /// seconds, across <paramref name="bounds"/>.
        /// </summary>
        /// <param name="isVertical">Whether time runs up or down the lane, rather than across it.</param>
        /// <param name="position">Where along the lane a time is drawn, in the lane's coordinates; linear.</param>
        /// <param name="gain">How much louder than it is to draw it, e.g. <see cref="FullScaleGain"/>.</param>
        /// <param name="pixel">How long a pixel of the screen is, in the lane's coordinates: each column is at most that long, so a slow
        /// lane isn't drawn as thousands of slivers, and a fast one shows as much of the waveform's detail as the screen can.</param>
        /// <param name="foreshortening">For a lane tilted back: how long a unit of the lane's coordinates along it is on the screen at a
        /// position, as a fraction of what it is where <paramref name="pixel"/> was measured (the near end): 1 there, less further. Its
        /// columns are as long as to be a pixel there, so the far end, which the screen shows smaller, doesn't have more than it can show.
        /// <see langword="null"/> for a flat lane.</param>
        /// <param name="budgeted">Make new chunks of levels only within the <see cref="PaintBudget"/>, standing others in for the rest.</param>
        /// <returns>Whether all of it was drawn as it is now: <see langword="false"/> when the budget ran out, so it should be drawn again soon.</returns>
        public static bool Draw(IHighwayCanvas context, Waveform waveform, Rect bounds, bool isVertical, double from, double to, Func<double, double> position, IBrush peakBrush, IBrush loudBrush, double gain = 1, double pixel = 1, bool budgeted = false, bool clip = true, Func<double, double>? foreshortening = null)
        {
            int perSecond = waveform.BucketsPerSecond;
            double origin = position(0);
            double perBucket = (position(1) - origin) / perSecond;
            if (waveform.ReadCount == 0 || perBucket == 0 || !double.IsFinite(perBucket) || !(pixel > 0) || !(gain > 0) || !double.IsFinite(gain) || bounds.Width <= 0 || bounds.Height <= 0)
                return true;

            // The buckets a pixel is long. It's worked out from where the lane is, which changes every frame it moves, and the noise
            // in that mustn't change the detail between frames: it's rounded.
            double bucketsPerPixel = Math.Round(pixel / Math.Abs(perBucket), ScaleDigits);
            if (!(bucketsPerPixel > 0) || !double.IsFinite(bucketsPerPixel))
                return true;

            double first = Math.Max(0, Math.Min(from, to) * perSecond);
            double end = Math.Min(waveform.ReadCount, Math.Max(from, to) * perSecond);
            if (end <= first)
                return true;

            PlanCover(s_cover, first, end, origin, perBucket, bucketsPerPixel, foreshortening);
            var outline = OutlineOf(waveform, gain, s_cover, budgeted);
            if (outline is null)
                return true;
            if (outline.Peak is not { } peak || outline.Loud is not { } loud)
                return outline.Complete;

            double centre = isVertical ? bounds.Center.X : bounds.Center.Y;
            double half = (isVertical ? bounds.Width : bounds.Height) / 2;

            // The outline's points are the buckets along the lane from its own start, and levels from -1 to 1 across it: to the
            // right, or upwards, from the centre.
            double along = origin + (perBucket * outline.Start);
            var matrix = isVertical
                ? new Matrix(0, perBucket, half, 0, centre, along)
                : new Matrix(perBucket, 0, 0, -half, along, centre);
            using (new OptionalClip(context, bounds, clip))
            using (context.PushRenderOptions(Smooth))
            using (context.PushTransform(matrix))
            {
                context.DrawGeometry(peakBrush, null, peak);
                context.DrawGeometry(loudBrush, null, loud);
            }

            return outline.Complete;
        }

        /// <summary>
        /// The level of detail a column of the lane is drawn at <paramref name="at"/> buckets: a power of two of buckets, the most that
        /// makes it a pixel long or less there, where a pixel is <paramref name="bucketsPerPixel"/> buckets long at the near end and
        /// <paramref name="foreshortening"/> of that, which is 1 there and less further, where the lane is tilted.
        /// </summary>
        /// <remarks>A column too fine only draws more points than the screen has pixels, which an outline can take, so it errs that way.</remarks>
        internal static int LevelAt(double at, double origin, double perBucket, double bucketsPerPixel, Func<double, double>? foreshortening)
        {
            double scale = foreshortening is null ? 1 : foreshortening(origin + (perBucket * at));

            // Nothing seen, or nonsense, is as far as it goes.
            if (!(scale > 0) || !double.IsFinite(scale))
                return CoarsestLevel;
            double wanted = bucketsPerPixel / scale;
            return double.IsFinite(wanted) ? (int)Math.Clamp(Math.Floor(Math.Log2(wanted)), FinestLevel, CoarsestLevel) : CoarsestLevel;
        }

        /// <summary>
        /// Chooses, into <paramref name="cover"/>, the chunks that cover the buckets from <paramref name="first"/> to <paramref name="end"/>,
        /// in order: each of the detail that suits where it starts. Chunks of different detail nest, so a finer one starts where the one
        /// before it ends; a coarser one starts before it, and only its columns past that are used. Never more than
        /// <see cref="MaxCover"/>: the last is as coarse as it takes to reach the end, so none of it is left out.
        /// </summary>
        internal static void PlanCover(List<(int Level, int Index)> cover, double first, double end, double origin, double perBucket, double bucketsPerPixel, Func<double, double>? foreshortening)
        {
            cover.Clear();
            double at = first;
            while (at < end)
            {
                // The detail the chunk's finest part needs: its start, or its end if that is shown larger, as a lane that runs the other way
                // has. A finer chunk is shorter, so the end is looked at again until it needs no finer.
                int level = LevelAt(at, origin, perBucket, bucketsPerPixel, foreshortening);
                double span, index;
                while (true)
                {
                    span = ColumnsPerChunk * Math.ScaleB(1.0, level);
                    index = Math.Floor(at / span);
                    int atEnd = LevelAt(Math.Min((index + 1) * span, end), origin, perBucket, bucketsPerPixel, foreshortening);
                    if (atEnd >= level)
                        break;
                    level = atEnd;
                }

                // The last there's room for, or one further than chunks can be counted: the rest is one chunk, coarser until it reaches the end.
                if (cover.Count == MaxCover - 1 || index >= int.MaxValue)
                {
                    while (level < CoarsestLevel && (index >= int.MaxValue || (index + 1) * span < end))
                    {
                        level++;
                        span = ColumnsPerChunk * Math.ScaleB(1.0, level);
                        index = Math.Floor(at / span);
                    }

                    if (index < int.MaxValue)
                        cover.Add((level, (int)index));
                    return;
                }

                cover.Add((level, (int)index));
                at = (index + 1) * span;
            }
        }

        /// <summary>
        /// The outline of what <paramref name="cover"/> covers of <paramref name="waveform"/>, at <paramref name="gain"/>: kept, and
        /// made if it isn't, or if what was kept was made with stand-ins.
        /// </summary>
        private static Outline? OutlineOf(Waveform waveform, double gain, List<(int Level, int Index)> cover, bool budgeted)
        {
            if (cover.Count == 0)
                return null;

            ulong hash = 14695981039346656037;
            foreach (var (level, index) in cover)
            {
                hash = (hash ^ (uint)level) * 1099511628211;
                hash = (hash ^ (uint)index) * 1099511628211;
            }

            var key = new OutlineKey(waveform, gain, waveform.ReadCount, hash);
            if (Outlines.TryGetValue(key, out var kept) && kept.Complete && SameCover(kept.Cover, cover))
            {
                kept.Used = ++s_used;
                return kept;
            }

            var made = MakeOutline(waveform, gain, cover, budgeted);
            if (!Outlines.ContainsKey(key))
            {
                while (Outlines.Count >= MaxOutlines)
                    DropLeastRecentOutline();
            }

            Outlines[key] = made;
            return made;
        }

        private static bool SameCover((int Level, int Index)[] kept, List<(int Level, int Index)> cover)
        {
            if (kept.Length != cover.Count)
                return false;
            for (int i = 0; i < kept.Length; i++)
            {
                if (kept[i] != cover[i])
                    return false;
            }

            return true;
        }

        /// <summary>
        /// The outline of the chunks in <paramref name="cover"/>, each made if it isn't kept as it is now; within the
        /// <see cref="PaintBudget"/> if <paramref name="budgeted"/>, others standing in for those there's no time for.
        /// </summary>
        private static Outline MakeOutline(Waveform waveform, double gain, List<(int Level, int Index)> cover, bool budgeted)
        {
            s_peakUp.Clear();
            s_peakDown.Clear();
            s_loudUp.Clear();
            s_loudDown.Clear();
            s_runs.Clear();
            s_runColumns.Clear();

            // The points are from the first chunk's start, so they're small, as a float of them can keep.
            int read = waveform.ReadCount;
            double start = ChunkStart(cover[0].Level, cover[0].Index);
            double last = double.NegativeInfinity;
            bool inRun = false;
            bool complete = true;
            foreach (var (level, index) in cover)
            {
                double end = ChunkStart(level, index + 1L);
                if (Current(new ChunkKey(waveform, level, index), read) is { } chunk)
                {
                    Add(chunk, level, index, end);
                }
                else if (!budgeted || PaintBudget.CanBuild())
                {
                    Add(Make(waveform, level, index), level, index, end);
                }
                else
                {
                    // No time to make it this frame: drawn from what's kept of its buckets, or left out, and drawn again soon.
                    complete = false;
                    if (!AddStandIn(level, index, end))
                        inRun = false;
                }
            }

            return new Outline(start, [.. cover], Shape(s_peakUp, s_peakDown), Shape(s_loudUp, s_loudDown), complete) { Used = ++s_used };

            // The columns of chunk, of index at level, whose middles are past the last added and before end.
            void Add(Chunk chunk, int level, long index, double end)
            {
                chunk.Used = ++s_used;
                double columnLength = Math.ScaleB(1.0, level);
                long firstColumn = index * ColumnsPerChunk;
                for (int i = 0; i < chunk.Count; i++)
                {
                    // A coarser chunk starts before the one it follows ends, and one standing in may go on past where the
                    // next starts: the points are kept going one way.
                    double x = (firstColumn + i + 0.5) * columnLength;
                    if (x <= last)
                        continue;
                    if (x >= end)
                        break;

                    if (!inRun)
                    {
                        s_runs.Add(s_peakUp.Count);
                        s_runColumns.Add(columnLength);
                        inRun = true;
                    }

                    last = x;
                    double at = x - start;
                    s_peakUp.Add(new Point(at, Math.Min(1, chunk.High[i] * gain)));
                    s_peakDown.Add(new Point(at, Math.Max(-1, chunk.Low[i] * gain)));
                    double loud = Math.Min(1, chunk.Loud[i] * gain);
                    s_loudUp.Add(new Point(at, loud));
                    s_loudDown.Add(new Point(at, -loud));
                }
            }

            // In place of the chunk at level and index: its levels from before more was read, a coarser chunk's that covers it, or
            // both halves of it a level finer. Whether any was kept.
            bool AddStandIn(int level, int index, double end)
            {
                if (Chunks.TryGetValue(new ChunkKey(waveform, level, index), out var before))
                {
                    Add(before, level, index, end);
                    return true;
                }

                for (int k = 1; k <= StandInLevels && level + k <= CoarsestLevel; k++)
                {
                    if (Chunks.TryGetValue(new ChunkKey(waveform, level + k, index >> k), out var coarser))
                    {
                        Add(coarser, level + k, index >> k, end);
                        return true;
                    }
                }

                if (level > FinestLevel && index < int.MaxValue / 2
                    && Chunks.TryGetValue(new ChunkKey(waveform, level - 1, 2 * index), out var firstHalf)
                    && Chunks.TryGetValue(new ChunkKey(waveform, level - 1, (2 * index) + 1), out var secondHalf))
                {
                    Add(firstHalf, level - 1, 2 * index, end);
                    Add(secondHalf, level - 1, (2 * index) + 1, end);
                    return true;
                }

                return false;
            }
        }

        /// <summary>
        /// The outline through <paramref name="up"/>, then back along <paramref name="down"/>, a figure for each run of them in
        /// <see cref="s_runs"/>; a run of one column is a bar as long as it is. None for no columns.
        /// </summary>
        private static HighwayPath? Shape(List<Point> up, List<Point> down)
        {
            if (up.Count == 0)
                return null;

            var builder = new HighwayPath.Builder();
            for (int run = 0; run < s_runs.Count; run++)
            {
                int from = s_runs[run];
                int to = run + 1 < s_runs.Count ? s_runs[run + 1] : up.Count;
                if (to - from == 1)
                {
                    double half = s_runColumns[run] / 2;
                    var top = up[from];
                    var bottom = down[from];
                    builder.BeginFigure(new Point(top.X - half, top.Y), isFilled: true);
                    builder.LineTo(new Point(top.X + half, top.Y));
                    builder.LineTo(new Point(bottom.X + half, bottom.Y));
                    builder.LineTo(new Point(bottom.X - half, bottom.Y));
                }
                else
                {
                    builder.BeginFigure(up[from], isFilled: true);
                    for (int i = from + 1; i < to; i++)
                        builder.LineTo(up[i]);
                    for (int i = to - 1; i >= from; i--)
                        builder.LineTo(down[i]);
                }

                builder.EndFigure(isClosed: true);
            }

            return builder.Build();
        }

        /// <summary>Where chunk <paramref name="index"/> at <paramref name="level"/> starts, in buckets from the waveform's.</summary>
        private static double ChunkStart(int level, long index) => index * ColumnsPerChunk * Math.ScaleB(1.0, level);

        /// <summary>
        /// The read count from which chunk <paramref name="index"/> at <paramref name="level"/> can't change: past the end of its last
        /// column, and the two buckets past that, which a curve through the buckets' middles looks to.
        /// </summary>
        internal static long ReadNeeded(int level, int index) => (long)Math.Ceiling(ChunkStart(level, index + 1L)) + 2;

        /// <summary>The levels of the chunk <paramref name="key"/> names, if they're kept and as they'd be made now <paramref name="read"/> buckets have been.</summary>
        private static Chunk? Current(ChunkKey key, int read)
            => Chunks.TryGetValue(key, out var kept) && (kept.Read == read || kept.Read >= kept.Needed) ? kept : null;

        /// <summary>Makes the levels of chunk <paramref name="index"/> at <paramref name="level"/> from what's been read, and keeps them.</summary>
        private static Chunk Make(Waveform waveform, int level, int index)
        {
            long started = Stopwatch.GetTimestamp();
            var key = new ChunkKey(waveform, level, index);
            var chunk = new Chunk(waveform.ReadCount, ReadNeeded(level, index));
            double columnLength = Math.ScaleB(1.0, level);
            long firstColumn = (long)index * ColumnsPerChunk;
            while (chunk.Count < ColumnsPerChunk && BarLevels(waveform, columnLength, firstColumn + chunk.Count) is (double low, double high, double loud))
            {
                chunk.Low[chunk.Count] = low;
                chunk.High[chunk.Count] = high;
                chunk.Loud[chunk.Count] = loud;
                chunk.Count++;
            }

            if (!Chunks.ContainsKey(key))
            {
                while (Chunks.Count >= MaxChunks)
                    DropLeastRecentChunk();
            }

            chunk.Used = ++s_used;
            Chunks[key] = chunk;
            ChunksBuilt++;
            PaintBudget.Spend(started);
            return chunk;
        }

        private static void DropLeastRecentChunk()
        {
            var oldest = default(KeyValuePair<ChunkKey, Chunk>);
            foreach (var entry in Chunks)
            {
                if (oldest.Value is null || entry.Value.Used < oldest.Value.Used)
                    oldest = entry;
            }

            if (oldest.Value is not null)
                Chunks.Remove(oldest.Key);
        }

        private static void DropLeastRecentOutline()
        {
            var oldest = default(KeyValuePair<OutlineKey, Outline>);
            foreach (var entry in Outlines)
            {
                if (oldest.Value is null || entry.Value.Used < oldest.Value.Used)
                    oldest = entry;
            }

            if (oldest.Value is not null)
                Outlines.Remove(oldest.Key);
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

        private readonly record struct ChunkKey(Waveform Waveform, int Level, int Index);

        /// <summary>Which outline: of which waveform, as much of it as had been read, at which gain, and which chunks it's of, by their hash.</summary>
        private readonly record struct OutlineKey(Waveform Waveform, double Gain, int Read, ulong Hash);

        /// <summary>
        /// The levels of a chunk's columns: lowest, highest, and how loud, as far as the waveform had been read, and how far it must
        /// have been for them to be final.
        /// </summary>
        private sealed class Chunk(int read, long needed)
        {
            public double[] Low { get; } = new double[ColumnsPerChunk];

            public double[] High { get; } = new double[ColumnsPerChunk];

            public double[] Loud { get; } = new double[ColumnsPerChunk];

            public int Count { get; set; }

            public int Read { get; } = read;

            public long Needed { get; } = needed;

            public long Used { get; set; }
        }

        /// <summary>
        /// The two outlines of a stretch of a waveform, in buckets from <see cref="Start"/>, the chunks they're of, and whether
        /// they were made from those chunks alone, rather than with others standing in.
        /// </summary>
        private sealed class Outline(double start, (int Level, int Index)[] cover, HighwayPath? peak, HighwayPath? loud, bool complete)
        {
            public bool Complete { get; } = complete;

            public double Start { get; } = start;

            public (int Level, int Index)[] Cover { get; } = cover;

            public HighwayPath? Peak { get; } = peak;

            public HighwayPath? Loud { get; } = loud;

            public long Used { get; set; }
        }
    }
}
