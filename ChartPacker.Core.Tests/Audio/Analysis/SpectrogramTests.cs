using System.Diagnostics;
using ChartPacker.Audio.Analysis;

namespace ChartPacker.Core.Tests.Audio.Analysis;

[TestClass]
public sealed class SpectrogramTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow(50.0, 44100)]
    [DataRow(100.0, 44100)]
    [DataRow(440.0, 44100)]
    [DataRow(1000.0, 44100)]
    [DataRow(5000.0, 44100)]
    [DataRow(12000.0, 44100)]
    [DataRow(50.0, 48000)]
    [DataRow(440.0, 48000)]
    [DataRow(12000.0, 48000)]
    public void ASineLightsItsRowAtItsLevel(double hz, int rate)
    {
        var spectrogram = Compute(AnalysisSignals.Sine(hz, 1, rate), rate);

        var column = spectrogram.Column(spectrogram.ColumnAt(0.5)).ToArray();

        // Low down, a few rows share the loudest level (to the half decibel): their middle is the peak.
        byte max = column.Max();
        double loudest = Enumerable.Range(0, column.Length).Where(r => column[r] == max).Average();
        Assert.AreEqual(SpectrogramRows.RowOf(hz), loudest, 1.0, $"The loudest row is {loudest}.");
        Assert.AreEqual(0, Spectrogram.DecibelsOf(max), 1.5, "A full-scale sine reads 0 dB.");
    }

    [TestMethod]
    public void ASineThirtyDecibelsQuieterIsSixtyStepsLower()
    {
        var loud = Compute(AnalysisSignals.Sine(1000, 1, 44100));
        var quiet = Compute(AnalysisSignals.Sine(1000, 1, 44100, Math.Pow(10, -30 / 20.0)));
        int row = (int)Math.Round(SpectrogramRows.RowOf(1000));

        int column = loud.ColumnAt(0.5);
        Assert.AreEqual(60, loud.Column(column)[row] - quiet.Column(column)[row], 2);
    }

    [TestMethod]
    public void SilenceIsZeroAndRowsAboveHalfTheRateStaySilent()
    {
        var silent = Compute(new float[22050], 22050);
        var noise = Compute(AnalysisSignals.Silence(1, 22050, noise: 0.5f), 22050);
        int nyquistRow = (int)Math.Ceiling(SpectrogramRows.RowOf(11025));

        Assert.IsTrue(silent.Column(silent.ColumnAt(0.5)).ToArray().All(b => b == 0));
        var column = noise.Column(noise.ColumnAt(0.5)).ToArray();
        Assert.IsTrue(column[..nyquistRow].All(b => b > 0), "Noise lights every row below half the rate.");
        Assert.IsTrue(column[nyquistRow..].All(b => b == 0), "Rows above half the rate stay silent.");
    }

    [TestMethod]
    public void AClicksColumnIsTheColumnAtItsTime()
    {
        var samples = new float[44100];
        samples[30000] = 1;

        var spectrogram = Compute(samples);

        int loudest = Enumerable.Range(0, spectrogram.ColumnCount).MaxBy(c => spectrogram.Column(c)[200]);
        Assert.AreEqual(spectrogram.ColumnAt(30000 / 44100.0), loudest, 1);
    }

    [TestMethod]
    public void ChunksComputedApartMatchOnePassByteForByte()
    {
        // An odd number of columns, so the last has no partner.
        var samples = AnalysisSignals.Silence(3.01, 44100, noise: 0.2f);
        AnalysisSignals.Add(samples, AnalysisSignals.Sine(700, 1, 44100, 0.5), 1, 44100);
        var input = AnalysisSignals.Input(samples, 44100);

        using var workers = new AnalysisWorkers(3);
        using var chunked = SpectrogramBuilder.Start(input, workers, focusSeconds: 1.7, chunkColumns: 64);
        using var whole = SpectrogramBuilder.Start(input, workers, focusSeconds: 0, chunkColumns: 1 << 16);
        Wait(chunked, whole);

        Assert.AreEqual(1, chunked.ColumnCount % 2);
        Assert.IsTrue(chunked.IsComplete && whole.IsComplete);
        for (int c = 0; c < chunked.ColumnCount; c++)
            CollectionAssert.AreEqual(whole.Column(c).ToArray(), chunked.Column(c).ToArray(), $"Column {c}");
    }

    [TestMethod]
    public void TheChunkAtTheFocusIsComputedFirst()
    {
        var input = AnalysisSignals.Input(AnalysisSignals.Silence(30, 44100, noise: 0.1f), 44100);

        using var workers = new AnalysisWorkers(1);
        using var spectrogram = SpectrogramBuilder.Start(input, workers, focusSeconds: 20, chunkColumns: 128);
        TestAudio.WaitUntil(() => spectrogram.IsChunkReady(spectrogram.ColumnAt(20) / 128));

        // Chunks spread outwards from the focus: the start, 20 s away, is far from done.
        Assert.IsFalse(spectrogram.IsReady(0));
        Assert.IsFalse(spectrogram.IsComplete);
    }

    [TestMethod]
    public void ReleasingItStopsReadingAndDropsItsColumns()
    {
        var source = new TestAudio.FakeSource(44100, 1, 44100 * 60, (n, _) => (float)Math.Sin(n * 0.1));
        var input = AnalysisSignals.Input(() => source);

        using var workers = new AnalysisWorkers(1);
        var spectrogram = SpectrogramBuilder.Start(input, workers, focusSeconds: 0, chunkColumns: 64);
        TestAudio.WaitUntil(() => spectrogram.IsReady(0));
        spectrogram.Dispose();
        Wait(spectrogram);
        Thread.Sleep(50);
        int reads = source.Reads;
        Thread.Sleep(100);

        Assert.IsTrue(spectrogram.IsReleased);
        Assert.IsFalse(spectrogram.IsComplete);
        Assert.IsTrue(spectrogram.Column(0).IsEmpty);
        Assert.AreEqual(reads, source.Reads, "Nothing more is read.");
    }

    [TestMethod]
    public void DamagedAudioIsSilentAndSaysWhy()
    {
        var input = AnalysisSignals.Input(() => new TestAudio.FakeSource(44100, 1, 44100, (_, _) => 0.5f) { IsDamaged = true });

        using var workers = new AnalysisWorkers(2);
        using var spectrogram = SpectrogramBuilder.Start(input, workers, 0);
        Wait(spectrogram);

        Assert.IsNotNull(spectrogram.Error);
        Assert.IsTrue(spectrogram.Column(10).ToArray().All(b => b == 0));
    }

    [TestMethod]
    public void EachBandPresetEmphasisesItsRanges()
    {
        foreach (var band in Enum.GetValues<ChartPacker.Core.Charting.Layout.SpectrogramBand>())
        {
            var emphasised = SpectrogramBands.Emphasised(band);
            var ranges = SpectrogramBands.RangesOf(band);
            for (int row = 0; row < SpectrogramRows.Count; row++)
            {
                double hz = SpectrogramRows.HzOf(row);
                bool inside = ranges.Any(r => hz >= r.LowHz * 0.99 && hz <= r.HighHz * 1.01);
                bool clearlyOutside = ranges.All(r => hz < r.LowHz * 0.97 || hz > r.HighHz * 1.03);
                if (clearlyOutside)
                    Assert.IsFalse(emphasised[row], $"{band}: row {row} ({hz:0} Hz) is outside.");
                else if (inside && ranges.Any(r => hz >= r.LowHz * 1.03 && hz <= r.HighHz * 0.97))
                    Assert.IsTrue(emphasised[row], $"{band}: row {row} ({hz:0} Hz) is inside.");
            }
        }

        Assert.IsTrue(SpectrogramBands.Emphasised(ChartPacker.Core.Charting.Layout.SpectrogramBand.Full).All(e => e));
    }

    /// <summary>AC C13.1: the first screen of a 10-minute song within a second, on four cores.</summary>
    [TestMethod]
    [TestCategory("Performance")]
    public void TheFirstScreenOfATenMinuteSongIsReadyWithinASecond()
    {
        if (Environment.ProcessorCount < 4)
            Assert.Inconclusive("AC C13.1 is for a 4-core machine.");

        var random = new Random(3);
        var noise = AnalysisSignals.Silence(20, 44100, noise: 0.3f);
        var input = AnalysisSignals.Input(() => new TestAudio.FakeSource(44100, 2, 44100L * 600, (n, _) => noise[n % noise.Length]));

        using var workers = new AnalysisWorkers(3);
        var clock = Stopwatch.StartNew();
        using var spectrogram = SpectrogramBuilder.Start(input, workers, focusSeconds: 0);
        int lastColumn = spectrogram.ColumnAt(20);
        TestAudio.WaitUntil(() => Enumerable.Range(0, (lastColumn / spectrogram.ChunkColumns) + 1).All(spectrogram.IsChunkReady));
        clock.Stop();

        Assert.IsLessThan(TimeSpan.FromSeconds(1), clock.Elapsed, $"Took {clock.ElapsedMilliseconds} ms.");
    }

    private static Spectrogram Compute(float[] samples, int rate = 44100)
    {
        using var workers = new AnalysisWorkers(2);
        var spectrogram = SpectrogramBuilder.Start(AnalysisSignals.Input(samples, rate), workers, 0);
        Wait(spectrogram);
        return spectrogram;
    }

    private static void Wait(params Spectrogram[] spectrograms)
    {
        foreach (var spectrogram in spectrograms)
            Assert.IsTrue(spectrogram.Completion.Wait(TimeSpan.FromSeconds(30)), "Timed out computing the spectrogram.");
    }
}
