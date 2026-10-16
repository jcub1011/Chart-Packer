using System.Text;
using ChartShared.Core.Ini;
using ChartShared.Core.Metadata;

namespace ChartShared.Core.Tests.Ini;

[TestClass]
public sealed class SongIniEditTests
{
    private DirectoryInfo _root = null!;

    [TestInitialize]
    public void CreateRoot() => _root = Directory.CreateTempSubdirectory("chartpacker-ini-edit-");

    [TestCleanup]
    public void DeleteRoot() => _root.Delete(recursive: true);

    [TestMethod]
    public void OnlyTheEditedLineChanges()
    {
        const string before = "; made by hand\n[song]\n  Name=Old   \nartist = Band\n\n[other]\nname = keep\n";

        string after = SongIni.ApplyEdits(before, Draft(d => d.Set("name", "New")));

        Assert.AreEqual("; made by hand\n[song]\n  Name = New\nartist = Band\n\n[other]\nname = keep\n", after);
    }

    [TestMethod]
    public void RemovedKeyLosesEveryLineInSongSection()
    {
        const string before = "[song]\nname = A\n# comment\nalbum = Z\n[other]\nalbum = keep\n[Song]\nALBUM = Y\n";

        string after = SongIni.ApplyEdits(before, Draft(d => d.Remove("album")));

        Assert.AreEqual("[song]\nname = A\n# comment\n[other]\nalbum = keep\n[Song]\n", after);
    }

    [TestMethod]
    public void SetCollapsesDuplicateLines()
    {
        const string before = "[song]\nname = One\nartist = A\nname = Two\n";

        string after = SongIni.ApplyEdits(before, Draft(d => d.Set("name", "New")));

        Assert.AreEqual("[song]\nname = New\nartist = A\n", after);
    }

    [TestMethod]
    public void NewKeyGoesAfterLastPairOfSongSection()
    {
        const string before = "[song]\nname = A\n\n[other]\nx = 1\n";

        string after = SongIni.ApplyEdits(before, Draft(d => d.Set("genre", "Rock")));

        Assert.AreEqual("[song]\nname = A\ngenre = Rock\n\n[other]\nx = 1\n", after);
    }

    [TestMethod]
    public void NewKeyInEmptySongSectionGoesUnderTheHeader()
    {
        string after = SongIni.ApplyEdits("[song]\n[other]\n", Draft(d => d.Set("name", "A")));

        Assert.AreEqual("[song]\nname = A\n[other]\n", after);
    }

    [TestMethod]
    public void MissingSongSectionIsAppended()
    {
        string after = SongIni.ApplyEdits("[other]\nx = 1", Draft(d => d.Set("name", "A")));

        Assert.AreEqual("[other]\nx = 1\n\n[song]\nname = A\n", after);
    }

    [TestMethod]
    public void CrlfIsKept()
    {
        string after = SongIni.ApplyEdits("[song]\r\nname = A\r\n", Draft(d =>
        {
            d.Set("name", "B");
            d.Set("genre", "Rock");
        }));

        Assert.AreEqual("[song]\r\nname = B\r\ngenre = Rock\r\n", after);
    }

    [TestMethod]
    public void MissingFinalNewlineStaysMissing()
    {
        string after = SongIni.ApplyEdits("[song]\nname = A", Draft(d => d.Set("genre", "Rock")));

        Assert.AreEqual("[song]\nname = A\ngenre = Rock", after);
    }

    [TestMethod]
    public void ParsesBackAsTheDraftAppliedToTheOriginal()
    {
        const string before = "[song]\nname = A\nartist = X\nartist = Y\nicon = i\n; c\n[song]\nyear = 1999\n";
        var draft = Draft(d =>
        {
            d.Set("ARTIST", "Z");
            d.Remove("icon");
            d.Set("genre", "Rock");
        });

        string after = SongIni.ApplyEdits(before, draft);

        var expected = draft.ApplyTo(SongIni.Parse(new StringReader(before))!);
        Assert.AreSequenceEqual(expected, [.. SongIni.Parse(new StringReader(after))!]);
    }

    [TestMethod]
    public async Task FileIsCreatedWhenMissing()
    {
        string path = Path.Combine(_root.FullName, "song.ini");

        await SongIni.ApplyEditsAsync(path, Draft(d => d.Set("name", "A")), TestContext.CancellationToken);

        var read = await SongIni.ReadAsync(path, TestContext.CancellationToken);
        Assert.AreEqual(SongIniStatus.Ok, read.Status);
        Assert.AreEqual(KeyValuePair.Create("name", "A"), read.Pairs.Single());
        Assert.AreEqual((byte)'[', File.ReadAllBytes(path)[0]);
    }

    [TestMethod]
    public async Task ByteOrderMarkIsKeptAndNoTempIsLeft()
    {
        string path = Path.Combine(_root.FullName, "Song.INI");
        File.WriteAllBytes(path, [.. Encoding.UTF8.Preamble, .. "[song]\r\nname = Ä\r\n"u8]);

        await SongIni.ApplyEditsAsync(path, Draft(d => d.Set("artist", "Ö")), TestContext.CancellationToken);

        Assert.AreSequenceEqual(
            (byte[])[.. Encoding.UTF8.Preamble, .. "[song]\r\nname = Ä\r\nartist = Ö\r\n"u8], File.ReadAllBytes(path));
        Assert.IsFalse(File.Exists(path + SongIni.TempSuffix));
    }

    [TestMethod]
    public async Task TooLargeFileIsRefusedAndUntouched()
    {
        string path = Path.Combine(_root.FullName, "song.ini");
        File.WriteAllText(path, "[song]\n" + new string('x', (int)SongIni.MaxFileSize));
        long length = new FileInfo(path).Length;

        await Assert.ThrowsAsync<IOException>(() => SongIni.ApplyEditsAsync(path, Draft(d => d.Set("name", "A")), TestContext.CancellationToken));

        Assert.AreEqual(length, new FileInfo(path).Length);
        Assert.IsFalse(File.Exists(path + SongIni.TempSuffix));
    }

    private static MetadataDraft Draft(Action<MetadataDraft> edit)
    {
        var draft = new MetadataDraft();
        edit(draft);
        return draft;
    }

    public TestContext TestContext { get; set; }
}
