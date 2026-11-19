using ChartShared.Audio.Dsp;

namespace ChartShared.Core.Tests.Audio;

[TestClass]
public sealed class HalfBandDecimatorTests
{
    private const int Rate = 48000;

    [TestMethod]
    [DataRow(0.01)]
    [DataRow(0.05)]
    [DataRow(0.10)]
    [DataRow(0.13)]
    public void WhatsLowPassesAtItsLevel(double ofRate)
    {
        // By its power, which is half its peak's square: its samples needn't land on its peaks.
        var halved = Halved(Sine(ofRate * Rate));
        double decibels = 10 * Math.Log10(2 * halved.Sum(sample => (double)sample * sample) / halved.Length);
        Assert.AreEqual(0, decibels, 0.05, $"{decibels} dB.");
    }

    [TestMethod]
    [DataRow(0.37)]
    [DataRow(0.40)]
    [DataRow(0.45)]
    [DataRow(0.49)]
    public void WhatWouldFoldBackIsTakenOut(double ofRate)
    {
        double decibels = 20 * Math.Log10(Math.Max(1e-12, PeakOf(Halved(Sine(ofRate * Rate)))));
        Assert.IsLessThan(-80, decibels, $"{decibels} dB.");
    }

    [TestMethod]
    public void ASteadyLevelStaysAsItIs()
    {
        var steady = new float[400];
        Array.Fill(steady, 0.7f);

        foreach (float sample in Halved(steady))
            Assert.AreEqual(0.7f, sample, 1e-6f);
    }

    [TestMethod]
    public void ASampleOutIsInTimeWithTheSampleInItsMadeFrom()
    {
        // A slow ramp comes out as itself: sample m out is sample 2m in.
        var ramp = new float[400];
        for (int n = 0; n < ramp.Length; n++)
            ramp[n] = n / 400f;

        var halved = Halved(ramp, out long start);

        for (int i = 0; i < halved.Length; i++)
            Assert.AreEqual(ramp[2 * (start + i)], halved[i], 1e-5f);
    }

    [TestMethod]
    public void AStretchIsTheSameHoweverTheSignalIsCut()
    {
        var random = new Random(5);
        var signal = new float[2000];
        for (int n = 0; n < signal.Length; n++)
            signal[n] = (float)((random.NextDouble() * 2) - 1);

        // Samples 300 to 399 out, from all of the signal and from just the stretch they're made from.
        var whole = new float[100];
        HalfBandDecimator.Decimate(signal, 0, whole, 300);
        var (from, count) = HalfBandDecimator.InputFor(300, 100);
        var cut = new float[100];
        HalfBandDecimator.Decimate(signal.AsSpan((int)from, count), from, cut, 300);

        Assert.AreSequenceEqual(whole, cut);
    }

    [TestMethod]
    public void AStretchOfInputTooShortIsRefused()
    {
        var signal = new float[100];
        Assert.ThrowsExactly<ArgumentException>(() => HalfBandDecimator.Decimate(signal, 0, new float[10], 0));
        Assert.ThrowsExactly<ArgumentException>(() => HalfBandDecimator.Decimate(signal, 0, new float[10], 40));
    }

    private static float[] Sine(double hz)
    {
        var samples = new float[4000];
        for (int n = 0; n < samples.Length; n++)
            samples[n] = (float)Math.Sin(2 * Math.PI * hz * n / Rate);
        return samples;
    }

    private static float[] Halved(float[] signal) => Halved(signal, out _);

    /// <summary>Every sample of the halved signal that <paramref name="signal"/> holds all the samples for, the first being its sample <paramref name="start"/>.</summary>
    private static float[] Halved(float[] signal, out long start)
    {
        start = (HalfBandDecimator.Reach + 1) / 2;
        int count = ((signal.Length - 1 - HalfBandDecimator.Reach) / 2) - (int)start + 1;
        var halved = new float[count];
        HalfBandDecimator.Decimate(signal, 0, halved, start);
        return halved;
    }

    private static double PeakOf(float[] samples) => samples.Max(Math.Abs);
}
