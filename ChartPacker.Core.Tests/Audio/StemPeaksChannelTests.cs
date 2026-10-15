using ChartPacker.Audio.Waveforms;

namespace ChartPacker.Core.Tests.Audio;

/// <summary>Peaks read by channel, for a lane's stereo waveform (charter-spec C12 item 4).</summary>
[TestClass]
public sealed class StemPeaksChannelTests
{
    private const int Rate = 8000;

    [TestMethod]
    public void EachChannelHasItsOwnPeaksBesideTheAverage()
    {
        // A sine on the left, silence on the right.
        var source = Stereo(Rate * 2);

        var peaks = WaveformPeaks.Compute("stem", source, null, TimeSpan.Zero, resolution: WaveformResolution.High, channels: true);

        Assert.IsTrue(peaks.HasChannels);
        Assert.AreEqual(0.8f, peaks.Left.Peak, 0.01f);
        Assert.AreEqual(0, peaks.Right.Peak);
        Assert.AreEqual(0.4f, peaks.Peak, 0.01f, "The average is as before.");
        Assert.AreEqual(peaks.Count, peaks.Left.Count);
        Assert.IsGreaterThan(WaveformPeaks.Compute("stem", Stereo(Rate * 2), null, TimeSpan.Zero, resolution: WaveformResolution.High).SizeBytes, peaks.SizeBytes);
    }

    [TestMethod]
    public void AMonoStemsChannelsAreItself()
    {
        var peaks = WaveformPeaks.Compute("stem", new TestAudio.FakeSource(Rate, 1, Rate, (_, _) => 0.5f), null, TimeSpan.Zero, channels: true);

        Assert.IsTrue(peaks.HasChannels);
        Assert.AreSame(peaks, peaks.Left);
        Assert.AreSame(peaks, peaks.Right);
    }

    [TestMethod]
    public void ReadWithoutChannelsTheyAreTheAverage()
    {
        var peaks = WaveformPeaks.Compute("stem", Stereo(Rate), null, TimeSpan.Zero);

        Assert.IsFalse(peaks.HasChannels);
        Assert.AreSame(peaks, peaks.Left);
    }

    [TestMethod]
    public void ChannelsReadInChunksMatchOneRead()
    {
        var whole = WaveformPeaks.Compute("stem", Stereo(Rate * 5), null, TimeSpan.Zero, resolution: WaveformResolution.High, channels: true);
        var reader = new StemPeaksReader("stem", Stereo(Rate * 5), chunkBuckets: 150, resolution: WaveformResolution.High, channels: true);
        for (int i = reader.ChunkCount - 1; i >= 0; i--)
            reader.ReadChunk(i, Stereo(Rate * 5));
        var chunked = reader.Finish();

        Assert.IsGreaterThan(1, reader.ChunkCount);
        Assert.AreSequenceEqual([.. whole.Left.Max], [.. chunked.Left.Max]);
        Assert.AreSequenceEqual([.. whole.Right.Min], [.. chunked.Right.Min]);
        Assert.AreSequenceEqual([.. whole.Max], [.. chunked.Max]);
    }

    [TestMethod]
    public void StemsMixChannelByChannel()
    {
        var a = WaveformPeaks.Compute("a", Stereo(Rate), null, TimeSpan.Zero, channels: true);
        var b = WaveformPeaks.Compute("b", new TestAudio.FakeSource(Rate, 2, Rate, (_, c) => c == 1 ? 0.25f : 0), null, TimeSpan.Zero, channels: true);

        var (left, right) = WaveformPeaks.MixChannels([new AudibleStem(a, false, false), new AudibleStem(b, false, false)]);

        Assert.AreEqual(0.8f, left.Peak, 0.02f);
        Assert.AreEqual(0.25f, right.Peak, 0.01f);
        Assert.ThrowsExactly<ArgumentException>(() => WaveformPeaks.MixChannels([new AudibleStem(WaveformPeaks.Compute("c", Stereo(Rate), null, TimeSpan.Zero), false, false)]));
    }

    [TestMethod]
    public void TheCacheFindsPeaksByChannelOnlyWhereTheyWereReadSo()
    {
        var cache = new StemPeaksCache(64L * 1024 * 1024);
        var key = new StemPeaksKey("C:\\songs\\a.ogg", "a.ogg", 100, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        cache.Add(key, WaveformPeaks.Compute("a", Stereo(Rate), WaveformResolution.High));

        Assert.IsTrue(cache.TryGet(key, WaveformResolution.High, needChannels: false, out _));
        Assert.IsFalse(cache.TryGet(key, WaveformResolution.High, needChannels: true, out _));

        cache.Add(key, WaveformPeaks.Compute("a", Stereo(Rate), null, TimeSpan.Zero, resolution: WaveformResolution.High, channels: true));
        Assert.IsTrue(cache.TryGet(key, WaveformResolution.High, needChannels: true, out var found));
        Assert.IsTrue(found.HasChannels);
        Assert.IsTrue(cache.TryGet(key, WaveformResolution.High, needChannels: false, out _), "Peaks by channel do for the average too.");
    }

    private static TestAudio.FakeSource Stereo(long frames) =>
        new(Rate, 2, frames, (n, c) => c == 0 ? (float)(0.8 * Math.Sin(2 * Math.PI * 100 * n / Rate)) : 0);
}
