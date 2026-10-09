using ChartPacker.Audio.Analysis;
using ChartPacker.Audio.Decoding;
using ChartPacker.Audio.Playback;
using ChartPacker.Core.Scanning;

namespace ChartPacker.Core.Tests.Audio.Analysis;

[TestClass]
public sealed class MonoReaderTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void AStemsChannelsAreAveraged()
    {
        var input = AnalysisSignals.Input(() => new TestAudio.FakeSource(44100, 2, 1000, (_, c) => c == 0 ? 0.5f : -0.1f));

        using var reader = input.Open();
        var mono = new float[1000];
        WindowRead.Read(reader, 0, mono);

        Assert.AreEqual(44100, input.SampleRate);
        Assert.AreEqual(1000, input.LengthFrames);
        Assert.IsTrue(mono.All(s => Math.Abs(s - 0.2f) < 1e-6), "Every sample is the channels' average.");
    }

    [TestMethod]
    public void StemsAtDifferentRatesMixAtTheCommonRate()
    {
        var input = AnalysisInput.Of(
        [
            () => new TestAudio.FakeSource(48000, 1, 48000, (_, _) => 0.25f),
            () => new TestAudio.FakeSource(48000, 2, 24000, (_, _) => 0.25f),
            () => new TestAudio.FakeSource(44100, 1, 44100, (_, _) => 0.1f),
        ]);

        using var reader = input.Open();
        var mono = new float[48000];
        WindowRead.Read(reader, 0, mono);

        Assert.AreEqual(48000, input.SampleRate);
        Assert.AreEqual(48000, input.LengthFrames, "The mix is as long as its longest stem.");
        Assert.AreEqual(0.6f, mono[1000], 1e-4, "All three stems sound at first.");
        Assert.AreEqual(0.35f, mono[30000], 1e-4, "The half-second stem has ended.");
    }

    [TestMethod]
    public void ADamagedStemGoesQuietAndTheOthersGoOn()
    {
        var input = AnalysisInput.Of(
        [
            () => new TestAudio.FakeSource(48000, 1, 4800, (_, _) => 0.25f),
            () => new TestAudio.FakeSource(48000, 1, 4800, (_, _) => 0.5f) { IsDamaged = true },
        ]);

        using var reader = input.Open();
        var mono = new float[4800];
        WindowRead.Read(reader, 0, mono);

        Assert.AreEqual(0.25f, mono[100], 1e-6);
        Assert.IsNotNull(reader.Error);
    }

    [TestMethod]
    public void AWindowIsSilentBeforeTheStartAndPastTheEnd()
    {
        var input = AnalysisSignals.Input(() => new TestAudio.FakeSource(1000, 1, 100, (n, _) => n + 1));
        using var reader = input.Open();

        var early = new float[20];
        WindowRead.Read(reader, -10, early);
        var late = new float[20];
        WindowRead.Read(reader, 90, late);

        Assert.AreSequenceEqual(new float[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }, early);
        Assert.AreSequenceEqual(new float[] { 91, 92, 93, 94, 95, 96, 97, 98, 99, 100, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, late);
    }

    [TestMethod]
    public void TheSongsMixLeavesOutThePreviewAndTheCrowd()
    {
        var folder = Directory.CreateTempSubdirectory("chartpacker-analysis-");
        try
        {
            string Wav(string name, float value)
            {
                string path = Path.Combine(folder.FullName, name);
                File.WriteAllBytes(path, TestAudio.Wav(4410, 44100, 1, (_, _) => value));
                return path;
            }

            SongTrack[] tracks =
            [
                new("song.wav", "song", Wav("song.wav", 0.25f)),
                new("preview.wav", AudioStems.Preview, Wav("preview.wav", 0.5f)),
                new("crowd.wav", AudioStems.Crowd, Wav("crowd.wav", 0.5f)),
            ];
            using var audio = SongAudio.Open(null, tracks, TestContext.CancellationToken);

            var input = AnalysisInput.SongMix(audio)!;
            using var reader = input.Open();
            var mono = new float[100];
            WindowRead.Read(reader, 1000, mono);

            Assert.AreEqual(0.25f, mono[50], 1e-3, "Only the song stem is heard.");
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void AStemWhoseLengthIsntKnownIsDecodedOnceToFindIt()
    {
        var input = AnalysisInput.Of([() => (IPcmSource)new TestAudio.FakeSource(8000, 1, 1234, (_, _) => 0.1f) { LengthFrames = -1 }]);

        Assert.AreEqual(1234, input.LengthFrames);
    }
}
