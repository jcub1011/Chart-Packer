using ChartPacker.Core.Scanning;
using ChartPacker.Core.Tests.Sng;

namespace ChartPacker.Core.Tests;

[TestClass]
public sealed class SongSummaryReaderTests
{
    private DirectoryInfo _root = null!;

    [TestInitialize]
    public void CreateRoot() => _root = Directory.CreateTempSubdirectory("chartpacker-summary-");

    [TestCleanup]
    public void DeleteRoot() => _root.Delete(recursive: true);

    [TestMethod]
    public async Task SngGivesItsMetadataAndFileSize()
    {
        byte[] bytes = new SngBuilder()
            .AddMetadata("name", "Song")
            .AddMetadata("artist", "Band")
            .AddFile("notes.chart", new byte[42])
            .Build();
        string path = Path.Combine(_root.FullName, "Song.sng");
        File.WriteAllBytes(path, bytes);

        var summary = await SongSummaryReader.ReadAsync(new SongEntry(SongEntryKind.SngFile, path, "Song.sng", "Song"));

        CollectionAssert.AreEqual(new[] { KeyValuePair.Create("name", "Song"), KeyValuePair.Create("artist", "Band") }, summary.Pairs.ToArray());
        Assert.AreEqual((ulong)bytes.Length, summary.TotalSize);
    }

    [TestMethod]
    public async Task SngWithErrorsStillGivesWhatWasRead()
    {
        byte[] bytes = new SngBuilder { FileCount = 99 }.AddMetadata("name", "Song").Build();
        string path = Path.Combine(_root.FullName, "Broken.sng");
        File.WriteAllBytes(path, bytes);

        var summary = await SongSummaryReader.ReadAsync(new SongEntry(SongEntryKind.SngFile, path, "Broken.sng", "Broken"));

        Assert.AreEqual("Song", summary.Pairs.Single().Value);
        Assert.AreEqual((ulong)bytes.Length, summary.TotalSize);
    }

    [TestMethod]
    public async Task FolderSumsEveryFileAndReadsItsSongIni()
    {
        string folder = CreateFolder("Song");
        Write(folder, "Song.INI", "[song]\nname = Song\ncharter = Someone\n");
        Write(folder, "notes.chart", new string('x', 100));
        Write(folder, "stems/guitar.ogg", new string('x', 50));
        // A song.ini in a subfolder belongs to something else.
        Write(folder, "stems/song.ini", "[song]\nname = Other\n");

        var summary = await SongSummaryReader.ReadAsync(Folder(folder));

        CollectionAssert.AreEqual(new[] { KeyValuePair.Create("name", "Song"), KeyValuePair.Create("charter", "Someone") }, summary.Pairs.ToArray());
        ulong expected = (ulong)Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        Assert.AreEqual(expected, summary.TotalSize);
    }

    [TestMethod]
    public async Task FolderWithoutSongIniHasNoPairs()
    {
        string folder = CreateFolder("Song");
        Write(folder, "stems/song.ini", "[song]\nname = Other\n");

        var summary = await SongSummaryReader.ReadAsync(Folder(folder));

        Assert.IsEmpty(summary.Pairs);
        Assert.AreEqual((ulong)new FileInfo(Path.Combine(folder, "stems", "song.ini")).Length, summary.TotalSize);
    }

    [TestMethod]
    public async Task SongIniWithoutSongSectionHasNoPairs()
    {
        string folder = CreateFolder("Song");
        Write(folder, "song.ini", "[other]\nname = Song\n");

        Assert.IsEmpty((await SongSummaryReader.ReadAsync(Folder(folder))).Pairs);
    }

    [TestMethod]
    public async Task MissingFolderThrows() =>
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => SongSummaryReader.ReadAsync(Folder(Path.Combine(_root.FullName, "nope"))));

    [TestMethod]
    public async Task CancelledReadThrows()
    {
        string folder = CreateFolder("Song");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => SongSummaryReader.ReadAsync(Folder(folder), cts.Token));
    }

    private string CreateFolder(string name) => Directory.CreateDirectory(Path.Combine(_root.FullName, name)).FullName;

    private SongEntry Folder(string path) =>
        new(SongEntryKind.SongFolder, path, Path.GetRelativePath(_root.FullName, path), Path.GetFileName(path));

    private static void Write(string folder, string relative, string text)
    {
        string path = Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }
}
