using ChartShared.Audio.Video;
using ChartShared.Audio.Waveforms;
using ChartShared.Ffmpeg;
using ChartShared.Video;

namespace ChartShared.Core.Tests.Video;

/// <summary>Reads a background video's audio, and rewrites a video without it. Needs FFmpeg beside the tests.</summary>
[TestClass]
public sealed class VideoAudioTests
{
    private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "Video", "Fixtures");

    private DirectoryInfo _root = null!;

    [TestInitialize]
    public void Initialize()
    {
        if (!FfmpegLibrary.IsAvailable)
            Assert.Inconclusive(FfmpegLibrary.UnavailableReason ?? "");
        _root = Directory.CreateTempSubdirectory("chartpacker-video-audio-");
    }

    [TestCleanup]
    public void Cleanup() => _root?.Delete(recursive: true);

    [TestMethod]
    [DataRow("tiny.mp4")]
    [DataRow("tiny.webm")]
    public void AVideoWithoutAudioSaysSo(string name)
    {
        var read = VideoAudio.Read(File.OpenRead(Path.Combine(Fixtures, name)), WaveformResolution.Low);

        Assert.IsFalse(read.HasAudio);
        Assert.IsNull(read.Peaks);
    }

    [TestMethod]
    [DataRow("tiny.mp4")]
    [DataRow("tiny.webm")]
    public void StrippingAVideoWithoutAudioChangesNothing(string name)
    {
        string copy = Path.Combine(_root.FullName, name);
        File.Copy(Path.Combine(Fixtures, name), copy);
        byte[] before = File.ReadAllBytes(copy);

        Assert.IsFalse(VideoAudioStripper.StripInPlace(copy));
        Assert.AreSequenceEqual(before, File.ReadAllBytes(copy));
    }

    [TestMethod]
    [DataRow("tiny-audio.mkv")]
    [DataRow("tiny-audio.mp4")]
    public void AVideoWithAudioIsReadAndSummarised(string name)
    {
        var read = VideoAudio.Read(File.OpenRead(Path.Combine(Fixtures, name)), WaveformResolution.Low);

        Assert.IsTrue(read.HasAudio);
        Assert.IsNotNull(read.Peaks);
        Assert.IsGreaterThan(0.5, WaveformPeaks.Of(read.Peaks).Seconds);
    }

    [TestMethod]
    [DataRow("tiny-audio.mkv")]
    [DataRow("tiny-audio.mp4")]
    public void StrippingAVideoWithAudioKeepsItsVideo(string name)
    {
        string copy = Path.Combine(_root.FullName, name);
        File.Copy(Path.Combine(Fixtures, name), copy);

        Assert.IsTrue(VideoAudioStripper.StripInPlace(copy));
        Assert.IsFalse(VideoAudio.Read(File.OpenRead(copy), WaveformResolution.Low).HasAudio);

        using var decoder = VideoDecoder.Open(File.OpenRead(copy), maxWidth: 720);
        int frames = 0;
        while (decoder.Next(out _))
            frames++;
        Assert.IsGreaterThan(0, frames);
    }

    [TestMethod]
    public void AnAudioTrackIsDecodedAndSummarised()
    {
        // Ogg Vorbis, because the release builds only the demuxers of the registered video formats: an audio-only
        // .ogv is one of those, while a bare .mp3 is a format the app never opens.
        string audio = Path.Combine(AppContext.BaseDirectory, "Audio", "Fixtures", "chirp-stereo-44100.ogg");

        var read = VideoAudio.Read(File.OpenRead(audio), WaveformResolution.Low);

        Assert.IsTrue(read.HasAudio);
        Assert.IsNotNull(read.Peaks);
        Assert.IsGreaterThan(0.5, WaveformPeaks.Of(read.Peaks).Seconds);
    }

    [TestMethod]
    public void StrippingIsAvailableWhenFfmpegIs()
    {
        Assert.AreEqual(FfmpegLibrary.IsAvailable, VideoAudioStripper.IsAvailable);
    }

    public TestContext TestContext { get; set; }
}
