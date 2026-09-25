using ChartPacker.Core.Sng;
using ChartPacker.Core.Validation;

namespace ChartPacker.Core.Tests.Sng;

[TestClass]
public sealed class FilenameRulesTests
{
    [TestMethod]
    [DataRow("notes.chart")]
    [DataRow("song.ogg")]
    [DataRow("sub/readme.txt")]
    [DataRow("Some Unknown File.txt")]
    [DataRow("a.b.c")]
    public void SafeNamesPass(string name) => Assert.IsNull(FilenameRules.FindProblem(name));

    [TestMethod]
    [DataRow("a<b")]
    [DataRow("a:b")]
    [DataRow("a\\b")]
    [DataRow("a|b")]
    [DataRow("a?b")]
    [DataRow("a*b")]
    [DataRow("a\"b")]
    [DataRow("a\tb")]
    [DataRow("a\u001Fb")]
    [DataRow("a\u007Fb")]
    [DataRow("../evil.txt")]
    [DataRow("a..b")]
    [DataRow("/etc/x")]
    [DataRow("a//b")]
    [DataRow("a/")]
    [DataRow("")]
    [DataRow("name.")]
    [DataRow("name ")]
    [DataRow("dir./file")]
    [DataRow("CON")]
    [DataRow("con.txt")]
    [DataRow("sub/LPT9.ogg")]
    [DataRow("COM0")]
    public void UnsafeNamesAreSng030(string name) => Assert.AreEqual(SngRules.Sng030, FilenameRules.FindProblem(name)?.RuleId);

    [TestMethod]
    public void ReservedNamesOnlyMatchWholeStems()
    {
        Assert.IsNull(FilenameRules.FindProblem("console.txt"));
        Assert.IsNull(FilenameRules.FindProblem("COM10"));
    }

    [TestMethod]
    public void LengthIsCountedInUtf8Bytes()
    {
        Assert.IsNull(FilenameRules.FindProblem(new string('a', 255)));
        Assert.AreEqual(SngRules.Sng031, FilenameRules.FindProblem(new string('a', 256))?.RuleId);
        // 128 × 'é' is 256 bytes.
        Assert.AreEqual(SngRules.Sng031, FilenameRules.FindProblem(new string('é', 128))?.RuleId);
    }

    [TestMethod]
    public void RegisteredNamesAreLowercasedAtTopLevelOnly()
    {
        Assert.AreEqual("song.ogg", FilenameRules.NormaliseStoredName("Song.OGG"));
        Assert.AreEqual("notes.mid", FilenameRules.NormaliseStoredName("NOTES.MID"));
        Assert.AreEqual("album.jpeg", FilenameRules.NormaliseStoredName("Album.JPEG"));
        Assert.AreEqual("Readme.TXT", FilenameRules.NormaliseStoredName("Readme.TXT"));
        Assert.AreEqual("stems/Song.ogg", FilenameRules.NormaliseStoredName("stems/Song.ogg"));
    }

    [TestMethod]
    [DataRow("desktop.ini")]
    [DataRow("DESKTOP.INI")]
    [DataRow(".ds_store")]
    [DataRow("Thumbs.db")]
    [DataRow("ps.dat")]
    [DataRow("ch.dat")]
    [DataRow("__MACOSX")]
    public void JunkIsMatchedIgnoringCase(string name) => Assert.IsTrue(FilenameRules.IsJunk(name));
}
