using ChartPacker.Core.Charts;
using ChartPacker.Core.Metadata;
using ChartPacker.Core.Scanning;

namespace ChartPacker.Core.Tests.Metadata;

[TestClass]
public sealed class DraftChartTests
{
    private DirectoryInfo _root = null!;

    [TestInitialize]
    public void CreateRoot() => _root = Directory.CreateTempSubdirectory("chartpacker-draftchart-");

    [TestCleanup]
    public void DeleteRoot() => _root.Delete(recursive: true);

    [TestMethod]
    public void TheLastStagedChartWins()
    {
        var draft = new MetadataDraft();
        draft.AddFile(new AddedFile("notes.chart", "a.chart", 1));
        draft.AddFile(new AddedFile("song.ogg", "s.ogg", 1));
        draft.AddFile(new AddedFile("notes.mid", "b.mid", 1));

        Assert.AreEqual(new DraftChart("notes.mid", "b.mid"), DraftChart.Of(draft, ["notes.chart"]));
    }

    [TestMethod]
    public void AStoredChartTheDraftRemovesIsSkipped()
    {
        var draft = new MetadataDraft();
        draft.RemoveFile("NOTES.MID");

        Assert.AreEqual(new DraftChart("notes.chart", null), DraftChart.Of(draft, ["notes.mid", "notes.chart", "song.ogg"]));
    }

    [TestMethod]
    public void NoChartIsNull()
    {
        var draft = new MetadataDraft();
        draft.RemoveFile("notes.chart");

        Assert.IsNull(DraftChart.Of(draft, ["notes.chart", "song.ogg"]));
        Assert.IsNull(DraftChart.Of(new MetadataDraft(), []));
    }

    [TestMethod]
    public async Task ReadsTheStagedFileOrTheStoredOne()
    {
        string folder = _root.CreateSubdirectory("Song").FullName;
        File.WriteAllBytes(Path.Combine(folder, "notes.chart"), [1, 2]);
        string staged = Path.Combine(_root.FullName, "other.mid");
        File.WriteAllBytes(staged, [3]);
        var entry = new SongEntry(SongEntryKind.SongFolder, folder, "Song", "Song");

        CollectionAssert.AreEqual(new byte[] { 1, 2 }, await new DraftChart("notes.chart", null).ReadAsync(entry));
        CollectionAssert.AreEqual(new byte[] { 3 }, await new DraftChart("notes.mid", staged).ReadAsync(entry));
    }

    [TestMethod]
    public async Task AStagedChartOverTheLimitReadsAsNull()
    {
        string staged = Path.Combine(_root.FullName, "huge.mid");
        using (var stream = File.Create(staged))
            stream.SetLength(ChartReader.MaxBytes + 1);
        var entry = new SongEntry(SongEntryKind.SongFolder, _root.FullName, "Song", "Song");

        Assert.IsNull(await new DraftChart("notes.mid", staged).ReadAsync(entry));
    }
}
