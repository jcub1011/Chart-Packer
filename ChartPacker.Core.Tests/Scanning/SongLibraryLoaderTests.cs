using ChartPacker.Core.Scanning;
using ChartPacker.Core.Tests.Sng;

namespace ChartPacker.Core.Tests.Scanning;

[TestClass]
public sealed class SongLibraryLoaderTests
{
    private DirectoryInfo _root = null!;

    [TestInitialize]
    public void CreateRoot() => _root = Directory.CreateTempSubdirectory("chartpacker-load-");

    [TestCleanup]
    public void DeleteRoot() => _root.Delete(recursive: true);

    [TestMethod]
    [DataRow(1)]
    [DataRow(4)]
    [DataRow(int.MaxValue)]
    public async Task ReportsEverySongOnceWithItsMetadata(int parallelism)
    {
        for (int i = 0; i < 40; i++)
            WriteSng($"f{i % 5}/song{i:D2}.sng", $"Song {i}");
        WriteText("Folder Song/song.ini", "[song]\nname = Folder Song\n");

        var reports = new List<LibraryLoadProgress>();
        int total = await SongLibraryLoader.LoadAsync(_root.FullName, 2, parallelism, new SyncProgress(reports.Add));

        var songs = reports.SelectMany(r => r.Songs).ToList();
        var expected = SongLibraryScanner.Enumerate(_root.FullName, 2).ToList();
        Assert.AreEqual(41, total);
        CollectionAssert.AreEqual(expected, songs.OrderBy(s => s.Position).Select(s => s.Entry).ToList());
        Assert.IsTrue(songs.All(s => s.Summary is not null));
        Assert.AreEqual("Folder Song", songs.Single(s => s.Entry.Kind == SongEntryKind.SongFolder).Summary!.Pairs.Single().Value);
        Assert.AreEqual("Song 7", songs.Single(s => s.Entry.DisplayName == "song07").Summary!.Pairs.Single().Value);
    }

    [TestMethod]
    public async Task LastReportHasTheFinalCounts()
    {
        for (int i = 0; i < 10; i++)
            WriteSng($"song{i}.sng", $"Song {i}");

        var reports = new List<LibraryLoadProgress>();
        await SongLibraryLoader.LoadAsync(_root.FullName, 2, 4, new SyncProgress(reports.Add));

        var last = reports[^1];
        Assert.IsTrue(last.SearchComplete);
        Assert.AreEqual(10, last.Found);
        Assert.AreEqual(10, last.Read);
    }

    [TestMethod]
    public async Task CountsNeverRunAhead()
    {
        for (int i = 0; i < 30; i++)
            WriteSng($"f{i % 3}/song{i}.sng", $"Song {i}");

        var reports = new List<LibraryLoadProgress>();
        await SongLibraryLoader.LoadAsync(_root.FullName, 2, 4, new SyncProgress(reports.Add));

        int read = 0;
        foreach (var report in reports)
        {
            read += report.Songs.Count;
            Assert.AreEqual(read, report.Read);
            Assert.IsLessThanOrEqualTo(report.Found, report.Read);
        }
    }

    [TestMethod]
    public async Task ReportsComeOneAtATime()
    {
        for (int i = 0; i < 30; i++)
            WriteSng($"f{i % 3}/song{i}.sng", $"Song {i}");

        int inside = 0;
        bool overlapped = false;
        await SongLibraryLoader.LoadAsync(_root.FullName, 2, int.MaxValue, new SyncProgress(_ =>
        {
            if (Interlocked.Increment(ref inside) > 1)
                overlapped = true;
            Thread.Sleep(5);
            Interlocked.Decrement(ref inside);
        }));

        Assert.IsFalse(overlapped);
    }

    [TestMethod]
    public async Task EmptyFolderGivesOneFinalReport()
    {
        var reports = new List<LibraryLoadProgress>();
        int total = await SongLibraryLoader.LoadAsync(_root.FullName, 2, 4, new SyncProgress(reports.Add));

        Assert.AreEqual(0, total);
        Assert.IsTrue(reports[^1].SearchComplete);
        Assert.IsTrue(reports.All(r => r.Songs.Count == 0));
    }

    [TestMethod]
    public void MissingRootThrows() =>
        Assert.ThrowsExactly<DirectoryNotFoundException>(
            () => SongLibraryLoader.LoadAsync(Path.Combine(_root.FullName, "nope"), 2, 4, new SyncProgress(_ => { })));

    [TestMethod]
    public void ParallelismBelowOneThrows() =>
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => SongLibraryLoader.LoadAsync(_root.FullName, 2, 0, new SyncProgress(_ => { })));

    [TestMethod]
    public async Task CancelledLoadThrows()
    {
        WriteSng("a.sng", "A");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => SongLibraryLoader.LoadAsync(_root.FullName, 2, 4, new SyncProgress(_ => { }), cts.Token));
    }

    private void WriteSng(string relative, string name) =>
        Write(relative, new SngBuilder().AddMetadata("name", name).Build());

    private void WriteText(string relative, string text) => Write(relative, System.Text.Encoding.UTF8.GetBytes(text));

    private void Write(string relative, byte[] bytes)
    {
        string path = Path.Combine(_root.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    /// <summary>Reports on the calling thread, unlike <see cref="Progress{T}"/>, so tests see every report before the load completes.</summary>
    private sealed class SyncProgress(Action<LibraryLoadProgress> onReport) : IProgress<LibraryLoadProgress>
    {
        public void Report(LibraryLoadProgress value) => onReport(value);
    }
}
