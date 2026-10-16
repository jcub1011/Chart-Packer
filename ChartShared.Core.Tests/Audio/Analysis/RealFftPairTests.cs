using ChartShared.Audio.Analysis;
using ChartShared.Audio.Dsp;

namespace ChartShared.Core.Tests.Audio.Analysis;

[TestClass]
public sealed class RealFftPairTests
{
    [TestMethod]
    public void APairsSpectraAreEachFramesOwn()
    {
        const int size = 64;
        var random = new Random(5);
        var a = Enumerable.Range(0, size).Select(_ => (float)((random.NextDouble() * 2) - 1)).ToArray();
        var b = Enumerable.Range(0, size).Select(_ => (float)((random.NextDouble() * 2) - 1)).ToArray();
        var fft = new RealFftPair(size);
        var powerA = new float[(size / 2) + 1];
        var powerB = new float[(size / 2) + 1];

        fft.Power(a, b, powerA, powerB);

        var window = Windowing.PeriodicHann(size);
        for (int k = 0; k <= size / 2; k++)
        {
            Assert.AreEqual(Dft(a, window, k), powerA[k], 1e-3 * Math.Max(1, powerA[k]), $"A, bin {k}");
            Assert.AreEqual(Dft(b, window, k), powerB[k], 1e-3 * Math.Max(1, powerB[k]), $"B, bin {k}");
        }
    }

    [TestMethod]
    public void AFrameWithoutAPartnerIsTakenAlone()
    {
        const int size = 64;
        var a = Enumerable.Range(0, size).Select(n => (float)Math.Sin(2 * Math.PI * 5 * n / size)).ToArray();
        var fft = new RealFftPair(size);
        var powerA = new float[(size / 2) + 1];
        var powerB = new float[(size / 2) + 1];

        fft.Power(a, [], powerA, powerB);

        var window = Windowing.PeriodicHann(size);
        for (int k = 0; k <= size / 2; k++)
        {
            Assert.AreEqual(Dft(a, window, k), powerA[k], 1e-3 * Math.Max(1, powerA[k]));
            Assert.AreEqual(0, powerB[k], 1e-6);
        }
    }

    [TestMethod]
    public void AFullScaleSinesBinIsHalfTheWindowsSum()
    {
        const int size = 2048;
        var a = Enumerable.Range(0, size).Select(n => (float)Math.Sin(2 * Math.PI * 64 * n / size)).ToArray();
        var fft = new RealFftPair(size);
        var power = new float[(size / 2) + 1];

        fft.Power(a, [], power, new float[(size / 2) + 1]);

        Assert.AreEqual(fft.WindowSum / 2, Math.Sqrt(power[64]), fft.WindowSum * 1e-4);
    }

    private static double Dft(float[] x, float[] window, int k)
    {
        double re = 0, im = 0;
        for (int n = 0; n < x.Length; n++)
        {
            double angle = -2 * Math.PI * k * n / x.Length;
            re += x[n] * window[n] * Math.Cos(angle);
            im += x[n] * window[n] * Math.Sin(angle);
        }

        return (re * re) + (im * im);
    }
}
