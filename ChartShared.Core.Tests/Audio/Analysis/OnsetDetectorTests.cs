using ChartShared.Audio.Analysis;
using ChartShared.Core.Charting.Analysis;

namespace ChartShared.Core.Tests.Audio.Analysis;

[TestClass]
public sealed class OnsetDetectorTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow(44100)]
    [DataRow(48000)]
    public void EveryClickIsFoundWithinFiveMilliseconds(int rate)
    {
        var random = new Random(7);
        var times = Enumerable.Range(0, 30).Select(i => 0.5 + (i * 0.37) + (random.NextDouble() * 0.05)).ToList();
        var samples = AnalysisSignals.ClickTrack(times, 13, rate);

        var onsets = OnsetDetector.Analyze(AnalysisSignals.Input(samples, rate), drumBands: false, TestContext.CancellationToken);
        var found = onsets.Times(OnsetBand.Full, OnsetDetector.DefaultSensitivity);

        foreach (double time in times)
            Assert.IsLessThanOrEqualTo(0.005, AnalysisSignals.DistanceToNearest(found, time), $"The click at {time:0.000} s.");
        Assert.HasCount(times.Count, found, "Nothing else is an onset.");
    }

    [TestMethod]
    public void EachDrumIsFoundInItsOwnBand()
    {
        const int rate = 44100;
        var random = new Random(11);
        var hits = new Dictionary<OnsetBand, List<double>> { [OnsetBand.Kick] = [], [OnsetBand.Snare] = [], [OnsetBand.Cymbals] = [] };
        var bands = hits.Keys.ToArray();
        var samples = AnalysisSignals.Silence(16, rate, noise: 0.0005f);
        for (int i = 0; i < 40; i++)
        {
            double at = 0.5 + (i * 0.375);
            // Mostly one drum at a time, sometimes two together.
            var playing = i % 5 == 0 ? [bands[i % 3], bands[(i + 1) % 3]] : new[] { bands[random.Next(3)] };
            foreach (var band in playing)
            {
                hits[band].Add(at);
                AnalysisSignals.Add(samples, AnalysisSignals.BandHit(band, rate), at, rate);
            }
        }

        var onsets = OnsetDetector.Analyze(AnalysisSignals.Input(samples, rate), drumBands: true, TestContext.CancellationToken);

        foreach (var band in bands)
        {
            var found = onsets.Times(band, OnsetDetector.DefaultSensitivity);
            foreach (double hit in hits[band])
                Assert.IsLessThanOrEqualTo(0.010, AnalysisSignals.DistanceToNearest(found, hit), $"{band} at {hit:0.000} s.");
            foreach (double onset in found)
                Assert.IsLessThanOrEqualTo(0.030, AnalysisSignals.DistanceToNearest(hits[band], onset), $"{band} has an onset at {onset:0.000} s with no hit.");
        }

        Assert.IsTrue(onsets.Select(OnsetDetector.DefaultSensitivity).All(o => o.Band != OnsetBand.Full), "Drums' onsets are by band.");
    }

    [TestMethod]
    public void MoreSensitivityFindsMoreAndKeepsWhatWasFound()
    {
        const int rate = 44100;
        var samples = AnalysisSignals.Silence(8, rate, noise: 0.0005f);
        var click = ChartShared.Audio.Synthesis.Synth.Click(true, rate).Samples.ToArray();
        for (int i = 0; i < 14; i++)
            AnalysisSignals.Add(samples, click, 0.5 + (i * 0.5), rate, (float)Math.Pow(10, -3 * (i % 7) / 20.0 * 2));

        var onsets = OnsetDetector.Analyze(AnalysisSignals.Input(samples, rate), drumBands: false, TestContext.CancellationToken);
        double[][] sets = [.. new[] { 0.0, 0.25, 0.5, 0.75, 1.0 }.Select(s => onsets.Times(OnsetBand.Full, s))];

        for (int i = 1; i < sets.Length; i++)
        {
            Assert.IsGreaterThanOrEqualTo(sets[i - 1].Length, sets[i].Length);
            CollectionAssert.IsSubsetOf(sets[i - 1], sets[i]);
        }

        Assert.IsGreaterThan(sets[0].Length, sets[^1].Length, "The quietest clicks only count when sensitive.");
    }

    [TestMethod]
    public void AClicksTimeDoesntDependOnItsLevel()
    {
        const int rate = 44100;
        double loud = Time(-6), quiet = Time(-30);

        Assert.AreEqual(loud, quiet, 0.001);

        double Time(double decibels)
        {
            var samples = AnalysisSignals.ClickTrack([1.2345], 2, rate, noise: 0.0001f);
            for (int n = 0; n < samples.Length; n++)
                samples[n] *= (float)Math.Pow(10, decibels / 20);
            var onsets = OnsetDetector.Analyze(AnalysisSignals.Input(samples, rate), false, TestContext.CancellationToken);
            return onsets.Times(OnsetBand.Full, 1).Single();
        }
    }

    [TestMethod]
    public void ChunksComputedApartMatchOnePass()
    {
        const int rate = 44100;
        var times = AnalysisSignals.Beats(133, 40, 0.3);
        var samples = AnalysisSignals.ClickTrack(times, 19, rate);
        var input = AnalysisSignals.Input(samples, rate);

        var whole = OnsetDetector.Analyze(input, true, 1 << 20, TestContext.CancellationToken);
        using var workers = new AnalysisWorkers(3);
        var chunked = OnsetDetector.Start(input, true, workers, false, 512, TestContext.CancellationToken).GetAwaiter().GetResult();

        foreach (var band in whole.Bands)
        {
            Assert.AreSequenceEqual(whole.Envelope(band).Values.ToArray(), chunked.Envelope(band).Values.ToArray(), $"{band}'s flux.");
            Assert.AreSequenceEqual(whole.Times(band, 1), chunked.Times(band, 1), $"{band}'s onsets.");
        }
    }

    [TestMethod]
    public void SilenceHasNoOnsetsAndASteadyToneOnlyItsStart()
    {
        const int rate = 44100;
        var silence = OnsetDetector.Analyze(AnalysisSignals.Input(new float[rate * 3], rate), true, TestContext.CancellationToken);
        // A tone from 1 s to 3 s, fading out over 50 ms rather than cut off, which would splash across the spectrum.
        var samples = new float[rate * 4];
        var sine = AnalysisSignals.Sine(440, 2, rate, 0.5);
        int fade = rate / 20;
        for (int n = 0; n < fade; n++)
            sine[sine.Length - 1 - n] *= (float)n / fade;
        AnalysisSignals.Add(samples, sine, 1, rate);
        var tone = OnsetDetector.Analyze(AnalysisSignals.Input(samples, rate), false, TestContext.CancellationToken);

        Assert.IsEmpty(silence.Select(1));
        var found = tone.Times(OnsetBand.Full, OnsetDetector.DefaultSensitivity);
        Assert.HasCount(1, found);
        Assert.AreEqual(1, found[0], 0.005);
    }

    [TestMethod]
    public void CancellingStopsIt()
    {
        var input = AnalysisSignals.Input(() => new TestAudio.FakeSource(44100, 1, 44100L * 600, (n, _) => (float)Math.Sin(n * 0.01)));
        using var workers = new AnalysisWorkers(1);
        using var cancel = new CancellationTokenSource();

        var task = OnsetDetector.Start(input, false, workers, false, cancel.Token);
        cancel.Cancel();

        Assert.ThrowsExactly<TaskCanceledException>(() => task.GetAwaiter().GetResult());
    }
}
