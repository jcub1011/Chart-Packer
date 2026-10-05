using ChartPacker.Audio.Dsp;

namespace ChartPacker.Core.Tests.Audio;

[TestClass]
public sealed class TimeStretcherTests
{
    private const int Rate = 48000;

    [TestMethod]
    [DataRow(0.01)]
    [DataRow(0.5)]
    [DataRow(0.75)]
    [DataRow(1.0)]
    [DataRow(1.5)]
    [DataRow(2.0)]
    [DataRow(4.0)]
    [DataRow(10.0)]
    public void TheOutputLastsAsLongAsTheInputOverTheRatio(double ratio)
    {
        var input = new Input(Rate / 2, (n, _) => Sine(n, 440));

        float[] output = ReadAll(Stretcher(input, ratio));

        Assert.AreEqual(Rate / 2 / ratio, output.Length / 2, 1.0);
    }

    [TestMethod]
    [DataRow(0.5)]
    [DataRow(2.0)]
    public void ThePitchStaysTheSame(double ratio)
    {
        var input = new Input(Rate * 2, (n, _) => Sine(n, 440));

        float[] output = ReadAll(Stretcher(input, ratio));

        Assert.AreEqual(440, Frequency(output), 440 * 0.01);
    }

    [TestMethod]
    [DataRow(0.5)]
    [DataRow(1.37)]
    [DataRow(2.0)]
    public void TheGrainsJoinWithoutJumps(double ratio)
    {
        var input = new Input(Rate, (n, _) => Sine(n, 440));

        float[] output = ReadAll(Stretcher(input, ratio));

        // The sine itself moves at most 0.029 a frame; a grain put down out of step would jump by much more.
        float largest = 0;
        for (int n = 2; n < output.Length; n += 2)
            largest = Math.Max(largest, Math.Abs(output[n] - output[n - 2]));
        Assert.IsLessThan(0.05f, largest);
    }

    [TestMethod]
    public void BothChannelsAreStretchedAlike()
    {
        var input = new Input(Rate, (n, _) => (float)(Sine(n, 440) + (0.3 * Sine(n, 97))));

        float[] output = ReadAll(Stretcher(input, 1.3));

        for (int n = 0; n < output.Length; n += 2)
            Assert.AreEqual(output[n], output[n + 1]);
    }

    [TestMethod]
    public void TheOutputStartsAsTheInputAfterAReset()
    {
        var input = new Input(Rate * 2, (n, c) => Sine(n, c == 0 ? 440 : 330));
        var stretcher = Stretcher(input, 1.5);
        stretcher.Read(new float[Rate]);

        input.Position = Rate;
        stretcher.Reset(Rate);
        var output = new float[512 * 2];
        stretcher.Read(output);

        for (int n = 0; n < 512; n++)
        {
            Assert.AreEqual(Sine(Rate + n, 440), output[2 * n]);
            Assert.AreEqual(Sine(Rate + n, 330), output[(2 * n) + 1]);
        }
    }

    [TestMethod]
    public void TheSourcePositionMovesOnByTheRatioForEachFrame()
    {
        var input = new Input(Rate * 2, (n, _) => Sine(n, 440));
        var stretcher = Stretcher(input, 0.75);
        input.Position = 1000;
        stretcher.Reset(1000);

        stretcher.Read(new float[4000 * 2]);

        Assert.AreEqual(1000 + (4000 * 0.75), stretcher.SourcePosition, 1e-6);
    }

    [TestMethod]
    public void NothingIsReadOnceTheInputHasRunOut()
    {
        var input = new Input(Rate / 10, (n, _) => Sine(n, 440));
        var stretcher = Stretcher(input, 1.5);
        ReadAll(stretcher);

        Assert.AreEqual(0, stretcher.Read(new float[1024]));
    }

    private static TimeStretcher Stretcher(Input input, double ratio)
    {
        var stretcher = new TimeStretcher(Rate, input.Read) { Ratio = ratio };
        stretcher.Reset(input.Position);
        return stretcher;
    }

    private static float Sine(long frame, double hz) => (float)(0.5 * Math.Sin(2 * Math.PI * hz * frame / Rate));

    private static float[] ReadAll(TimeStretcher stretcher)
    {
        var all = new List<float>();
        var buffer = new float[1000 * 2];
        int frames;
        while ((frames = stretcher.Read(buffer)) > 0)
            all.AddRange(buffer.AsSpan(0, frames * 2));
        return [.. all];
    }

    /// <summary>The left channel's frequency, from how often it crosses zero, away from the ends.</summary>
    private static double Frequency(float[] stereo)
    {
        int frames = stereo.Length / 2;
        int first = frames / 10, last = frames * 9 / 10;
        int crossings = 0;
        for (int n = first + 1; n < last; n++)
        {
            if ((stereo[2 * (n - 1)] < 0) != (stereo[2 * n] < 0))
                crossings++;
        }

        return crossings / 2.0 / ((double)(last - first) / Rate);
    }

    /// <summary>Stereo input of <paramref name="frames"/> frames, read from <see cref="Position"/> on.</summary>
    private sealed class Input(long frames, Func<long, int, float> value)
    {
        public long Position { get; set; }

        public int Read(Span<float> stereo)
        {
            int count = (int)Math.Max(0, Math.Min(stereo.Length / 2, frames - Position));
            for (int n = 0; n < count; n++)
            {
                stereo[2 * n] = value(Position + n, 0);
                stereo[(2 * n) + 1] = value(Position + n, 1);
            }

            Position += count;
            return count;
        }
    }
}
