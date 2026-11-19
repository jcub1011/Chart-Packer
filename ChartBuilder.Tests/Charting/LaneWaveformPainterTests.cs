using ChartShared.Audio.Decoding;
using ChartShared.Audio.Waveforms;
using ChartShared.UI.Controls;

namespace ChartBuilder.Tests.Charting;

/// <summary>How a lane's waveform is drawn a bar to a pixel at any zoom (issue #296).</summary>
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
