using System.Text;
using ChartPacker.Core.Charting;
using ChartPacker.Core.Charts;
using ChartPacker.Core.Scanning;
using ChartPacker.Core.Sng;

namespace ChartBuilder.Tests.Charting;

[TestClass]
public sealed class SongOpenerTests
{
    private const string Chart = """
        [Song]
        {
          Name = "Converted"
          Offset = 0.25
          Resolution = 192
        }
        [SyncTrack]
        {
          0 = TS 4
          0 = B 120000
        }
        [Events]
        {
        }
        [ExpertSingle]
        {
          0 = N 0 0
          192 = N 1 0
        }
        """;

    private string _root = "";

    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void CreateRoot() => _root = Directory.CreateTempSubdirectory("chartbuilder-open-").FullName;

    [TestCleanup]
    public void DeleteRoot() => Directory.Delete(_root, recursive: true);

    [TestMethod]
    public async Task AFolderOpensWithItsMetadataAndItsMidiChart()
    {
        string folder = Song("Song", ("song.ini", Ini("name = Title", "artist = Artist", "delay = 40")), ("notes.mid", Midi()), ("guitar.ogg", [1]));

        var result = await SongOpener.OpenAsync(SongOpener.EntryOf(folder)!, TestContext.CancellationToken);

        Assert.IsNull(result.Problem);
        var song = result.Song!;
        Assert.AreEqual(SongEntryKind.SongFolder, song.Entry.Kind);
        Assert.AreEqual("notes.mid", song.ChartName);
        Assert.AreEqual("Artist – Title", song.Title);
        Assert.AreEqual(TimeSpan.FromMilliseconds(40), song.Delay);
        Assert.IsNull(song.Import);
        Assert.IsNotNull(song.Chart.PartOf(ChartInstrument.Guitar));
        CollectionAssert.AreEquivalent(new[] { "song.ini", "notes.mid", "guitar.ogg" }, song.StoredNames.ToArray());
    }

    [TestMethod]
    public async Task ASngOpensWithItsMetadataAndItsChart()
    {
        string sng = Path.Combine(_root, "Song.sng");
        string midi = Path.Combine(_root, "notes.mid");
        File.WriteAllBytes(midi, Midi());
        await SngWriter.WriteAsync(
            sng, Enumerable.Range(1, SngMask.MaskSize).Select(b => (byte)b).ToArray(), [KeyValuePair.Create("name", "Packed")],
            [new SngSourceFile("notes.mid", midi, new FileInfo(midi).Length)], overwrite: false, cancellationToken: TestContext.CancellationToken);

        var result = await SongOpener.OpenAsync(SongOpener.EntryOf(sng)!, TestContext.CancellationToken);

        Assert.IsNull(result.Problem);
        Assert.AreEqual(SongEntryKind.SngFile, result.Song!.Entry.Kind);
        Assert.AreEqual("Packed", result.Song.Title);
        Assert.IsNotNull(result.Song.Chart.PartOf(ChartInstrument.Guitar));
    }

    [TestMethod]
    public async Task ADotChartIsConvertedAndItsOffsetIsTheDelayWhenSongIniHasNone()
    {
        string folder = Song("Song", ("notes.chart", Encoding.UTF8.GetBytes(Chart)));

        var result = await SongOpener.OpenAsync(SongOpener.EntryOf(folder)!, TestContext.CancellationToken);

        Assert.IsNull(result.Problem);
        var song = result.Song!;
        Assert.AreEqual("notes.chart", song.ChartName);
        Assert.IsNotNull(song.Import);
        Assert.AreEqual(TimeSpan.FromSeconds(0.25), song.Import.Offset);
        Assert.AreEqual(TimeSpan.FromSeconds(0.25), song.Delay);
        Assert.AreEqual("Song", song.Title, "With no metadata, the folder's name.");
        Assert.AreEqual(2, song.Chart.PartOf(ChartInstrument.Guitar)!.Tracks.Single(t => t.Difficulty == ChartDifficulty.Expert).Notes.Count);
    }

    [TestMethod]
    public async Task NotesMidIsOpenedRatherThanNotesChart()
    {
        string folder = Song("Song", ("notes.chart", Encoding.UTF8.GetBytes(Chart)), ("notes.mid", Midi()));

        var result = await SongOpener.OpenAsync(SongOpener.EntryOf(folder)!, TestContext.CancellationToken);

        Assert.AreEqual("notes.mid", result.Song!.ChartName);
        Assert.IsNull(result.Song.Import);
    }

    [TestMethod]
    public async Task ASongWithNoChartOrABrokenOneSaysWhy()
    {
        string none = Song("None", ("song.ini", Ini("name = None")));
        string broken = Song("Broken", ("notes.mid", "not a midi file"u8.ToArray()));

        var noChart = await SongOpener.OpenAsync(SongOpener.EntryOf(none)!, TestContext.CancellationToken);
        var unreadable = await SongOpener.OpenAsync(SongOpener.EntryOf(broken)!, TestContext.CancellationToken);
        var missing = await SongOpener.OpenAsync(new SongEntry(SongEntryKind.SongFolder, Path.Combine(_root, "Gone"), ".", "Gone"), TestContext.CancellationToken);

        Assert.AreEqual(SongOpenProblemKind.NoChart, noChart.Problem!.Kind);
        Assert.AreEqual(SongOpenProblemKind.Unreadable, unreadable.Problem!.Kind);
        Assert.IsFalse(string.IsNullOrEmpty(unreadable.Problem.Detail));
        Assert.AreEqual(SongOpenProblemKind.Missing, missing.Problem!.Kind);
        Assert.IsNull(missing.Song);
    }

    [TestMethod]
    public void AFileInASongFolderStandsForTheFolder()
    {
        string folder = Song("Song", ("song.ini", Ini("name = Title")));

        var entry = SongOpener.EntryOf(Path.Combine(folder, "song.ini"));

        Assert.AreEqual(SongEntryKind.SongFolder, entry!.Kind);
        Assert.AreEqual(folder, entry.FullPath);
        Assert.AreEqual("Song", entry.DisplayName);
        Assert.AreEqual(folder, SongOpener.EntryOf(folder + Path.DirectorySeparatorChar)!.FullPath);
        Assert.IsNull(SongOpener.EntryOf(Path.Combine(_root, "Gone")));
    }

    private string Song(string name, params (string Name, byte[] Bytes)[] files)
    {
        string folder = Path.Combine(_root, name);
        Directory.CreateDirectory(folder);
        foreach (var (file, bytes) in files)
            File.WriteAllBytes(Path.Combine(folder, file), bytes);
        return folder;
    }

    private static byte[] Ini(params string[] lines) => Encoding.UTF8.GetBytes(string.Join("\n", ["[song]", .. lines]));

    private static byte[] Midi() =>
        NewChartMidi.Build(new NewChartSettings("Song", 480, 120, 4, 4, new HashSet<ChartInstrument> { ChartInstrument.Guitar }, DrumsVariant.Pro));
}
