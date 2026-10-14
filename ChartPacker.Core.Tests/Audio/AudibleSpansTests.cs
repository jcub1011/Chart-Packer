using ChartPacker.Audio.Waveforms;
using ChartPacker.Core.Charting;

namespace ChartPacker.Core.Tests.Audio;

/// <summary>Where a song's stems are heard, for placing <c>[music_start]</c> and <c>[music_end]</c> (charter-spec C8 item 5).</summary>
[TestClass]
public sealed class AudibleSpansTests
{
    // At 8000 Hz and the editor's resolution, a bucket is 40 frames, 5 ms.
    private const int Rate = 8000;

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void TheSoundIsFoundBetweenTheSilences()
    {
        // Half a second of silence, a second of sound, half a second of silence.
        var stem = Peaks("song", 16000, n => n is >= 4000 and < 12000 ? 0.5f : 0f);

        Assert.AreEqual(new AudibleRange(0.5, 1.5), AudibleSpans.Of([stem]));
    }

    [TestMethod]
    public void AClickShorterThanFiftyMillisecondsIsntTheMusic()
    {
        // 25 ms of click at 0.1 s, then the sound from 1 s.
        var stem = Peaks("song", 16000, n => n is >= 800 and < 1000 ? 0.9f : n is >= 8000 and < 12000 ? 0.5f : 0f);

        Assert.AreEqual(new AudibleRange(1.0, 1.5), AudibleSpans.Of([stem]));
    }

    [TestMethod]
    public void SoundTheStemEndsInCountsHoweverShort()
    {
        // 25 ms at the very end: it's cut off, not a click.
        var stem = Peaks("song", 16000, n => n >= 15800 ? 0.5f : 0f);

        Assert.AreEqual(new AudibleRange(15800.0 / Rate, 2.0), AudibleSpans.Of([stem]));
    }

    [TestMethod]
    public void NoiseUnderSixtyDecibelsDownIsSilence()
    {
        // -70 dBFS of hiss throughout.
        var stem = Peaks("song", 16000, n => n is >= 4000 and < 8000 ? 0.5f : 0.0003f);

        Assert.AreEqual(new AudibleRange(0.5, 1.0), AudibleSpans.Of([stem]));
    }

    [TestMethod]
    public void TheStemsAreHeardFromTheEarliestStartToTheLatestEnd()
    {
        var guitar = Peaks("guitar", 16000, n => n is >= 4000 and < 8000 ? 0.5f : 0f);
        var drums = Peaks("drums", 16000, n => n is >= 6000 and < 12000 ? -0.5f : 0f);

        Assert.AreEqual(new AudibleRange(0.5, 1.5), AudibleSpans.Of([guitar, drums]));
    }

    [TestMethod]
    public void SilenceOrNoStemsIsNothingHeard()
    {
        Assert.IsNull(AudibleSpans.Of([Peaks("song", 16000, _ => 0f)]));
        Assert.IsNull(AudibleSpans.Of([]));
    }

    [TestMethod]
    public void AStemNotReadToItsEndIsRefused()
    {
        var partial = new StemPeaks("song", WaveformResolution.High, new float[10], new float[10], new float[10], [], 10, 5, 0);

        Assert.ThrowsExactly<ArgumentException>(() => AudibleSpans.Of([partial]));
    }

    private StemPeaks Peaks(string name, long frames, Func<long, float> value) =>
        WaveformPeaks.Compute(name, new TestAudio.FakeSource(Rate, 1, frames, (n, _) => value(n)), WaveformResolution.High, TestContext.CancellationToken);
}
