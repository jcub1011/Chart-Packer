using ChartShared.Core.Lyrics;

namespace ChartShared.Core.Tests.Lyrics;

[TestClass]
public sealed class CueParserTests
{
    [TestMethod]
    public void SrtCuesAreLinesWithTheirTimes()
    {
        var document = CueParser.Parse("1\r\n00:00:01,500 --> 00:00:03,000\r\n<i>Hello</i> world\r\nsecond &amp; line\r\n\r\n2\r\n00:01:00,000 --> 00:01:02,250\r\n{\\an8}Again\r\n");

        Assert.HasCount(2, document.Lines);
        Assert.AreEqual(TimeSpan.FromSeconds(1.5), document.Lines[0].Start);
        Assert.AreEqual(TimeSpan.FromSeconds(3), document.Lines[0].End);
        Assert.AreSequenceEqual(["Hello", "world", "second", "&", "line"], [.. document.Lines[0].Words.Select(static w => w.Text)]);
        Assert.AreEqual(TimeSpan.FromSeconds(62.25), document.Lines[1].End);
        Assert.AreEqual("Again", document.Lines[1].Words[0].Text);
    }

    [TestMethod]
    public void VttHeaderNotesAndSettingsAreSkipped()
    {
        const string vtt = "WEBVTT - lyrics\n\nNOTE a comment\nover two lines\n\nSTYLE\n::cue { color: red }\n\nintro\n00:01.000 --> 00:02.500 align:start line:90%\n<c.yellow>Hel</c><00:01.500><c>lo</c> there\n";

        var document = CueParser.Parse(vtt);

        Assert.HasCount(1, document.Lines);
        Assert.AreEqual(TimeSpan.FromSeconds(1), document.Lines[0].Start);
        Assert.AreEqual(TimeSpan.FromSeconds(2.5), document.Lines[0].End);
        Assert.AreSequenceEqual(["Hello", "there"], [.. document.Lines[0].Words.Select(static w => w.Text)]);
    }

    [TestMethod]
    public void ACueWithNothingSungIsLeftOut()
    {
        var document = CueParser.Parse("1\n00:00:01,000 --> 00:00:02,000\n♪\n");

        Assert.AreEqual(0, document.Lines.Count);
    }
}
