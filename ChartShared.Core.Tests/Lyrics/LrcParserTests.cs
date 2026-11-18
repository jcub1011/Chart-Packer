using ChartShared.Core.Lyrics;

namespace ChartShared.Core.Tests.Lyrics;

[TestClass]
public sealed class LrcParserTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    [TestMethod]
    public void ALineIsItsTimeTagAndItsWords()
    {
        var document = LrcParser.Parse("[ti:Song]\n[ar:Someone]\n[00:12.50]Hello big world\n[01:02.5]Again\n");

        Assert.HasCount(2, document.Lines);
        Assert.AreEqual(S(12.5), document.Lines[0].Start);
        Assert.AreSequenceEqual(["Hello", "big", "world"], [.. document.Lines[0].Words.Select(static w => w.Text)]);
        Assert.IsTrue(document.Lines[0].Words.All(static w => w.Start is null));
        Assert.AreEqual(S(62.5), document.Lines[1].Start);
    }

    [TestMethod]
    public void ALineWithSeveralTimeTagsIsEachTime()
    {
        var document = LrcParser.Parse("[00:10.00][00:30.00]La la\n");

        Assert.AreSequenceEqual([S(10), S(30)], [.. document.Lines.Select(static l => l.Start)]);
    }

    [TestMethod]
    public void AnEmptyTimedLineEndsTheLineBeforeIt()
    {
        var document = LrcParser.Parse("[00:10.00]One\n[00:12.00]\n[00:20.00]Two\n");

        Assert.HasCount(2, document.Lines);
        Assert.AreEqual(S(12), document.Lines[0].End);
        Assert.IsNull(document.Lines[1].End);
    }

    [TestMethod]
    public void AnOffsetBringsTheLyricsForward()
    {
        var document = LrcParser.Parse("[offset:+500]\n[00:10.00]One\n");

        Assert.AreEqual(S(9.5), document.Lines[0].Start);
    }

    [TestMethod]
    public void A2TagsGiveEachWordItsTime()
    {
        var document = LrcParser.Parse("[00:10.00]<00:10.00>Hel<00:10.40>lo <00:10.80>world<00:11.50>\n");

        var line = document.Lines[0];
        Assert.AreSequenceEqual(["Hel-", "lo", "world"], [.. line.Words.Select(static w => w.Text)]);
        Assert.AreSequenceEqual([S(10), S(10.4), S(10.8)], [.. line.Words.Select(static w => w.Start!.Value)]);
        Assert.AreEqual(S(11.5), line.End);
        Assert.AreEqual(S(11.5), line.Words[2].End);
    }

    [TestMethod]
    public void A2TagsCanBeIgnored()
    {
        var document = LrcParser.Parse("[00:10.00]<00:10.00>Hello <00:10.80>world\n", useWordTimes: false);

        var line = document.Lines[0];
        Assert.AreSequenceEqual(["Hello", "world"], [.. line.Words.Select(static w => w.Text)]);
        Assert.IsTrue(line.Words.All(static w => w.Start is null));
    }

    [TestMethod]
    public void LinesAreInOrderOfTime()
    {
        var document = LrcParser.Parse("[00:20.00]Two\n[00:10.00]One\n");

        Assert.AreEqual("One", document.Lines[0].Words[0].Text);
    }

    [TestMethod]
    public void TextThatIsntLrcIsRecognised()
    {
        Assert.IsTrue(LrcParser.LooksLikeLrc("[ti:x]\n[00:01.00]hi"));
        Assert.IsFalse(LrcParser.LooksLikeLrc("just some words\nacross lines"));
    }
}
