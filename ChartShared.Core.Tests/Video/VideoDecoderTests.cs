using ChartShared.Core.Scanning;
using ChartShared.Core.Sng;
using ChartShared.Core.Tests.Sng;
using ChartShared.Ffmpeg;
using ChartShared.Video;

namespace ChartShared.Core.Tests.Video;

/// <summary>
/// Decodes the fixtures: two seconds of a 64×36 test pattern at 10 fps, with a keyframe every half
/// second, as MPEG-4 in mp4 and as VP8 in webm. Needs FFmpeg beside the tests (build/ffmpeg);
/// without it these are inconclusive.
/// </summary>
[TestClass]
public sealed class VideoDecoderTests
{
    private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "Video", "Fixtures");

    private DirectoryInfo _root = null!;

    [TestInitialize]
    public void Initialize()
    {
        if (!FfmpegLibrary.IsAvailable)
            Assert.Inconclusive(FfmpegLibrary.UnavailableReason ?? "");
        _root = Directory.CreateTempSubdirectory("chartpacker-video-");
    }

    [TestCleanup]
    public void Cleanup() => _root?.Delete(recursive: true);

    [TestMethod]
    [DataRow("tiny.mp4")]
    [DataRow("tiny.webm")]
    public void EveryFrameIsDecodedInOrder(string name)
    {
        using var decoder = Open(name, maxWidth: 720);

        Assert.AreEqual(64, decoder.Width);
        Assert.AreEqual(36, decoder.Height);
        Assert.IsLessThan(0.2, Math.Abs(decoder.Duration.TotalSeconds - 2), $"Duration {decoder.Duration}");

        var times = new List<TimeSpan>();
        while (decoder.Next(out var time))
            times.Add(time);

        Assert.HasCount(20, times);
        Assert.AreEqual(TimeSpan.Zero, times[0]);
        Assert.AreSequenceEqual(times.Order().ToList(), times);
        Assert.IsLessThan(0.01, Math.Abs(times[^1].TotalSeconds - 1.9), $"Last frame at {times[^1]}");
    }

    // swscale writes rows in whole blocks of pixels, past the end of a row whose width isn't a multiple of the block
    // (as these widths aren't); none of it may reach past the frame's own bytes in the caller's buffer.
    [TestMethod]
    [DataRow("tiny.mp4")]
    [DataRow("tiny.webm")]
    public void ConvertingWritesNothingPastTheFrame(string name)
    {
        const int guard = 256;
        const byte unwritten = 0xA5;
        for (int maxWidth = 10; maxWidth <= 63; maxWidth++)
        {
            using var decoder = Open(name, maxWidth);
            int frameBytes = decoder.Width * decoder.Height * 4;
            var buffer = new byte[guard + frameBytes + guard];
            buffer.AsSpan().Fill(unwritten);
            Assert.IsTrue(decoder.Next(out _));
            decoder.Keep();

            Assert.IsTrue(decoder.CopyKept(buffer.AsSpan(guard, frameBytes)));
            Assert.IsFalse(buffer.AsSpan(0, guard).ContainsAnyExcept(unwritten), $"Written before the frame at width {maxWidth}");
            Assert.IsFalse(buffer.AsSpan(guard + frameBytes).ContainsAnyExcept(unwritten), $"Written past the frame at width {maxWidth}");
        }
    }

    // Every frame is converted into the one buffer the decoder sets up for the first, and still after a seek.
    [TestMethod]
    [DataRow("tiny.mp4")]
    [DataRow("tiny.webm")]
    public void EveryFrameConvertsIntoTheSameDecoder(string name)
    {
        using var decoder = Open(name, maxWidth: 720);
        byte[] bgra = new byte[decoder.Width * decoder.Height * 4];
        var frames = new HashSet<string>();
        while (decoder.Next(out var time))
        {
            decoder.Keep();
            Assert.IsTrue(decoder.CopyKept(bgra), $"The frame at {time} wasn't converted");
            frames.Add(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bgra)));
        }

        Assert.IsGreaterThan(1, frames.Count, "Every frame converted to the same picture");

        decoder.Seek(TimeSpan.FromSeconds(0.6));
        Assert.IsTrue(decoder.Next(out _));
        decoder.Keep();
        Assert.IsTrue(decoder.CopyKept(bgra));
    }

    [TestMethod]
    public void WideFramesAreScaledDownKeepingTheirShape()
    {
        using var decoder = Open("tiny.mp4", maxWidth: 32);

        Assert.AreEqual(32, decoder.Width);
        Assert.AreEqual(18, decoder.Height);
    }

    [TestMethod]
    public void TheKeptFrameIsConvertedToOpaqueBgra()
    {
        using var decoder = Open("tiny.webm", maxWidth: 720);
        byte[] bgra = new byte[decoder.Width * decoder.Height * 4];

        Assert.IsFalse(decoder.CopyKept(bgra));
        Assert.IsTrue(decoder.Next(out _));
        decoder.Keep();

        Assert.IsTrue(decoder.CopyKept(bgra));
        Assert.IsTrue(Enumerable.Range(0, bgra.Length / 4).All(i => bgra[(i * 4) + 3] == 255));
        Assert.Contains(b => b != 0 && b != 255, bgra);
    }

    [TestMethod]
    [DataRow("tiny.mp4")]
    [DataRow("tiny.webm")]
    public void SeekingLandsOnTheKeyframeBefore(string name)
    {
        using var decoder = Open(name, maxWidth: 720);
        Assert.IsTrue(decoder.Next(out _));
        decoder.Keep();

        decoder.Seek(TimeSpan.FromSeconds(1.2));

        Assert.IsFalse(decoder.HasKept);
        Assert.IsTrue(decoder.Next(out var time));
        Assert.IsLessThan(0.01, Math.Abs(time.TotalSeconds - 1.0), $"First frame after the seek at {time}");
    }

    [TestMethod]
    public void AVideoInASngIsReadWithoutExtracting()
    {
        var builder = new SngBuilder();
        byte[] video = File.ReadAllBytes(Path.Combine(Fixtures, "tiny.webm"));
        SngMask.Apply(video, builder.Mask);
        builder.AddFile("notes.chart", [1]).AddFile("video.webm", video);
        string path = Path.Combine(_root.FullName, "Song.sng");
        File.WriteAllBytes(path, builder.Build());
        var entry = new SongEntry(SongEntryKind.SngFile, path, "Song.sng", "Song");

        using var decoder = VideoDecoder.Open(SongFile.OpenRead(entry, "video.webm", TestContext.CancellationToken)!, 720);
        int frames = 0;
        while (decoder.Next(out _))
            frames++;

        Assert.AreEqual(20, frames);
    }

    [TestMethod]
    public void CheckAcceptsAVideoAndRejectsAnythingElse()
    {
        VideoDecoder.Check(Path.Combine(Fixtures, "tiny.mp4"));

        string junk = Path.Combine(_root.FullName, "video.mp4");
        File.WriteAllBytes(junk, [.. Enumerable.Range(0, 4096).Select(i => (byte)(i * 7))]);
        Assert.ThrowsExactly<InvalidDataException>(() => VideoDecoder.Check(junk));
    }

    [TestMethod]
    public async Task PlaybackPublishesTheFrameDueAndClosesTheFile()
    {
        string copy = Path.Combine(_root.FullName, "video.mp4");
        File.Copy(Path.Combine(Fixtures, "tiny.mp4"), copy);
        using var published = new SemaphoreSlim(0);
        var playback = new VideoPlayback(() => File.OpenRead(copy), 720);
        playback.Changed += (_, _) => published.Release();

        playback.RequestTime(TimeSpan.FromSeconds(1.5));
        int version = 0;
        byte[] bgra = new byte[64 * 36 * 4];
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!playback.TryCopyFrame(bgra, ref version) && DateTime.UtcNow < deadline)
            await published.WaitAsync(TimeSpan.FromMilliseconds(200), TestContext.CancellationToken);

        Assert.IsNull(playback.Error);
        Assert.AreEqual(64, playback.Width);
        Assert.IsGreaterThan(0, version);
        Assert.IsFalse(playback.TryCopyFrame(bgra, ref version));

        playback.Dispose();
        await playback.WhenClosedAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.CancellationToken);
        File.Delete(copy);
    }

    [TestMethod]
    [DataRow("tiny.mp4")]
    [DataRow("tiny.webm")]
    public async Task PlaybackShowsAFarFirstTime(string name)
    {
        // Far enough in that the first request seeks rather than decoding from the start.
        using var published = new SemaphoreSlim(0);
        var playback = new VideoPlayback(() => File.OpenRead(Path.Combine(Fixtures, name)), 720);
        playback.Changed += (_, _) => published.Release();

        playback.RequestTime(TimeSpan.FromSeconds(1.8));
        int version = 0;
        byte[] bgra = new byte[64 * 36 * 4];
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!playback.TryCopyFrame(bgra, ref version) && DateTime.UtcNow < deadline)
            await published.WaitAsync(TimeSpan.FromMilliseconds(200), TestContext.CancellationToken);

        Assert.IsNull(playback.Error);
        Assert.IsGreaterThan(0, version);

        playback.Dispose();
        await playback.WhenClosedAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.CancellationToken);
    }

    [TestMethod]
    public async Task PlaybackReportsAFileThatIsNotAVideo()
    {
        var playback = new VideoPlayback(() => new MemoryStream([1, 2, 3, 4]), 720);
        playback.Dispose();
        await playback.WhenClosedAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.CancellationToken);

        var failing = new VideoPlayback(() => new MemoryStream([.. Enumerable.Range(0, 4096).Select(i => (byte)i)]), 720);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (failing.Error is null && DateTime.UtcNow < deadline)
            await Task.Delay(20, TestContext.CancellationToken);

        Assert.IsNotNull(failing.Error);
        failing.Dispose();
        await failing.WhenClosedAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.CancellationToken);
    }

    private static VideoDecoder Open(string name, int maxWidth) =>
        VideoDecoder.Open(File.OpenRead(Path.Combine(Fixtures, name)), maxWidth);

    public TestContext TestContext { get; set; }
}
