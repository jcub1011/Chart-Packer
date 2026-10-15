using System.Text;
using ChartShared.Core.Ini;

namespace ChartShared.Core.Tests.Ini;

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
        Assert.AreSequenceEqual(pairs, [.. Parse(writer.ToString())!]);
    }

    [TestMethod]
    public async Task WriteAsyncHasNoByteOrderMark()
    {
        string path = Path.Combine(_root.FullName, "song.ini");

        await SongIni.WriteAsync(path, [P("name", "Title")], TestContext.CancellationToken);

        Assert.AreEqual((byte)'[', File.ReadAllBytes(path)[0]);
        Assert.AreEqual("Title", (await SongIni.ReadAsync(path, TestContext.CancellationToken)).Pairs.Single().Value);
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

        Assert.AreSequenceEqual([P("name", "Title"), P("artist", "Band"), P("diff_guitar", "3")], [.. pairs!]);
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

        Assert.AreSequenceEqual([P("name", "Right")], [.. pairs!]);
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

        Assert.AreSequenceEqual([P("name", "Title")], [.. pairs!]);
    }

    [TestMethod]
    public void SplitsOnFirstEqualsAndTrims()
    {
        var pairs = Parse("[song]\n   loading_phrase   =   <color=#00FF00>Hi</color>  \nempty =\n");

        Assert.AreSequenceEqual([P("loading_phrase", "<color=#00FF00>Hi</color>"), P("empty", "")], [.. pairs!]);
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

        Assert.AreSequenceEqual([P("name", "Second"), P("artist", "Band")], [.. pairs!]);
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

        Assert.AreSequenceEqual([P("name", "Title"), P("artist", "Band")], [.. pairs!]);
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
        await File.WriteAllTextAsync(path, "[song]\r\nname = Café\r\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), TestContext.CancellationToken);

        var result = await SongIni.ReadAsync(path, TestContext.CancellationToken);

        Assert.AreEqual(SongIniStatus.Ok, result.Status);
        Assert.AreSequenceEqual([P("name", "Café")], [.. result.Pairs]);
    }

    [TestMethod]
    public async Task ReadAsyncReportsMissingSection()
    {
        string path = Path.Combine(_root.FullName, "song.ini");
        await File.WriteAllTextAsync(path, "[notsong]\nname = A\n", TestContext.CancellationToken);

        var result = await SongIni.ReadAsync(path, TestContext.CancellationToken);

        Assert.AreEqual(SongIniStatus.NoSongSection, result.Status);
        Assert.IsEmpty(result.Pairs);
    }

    [TestMethod]
    public async Task ReadAsyncReportsRepeatedKeysOnceAsFirstSpelled()
    {
        string path = Path.Combine(_root.FullName, "song.ini");
        await File.WriteAllTextAsync(path, "[song]\nName = A\nname = B\nNAME = C\ndelay = 0\n", TestContext.CancellationToken);

        var result = await SongIni.ReadAsync(path, TestContext.CancellationToken);

        Assert.AreSequenceEqual([P("Name", "C"), P("delay", "0")], [.. result.Pairs]);
        Assert.AreSequenceEqual(["Name"], [.. result.DuplicateKeys]);
    }

    [TestMethod]
    public async Task ReadAsyncReportsNoDuplicatesForDistinctKeys()
    {
        string path = Path.Combine(_root.FullName, "song.ini");
        await File.WriteAllTextAsync(path, "[song]\nname = A\nartist = B\n", TestContext.CancellationToken);

        Assert.IsEmpty((await SongIni.ReadAsync(path, TestContext.CancellationToken)).DuplicateKeys);
    }

    [TestMethod]
    public async Task ReadAsyncRefusesOversizedFile()
    {
        string path = Path.Combine(_root.FullName, "song.ini");
        await File.WriteAllBytesAsync(path, new byte[SongIni.MaxFileSize + 1], TestContext.CancellationToken);

        var result = await SongIni.ReadAsync(path, TestContext.CancellationToken);

        Assert.AreEqual(SongIniStatus.TooLarge, result.Status);
    }

    private static IReadOnlyList<KeyValuePair<string, string>>? Parse(string text) => SongIni.Parse(new StringReader(text));

    private static KeyValuePair<string, string> P(string key, string value) => new(key, value);

    public TestContext TestContext { get; set; }
}
