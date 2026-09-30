using ChartPacker.Core.Scanning;

namespace ChartPacker.Core.Tests;

[TestClass]
public sealed class SongLibraryScannerTests
{
    private DirectoryInfo _root = null!;

    [TestInitialize]
    public void CreateRoot() => _root = Directory.CreateTempSubdirectory("chartpacker-scan-");

    [TestCleanup]
    public void DeleteRoot() => _root.Delete(recursive: true);

    [TestMethod]
    public void FindsSngFilesDownToMaxDepth()
    {
        Touch("root.sng");
        Touch("a/one.sng");
        Touch("a/b/two.sng");
        Touch("a/b/c/three.sng");

        CollectionAssert.AreEqual(
            new[] { "root.sng", P("a/one.sng"), P("a/b/two.sng") },
            RelativePaths(maxDepth: 2));
    }

    [TestMethod]
    public void MaxDepthZeroExaminesOnlyTheRoot()
    {
        Touch("root.sng");
        Touch("a/one.sng");
        Touch("b/song.ini");

        CollectionAssert.AreEqual(new[] { "root.sng" }, RelativePaths(maxDepth: 0));
    }

    [TestMethod]
    public void SongFolderIsListedOnceAndNotDescended()
    {
        Touch("Artist - Title/song.ini");
        Touch("Artist - Title/notes.chart");
        Touch("Artist - Title/inner.sng");
        Touch("Artist - Title/stems/song.ini");

        var entries = SongLibraryScanner.Enumerate(_root.FullName, maxDepth: 2).ToList();

        Assert.HasCount(1, entries);
        Assert.AreEqual(SongEntryKind.SongFolder, entries[0].Kind);
        Assert.AreEqual("Artist - Title", entries[0].DisplayName);
        Assert.AreEqual("Artist - Title", entries[0].RelativePath);
        Assert.AreEqual(Path.Combine(_root.FullName, "Artist - Title"), entries[0].FullPath);
    }

    [TestMethod]
    public void RootThatIsASongFolderYieldsOneEntry()
    {
        Touch("song.ini");
        Touch("other.sng");
        Touch("sub/deeper.sng");

        var entries = SongLibraryScanner.Enumerate(_root.FullName, maxDepth: 2).ToList();

        Assert.HasCount(1, entries);
        Assert.AreEqual(SongEntryKind.SongFolder, entries[0].Kind);
        Assert.AreEqual(".", entries[0].RelativePath);
        Assert.AreEqual(_root.Name, entries[0].DisplayName);
    }

    [TestMethod]
    public void MatchingIgnoresCase()
    {
        Touch("Upper/SONG.INI");
        Touch("Loud.SNG");

        var entries = SongLibraryScanner.Enumerate(_root.FullName, maxDepth: 2).ToList();

        Assert.HasCount(2, entries);
        Assert.AreEqual(new SongEntry(SongEntryKind.SngFile, Path.Combine(_root.FullName, "Loud.SNG"), "Loud.SNG", "Loud"), entries[0]);
        Assert.AreEqual(SongEntryKind.SongFolder, entries[1].Kind);
        Assert.AreEqual("Upper", entries[1].RelativePath);
    }

    [TestMethod]
    public void IgnoresOtherFilesAndFoldersWithoutSongIni()
    {
        Touch("notes.chart");
        Touch("song.ogg");
        Touch("archive.sng.bak");
        Touch("Loose Chart/notes.chart");
        Touch("Loose Chart/song.ogg");
        Directory.CreateDirectory(Path.Combine(_root.FullName, "Empty"));

        Assert.IsEmpty(RelativePaths(maxDepth: 2));
    }

    [TestMethod]
    public void EmptyFolderYieldsNothing() => Assert.IsEmpty(RelativePaths(maxDepth: 2));

    [TestMethod]
    public void SkipsMacOsResourceForkFolder()
    {
        Touch("__MACOSX/._song.sng");
        Touch("__MACOSX/Song/song.ini");
        Touch("real.sng");

        CollectionAssert.AreEqual(new[] { "real.sng" }, RelativePaths(maxDepth: 2));
    }

    [TestMethod]
    public void OrderIsDepthFirstAndCaseInsensitive()
    {
        Touch("b/song.ini");
        Touch("A/z.sng");
        Touch("A/a.sng");
        Touch("A/Nested/song.ini");
        Touch("c.sng");
        Touch("B2.sng");

        CollectionAssert.AreEqual(
            new[] { "B2.sng", "c.sng", P("A/a.sng"), P("A/z.sng"), P("A/Nested"), "b" },
            RelativePaths(maxDepth: 2));
    }

    [TestMethod]
    public void MissingRootThrows()
    {
        string missing = Path.Combine(_root.FullName, "nope");

        Assert.ThrowsExactly<DirectoryNotFoundException>(() => SongLibraryScanner.Enumerate(missing, 2));
        Assert.ThrowsExactly<DirectoryNotFoundException>(
            () => SongLibraryScanner.ScanAsync(missing, 2, new Progress<IReadOnlyList<SongEntry>>()));
    }

    [TestMethod]
    public void NegativeMaxDepthThrows() =>
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => SongLibraryScanner.Enumerate(_root.FullName, -1));

    [TestMethod]
    public void CancelledEnumerateThrows()
    {
        Touch("a.sng");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsExactly<OperationCanceledException>(
            () => SongLibraryScanner.Enumerate(_root.FullName, 2, cts.Token).ToList());
    }

    [TestMethod]
    public async Task CancelledScanAsyncThrows()
    {
        Touch("a.sng");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => SongLibraryScanner.ScanAsync(_root.FullName, 2, new SyncProgress(_ => { }), cts.Token));
    }

    [TestMethod]
    public async Task ScanAsyncBatchesMatchEnumerate()
    {
        // More entries than one batch holds, so at least two reports are made.
        for (int i = 0; i < 300; i++)
            Touch($"f{i / 50}/song{i:D3}.sng");
        for (int i = 0; i < 20; i++)
            Touch($"f{i}/folder/song.ini");

        var batches = new List<IReadOnlyList<SongEntry>>();
        int total = await SongLibraryScanner.ScanAsync(_root.FullName, 2, new SyncProgress(batches.Add));

        var expected = SongLibraryScanner.Enumerate(_root.FullName, 2).ToList();
        Assert.HasCount(320, expected);
        Assert.AreEqual(expected.Count, total);
        Assert.IsGreaterThanOrEqualTo(2, batches.Count);
        Assert.IsTrue(batches.All(b => b.Count > 0));
        CollectionAssert.AreEqual(expected, batches.SelectMany(b => b).ToList());
    }

    private string[] RelativePaths(int maxDepth) =>
        [.. SongLibraryScanner.Enumerate(_root.FullName, maxDepth).Select(e => e.RelativePath)];

    private static string P(string relative) => relative.Replace('/', Path.DirectorySeparatorChar);

    private void Touch(string relative)
    {
        string path = Path.Combine(_root.FullName, P(relative));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, []);
    }

    /// <summary>Reports on the calling thread, unlike <see cref="Progress{T}"/>, so tests see every batch before the scan completes.</summary>
    private sealed class SyncProgress(Action<IReadOnlyList<SongEntry>> onReport) : IProgress<IReadOnlyList<SongEntry>>
    {
        public void Report(IReadOnlyList<SongEntry> value) => onReport(value);
    }
}
