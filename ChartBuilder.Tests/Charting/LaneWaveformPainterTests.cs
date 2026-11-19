using System.Diagnostics;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using ChartShared.UI.Controls.Skia;
using SkiaSharp;
using ChartShared.Audio.Decoding;
using ChartShared.Audio.Waveforms;
using ChartShared.UI.Controls;

namespace ChartBuilder.Tests.Charting;

/// <summary>How a lane's waveform is drawn as an outline, a column to a pixel or less at any zoom (issues #296 and #300).</summary>
[TestClass]
public sealed class LaneWaveformPainterTests
{
    // Read at the default 50 buckets a second: at 1000 frames a second, each bucket is 20 frames.
    // Levels are kept at half precision, so they're compared to within that.
    private const double Precision = 1e-3;
    private const int Rate = 1000;
    private const int FramesPerBucket = 20;

    [TestMethod]
    [DataRow(2.0)]
    [DataRow(2.7)]
    [DataRow(1.0)]
    public void ZoomedOutABarHasTheLevelsOfEveryBucketItTouches(double bucketsPerBar)
    {
        var waveform = Of([0.1, 0.5, 0.2, 0.9, 0.3, 0.3, 0.7, 0.05, 0.4, 0.6, 0.2, 0.8]);

        for (long bar = 0; bar * bucketsPerBar < waveform.ReadCount; bar++)
        {
            int first = (int)Math.Floor(bar * bucketsPerBar);
            int last = (int)Math.Min(waveform.ReadCount, Math.Ceiling((bar + 1) * bucketsPerBar));
            var (low, high, rms) = waveform.LevelsOf(first, last);

            var levels = LaneWaveformPainter.BarLevels(waveform, bucketsPerBar, bar);

            Assert.IsNotNull(levels);
            Assert.AreEqual(low, levels.Value.Low, 1e-9, $"Bar {bar}.");
            Assert.AreEqual(high, levels.Value.High, 1e-9, $"Bar {bar}.");
            Assert.AreEqual(WaveformLevels.Loudness(low, high, rms), levels.Value.Loud, 1e-9, $"Bar {bar}.");
        }
    }

    [TestMethod]
    public void ZoomedInTheCurvePassesThroughEachBucketsMiddle()
    {
        double[] levels = [0.1, 0.5, 0.2, 0.9, 0.3, 0.7];
        var waveform = Of(levels);

        // Five bars to a bucket: bar 5j + 2 is in the middle of bucket j.
        for (int bucket = 0; bucket < levels.Length; bucket++)
        {
            var bar = LaneWaveformPainter.BarLevels(waveform, 0.2, (5 * bucket) + 2);

            Assert.IsNotNull(bar);
            Assert.AreEqual(levels[bucket], bar.Value.High, Precision, $"Bucket {bucket}.");
            Assert.AreEqual(-levels[bucket], bar.Value.Low, Precision, $"Bucket {bucket}.");
        }
    }

    [TestMethod]
    public void ZoomedInTheCurveNeverGoesPastEitherBucketItsBetween()
    {
        // A spike among quiet buckets, and a drop to silence.
        double[] levels = [0.2, 0.25, 0.1, 1.0, 0.1, 0.3, 0.0, 0.0, 0.6, 0.55];
        var waveform = Of(levels);
        const double bucketsPerBar = 0.1;

        for (long bar = 0; bar < levels.Length / bucketsPerBar; bar++)
        {
            double at = (bar * bucketsPerBar) + (bucketsPerBar / 2) - 0.5;
            int before = Math.Clamp((int)Math.Floor(at), 0, levels.Length - 1);
            int after = Math.Clamp(before + 1, 0, levels.Length - 1);
            double lowest = Math.Min(levels[before], levels[after]), highest = Math.Max(levels[before], levels[after]);

            var levelsAt = LaneWaveformPainter.BarLevels(waveform, bucketsPerBar, bar);

            Assert.IsNotNull(levelsAt);
            Assert.IsGreaterThanOrEqualTo(lowest - Precision, levelsAt.Value.High, $"Bar {bar}.");
            Assert.IsLessThanOrEqualTo(highest + Precision, levelsAt.Value.High, $"Bar {bar}.");
        }
    }

    [TestMethod]
    public void ZoomedInTheCurveRisesWhereTheBucketsDo()
    {
        var waveform = Of([0.1, 0.15, 0.3, 0.32, 0.6, 0.9]);
        double previous = double.NegativeInfinity;

        // From the first bucket's middle to the last's.
        for (long bar = 2; bar <= 27; bar++)
        {
            var levels = LaneWaveformPainter.BarLevels(waveform, 0.2, bar);

            Assert.IsNotNull(levels);
            Assert.IsGreaterThanOrEqualTo(previous - 1e-9, levels.Value.High, $"Bar {bar}.");
            previous = levels.Value.High;
        }
    }

    [TestMethod]
    [DataRow(0.25)]
    [DataRow(3.0)]
    public void NoBarStartsPastWhatsBeenReadAndTheEndsAreDrawn(double bucketsPerBar)
    {
        var waveform = Of([0.4, 0.6, 0.5]);
        long lastBar = (long)Math.Ceiling(3 / bucketsPerBar) - 1;

        Assert.IsNotNull(LaneWaveformPainter.BarLevels(waveform, bucketsPerBar, 0));
        Assert.IsNotNull(LaneWaveformPainter.BarLevels(waveform, bucketsPerBar, lastBar));
        Assert.IsNull(LaneWaveformPainter.BarLevels(waveform, bucketsPerBar, lastBar + 1));
        Assert.IsNull(LaneWaveformPainter.BarLevels(waveform, bucketsPerBar, -1));
    }

    [TestMethod]
    public void TheCurveRunsFromOneEndToTheOther()
    {
        Assert.AreEqual(0.3, LaneWaveformPainter.MonotoneCubic(0.1, 0.3, 0.8, 0.2, 0), 1e-12);
        Assert.AreEqual(0.8, LaneWaveformPainter.MonotoneCubic(0.1, 0.3, 0.8, 0.2, 1), 1e-12);

        // Flat between equal ends, whatever is either side.
        Assert.AreEqual(0.5, LaneWaveformPainter.MonotoneCubic(0.0, 0.5, 0.5, 1.0, 0.4), 1e-12);
    }

    [TestMethod]
    [DataRow(3.0, 1)]
    [DataRow(4.0, 2)]
    [DataRow(1.0, 0)]
    [DataRow(0.3, -2)]
    [DataRow(1e-9, -8)]
    [DataRow(1e12, 20)]
    public void AColumnIsAPixelLongOrLessAtTheNearEnd(double bucketsPerPixel, int level)
    {
        Assert.AreEqual(level, LaneWaveformPainter.LevelAt(5, 0, 1, bucketsPerPixel, null));
    }

    [TestMethod]
    public void AColumnIsLongerWhereTheScreenShowsTheLaneSmaller()
    {
        // A quarter as long on the screen as at the near end: four times the buckets to a pixel.
        Assert.AreEqual(2, LaneWaveformPainter.LevelAt(5, 0, 1, 1, _ => 0.25));
        Assert.AreEqual(1, LaneWaveformPainter.LevelAt(5, 0, 1, 1, _ => 0.5));

        // Nothing seen, or nonsense, is as far as it goes.
        int farthest = LaneWaveformPainter.LevelAt(5, 0, 1, 1, _ => 1e-30);
        Assert.IsTrue(farthest >= 10);
        Assert.AreEqual(farthest, LaneWaveformPainter.LevelAt(5, 0, 1, 1, _ => double.NaN));
        Assert.AreEqual(farthest, LaneWaveformPainter.LevelAt(5, 0, 1, 1, _ => -1));
    }

    [TestMethod]
    public void ALaneNotForeshortenedIsChunksOfOneDetailThatCoverItAll()
    {
        var cover = new List<(int Level, int Index)>();
        LaneWaveformPainter.PlanCover(cover, 10, 5000, 0, 2, 1, null);

        const double Span = 256;
        Assert.IsTrue(cover.All(chunk => chunk.Level == 0));
        for (int i = 0; i < cover.Count; i++)
            Assert.AreEqual(i, cover[i].Index);
        Assert.IsTrue(cover[0].Index * Span <= 10);
        Assert.IsTrue((cover[^1].Index + 1) * Span >= 5000);
    }

    [TestMethod]
    [DataRow(-2.0, 1000.0)]
    [DataRow(2.0, 100.0)]
    public void ALaneForeshortenedMoreTheFurtherIsChunksOfLongerColumnsWithNoGaps(double perBucket, double origin)
    {
        // Coordinates run from the origin, where it's 1, to a tenth of that at the far end, in either direction along the lane.
        double Scale(double coordinate) => Math.Clamp(coordinate / 1000, 0.1, 1);
        var cover = new List<(int Level, int Index)>();
        LaneWaveformPainter.PlanCover(cover, 0, 400, origin, perBucket, 1, Scale);

        static double Start((int Level, int Index) chunk) => chunk.Index * 256 * Math.Pow(2, chunk.Level);
        static double End((int Level, int Index) chunk) => (chunk.Index + 1) * 256 * Math.Pow(2, chunk.Level);

        Assert.IsTrue(cover.Count > 1);
        Assert.IsTrue(Start(cover[0]) <= 0);
        Assert.IsTrue(End(cover[^1]) >= 400);
        for (int i = 1; i < cover.Count; i++)
        {
            Assert.IsTrue(Start(cover[i]) <= End(cover[i - 1]), $"Chunk {i} starts past where the one before ends.");
            Assert.IsTrue(End(cover[i]) > End(cover[i - 1]), $"Chunk {i} doesn't go further than the one before.");
        }

        // Later in time is further for the first, which has longer columns as it goes. The second runs the other way, with its near end last,
        // and a chunk is no coarser than the finest the near end of it needs.
        if (perBucket < 0)
            Assert.IsTrue(cover[^1].Level > cover[0].Level);
        else
            Assert.IsTrue(cover.All(chunk => chunk.Level <= LaneWaveformPainter.LevelAt(400, origin, perBucket, 1, Scale)));
    }

    [TestMethod]
    public void ALaneTooLongForItsDetailIsStillCoveredToItsEnd()
    {
        // A bucket a chunk, the finest there is, over a million buckets: far more chunks than are allowed.
        var cover = new List<(int Level, int Index)>();
        LaneWaveformPainter.PlanCover(cover, 0, 1_000_000, 0, 1, 1e-9, null);

        static double Start((int Level, int Index) chunk) => chunk.Index * 256 * Math.Pow(2, chunk.Level);
        static double End((int Level, int Index) chunk) => (chunk.Index + 1) * 256 * Math.Pow(2, chunk.Level);

        Assert.IsTrue(cover.Count <= 256);
        Assert.IsTrue(End(cover[^1]) >= 1_000_000, "The last chunk reaches the end.");
        Assert.IsTrue(cover[^1].Level > cover[0].Level, "The last chunk is coarser.");
        for (int i = 1; i < cover.Count; i++)
            Assert.IsTrue(Start(cover[i]) <= End(cover[i - 1]), $"Chunk {i} starts past where the one before ends.");
    }

    [TestMethod]
    [DataRow(0, 0)]
    [DataRow(2, 3)]
    [DataRow(6, 1)]
    [DataRow(-1, 0)]
    [DataRow(-2, 5)]
    [DataRow(-8, 0)]
    public void AChunkIsFinalOnlyOnceEveryBucketItsColumnsLookToHasBeenRead(int level, int index)
    {
        // Its last column ends here, and, finer than a bucket, is on a curve through the middles of buckets i to i + 2.
        double length = Math.Pow(2, level);
        long lastColumn = ((long)index * 256) + 255;
        double end = (lastColumn + 1) * length;
        int i = (int)Math.Floor((lastColumn * length) + (length / 2) - 0.5);

        long needed = LaneWaveformPainter.ReadNeeded(level, index);

        Assert.IsTrue(needed >= Math.Ceiling(end), "Past the end of its last column.");
        if (level < 0)
            Assert.IsTrue(needed >= i + 3, "Past the last bucket its curve looks to.");
    }

    [TestMethod]
    public void AWaveformOfASingleColumnIsDrawnAsABar()
    {
        // A second read, at 64 buckets a column: one column, from the near end at the bottom to 64 pixels up.
        LaneWaveformPainter.Clear();
        var waveform = Of([.. Enumerable.Repeat(0.5, 50)]);
        var red = new ImmutableSolidColorBrush(Colors.Red);

        var canvas = SkiaRaster.Begin();
        LaneWaveformPainter.Draw(canvas, waveform, new Rect(0, 0, 40, 100), isVertical: true, 0, 2, time => 100 - (time * 50), red, new ImmutableSolidColorBrush(Colors.Transparent), pixel: 100);
        using var image = SkiaRaster.End(40, 100);
        using var pixels = SKBitmap.FromImage(image);

        Assert.AreEqual(255, pixels.GetPixel(20, 80).Alpha, "Inside the bar.");
        Assert.AreEqual(0, pixels.GetPixel(20, 20).Alpha, "Past its end.");
    }

    [TestMethod]
    public void WithNoTimeLeftAChunkNotKeptIsStoodInForByOneOfAnotherDetailAndDrawnAgainSoon()
    {
        LaneWaveformPainter.Clear();
        var waveform = Of([.. Enumerable.Repeat(0.5, 100)]);
        var red = new ImmutableSolidColorBrush(Colors.Red);
        var none = new ImmutableSolidColorBrush(Colors.Transparent);
        static double Position(double time) => 100 - (time * 50);

        // Nothing kept, and no time: nothing's drawn, and it isn't all there.
        Spent();
        var canvas = SkiaRaster.Begin();
        Assert.IsFalse(LaneWaveformPainter.Draw(canvas, waveform, new Rect(0, 0, 40, 100), isVertical: true, 0, 2, Position, red, none, budgeted: true));
        using (var image = SkiaRaster.End(40, 100))
        using (var pixels = SKBitmap.FromImage(image))
            Assert.AreEqual(0, pixels.GetPixel(20, 50).Alpha, "Nothing to stand in.");

        // Drawn at a bucket a pixel, then at half that with no time: the coarser chunk stands in.
        LaneWaveformPainter.Draw(SkiaRaster.Begin(), waveform, new Rect(0, 0, 40, 100), isVertical: true, 0, 2, Position, red, none);
        SkiaRaster.End(40, 100).Dispose();
        Spent();
        canvas = SkiaRaster.Begin();
        Assert.IsFalse(LaneWaveformPainter.Draw(canvas, waveform, new Rect(0, 0, 40, 100), isVertical: true, 0, 2, Position, red, none, pixel: 0.5, budgeted: true));
        using (var image = SkiaRaster.End(40, 100))
        using (var pixels = SKBitmap.FromImage(image))
            Assert.AreEqual(255, pixels.GetPixel(20, 50).Alpha, "Stood in for.");

        // Once there's time again, it's made.
        Thread.Sleep(40);
        Assert.IsTrue(LaneWaveformPainter.Draw(SkiaRaster.Begin(), waveform, new Rect(0, 0, 40, 100), isVertical: true, 0, 2, Position, red, none, pixel: 0.5, budgeted: true));
        SkiaRaster.End(40, 100).Dispose();

        // A second of drawing spent in this frame.
        static void Spent()
        {
            PaintBudget.CanBuild();
            PaintBudget.Spend(Stopwatch.GetTimestamp() - Stopwatch.Frequency);
        }
    }

    [TestMethod]
    public void AWaveformIsDrawnAsAnOutlineAsWideAsItsPeaks()
    {
        // Half scale all along: from a quarter to three quarters of the way across a lane 40 pixels wide and 100 long, a pixel to a bucket.
        var waveform = Of([.. Enumerable.Repeat(0.5, 100)]);
        var red = new ImmutableSolidColorBrush(Colors.Red);

        var canvas = SkiaRaster.Begin();
        LaneWaveformPainter.Draw(canvas, waveform, new Rect(0, 0, 40, 100), isVertical: true, 0, 2, time => 100 - (time * 50), red, new ImmutableSolidColorBrush(Colors.Transparent));
        using var image = SkiaRaster.End(40, 100);
        using var pixels = SKBitmap.FromImage(image);

        Assert.AreEqual(255, pixels.GetPixel(20, 50).Alpha, "Inside the peaks.");
        Assert.AreEqual(0, pixels.GetPixel(3, 50).Alpha, "Outside them, to the left.");
        Assert.AreEqual(0, pixels.GetPixel(37, 50).Alpha, "Outside them, to the right.");
    }

    [TestMethod]
    public void ALaneTiltedBackIsDrawnToItsFarEnd()
    {
        var waveform = Of([.. Enumerable.Repeat(0.5, 100)]);
        var red = new ImmutableSolidColorBrush(Colors.Red);

        // Its far end, at the top, shown a fifth as long as its near end.
        var canvas = SkiaRaster.Begin();
        LaneWaveformPainter.Draw(canvas, waveform, new Rect(0, 0, 40, 100), isVertical: true, 0, 2, time => 100 - (time * 50), red, new ImmutableSolidColorBrush(Colors.Transparent),
            foreshortening: position => Math.Clamp(position / 100, 0.2, 1));
        using var image = SkiaRaster.End(40, 100);
        using var pixels = SKBitmap.FromImage(image);

        Assert.AreEqual(255, pixels.GetPixel(20, 90).Alpha, "Near.");
        Assert.AreEqual(255, pixels.GetPixel(20, 50).Alpha, "Between.");
        Assert.AreEqual(255, pixels.GetPixel(20, 5).Alpha, "Far.");
        Assert.AreEqual(0, pixels.GetPixel(3, 5).Alpha, "Outside the peaks at the far end.");
    }

    /// <summary>A waveform read at 50 buckets a second whose buckets reach <paramref name="levels"/> either side of silence, and are that loud.</summary>
    private static Waveform Of(double[] levels)
    {
        var source = new Source(levels.Length * FramesPerBucket, n => (float)(n % 2 == 0 ? levels[n / FramesPerBucket] : -levels[n / FramesPerBucket]));
        return WaveformPeaks.Of(WaveformPeaks.Compute("x", source, WaveformResolution.Default));
    }

    /// <summary>Mono audio at <see cref="Rate"/>, each frame's sample given.</summary>
    private sealed class Source(long frames, Func<long, float> sample) : IPcmSource
    {
        private long _position;

        public int SampleRate => Rate;

        public int Channels => 1;

        public long LengthFrames => frames;

        public int ReadFrames(Span<float> buffer)
        {
            int count = (int)Math.Max(0, Math.Min(buffer.Length, frames - _position));
            for (int n = 0; n < count; n++)
                buffer[n] = sample(_position + n);
            _position += count;
            return count;
        }

        public void SeekToFrame(long frame) => _position = Math.Min(frame, frames);

        public void Dispose()
        {
        }
    }
}
