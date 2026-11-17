using System.Text;
using ChartShared.Core.Charts;
using ChartShared.Core.Lyrics;

namespace ChartShared.Core.Tests.Lyrics;

[TestClass]
public sealed class LyricFilesTests
{
    private DirectoryInfo _root = null!;

    [TestInitialize]
    public void CreateRoot() => _root = Directory.CreateTempSubdirectory("chartpacker-lyrics-");

    [TestCleanup]
    public void DeleteRoot() => _root.Delete(recursive: true);

    private string Write(string name, string text)
    {
        string path = Path.Combine(_root.FullName, name);
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return path;
    }

    [TestMethod]
    [DataRow("a.lrc", LyricFormat.Lrc)]
    [DataRow("a.SRT", LyricFormat.Srt)]
    [DataRow("a.vtt", LyricFormat.Vtt)]
    [DataRow("a.ttml", LyricFormat.Ttml)]
    [DataRow("a.kar", LyricFormat.Kar)]
    [DataRow("a.txt", LyricFormat.UltraStar)]
    [DataRow("a.mp3", LyricFormat.AudioTags)]
    [DataRow("a.flac", LyricFormat.AudioTags)]
    public void TheExtensionSaysTheFormat(string path, LyricFormat format)
    {
        Assert.AreEqual(format, LyricFiles.FormatOf(path));
    }

    [TestMethod]
    public void AnotherExtensionIsRefused()
    {
        Assert.IsNull(LyricFiles.FormatOf("a.docx"));
        Assert.ThrowsExactly<ChartFormatException>(() => LyricFiles.Read(Write("a.docx", "x")));
    }

    [TestMethod]
    public void AFileIsReadByItsFormat()
    {
        var lrc = LyricFiles.Read(Write("song.lrc", "[00:01.00]Hi there\n"));
        var srt = LyricFiles.Read(Write("song.srt", "1\n00:00:01,000 --> 00:00:02,000\nHi there\n"));

        Assert.AreEqual(2, lrc.WordCount);
        Assert.AreEqual(2, srt.WordCount);
    }

    [TestMethod]
    public void ATextFileThatIsntUltraStarIsRefused()
    {
        Assert.ThrowsExactly<ChartFormatException>(() => LyricFiles.Read(Write("notes.txt", "just some notes\n")));
    }

    [TestMethod]
    public void AFileWithNoLyricsInItIsRefused()
    {
        Assert.ThrowsExactly<ChartFormatException>(() => LyricFiles.Read(Write("empty.lrc", "[ti:Nothing]\n")));
    }

    [TestMethod]
    public void WordTimesCanBeTakenAway()
    {
        var document = LrcParser.Parse("[00:10.00]<00:10.00>Hello <00:10.80>world<00:11.50>\n");
        Assert.IsTrue(document.HasWordTimes);

        var plain = document.WithoutWordTimes();

        Assert.IsFalse(plain.HasWordTimes);
        Assert.AreEqual(2, plain.WordCount);
        Assert.AreEqual(document.Lines[0].End, plain.Lines[0].End);
    }
}
