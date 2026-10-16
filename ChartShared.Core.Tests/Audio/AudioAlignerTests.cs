using ChartShared.Audio.Alignment;
using ChartShared.Audio.Decoding;
using ChartShared.Audio.Spectrum;
using ChartShared.Audio.Waveforms;
using ChartShared.Core.Merging;
using ChartShared.Core.Scanning;

namespace ChartShared.Core.Tests.Audio;

[TestClass]
public sealed class AudioAlignerTests
{
    private const int Rate = 8000;

    private static readonly double[] Gaps = [0.125, 0.25, 0.375, 0.5, 0.75];

    [TestMethod]
    public void TheFftUndoesItself()
    {
        var fft = new Fft(16);
        var random = new Random(1);
        float[] signal = [.. Enumerable.Range(0, 16).Select(_ => (float)random.NextDouble())];
        float[] re = [.. signal];
        float[] im = new float[16];

        fft.Forward(re, im);
        fft.Inverse(re, im);

        for (int i = 0; i < 16; i++)
        {
            Assert.AreEqual(signal[i], re[i], 1e-5f);
            Assert.AreEqual(0f, im[i], 1e-5f);
        }
    }

    [TestMethod]
    public void TheFftOfAnImpulseIsFlat()
    {
        var fft = new Fft(8);
        float[] re = [0, 1, 0, 0, 0, 0, 0, 0];
        float[] im = new float[8];

        fft.Forward(re, im);

        // An impulse one sample in is e^(-2πik/8) at bin k.
        for (int k = 0; k < 8; k++)
        {
            Assert.AreEqual((float)Math.Cos(2 * Math.PI * k / 8), re[k], 1e-5f);
            Assert.AreEqual((float)-Math.Sin(2 * Math.PI * k / 8), im[k], 1e-5f);
        }
    }

    [TestMethod]
    public void FftSizesArePowersOfTwo()
    {
        Assert.AreEqual(2, Fft.SizeFor(0));
        Assert.AreEqual(1024, Fft.SizeFor(1000));
        Assert.AreEqual(1024, Fft.SizeFor(1024));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new Fft(12));
    }

    [TestMethod]
    public void TheSameAudioLaterIsFound()
    {
        // The hits run to the end, so both ends of the song can be lined up.
        double[] times = Times(80, seed: 1);
        double seconds = times[^1] + 0.5;
        var a = Audio(Clicks(times, seconds, seed: 2));
        var b = Audio(Clicks(times.Select(t => t - 0.73), seconds, seed: 3));

        var estimate = AudioAligner.Align(a, b);

        Assert.AreEqual(AlignmentMethod.Audio, estimate.Method);
        Assert.AreEqual(730, estimate.OffsetMs, 3);
        Assert.IsGreaterThan(0.8, estimate.Match, $"Match was {estimate.Match}.");
        Assert.IsTrue(estimate.IsReliable);
        Assert.IsNotNull(estimate.DriftMs);
        Assert.AreEqual(0, estimate.DriftMs.Value, 3);
        Assert.IsFalse(estimate.HasDrift);
    }

    [TestMethod]
    public void QuieterNoisierAudioStillLinesUp()
    {
        double[] times = Times(80, seed: 4);
        var a = Audio(Clicks(times, 50, seed: 5));
        var b = Audio(Clicks(times.Select(t => t + 1.234), 52, seed: 6, gain: 0.3, noise: 0.05));

        var estimate = AudioAligner.Align(a, b);

        Assert.AreEqual(-1234, estimate.OffsetMs, 3);
        Assert.IsTrue(estimate.IsReliable, $"Match was {estimate.Match}.");
    }

    [TestMethod]
    public void StemsAreMixedBeforeLiningUp()
    {
        // The second song is two stems, each with every other hit of the first song's one track.
        double[] times = Times(80, seed: 7);
        var a = Audio(Clicks(times, 50, seed: 8));
        var shifted = times.Select(t => t - 0.4).ToArray();
        var b = AlignmentAudio.Of([Peaks(Clicks(shifted.Where((_, i) => i % 2 == 0), 50, seed: 9)), Peaks(Clicks(shifted.Where((_, i) => i % 2 == 1), 50, seed: 10))])!;

        var estimate = AudioAligner.Align(a, b);

        Assert.AreEqual(400, estimate.OffsetMs, 3);
        Assert.IsTrue(estimate.IsReliable);
    }

    [TestMethod]
    public void DifferentMusicIsntReliable()
    {
        var a = Audio(Clicks(Times(80, seed: 11), 50, seed: 12));
        var b = Audio(Clicks(Times(80, seed: 13), 50, seed: 14));

        var estimate = AudioAligner.Align(a, b);

        Assert.IsFalse(estimate.IsReliable, $"Match was {estimate.Match}.");
    }

    [TestMethod]
    public void AMetronomeIsAmbiguous()
    {
        double[] beats = [.. Enumerable.Range(0, 80).Select(i => 1 + (i * 0.5))];
        var a = Audio(Clicks(beats, 45, seed: 15));
        var b = Audio(Clicks(beats, 45, seed: 16));

        Assert.IsTrue(AudioAligner.Align(a, b).IsAmbiguous);
    }

    [TestMethod]
    public void AudioPlayedFasterDrifts()
    {
        // The second song's hits come 0.05% sooner each, about 40 ms by the end.
        double[] times = Times(200, seed: 17);
        var a = Audio(Clicks(times, 85, seed: 18));
        var b = Audio(Clicks(times.Select(t => t * 0.9995), 85, seed: 19));

        var estimate = AudioAligner.Align(a, b);

        Assert.IsNotNull(estimate.DriftMs);
        Assert.IsTrue(estimate.HasDrift, $"Drift was {estimate.DriftMs} ms.");
        Assert.IsTrue(estimate.DriftMs > 0);
    }

    [TestMethod]
    public void LinksAreMadeBetweenEverySongWithAudioEachWayRound()
    {
        double[] times = Times(60, seed: 20);
        var a = Audio(Clicks(times, 40, seed: 21));
        var b = Audio(Clicks(times.Select(t => t - 0.25), 40, seed: 22));

        var links = AudioAligner.Links([a, null, b], TestContext.CancellationToken);

        Assert.HasCount(2, links);
        var forward = links.Single(l => l.From == 0);
        Assert.AreEqual(2, forward.To);
        Assert.IsTrue(forward.IsAudio);
        Assert.IsNull(forward.PartId);
        Assert.AreEqual(AlignmentMethod.Audio, forward.PreferredMethod);
        Assert.AreEqual(250, forward.Estimate(AlignmentMethod.Audio).OffsetMs, 3);
        Assert.AreEqual(-forward.Estimate(AlignmentMethod.Audio).OffsetMs, links.Single(l => l.From == 2).Estimate(AlignmentMethod.Audio).OffsetMs);
    }

    [TestMethod]
    public void LinksLineSongsOfDifferentLengthsUpAsEachPairWould()
    {
        // Links takes every song's spectrum once, at one size for all of them.
        double[] times = Times(80, seed: 23);
        AlignmentAudio[] songs =
        [
            Audio(Clicks(times, 50, seed: 24)),
            Audio(Clicks(times.Select(t => t + 0.4), 30, seed: 25)),
            Audio(Clicks(times.Select(t => t - 0.9), 60, seed: 26)),
        ];

        var links = AudioAligner.Links(songs, TestContext.CancellationToken);

        foreach (var link in links.Where(l => l.From < l.To))
        {
            var linked = link.Estimate(AlignmentMethod.Audio);
            var paired = AudioAligner.Align(songs[link.From], songs[link.To]);
            Assert.AreEqual(paired.OffsetMs, linked.OffsetMs, 1, $"{link.From} to {link.To}");
            Assert.AreEqual(paired.Match, linked.Match, 1e-3, $"{link.From} to {link.To}");
            Assert.AreEqual(paired.IsAmbiguous, linked.IsAmbiguous, $"{link.From} to {link.To}");
        }
    }

    [TestMethod]
    public void ASongsTracksAreReadWithoutThePreviewOrTheCrowdAndKept()
    {
        double[] times = Times(40, seed: 23);
        var folder = Directory.CreateTempSubdirectory("chartpacker-align-");
        try
        {
            File.WriteAllBytes(Path.Combine(folder.FullName, "guitar.wav"), TestAudio.Wav(Clicks(times, 30, seed: 24), Rate, 1));
            File.WriteAllBytes(Path.Combine(folder.FullName, "drums.wav"), TestAudio.Wav(Clicks(times, 30, seed: 25), Rate, 1));
            File.WriteAllBytes(Path.Combine(folder.FullName, "preview.wav"), TestAudio.Wav(new float[Rate], Rate, 1));
            File.WriteAllBytes(Path.Combine(folder.FullName, "crowd.wav"), TestAudio.Wav(Clicks(Times(40, seed: 28), 30, seed: 29), Rate, 1));
            var entry = new SongEntry(SongEntryKind.SongFolder, folder.FullName, folder.Name, folder.Name);
            string[] names = ["guitar.wav", "drums.wav", "preview.wav", "crowd.wav"];
            var cache = new StemPeaksCache(64L * 1024 * 1024);

            var read = AudioAligner.Read(entry, names, WaveformResolution.Low, cache, TestContext.CancellationToken);

            Assert.IsNotNull(read);
            Assert.AreEqual(30, read.Seconds, 0.1);
            foreach (string name in names)
            {
                var key = StemPeaksCache.KeyOf(entry, new SongTrack(name, Path.GetFileNameWithoutExtension(name), null))!.Value;
                Assert.AreEqual(name is not ("preview.wav" or "crowd.wav"), cache.TryGet(key, out _), name);
            }

            // Read again, from the cache, it's the same.
            var again = AudioAligner.Read(entry, names, WaveformResolution.Medium, cache, TestContext.CancellationToken);
            Assert.AreEqual(0, AudioAligner.Align(read, again!).OffsetMs);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void TheCrowdDoesntThrowOffAutoAlignment()
    {
        // The second song's guitar is half a second early; its crowd, louder, cheers on a beat of its own.
        double[] times = Times(60, seed: 33);
        var root = Directory.CreateTempSubdirectory("chartpacker-align-");
        try
        {
            var a = root.CreateSubdirectory("a");
            var b = root.CreateSubdirectory("b");
            File.WriteAllBytes(Path.Combine(a.FullName, "song.wav"), TestAudio.Wav(Clicks(times, 40, seed: 34), Rate, 1));
            File.WriteAllBytes(Path.Combine(b.FullName, "guitar.wav"), TestAudio.Wav(Clicks(times.Select(t => t - 0.5), 40, seed: 35, gain: 0.3), Rate, 1));
            File.WriteAllBytes(Path.Combine(b.FullName, "crowd.wav"), TestAudio.Wav(Clicks(Times(60, seed: 36), 40, seed: 37), Rate, 1));

            var first = AudioAligner.Read(new SongEntry(SongEntryKind.SongFolder, a.FullName, a.Name, a.Name), ["song.wav"], WaveformResolution.Low, cancellationToken: TestContext.CancellationToken)!;
            var second = AudioAligner.Read(new SongEntry(SongEntryKind.SongFolder, b.FullName, b.Name, b.Name), ["guitar.wav", "crowd.wav"], WaveformResolution.Low, cancellationToken: TestContext.CancellationToken)!;
            var estimate = AudioAligner.Links([first, second], TestContext.CancellationToken).Single(l => l.From == 0).Estimate(AlignmentMethod.Audio);

            Assert.AreEqual(500, estimate.OffsetMs, 3);
            Assert.IsGreaterThan(0.8, estimate.Match, $"Match was {estimate.Match}.");
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void PeaksForDrawingAreReadAtTheResolutionAskedFor()
    {
        var folder = Directory.CreateTempSubdirectory("chartpacker-align-");
        try
        {
            File.WriteAllBytes(Path.Combine(folder.FullName, "song.wav"), TestAudio.Wav(Clicks(Times(10, seed: 26), 8, seed: 27), Rate, 1));
            var entry = new SongEntry(SongEntryKind.SongFolder, folder.FullName, folder.Name, folder.Name);
            var cache = new StemPeaksCache(64L * 1024 * 1024);
            AudioAligner.ReadPeaks(entry, ["song.wav"], WaveformResolution.Low, cache, anyResolution: false, TestContext.CancellationToken);

            // Any resolution will do for lining up, but drawing mixes tracks of one.
            Assert.AreEqual(WaveformResolution.Low, AudioAligner.ReadPeaks(entry, ["song.wav"], WaveformResolution.High, cache, anyResolution: true, TestContext.CancellationToken).Single().Resolution);
            Assert.AreEqual(WaveformResolution.High, AudioAligner.ReadPeaks(entry, ["song.wav"], WaveformResolution.High, cache, anyResolution: false, TestContext.CancellationToken).Single().Resolution);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void SilenceIsNothingToLineUpBy()
    {
        Assert.IsNull(AlignmentAudio.Of([Peaks(new float[Rate * 5])]));
        Assert.IsNull(AlignmentAudio.Of([]));
    }

    [TestMethod]
    public void AudioTooShortToRiseIsNothingToLineUpBy()
    {
        // Up to 12 ms: shorter than a frame's window, which was read past, and then too few frames to rise over.
        for (int ms = 1; ms <= 12; ms++)
        {
            var random = new Random(ms);
            float[] noise = [.. Enumerable.Range(0, Rate * ms / 1000).Select(_ => (float)((random.NextDouble() * 2) - 1))];
            Assert.IsNull(AlignmentAudio.Of([Peaks(noise)]), $"{ms} ms");
        }
    }

    /// <summary>Times of hits with gaps of an eighth to a beat and a half at 120 BPM, the same for a seed, from a second in.</summary>
    private static double[] Times(int count, int seed)
    {
        var random = new Random(seed);
        var times = new double[count];
        double t = 1;
        for (int i = 0; i < count; i++)
        {
            times[i] = t;
            t += Gaps[random.Next(Gaps.Length)];
        }

        return times;
    }

    /// <summary><paramref name="seconds"/> of quiet noise with a hit, a 20 ms burst of noise dying away, at each of <paramref name="times"/>.</summary>
    private static float[] Clicks(IEnumerable<double> times, double seconds, int seed, double gain = 1, double noise = 0.01)
    {
        var random = new Random(seed);
        var samples = new float[(int)(seconds * Rate)];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = (float)(((random.NextDouble() * 2) - 1) * noise);

        foreach (double time in times)
        {
            int start = (int)Math.Round(time * Rate);
            for (int k = 0; k < Rate / 50; k++)
            {
                if (start + k >= 0 && start + k < samples.Length)
                    samples[start + k] += (float)(gain * 0.8 * Math.Exp(-k / 40.0) * ((random.NextDouble() * 2) - 1));
            }
        }

        for (int i = 0; i < samples.Length; i++)
            samples[i] = Math.Clamp(samples[i], -1f, 1f);
        return samples;
    }

    private static StemPeaks Peaks(float[] samples) =>
        WaveformPeaks.Compute("song.wav", new WavSource(new MemoryStream(TestAudio.Wav(samples, Rate, 1))), WaveformResolution.Low);

    private static AlignmentAudio Audio(float[] samples) => AlignmentAudio.Of([Peaks(samples)])!;

    public TestContext TestContext { get; set; }
}
