using ChartPacker.Core.Library;
using ChartPacker.Core.Scanning;
using ChartPacker.Core.Sng;
using ChartPacker.Core.Tests.Charts;
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
    public async Task FolderGivesItsTopLevelNamesAndChart()
    {
        string folder = CreateFolder("Song");
        Write(folder, "notes.chart", ChartReaderTests.Chart);
        Write(folder, "guitar.ogg", "x");
        Write(folder, "drums_1.ogg", "x");
        Write(folder, "video.webm", "x");
        Write(folder, "album.png", "x");
        Write(folder, "stems/bass.ogg", "x");

        var summary = await SongSummaryReader.ReadAsync(Folder(folder));

        CollectionAssert.AreEquivalent(new[] { "album.png", "drums_1.ogg", "guitar.ogg", "notes.chart", "video.webm" }, summary.StoredNames.ToArray());
        Assert.AreEqual(LibraryDifficulties.Hard | LibraryDifficulties.Expert, summary.Chart?.Difficulties);
        Assert.IsTrue(summary.Chart!.Instruments.HasFlag(LibraryInstruments.Drums));
    }

    [TestMethod]
    public async Task SngGivesItsTopLevelNamesAndChart()
    {
        var builder = new SngBuilder();
        // The builder stores data as given, so the chart is masked the way a real .sng stores it.
        byte[] midi = new MidiBuilder().Track().Tempo(0, 120).Track("PART VOCALS").Note(0, 60).Build();
        SngMask.Apply(midi, builder.Mask);
        byte[] bytes = builder
            .AddMetadata("song_length", "1000")
            .AddFile("notes.mid", midi)
            .AddFile("vocals.ogg", new byte[4])
            .AddFile("extras/readme.txt", new byte[4])
            .Build();
        string path = Path.Combine(_root.FullName, "Song.sng");
        File.WriteAllBytes(path, bytes);

        var summary = await SongSummaryReader.ReadAsync(new SongEntry(SongEntryKind.SngFile, path, "Song.sng", "Song"));

        CollectionAssert.AreEquivalent(new[] { "notes.mid", "vocals.ogg" }, summary.StoredNames.ToArray());
        Assert.AreEqual(new ChartContents(LibraryInstruments.Vocals, LibraryDifficulties.None), summary.Chart);
    }

    [TestMethod]
    public async Task SongWithoutChartHasNone()
    {
        string folder = CreateFolder("Song");
        Write(folder, "song.ogg", "x");

        Assert.AreEqual(ChartContents.None, (await SongSummaryReader.ReadAsync(Folder(folder))).Chart);
    }

    [TestMethod]
    public async Task MalformedChartIsUnknown()
    {
        string folder = CreateFolder("Song");
        Write(folder, "notes.mid", "not a midi file");

        Assert.IsNull((await SongSummaryReader.ReadAsync(Folder(folder))).Chart);
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
