using System.Diagnostics;
using ChartShared.Audio.Analysis;

namespace ChartShared.Core.Tests.Audio.Analysis;

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
        foreach (bool sharpen in (bool[])[true, false])
        {
            var spectrogram = Compute(AnalysisSignals.Sine(hz, 1, rate), rate, sharpen);

            var column = spectrogram.Column(spectrogram.ColumnAt(0.5)).ToArray();

            // Low down, unsharpened, a few rows share the loudest level (to the half decibel): their middle is the peak,
            // which is within a third of a semitone. Sharpened, it's the sine's own row or the next.
            byte max = column.Max();
            double loudest = Enumerable.Range(0, column.Length).Where(r => column[r] == max).Average();
            Assert.AreEqual(SpectrogramRows.RowOf(hz), loudest, sharpen ? 1.0 : 2.5, $"Sharpened {sharpen}: the loudest row is {loudest}.");

            // Sharpened, a sine between two rows is shared between them: up to 3 dB less in each.
            double level = Spectrogram.DecibelsOf(max);
            Assert.IsTrue(sharpen ? level is >= -3.5 and <= 0.5 : Math.Abs(level) <= 1.5, $"Sharpened {sharpen}: a full-scale sine reads {level} dB.");
        }
    }

    [TestMethod]
    [DataRow(110.0)]
    [DataRow(440.0)]
    [DataRow(5000.0)]
    public void ASharpenedSineIsAThinLine(double hz)
    {
        var sharp = Column(Compute(AnalysisSignals.Sine(hz, 1, 44100)));
        var plain = Column(Compute(AnalysisSignals.Sine(hz, 1, 44100), sharpen: false));

        // Within 20 dB of its loudest: the sine's row and perhaps the next, where unsharpened it's as wide as the window's
        // main lobe, which is a few rows in the low rows and about one in the high.
        Assert.IsLessThanOrEqualTo(2, Wide(sharp), $"Sharpened, {Wide(sharp)} rows.");
        Assert.IsGreaterThanOrEqualTo(hz < 1000 ? 2 * Wide(sharp) : Wide(sharp), Wide(plain), $"Unsharpened, {Wide(plain)} rows.");
        Assert.AreEqual(Array.IndexOf(plain, plain.Max()), Array.IndexOf(sharp, sharp.Max()), 3, "Both have it at the same place.");

        static byte[] Column(Spectrogram spectrogram) => spectrogram.Column(spectrogram.ColumnAt(0.5)).ToArray();
        static int Wide(byte[] column) => column.Count(level => level >= column.Max() - 40);
    }

    [TestMethod]
    [DataRow(SpectrogramTiers.LowMidHz)]
    [DataRow(SpectrogramTiers.MidHighHz)]
    public void ASineReadsTheSameWhereTwoBandsMeet(double hz)
    {
        // From below the blend of the two bands to above it.
        foreach (bool sharpen in (bool[])[true, false])
        {
            for (double octaves = -0.4; octaves <= 0.4; octaves += 0.1)
            {
                double at = hz * Math.Pow(2, octaves);
                var spectrogram = Compute(AnalysisSignals.Sine(at, 0.6, 44100), sharpen: sharpen);
                var column = spectrogram.Column(spectrogram.ColumnAt(0.3)).ToArray();
                byte max = column.Max();
                double level = Spectrogram.DecibelsOf(max);

                Assert.AreEqual(SpectrogramRows.RowOf(at), Enumerable.Range(0, column.Length).Where(r => column[r] == max).Average(), sharpen ? 1.0 : 2.5, $"{at:0} Hz, sharpened {sharpen}.");
                Assert.IsTrue(sharpen ? level is >= -3.5 and <= 0.5 : Math.Abs(level) <= 1.5, $"{at:0} Hz, sharpened {sharpen}: {level} dB.");
            }
        }
    }

    [TestMethod]
    public void ASemitoneIsTheSameNumberOfRowsAnywhere()
    {
        Assert.AreEqual(SpectrogramRows.RowsPerSemitone, SpectrogramRows.RowOf(SpectrogramRows.HzOfPitch(61)) - SpectrogramRows.RowOf(SpectrogramRows.HzOfPitch(60)), 1e-9);
        Assert.AreEqual(SpectrogramRows.RowsPerSemitone, SpectrogramRows.RowOf(SpectrogramRows.HzOfPitch(85)) - SpectrogramRows.RowOf(SpectrogramRows.HzOfPitch(84)), 1e-9);
        Assert.AreEqual(7.065, SpectrogramRows.RowsPerSemitone, 0.001);
    }

    [TestMethod]
    public void ANoteIsAtItsFrequencyAndTheHigherOnesAreHigherUpThePicture()
    {
        Assert.AreEqual(440, SpectrogramRows.HzOfPitch(69), 1e-9);
        Assert.AreEqual(261.6256, SpectrogramRows.HzOfPitch(60), 1e-3);
        Assert.IsLessThan(SpectrogramRows.PictureYOfPitch(60), SpectrogramRows.PictureYOfPitch(61), "A higher note is nearer the picture's top, which is y 0.");
        Assert.AreEqual(SpectrogramRows.RowsPerSemitone, SpectrogramRows.PictureYOfPitch(60) - SpectrogramRows.PictureYOfPitch(61), 1e-9);
    }

    [TestMethod]
    [DataRow(36)]
    [DataRow(48)]
    [DataRow(60)]
    [DataRow(72)]
    [DataRow(84)]
    public void ASungNoteLightsTheRowsAcrossItsKey(int pitch)
    {
        double hz = SpectrogramRows.HzOfPitch(pitch);
        var spectrogram = Compute(AnalysisSignals.Sine(hz, 1, 44100), 44100);
        var column = spectrogram.Column(spectrogram.ColumnAt(0.5)).ToArray();
        byte max = column.Max();
        double loudest = Enumerable.Range(0, column.Length).Where(r => column[r] == max).Average();

        // The loudest row, as the picture is drawn, is on the pitch's key: within half a semitone of its middle.
        double pictureY = SpectrogramRows.Count - (loudest + 0.5);
        Assert.AreEqual(SpectrogramRows.PictureYOfPitch(pitch), pictureY, SpectrogramRows.RowsPerSemitone / 2);
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
        int nyquistRow = (int)Math.Ceiling(SpectrogramRows.RowOf(11025));
        foreach (bool sharpen in (bool[])[true, false])
        {
            var silent = Compute(new float[22050], 22050, sharpen);
            var noise = Compute(AnalysisSignals.Silence(1, 22050, noise: 0.5f), 22050, sharpen);

            Assert.IsTrue(silent.Column(silent.ColumnAt(0.5)).ToArray().All(b => b == 0));
            var column = noise.Column(noise.ColumnAt(0.5)).ToArray();
            Assert.IsTrue(column[..nyquistRow].All(b => b > 0), $"Sharpened {sharpen}: noise lights every row below half the rate.");
            Assert.IsTrue(column[nyquistRow..].All(b => b == 0), $"Sharpened {sharpen}: rows above half the rate stay silent.");
        }
    }

    [TestMethod]
    [DataRow(44100)]
    [DataRow(48000)]
    public void SharpenedNoiseLeavesHardlyAnyCellsEmpty(int rate)
    {
        // Its bins point all over, so each is spread over the rows and columns around where it points.
        var noise = Compute(AnalysisSignals.Silence(2, rate, noise: 0.5f), rate);
        int rows = (int)Math.Ceiling(SpectrogramRows.RowOf(Math.Min(SpectrogramRows.HighHz, rate / 2.0)));
        long empty = 0, cells = 0;
        for (int c = noise.ColumnAt(0.5); c < noise.ColumnAt(1.5); c++)
        {
            var column = noise.Column(c);
            for (int row = 0; row < rows; row++, cells++)
            {
                // Empty, or as good as: more than 40 dB under the row's usual level.
                if (column[row] < 60)
                    empty++;
            }
        }

        Assert.IsLessThan(0.002, (double)empty / cells, $"{empty} of {cells} cells.");
    }

    [TestMethod]
    [DataRow(100.0, true)]
    [DataRow(1000.0, true)]
    [DataRow(8000.0, true)]
    [DataRow(8000.0, false)]
    public void AClicksColumnIsTheColumnAtItsTime(double hz, bool sharpen)
    {
        var samples = new float[44100];
        samples[30000] = 1;

        var spectrogram = Compute(samples, sharpen: sharpen);

        // In each band's rows. (Unsharpened, the low rows' long windows are as loud for several columns around it.)
        int row = (int)Math.Round(SpectrogramRows.RowOf(hz));
        int loudest = Enumerable.Range(0, spectrogram.ColumnCount).MaxBy(c => spectrogram.Column(c)[row]);
        Assert.AreEqual(spectrogram.ColumnAt(30000 / 44100.0), loudest, 1);
    }

    [TestMethod]
    public void ASharpenedClickIsAThinColumnInTheLowRows()
    {
        var samples = new float[44100];
        samples[30000] = 1;
        int row = (int)Math.Round(SpectrogramRows.RowOf(150));

        // Within 30 dB of its loudest: the low rows' window is 186 ms, which is 32 columns.
        int sharp = Wide(Compute(samples));
        int plain = Wide(Compute(samples, sharpen: false));
        Assert.IsLessThanOrEqualTo(4, sharp, $"Sharpened, {sharp} columns.");
        Assert.IsGreaterThan(12, plain, $"Unsharpened, {plain} columns.");

        int Wide(Spectrogram spectrogram)
        {
            var levels = Enumerable.Range(0, spectrogram.ColumnCount).Select(c => spectrogram.Column(c)[row]).ToArray();
            return levels.Count(level => level > 0 && level >= levels.Max() - 60);
        }
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void ChunksComputedApartMatchOnePassByteForByte(bool sharpen)
    {
        // An odd number of columns, so the last has no partner.
        var samples = AnalysisSignals.Silence(3.01, 44100, noise: 0.2f);
        AnalysisSignals.Add(samples, AnalysisSignals.Sine(700, 1, 44100, 0.5), 1, 44100);
        var input = AnalysisSignals.Input(samples, 44100);

        using var workers = new AnalysisWorkers(3);
        using var chunked = SpectrogramBuilder.Start(input, workers, focusSeconds: 1.7, chunkColumns: 64, sharpen);
        using var whole = SpectrogramBuilder.Start(input, workers, focusSeconds: 0, chunkColumns: 1 << 16, sharpen);
        Wait(chunked, whole);

        Assert.AreEqual(1, chunked.ColumnCount % 2);
        Assert.IsTrue(chunked.IsComplete && whole.IsComplete);
        for (int c = 0; c < chunked.ColumnCount; c++)
            Assert.AreSequenceEqual(whole.Column(c).ToArray(), chunked.Column(c).ToArray(), $"Column {c}");
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
        foreach (var band in Enum.GetValues<ChartShared.Core.Charting.Layout.SpectrogramBand>())
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

        Assert.IsTrue(SpectrogramBands.Emphasised(ChartShared.Core.Charting.Layout.SpectrogramBand.Full).All(e => e));
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
        using var spectrogram = SpectrogramBuilder.Start(input, workers, focusSeconds: 0, sharpen: true);
        int lastColumn = spectrogram.ColumnAt(20);
        TestAudio.WaitUntil(() => Enumerable.Range(0, (lastColumn / spectrogram.ChunkColumns) + 1).All(spectrogram.IsChunkReady));
        clock.Stop();

        TestContext.WriteLine($"Took {clock.ElapsedMilliseconds} ms.");
        Assert.IsLessThan(TimeSpan.FromSeconds(1), clock.Elapsed, $"Took {clock.ElapsedMilliseconds} ms.");
    }

    private static Spectrogram Compute(float[] samples, int rate = 44100, bool sharpen = true)
    {
        using var workers = new AnalysisWorkers(2);
        var spectrogram = SpectrogramBuilder.Start(AnalysisSignals.Input(samples, rate), workers, 0, sharpen);
        Wait(spectrogram);
        return spectrogram;
    }

    private static void Wait(params Spectrogram[] spectrograms)
    {
        foreach (var spectrogram in spectrograms)
            Assert.IsTrue(spectrogram.Completion.Wait(TimeSpan.FromSeconds(30)), "Timed out computing the spectrogram.");
    }
}
