using ChartShared.Core.Library;
using ChartShared.Core.Scanning;
using ChartShared.Core.Sng;
using ChartShared.Core.Tests.Charts;
using ChartShared.Core.Tests.Sng;

namespace ChartShared.Core.Tests;

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

        var summary = await SongSummaryReader.ReadAsync(new SongEntry(SongEntryKind.SngFile, path, "Song.sng", "Song"), TestContext.CancellationToken);

        Assert.AreSequenceEqual([KeyValuePair.Create("name", "Song"), KeyValuePair.Create("artist", "Band")], [.. summary.Pairs]);
        Assert.AreEqual((ulong)bytes.Length, summary.TotalSize);
    }

    [TestMethod]
    public async Task SngWithErrorsStillGivesWhatWasRead()
    {
        byte[] bytes = new SngBuilder { FileCount = 99 }.AddMetadata("name", "Song").Build();
        string path = Path.Combine(_root.FullName, "Broken.sng");
        File.WriteAllBytes(path, bytes);

        var summary = await SongSummaryReader.ReadAsync(new SongEntry(SongEntryKind.SngFile, path, "Broken.sng", "Broken"), TestContext.CancellationToken);

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

        var summary = await SongSummaryReader.ReadAsync(Folder(folder), TestContext.CancellationToken);

        Assert.AreSequenceEqual([KeyValuePair.Create("name", "Song"), KeyValuePair.Create("charter", "Someone")], [.. summary.Pairs]);
        ulong expected = (ulong)Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        Assert.AreEqual(expected, summary.TotalSize);
    }

    [TestMethod]
    public async Task FolderWithoutSongIniHasNoPairs()
    {
        string folder = CreateFolder("Song");
        Write(folder, "stems/song.ini", "[song]\nname = Other\n");

        var summary = await SongSummaryReader.ReadAsync(Folder(folder), TestContext.CancellationToken);

        Assert.IsEmpty(summary.Pairs);
        Assert.AreEqual((ulong)new FileInfo(Path.Combine(folder, "stems", "song.ini")).Length, summary.TotalSize);
    }

    [TestMethod]
    public async Task SongIniWithoutSongSectionHasNoPairs()
    {
        string folder = CreateFolder("Song");
        Write(folder, "song.ini", "[other]\nname = Song\n");

        Assert.IsEmpty((await SongSummaryReader.ReadAsync(Folder(folder), TestContext.CancellationToken)).Pairs);
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

        var summary = await SongSummaryReader.ReadAsync(Folder(folder), TestContext.CancellationToken);

        Assert.AreSequenceEqual(["album.png", "drums_1.ogg", "guitar.ogg", "notes.chart", "video.webm"], [.. summary.StoredNames], Microsoft.VisualStudio.TestTools.UnitTesting.SequenceOrder.InAnyOrder);
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

        var summary = await SongSummaryReader.ReadAsync(new SongEntry(SongEntryKind.SngFile, path, "Song.sng", "Song"), TestContext.CancellationToken);

        Assert.AreSequenceEqual(["notes.mid", "vocals.ogg"], [.. summary.StoredNames], Microsoft.VisualStudio.TestTools.UnitTesting.SequenceOrder.InAnyOrder);
        Assert.AreEqual(new ChartContents(LibraryInstruments.Vocals, LibraryDifficulties.None), summary.Chart);
    }

    [TestMethod]
    public async Task SngChartLargerThanTheFirstReadIsUnmaskedWhole()
    {
        // Enough notes that the chart runs well past the index, with its first byte at an odd place in the mask's cycle.
        var midiBuilder = new MidiBuilder().Track().Tempo(0, 120).Track("PART GUITAR");
        for (int i = 0; i < 20_000; i++)
            midiBuilder.Note(i * 120, 96 + (i % 5));
        byte[] midi = midiBuilder.Build();
        Assert.IsGreaterThan(SngReader.PrefixSize, midi.Length);

        var builder = new SngBuilder();
        byte[] masked = (byte[])midi.Clone();
        SngMask.Apply(masked, builder.Mask);
        byte[] bytes = builder.AddMetadata("song_length", "1000").AddFile("a.bin", new byte[37]).AddFile("notes.mid", masked).Build();
        string path = Path.Combine(_root.FullName, "Song.sng");
        File.WriteAllBytes(path, bytes);

        var summary = await SongSummaryReader.ReadAsync(new SongEntry(SongEntryKind.SngFile, path, "Song.sng", "Song"), TestContext.CancellationToken);

        Assert.AreEqual(ChartContents.Read(midi, "notes.mid"), summary.Chart);
        Assert.AreEqual(new ChartContents(LibraryInstruments.Guitar, LibraryDifficulties.Expert), summary.Chart);
    }

    [TestMethod]
    public async Task SngChartRunningPastTheEndIsUnknown()
    {
        var builder = new SngBuilder().AddMetadata("song_length", "1000").AddFile("notes.mid", new byte[10]);
        builder.LengthOverrides[0] = 1_000;
        string path = Path.Combine(_root.FullName, "Song.sng");
        File.WriteAllBytes(path, builder.Build());

        var summary = await SongSummaryReader.ReadAsync(new SongEntry(SongEntryKind.SngFile, path, "Song.sng", "Song"), TestContext.CancellationToken);

        Assert.IsNull(summary.Chart);
        Assert.AreEqual("1000", summary.Pairs.Single().Value);
    }

    [TestMethod]
    public async Task SongWithoutChartHasNone()
    {
        string folder = CreateFolder("Song");
        Write(folder, "song.ogg", "x");

        Assert.AreEqual(ChartContents.None, (await SongSummaryReader.ReadAsync(Folder(folder), TestContext.CancellationToken)).Chart);
    }

    [TestMethod]
    public async Task MalformedChartIsUnknown()
    {
        string folder = CreateFolder("Song");
        Write(folder, "notes.mid", "not a midi file");

        Assert.IsNull((await SongSummaryReader.ReadAsync(Folder(folder), TestContext.CancellationToken)).Chart);
    }

    [TestMethod]
    public async Task MissingFolderThrows() =>
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => SongSummaryReader.ReadAsync(Folder(Path.Combine(_root.FullName, "nope")), TestContext.CancellationToken));

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

    public TestContext TestContext { get; set; }
}
