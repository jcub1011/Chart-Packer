using ChartShared.Audio.Waveforms;
using ChartShared.Core.Scanning;

namespace ChartShared.Core.Tests.Audio;

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
        // A minute, sounding both ways throughout: 3000 buckets of three half-precision floats (low, high and loudness), in
        // chunks of 1024, and 120000 samples for mixing of two bytes, in chunks of 8192; and a pointer to each chunk.
        var peaks = WaveformPeaks.Compute("x", new TestAudio.FakeSource(1000, 1, 60000, (n, _) => n % 2 == 0 ? 0.5f : -0.5f), TestContext.CancellationToken);

        long buckets = 3 * ((3 * 1024 * 2) + (3 * IntPtr.Size));
        long signal = (15 * 8192 * 2) + (15 * IntPtr.Size);
        Assert.AreEqual(buckets + signal, peaks.SizeBytes);
    }

    [TestMethod]
    public void SilenceTakesNoRoom()
    {
        // A minute, silent but for its last two seconds.
        static TestAudio.FakeSource Mostly(float level) => new(1000, 1, 60000, (n, _) => n >= 58000 ? 0.5f : level);
        var loud = WaveformPeaks.Compute("loud", Mostly(0.25f), TestContext.CancellationToken);
        var quiet = WaveformPeaks.Compute("quiet", Mostly(0f), TestContext.CancellationToken);

        Assert.IsLessThan(loud.SizeBytes / 5, quiet.SizeBytes);
        Assert.AreEqual(0f, quiet.Max.Take(2900).Max());
        Assert.AreEqual(0.5f, quiet.Max.Skip(2900).Min());
        var waveform = WaveformPeaks.Of(quiet);
        Assert.AreEqual((0.0, 0.0, 0.0), waveform.LevelsOf(0, 2900));
        Assert.AreEqual(0.5, waveform.LevelsOf(0, 3000).High);
        Assert.AreEqual(0.5, waveform.LevelsOf(2900, 3000).Rms, 1e-3);
    }

    [TestMethod]
    public void SmallerTheLeastRecentlyUsedGoAndLargerTheyStay()
    {
        var cache = new StemPeaksCache(long.MaxValue);
        var peaks = WaveformPeaks.Compute("x", new TestAudio.FakeSource(1000, 1, 1000, (_, _) => 0.5f), TestContext.CancellationToken);
        var first = new StemPeaksKey("C:\\songs\\a.wav", "a.wav", 1, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var second = first with { FilePath = "C:\\songs\\b.wav", StoredName = "b.wav" };
        cache.Add(first, peaks);
        cache.Add(second, peaks);

        cache.CapacityBytes = peaks.SizeBytes * 3;
        Assert.IsTrue(cache.TryGet(second, out _));
        Assert.IsTrue(cache.TryGet(first, out _));

        // Looked at last, the first stays.
        cache.CapacityBytes = peaks.SizeBytes;
        Assert.IsTrue(cache.TryGet(first, out _));
        Assert.IsFalse(cache.TryGet(second, out _));
        Assert.AreEqual(peaks.SizeBytes, cache.SizeBytes);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => cache.CapacityBytes = -1);
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
