using System.Buffers.Binary;
using System.Text;
using ChartPacker.Core.Metadata;
using ChartPacker.Core.Scanning;

namespace ChartPacker.Core.Tests.Metadata;

[TestClass]
public sealed class SongLengthEstimatorTests
{
    private const int SampleRate = 8000;

    // 120 BPM at 192 ticks a beat: a beat is 500 ms. The last note starts at beat 4 and is held
    // for one beat, so the chart ends at 2500 ms; the other track ends sooner.
    private const string Chart = """
        [Song]
        {
          Resolution = 192
        }
        [SyncTrack]
        {
          0 = TS 4
          0 = B 120000
        }
        [ExpertSingle]
        {
          0 = N 0 0
          768 = N 1 192
        }
        [EasyDoubleBass]
        {
          384 = N 0 0
        }
        """;

    private DirectoryInfo _root = null!;

    [TestInitialize]
    public void CreateRoot() => _root = Directory.CreateTempSubdirectory("chartpacker-length-");

    [TestCleanup]
    public void DeleteRoot() => _root.Delete(recursive: true);

    [TestMethod]
    public void LengthIsTheLongestPartPlusTheBuffer()
    {
        Assert.AreEqual(6000L, new SongLengthEstimate(3000, 5000, 4000).SongLengthMs);
        Assert.AreEqual(1500L, new SongLengthEstimate(null, 500, null).SongLengthMs);
        Assert.IsNull(new SongLengthEstimate(null, null, null).SongLengthMs);
    }

    [TestMethod]
    [DataRow(null, null, false, 10_000L)]
    [DataRow(2000L, null, false, 8000L)]
    [DataRow(-1000L, null, false, 11_000L)]
    [DataRow(null, 5000L, false, 5000L)]
    [DataRow(2000L, 5000L, false, 3000L)]
    [DataRow(null, 20_000L, false, 10_000L)]
    [DataRow(12_000L, null, false, null)]
    [DataRow(5000L, 5000L, false, null)]
    [DataRow(null, null, true, null)]
    public void VideoEndFollowsTheTimeline(long? startMs, long? endMs, bool loop, long? expected)
    {
        var timeline = new VideoTimeline(
            TimeSpan.FromMilliseconds(startMs ?? 0),
            endMs is { } e ? TimeSpan.FromMilliseconds(e) : null,
            loop);

        Assert.AreEqual(expected, SongLengthEstimator.VideoEndMs(timeline, TimeSpan.FromSeconds(10)));
    }

    [TestMethod]
    public void ChartEndCountsSustainsAcrossEveryTrack()
    {
        var entry = CreateSong(("notes.chart", Encoding.UTF8.GetBytes(Chart)), ("song.wav", Wav(2000)));

        var estimate = Estimate(entry);

        Assert.AreEqual(2500L, estimate.ChartEndMs);
        Assert.AreEqual(3500L, estimate.SongLengthMs);
    }

    [TestMethod]
    public void ChartEndIncludesTheDelay()
    {
        var entry = CreateSong(("notes.chart", Encoding.UTF8.GetBytes(Chart)));

        var estimate = Estimate(entry, pairs: [new("delay", "500")]);

        Assert.AreEqual(3000L, estimate.ChartEndMs);
    }

    [TestMethod]
    public void PreviewAudioIsLeftOut()
    {
        var entry = CreateSong(("song.wav", Wav(2000)), ("preview.wav", Wav(9000)));

        Assert.AreEqual(2000L, Estimate(entry).AudioMs);
    }

    [TestMethod]
    public void StagedTracksCountAndRemovedTracksDont()
    {
        var entry = CreateSong(("song.wav", Wav(3000)), ("guitar.wav", Wav(1000)));
        string staged = WriteFile("staged-drums.wav", Wav(2000));
        var draft = new MetadataDraft();
        draft.RemoveFile("song.wav");
        draft.AddFile(new AddedFile("drums.wav", staged, new FileInfo(staged).Length));

        Assert.AreEqual(2000L, Estimate(entry, draft).AudioMs);
    }

    [TestMethod]
    public void StoredVideoCountsUnlessItLoops()
    {
        var entry = CreateSong(("video.mp4", [1, 2, 3]), ("song.wav", Wav(1000)));
        var probe = new FakeProbe { ["video.mp4"] = TimeSpan.FromSeconds(6) };

        Assert.AreEqual(6000L, Estimate(entry, probe: probe).VideoEndMs);
        Assert.IsNull(Estimate(entry, pairs: [new("video_loop", "1")], probe: probe).VideoEndMs);
    }

    [TestMethod]
    public void StagedVideoReplacesTheStoredOneAndARemovedVideoDoesntCount()
    {
        var entry = CreateSong(("video.mp4", [1, 2, 3]));
        string staged = WriteFile("other.webm", [4, 5, 6]);
        var probe = new FakeProbe { ["video.mp4"] = TimeSpan.FromSeconds(6), ["other.webm"] = TimeSpan.FromSeconds(9) };

        var replaced = new MetadataDraft();
        replaced.SetBackground(BackgroundKind.Video, staged);
        Assert.AreEqual(9000L, Estimate(entry, replaced, probe: probe).VideoEndMs);

        var removed = new MetadataDraft();
        removed.RemoveBackground(BackgroundKind.Video);
        Assert.IsNull(Estimate(entry, removed, probe: probe).VideoEndMs);
    }

    [TestMethod]
    public void SongWithNothingToMeasureHasNoLength()
    {
        var entry = CreateSong(("notes.chart", "not a chart"u8.ToArray()), ("album.png", [1]));

        Assert.IsNull(Estimate(entry).SongLengthMs);
    }

    private static SongLengthEstimate Estimate(SongEntry entry, MetadataDraft? draft = null, KeyValuePair<string, string>[]? pairs = null, FakeProbe? probe = null) =>
        SongLengthEstimator.Estimate(entry, draft ?? new MetadataDraft(), pairs ?? [], probe ?? new FakeProbe());

    private SongEntry CreateSong(params (string Name, byte[] Data)[] files)
    {
        string folder = Path.Combine(_root.FullName, "Song");
        Directory.CreateDirectory(folder);
        foreach (var (name, data) in files)
            File.WriteAllBytes(Path.Combine(folder, name), data);
        return new SongEntry(SongEntryKind.SongFolder, folder, "Song", "Song");
    }

    private string WriteFile(string name, byte[] data)
    {
        string path = Path.Combine(_root.FullName, name);
        File.WriteAllBytes(path, data);
        return path;
    }

    private static byte[] Wav(int ms)
    {
        int dataLength = SampleRate * ms / 1000 * 2;
        byte[] wav = new byte[44 + dataLength];
        var span = wav.AsSpan();

        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], 36 + dataLength);
        "WAVE"u8.CopyTo(span[8..]);
        "fmt "u8.CopyTo(span[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(span[20..], 1); // PCM
        BinaryPrimitives.WriteInt16LittleEndian(span[22..], 1); // mono
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], SampleRate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(span[32..], 2);
        BinaryPrimitives.WriteInt16LittleEndian(span[34..], 16);
        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], dataLength);
        return wav;
    }

    /// <summary>Video lengths by file name.</summary>
    private sealed class FakeProbe : Dictionary<string, TimeSpan>, IVideoDurationProbe
    {
        public FakeProbe() : base(StringComparer.OrdinalIgnoreCase) { }

        public TimeSpan? Duration(Stream stream, string name) => TryGetValue(name, out var d) ? d : null;
    }
}
