using System.Text;
using ChartPacker.Core.Ini;

namespace ChartPacker.Core.Tests.Ini;

[TestClass]
public sealed class SongIniTests
{
    private DirectoryInfo _root = null!;

    [TestInitialize]
    public void CreateRoot() => _root = Directory.CreateTempSubdirectory("chartpacker-ini-");

    [TestCleanup]
    public void DeleteRoot() => _root.Delete(recursive: true);

    [TestMethod]
    public void FindInMatchesTheFolderOwnSongIniIgnoringCase()
    {
        string path = Path.Combine(_root.FullName, "Song.INI");
        File.WriteAllText(path, "[song]\n");

        Assert.AreEqual(path, SongIni.FindIn(_root.FullName));
    }

    [TestMethod]
    public void FindInIgnoresASongIniInASubfolder()
    {
        var sub = _root.CreateSubdirectory("Other");
        File.WriteAllText(Path.Combine(sub.FullName, "song.ini"), "[song]\n");

        Assert.IsNull(SongIni.FindIn(_root.FullName));
    }

    [TestMethod]
    public void WriteProducesOneSongSectionThatParsesBack()
    {
        var pairs = new[] { P("name", "Title"), P("loading_phrase", "a = b"), P("delay", "0") };
        var writer = new StringWriter { NewLine = "\n" };

        SongIni.Write(writer, pairs);

        Assert.AreEqual("[song]\nname = Title\nloading_phrase = a = b\ndelay = 0\n", writer.ToString());
        CollectionAssert.AreEqual(pairs, Parse(writer.ToString())!.ToArray());
    }

    [TestMethod]
    public async Task WriteAsyncHasNoByteOrderMark()
    {
        string path = Path.Combine(_root.FullName, "song.ini");

        await SongIni.WriteAsync(path, [P("name", "Title")]);

        Assert.AreEqual((byte)'[', File.ReadAllBytes(path)[0]);
        Assert.AreEqual("Title", (await SongIni.ReadAsync(path)).Pairs.Single().Value);
    }

    [TestMethod]
    public void ReadsSongSectionInFileOrder()
    {
        var pairs = Parse("""
            [song]
            name = Title
            artist = Band
            diff_guitar = 3
            """);

        CollectionAssert.AreEqual(new[] { P("name", "Title"), P("artist", "Band"), P("diff_guitar", "3") }, pairs!.ToArray());
    }

    [TestMethod]
    public void SectionNameIgnoresCaseButIsNotTrimmed()
    {
        Assert.HasCount(1, Parse("[SONG]\nname = A")!);
        Assert.IsNull(Parse("[ song ]\nname = A"));
    }

    [TestMethod]
    public void OnlyTheSongSectionIsRead()
    {
        var pairs = Parse("""
            before = ignored
            [other]
            name = Wrong
            [Song]
            name = Right
            [more]
            artist = Wrong
            """);

        CollectionAssert.AreEqual(new[] { P("name", "Right") }, pairs!.ToArray());
    }

    [TestMethod]
    public void SkipsCommentsBlankLinesAndLinesWithoutEquals()
    {
        var pairs = Parse("""
            ; comment
            [song]
            # another comment

              ; indented comment
            not a pair
            name = Title
            """);

        CollectionAssert.AreEqual(new[] { P("name", "Title") }, pairs!.ToArray());
    }

    [TestMethod]
    public void SplitsOnFirstEqualsAndTrims()
    {
        var pairs = Parse("[song]\n   loading_phrase   =   <color=#00FF00>Hi</color>  \nempty =\n");

        CollectionAssert.AreEqual(new[] { P("loading_phrase", "<color=#00FF00>Hi</color>"), P("empty", "") }, pairs!.ToArray());
    }

    [TestMethod]
    public void DuplicateKeyLastValueWinsAtFirstPosition()
    {
        var pairs = Parse("""
            [song]
            name = First
            artist = Band
            NAME = Second
            """);

        CollectionAssert.AreEqual(new[] { P("name", "Second"), P("artist", "Band") }, pairs!.ToArray());
    }

    [TestMethod]
    public void RepeatedSongSectionsMerge()
    {
        var pairs = Parse("""
            [song]
            name = Title
            [other]
            x = y
            [song]
            artist = Band
            """);

        CollectionAssert.AreEqual(new[] { P("name", "Title"), P("artist", "Band") }, pairs!.ToArray());
    }

    [TestMethod]
    public void EmptySongSectionIsNotMissing()
    {
        Assert.IsEmpty(Parse("[song]\n")!);
        Assert.IsNull(Parse(""));
    }

    [TestMethod]
    public async Task ReadAsyncHandlesBomAndCrLf()
    {
        string path = Path.Combine(_root.FullName, "song.ini");
        await File.WriteAllTextAsync(path, "[song]\r\nname = Café\r\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var result = await SongIni.ReadAsync(path);

        Assert.AreEqual(SongIniStatus.Ok, result.Status);
        CollectionAssert.AreEqual(new[] { P("name", "Café") }, result.Pairs.ToArray());
    }

    [TestMethod]
    public async Task ReadAsyncReportsMissingSection()
    {
        string path = Path.Combine(_root.FullName, "song.ini");
        await File.WriteAllTextAsync(path, "[notsong]\nname = A\n");

        var result = await SongIni.ReadAsync(path);

        Assert.AreEqual(SongIniStatus.NoSongSection, result.Status);
        Assert.IsEmpty(result.Pairs);
    }

    [TestMethod]
    public async Task ReadAsyncReportsRepeatedKeysOnceAsFirstSpelled()
    {
        string path = Path.Combine(_root.FullName, "song.ini");
        await File.WriteAllTextAsync(path, "[song]\nName = A\nname = B\nNAME = C\ndelay = 0\n");

        var result = await SongIni.ReadAsync(path);

        CollectionAssert.AreEqual(new[] { P("Name", "C"), P("delay", "0") }, result.Pairs.ToArray());
        CollectionAssert.AreEqual(new[] { "Name" }, result.DuplicateKeys.ToArray());
    }

    [TestMethod]
    public async Task ReadAsyncReportsNoDuplicatesForDistinctKeys()
    {
        string path = Path.Combine(_root.FullName, "song.ini");
        await File.WriteAllTextAsync(path, "[song]\nname = A\nartist = B\n");

        Assert.IsEmpty((await SongIni.ReadAsync(path)).DuplicateKeys);
    }

    [TestMethod]
    public async Task ReadAsyncRefusesOversizedFile()
    {
        string path = Path.Combine(_root.FullName, "song.ini");
        await File.WriteAllBytesAsync(path, new byte[SongIni.MaxFileSize + 1]);

        var result = await SongIni.ReadAsync(path);

        Assert.AreEqual(SongIniStatus.TooLarge, result.Status);
    }

    private static IReadOnlyList<KeyValuePair<string, string>>? Parse(string text) => SongIni.Parse(new StringReader(text));

    private static KeyValuePair<string, string> P(string key, string value) => new(key, value);
}
