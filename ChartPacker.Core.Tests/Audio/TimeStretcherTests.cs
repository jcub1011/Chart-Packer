using ChartPacker.Audio.Dsp;

namespace ChartPacker.Core.Tests.Audio;

[TestClass]
public sealed class TimeStretcherTests
{
    private const int Rate = 48000;

    // The stretcher's grain step: 20 ms.
    private const int Hop = Rate / 50;

    [TestMethod]
    [DataRow(0.01)]
    [DataRow(0.5)]
    [DataRow(0.75)]
    [DataRow(1.0)]
    [DataRow(1.5)]
    [DataRow(2.0)]
    [DataRow(4.0)]
    [DataRow(10.0)]
    [DataRow(100.0)]
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
    [DataRow(0.1)]
    [DataRow(1.5)]
    public void TheOutputStartsFromTheInputAfterAReset(double ratio)
    {
        var input = new Input(Rate * 2, (n, c) => Sine(n, c == 0 ? 440 : 330));
        var stretcher = Stretcher(input, ratio);
        stretcher.Read(new float[Rate]);

        input.Position = Rate;
        stretcher.Reset(Rate);
        var output = new float[Hop * 2];
        stretcher.Read(output);

        // Its first frame is the input's own, and it carries on from there without a jump or a fade in.
        Assert.AreEqual(Sine(Rate, 440), output[0]);
        Assert.AreEqual(Sine(Rate, 330), output[1]);
        for (int n = 2; n < output.Length; n++)
            Assert.IsLessThan(0.05f, Math.Abs(output[n] - output[n - 2]));
    }

    [TestMethod]
    public void AtItsOwnSpeedTheOutputIsTheInput()
    {
        var input = new Input(Rate, (n, c) => Sine(n, c == 0 ? 440 : 330));
        input.Position = 1000;

        float[] output = ReadAll(Stretcher(input, 1));

        Assert.AreEqual(Rate - 1000, output.Length / 2);
        for (int n = 0; n < output.Length / 2; n++)
        {
            Assert.AreEqual(Sine(1000 + n, 440), output[2 * n], 1e-6f);
            Assert.AreEqual(Sine(1000 + n, 330), output[(2 * n) + 1], 1e-6f);
        }
    }

    [TestMethod]
    [DataRow(0.1)]
    [DataRow(4.0)]
    public void AClickIsHeardWhereItsSourcePositionIs(double ratio)
    {
        // Silence but for one frame, about a second in, where a grain is centred at either ratio.
        const int click = 46080;
        var input = new Input(Rate * 2, (n, _) => n == click ? 1f : 0f);

        float[] output = ReadAll(Stretcher(input, ratio));

        // It's in every grain that takes it in, weighted by where in each grain it falls: in all, it's heard where the
        // output stands for it. A grain starting where it stood for, rather than centred there, would hear it a hop early.
        double weight = 0, sum = 0;
        for (int n = 0; n < output.Length / 2; n++)
        {
            weight += output[2 * n];
            sum += output[2 * n] * (double)n;
        }

        Assert.AreEqual(click / ratio, sum / weight, Hop / 8.0);
    }

    [TestMethod]
    [DataRow(0.5)]
    [DataRow(0.75)]
    [DataRow(1.5)]
    public void ThePlayedPositionIsWhereAClickIsHeard(double ratio)
    {
        // Music on the left, which the grains are chosen by and nudged up to 12 ms from where the ratio says; on the
        // right, clicks too quiet to sway the choice, which go wherever the grains take them.
        int[] clicks = [.. Enumerable.Range(0, 12).Select(i => (Rate * 3 / 10) + (i * Rate / 7))];
        var input = new Input(Rate * 2, (n, channel) => channel == 0
            ? (float)(Sine(n, 440) + (0.6 * Sine(n, 97)) + (0.4 * Sine(n, 313.7)))
            : clicks.Contains((int)n) ? 1e-3f : 0f);
        var stretcher = Stretcher(input, ratio);

        var right = new List<double>();
        var played = new List<double>();
        var nominal = new List<double>();
        var frame = new float[2];
        while (true)
        {
            double playedAt = stretcher.PlayedPosition, nominalAt = stretcher.SourcePosition;
            if (stretcher.Read(frame) == 0)
                break;
            right.Add(frame[1]);
            played.Add(playedAt);
            nominal.Add(nominalAt);
        }

        // Each grain that takes a click in carries a copy of it, as loud as the grain is in the frame it lands on; it's
        // heard where its loudest copy is. Two grains nearly as loud as each other there make two copies a few
        // milliseconds apart, and the output stands for somewhere between, so the test is how far off it is on average.
        double playedOff = 0, nominalOff = 0;
        foreach (int click in clicks)
        {
            int around = (int)(click / ratio);
            int loudest = Enumerable.Range(around - (4 * Hop), 8 * Hop).MaxBy(n => right[n]);
            playedOff += Math.Abs(played[loudest] - click) / clicks.Length;
            nominalOff += Math.Abs(nominal[loudest] - click) / clicks.Length;
        }

        Assert.IsLessThanOrEqualTo(1.5 * Rate / 1000, playedOff, $"The played position is {playedOff * 1000 / Rate:F2} ms from a click on average.");
        Assert.IsLessThan(nominalOff / 2, playedOff, $"The played position is {playedOff * 1000 / Rate:F2} ms off, the nominal {nominalOff * 1000 / Rate:F2} ms.");
    }

    [TestMethod]
    public void ThePlayedPositionStartsWhereTheInputDoesAndCarriesOnBetweenGrains()
    {
        var input = new Input(Rate * 2, (n, _) => Sine(n, 440) + (0.5f * Sine(n, 97)));
        var stretcher = Stretcher(input, 0.75);
        input.Position = 1000;
        stretcher.Reset(1000);

        Assert.AreEqual(1000, stretcher.PlayedPosition);
        Assert.AreEqual(0, stretcher.FramesToNextGrain);

        // Each grain's frames carry on from the last's, a frame at a time but for the fade between grains.
        var frame = new float[2];
        double last = stretcher.PlayedPosition;
        for (int n = 0; n < 10 * Hop; n++)
        {
            stretcher.Read(frame);
            double step = stretcher.PlayedPosition - last;
            Assert.AreEqual(1, step, 2.0, $"Frame {n} moved the played position {step} frames.");
            last = stretcher.PlayedPosition;
        }

        Assert.AreEqual(Hop, stretcher.GrainStep);
        Assert.AreEqual(0, stretcher.FramesToNextGrain);
    }

    [TestMethod]
    public void ARatioChangeTakesEffectAtTheNextGrain()
    {
        var input = new Input(Rate * 2, (n, _) => Sine(n, 440));
        var stretcher = Stretcher(input, 0.75);
        stretcher.Read(new float[4 * Hop * 2]);

        stretcher.Ratio = 2;
        stretcher.Read(new float[4 * Hop * 2]);

        Assert.AreEqual((4 * Hop * 0.75) + (4 * Hop * 2), stretcher.SourcePosition, 1e-6);
    }

    [TestMethod]
    [DataRow(0.1)]
    [DataRow(2.5)]
    public void InputForgottenAndReadAgainCarriesOnAsBefore(double ratio)
    {
        var input = new Input(Rate * 2, (n, _) => (float)(Sine(n, 440) + (0.3 * Sine(n, 97))));
        float[] whole = ReadAll(Stretcher(input, ratio));

        input.Position = 0;
        var stretcher = Stretcher(input, ratio);
        var first = new float[(Hop * 5 / 2) * 2];
        stretcher.Read(first);
        input.Position = stretcher.ForgetInput();
        float[] rest = ReadAll(stretcher);

        Assert.AreSequenceEqual(whole, [.. first, .. rest]);
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
