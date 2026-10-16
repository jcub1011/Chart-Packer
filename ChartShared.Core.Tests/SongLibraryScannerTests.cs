using ChartShared.Core.Scanning;

namespace ChartShared.Core.Tests;

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

        Assert.AreSequenceEqual(
            ["root.sng", P("a/one.sng"), P("a/b/two.sng")], RelativePaths(maxDepth: 2));
    }

    [TestMethod]
    public void MaxDepthZeroExaminesOnlyTheRoot()
    {
        Touch("root.sng");
        Touch("a/one.sng");
        Touch("b/song.ini");

        Assert.AreSequenceEqual(["root.sng"], RelativePaths(maxDepth: 0));
    }

    [TestMethod]
    public void SongFolderIsListedOnceAndNotDescended()
    {
        Touch("Artist - Title/song.ini");
        Touch("Artist - Title/notes.chart");
        Touch("Artist - Title/inner.sng");
        Touch("Artist - Title/stems/song.ini");

        var entries = SongLibraryScanner.Enumerate(_root.FullName, maxDepth: 2, TestContext.CancellationToken).ToList();

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

        var entries = SongLibraryScanner.Enumerate(_root.FullName, maxDepth: 2, TestContext.CancellationToken).ToList();

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

        var entries = SongLibraryScanner.Enumerate(_root.FullName, maxDepth: 2, TestContext.CancellationToken).ToList();

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

        Assert.AreSequenceEqual(["real.sng"], RelativePaths(maxDepth: 2));
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

        Assert.AreSequenceEqual(
            ["B2.sng", "c.sng", P("A/a.sng"), P("A/z.sng"), P("A/Nested"), "b"], RelativePaths(maxDepth: 2));
    }

    [TestMethod]
    public void MissingRootThrows()
    {
        string missing = Path.Combine(_root.FullName, "nope");

        Assert.ThrowsExactly<DirectoryNotFoundException>(() => SongLibraryScanner.Enumerate(missing, 2, TestContext.CancellationToken));
        Assert.ThrowsExactly<DirectoryNotFoundException>(() => SongLibraryScanner.ScanAsync(missing, 2, 4, _ => { }, TestContext.CancellationToken));
    }

    [TestMethod]
    public void ParallelismBelowOneThrows() =>
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => SongLibraryScanner.ScanAsync(_root.FullName, 2, 0, _ => { }, TestContext.CancellationToken));

    [TestMethod]
    public void NegativeMaxDepthThrows() =>
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => SongLibraryScanner.Enumerate(_root.FullName, -1, TestContext.CancellationToken));

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
            () => SongLibraryScanner.ScanAsync(_root.FullName, 2, 4, _ => { }, cts.Token));
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(4)]
    [DataRow(int.MaxValue)]
    public async Task ScanAsyncInPositionOrderMatchesEnumerate(int parallelism)
    {
        for (int i = 0; i < 300; i++)
            Touch($"f{i / 50}/song{i:D3}.sng");
        for (int i = 0; i < 20; i++)
            Touch($"f{i}/folder/song.ini");
        Touch("b/song.ini");
        Touch("A/z.sng");
        Touch("A/a.sng");
        Touch("A/Nested/song.ini");
        Touch("A/Nested/Deeper/ignored.sng");
        Touch("c.sng");
        Touch("B2.sng");
        Touch("__MACOSX/._song.sng");
        Touch("x/y/z/too-deep.sng");

        var found = await ScanAllAsync(maxDepth: 2, parallelism);

        var expected = SongLibraryScanner.Enumerate(_root.FullName, 2, TestContext.CancellationToken).ToList();
        Assert.HasCount(326, expected);
        Assert.AreSequenceEqual(expected, [.. found.OrderBy(s => s.Position).Select(s => s.Entry)]);
    }

    [TestMethod]
    public async Task ScanAsyncGivesEachSongItsOwnPosition()
    {
        Touch("a.sng");
        Touch("b/c.sng");
        Touch("b/d/song.ini");

        var found = await ScanAllAsync(maxDepth: 2, parallelism: 4);

        Assert.HasCount(3, found);
        Assert.HasCount(3, found.Select(s => s.Position).Distinct().ToList());
    }

    [TestMethod]
    public async Task RootThatIsASongFolderIsAtTheRootPosition()
    {
        Touch("song.ini");
        Touch("sub/deeper.sng");

        var found = await ScanAllAsync(maxDepth: 2, parallelism: 4);

        Assert.AreEqual(ScanPosition.Root, found.Single().Position);
        Assert.AreEqual(".", found.Single().Entry.RelativePath);
    }

    private async Task<List<ScannedSong>> ScanAllAsync(int maxDepth, int parallelism)
    {
        var found = new System.Collections.Concurrent.ConcurrentBag<ScannedSong>();
        int total = await SongLibraryScanner.ScanAsync(_root.FullName, maxDepth, parallelism, found.Add, TestContext.CancellationToken);
        Assert.AreEqual(found.Count, total);
        return [.. found];
    }

    private string[] RelativePaths(int maxDepth) =>
        [.. SongLibraryScanner.Enumerate(_root.FullName, maxDepth, TestContext.CancellationToken).Select(e => e.RelativePath)];

    private static string P(string relative) => relative.Replace('/', Path.DirectorySeparatorChar);

    private void Touch(string relative)
    {
        string path = Path.Combine(_root.FullName, P(relative));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, []);
    }

    public TestContext TestContext { get; set; }
}
