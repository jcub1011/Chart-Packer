using ChartPacker.Core.Scanning;

namespace ChartPacker.Core.Tests;

[TestClass]
public sealed class SongFolderListingTests
{
    private DirectoryInfo _root = null!;

    [TestInitialize]
    public void CreateRoot() => _root = Directory.CreateTempSubdirectory("chartpacker-listing-");

    [TestCleanup]
    public void DeleteRoot()
    {
        foreach (var file in _root.EnumerateFiles("*", SearchOption.AllDirectories))
            file.Attributes = FileAttributes.Normal;
        _root.Delete(recursive: true);
    }

    [TestMethod]
    public void ListsFilesRecursivelyWithSlashPathsAndSizes()
    {
        Write("song.ini", 10);
        Write("notes.chart", 42);
        Write("stems/Guitar.ogg", 7);
        Write("stems/deep/extra.txt", 0);

        var files = SongFolderListing.List(_root.FullName, TestContext.CancellationToken);

        Assert.AreSequenceEqual(
            [
                new FolderFileEntry("notes.chart", 42),
                new FolderFileEntry("song.ini", 10),
                new FolderFileEntry("stems/deep/extra.txt", 0),
                new FolderFileEntry("stems/Guitar.ogg", 7),
            ], [.. files]);
    }

    [TestMethod]
    public void OrderIgnoresCase()
    {
        Write("b.txt", 1);
        Write("A.txt", 1);
        Write("C.txt", 1);

        Assert.AreSequenceEqual(["A.txt", "b.txt", "C.txt"], [.. SongFolderListing.List(_root.FullName, TestContext.CancellationToken).Select(f => f.RelativePath)]);
    }

    [TestMethod]
    public void IncludesHiddenFiles()
    {
        string path = Write("desktop.ini", 3);
        File.SetAttributes(path, FileAttributes.Hidden | FileAttributes.System);

        Assert.AreEqual("desktop.ini", SongFolderListing.List(_root.FullName, TestContext.CancellationToken).Single().RelativePath);
    }

    [TestMethod]
    public void StampedListingMatchesListingAndCarriesWriteTimes()
    {
        string chart = Write("notes.chart", 42);
        Write("stems/Guitar.ogg", 7);
        var written = new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(chart, written);

        var stamped = SongFolderListing.ListStamped(_root.FullName, TestContext.CancellationToken);

        Assert.AreSequenceEqual(
            [.. SongFolderListing.List(_root.FullName, TestContext.CancellationToken)], [.. stamped.Select(f => new FolderFileEntry(f.RelativePath, f.Length))]);
        Assert.AreEqual(written, stamped.Single(f => f.RelativePath == "notes.chart").LastWriteTimeUtc);
    }

    [TestMethod]
    public void EmptyFolderListsNothing() => Assert.IsEmpty(SongFolderListing.List(_root.FullName, TestContext.CancellationToken));

    [TestMethod]
    public void MissingFolderThrows() =>
        Assert.ThrowsExactly<DirectoryNotFoundException>(() => SongFolderListing.List(Path.Combine(_root.FullName, "nope"), TestContext.CancellationToken));

    [TestMethod]
    public async Task CancelledListAsyncThrows()
    {
        Write("a.txt", 1);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => SongFolderListing.ListAsync(_root.FullName, cts.Token));
    }

    private string Write(string relative, int size)
    {
        string path = Path.Combine(_root.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[size]);
        return path;
    }

    public TestContext TestContext { get; set; }
}
