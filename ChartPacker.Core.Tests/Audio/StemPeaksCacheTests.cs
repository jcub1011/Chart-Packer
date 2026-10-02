using ChartPacker.Audio.Waveforms;
using ChartPacker.Core.Scanning;

namespace ChartPacker.Core.Tests.Audio;

[TestClass]
public sealed class StemPeaksCacheTests
{
    private DirectoryInfo _root = null!;

    [TestInitialize]
    public void CreateRoot() => _root = Directory.CreateTempSubdirectory("chartpacker-peakscache-");

    [TestCleanup]
    public void DeleteRoot() => _root.Delete(recursive: true);

    [TestMethod]
    public void PeaksAreFoundAgainWhileTheFileIsUnchanged()
    {
        var (entry, track) = FolderSong("guitar.wav", 100);
        var cache = new StemPeaksCache(1024 * 1024);
        var peaks = Peaks(1000);

        cache.Add(StemPeaksCache.KeyOf(entry, track)!.Value, peaks);

        Assert.IsTrue(cache.TryGet(StemPeaksCache.KeyOf(entry, track)!.Value, out var found));
        Assert.AreSame(peaks, found);
    }

    [TestMethod]
    public void AFileWrittenSinceIsNotFound()
    {
        var (entry, track) = FolderSong("guitar.wav", 100);
        var cache = new StemPeaksCache(1024 * 1024);
        cache.Add(StemPeaksCache.KeyOf(entry, track)!.Value, Peaks(1000));

        string path = Path.Combine(entry.FullPath, "guitar.wav");
        File.WriteAllBytes(path, new byte[100]);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));

        Assert.IsFalse(cache.TryGet(StemPeaksCache.KeyOf(entry, track)!.Value, out _));
    }

    [TestMethod]
    public void AFileOfAnotherLengthIsNotFound()
    {
        var (entry, track) = FolderSong("guitar.wav", 100);
        string path = Path.Combine(entry.FullPath, "guitar.wav");
        var written = File.GetLastWriteTimeUtc(path);
        var cache = new StemPeaksCache(1024 * 1024);
        cache.Add(StemPeaksCache.KeyOf(entry, track)!.Value, Peaks(1000));

        File.WriteAllBytes(path, new byte[200]);
        File.SetLastWriteTimeUtc(path, written);

        Assert.IsFalse(cache.TryGet(StemPeaksCache.KeyOf(entry, track)!.Value, out _));
    }

    [TestMethod]
    public void TracksAreKnownByTheFileTheyAreReadFrom()
    {
        var (entry, track) = FolderSong("guitar.wav", 100);
        string staged = Path.Combine(_root.FullName, "My Take.wav");
        File.WriteAllBytes(staged, new byte[50]);
        string sng = Path.Combine(_root.FullName, "Song.sng");
        File.WriteAllBytes(sng, new byte[300]);

        var stagedKey = StemPeaksCache.KeyOf(entry, track with { StagedSourcePath = staged });
        var sngKey = StemPeaksCache.KeyOf(new SongEntry(SongEntryKind.SngFile, sng, "Song.sng", "Song"), track);

        Assert.AreEqual(Path.GetFullPath(Path.Combine(entry.FullPath, "guitar.wav")), StemPeaksCache.KeyOf(entry, track)!.Value.FilePath);
        Assert.AreEqual(staged, stagedKey!.Value.FilePath);
        Assert.AreEqual(50, stagedKey.Value.Length);
        Assert.AreEqual(sng, sngKey!.Value.FilePath);
        Assert.AreEqual("guitar.wav", sngKey.Value.StoredName);
    }

    [TestMethod]
    public void AMissingFileHasNoKey()
    {
        var entry = new SongEntry(SongEntryKind.SongFolder, _root.FullName, "Song", "Song");

        Assert.IsNull(StemPeaksCache.KeyOf(entry, new SongTrack("drums.wav", "drums", null)));
    }

    [TestMethod]
    public void TheLeastRecentlyUsedAreDroppedPastTheCapacity()
    {
        var a = Key("a");
        var b = Key("b");
        var c = Key("c");
        var peaks = Peaks(1000);
        var cache = new StemPeaksCache(peaks.SizeBytes * 2);
        cache.Add(a, peaks);
        cache.Add(b, Peaks(1000));

        // Using a makes b the oldest.
        Assert.IsTrue(cache.TryGet(a, out _));
        cache.Add(c, Peaks(1000));

        Assert.IsTrue(cache.TryGet(a, out _));
        Assert.IsFalse(cache.TryGet(b, out _));
        Assert.IsTrue(cache.TryGet(c, out _));
        Assert.AreEqual(peaks.SizeBytes * 2, cache.SizeBytes);
    }

    [TestMethod]
    public void AddingAgainReplacesThePeaks()
    {
        var key = Key("a");
        var cache = new StemPeaksCache(1024 * 1024);
        var replacement = Peaks(500);
        cache.Add(key, Peaks(1000));

        cache.Add(key, replacement);

        Assert.IsTrue(cache.TryGet(key, out var found));
        Assert.AreSame(replacement, found);
        Assert.AreEqual(replacement.SizeBytes, cache.SizeBytes);
    }

    [TestMethod]
    public void PeaksLargerThanTheCacheArentKept()
    {
        var cache = new StemPeaksCache(16);

        cache.Add(Key("a"), Peaks(1000));

        Assert.IsFalse(cache.TryGet(Key("a"), out _));
        Assert.AreEqual(0, cache.SizeBytes);
    }

    [TestMethod]
    public void PeaksTakeRoughlyTheirSamplesSize()
    {
        // One second: 50 buckets of three floats (low, high and loudness), and 2000 samples of two bytes.
        var peaks = WaveformPeaks.Compute("x", new TestAudio.FakeSource(48000, 2, 48000, (_, _) => 0.5f), TestContext.CancellationToken);

        Assert.AreEqual((50 * 3 * sizeof(float)) + (2000 * 2), peaks.SizeBytes);
    }

    private (SongEntry Entry, SongTrack Track) FolderSong(string name, int bytes)
    {
        string folder = Directory.CreateDirectory(Path.Combine(_root.FullName, "Song")).FullName;
        File.WriteAllBytes(Path.Combine(folder, name), new byte[bytes]);
        return (new SongEntry(SongEntryKind.SongFolder, folder, "Song", "Song"), new SongTrack(name, Path.GetFileNameWithoutExtension(name), null));
    }

    private static StemPeaksKey Key(string name) => new(Path.Combine("C:\\songs", name), name, 100, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

    private static StemPeaks Peaks(int frames) =>
        WaveformPeaks.Compute("stem", new TestAudio.FakeSource(1000, 1, frames, (_, _) => 0.5f));

    public TestContext TestContext { get; set; }
}
