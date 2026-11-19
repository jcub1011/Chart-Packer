using ChartShared.Core.Charts;
using ChartShared.Core.Lyrics;

namespace ChartShared.Core.Tests.Lyrics;

[TestClass]
public sealed class TtmlParserTests
{
    private const string Header = "<?xml version=\"1.0\" encoding=\"utf-8\"?><tt xmlns=\"http://www.w3.org/ns/ttml\" xmlns:ttp=\"http://www.w3.org/ns/ttml#parameter\" ttp:tickRate=\"1000\">";

    [TestMethod]
    public void ParagraphsAreLinesAndTimedSpansAreWords()
    {
        string ttml = Header + "<body><div><p begin=\"00:00:10.000\" end=\"00:00:12.000\">"
            + "<span begin=\"00:00:10.000\" end=\"00:00:10.500\">Hel</span><span begin=\"00:00:10.500\" end=\"00:00:11.000\">lo</span> "
            + "<span begin=\"00:00:11.000\" end=\"00:00:12.000\">world</span></p></div></body></tt>";

        var document = TtmlParser.Parse(ttml);

        var line = document.Lines[0];
        Assert.AreEqual(TimeSpan.FromSeconds(10), line.Start);
        Assert.AreEqual(TimeSpan.FromSeconds(12), line.End);
        Assert.AreSequenceEqual(["Hel-", "lo", "world"], [.. line.Words.Select(static w => w.Text)]);
        Assert.AreSequenceEqual([10.0, 10.5, 11.0], [.. line.Words.Select(static w => w.Start!.Value.TotalSeconds)]);
        Assert.AreEqual(TimeSpan.FromSeconds(11), line.Words[1].End);
    }

    [TestMethod]
    public void SpanTimesAreRelativeToTheirParagraphAndOffsetsAreRead()
    {
        string ttml = Header + "<body><div begin=\"1s\"><p begin=\"2s\" dur=\"3s\"><span begin=\"500ms\">one</span> <span begin=\"1500t\">two</span></p></div></body></tt>";

        var document = TtmlParser.Parse(ttml);

        var line = document.Lines[0];
        Assert.AreEqual(TimeSpan.FromSeconds(3), line.Start);
        Assert.AreEqual(TimeSpan.FromSeconds(6), line.End);
        Assert.AreEqual(3.5, line.Words[0].Start!.Value.TotalSeconds, 1e-9);
        Assert.AreEqual(4.5, line.Words[1].Start!.Value.TotalSeconds, 1e-9);
    }

    [TestMethod]
    public void UntimedTextInAParagraphIsSplitIntoUntimedWords()
    {
        var document = TtmlParser.Parse(Header + "<body><div><p begin=\"5s\" end=\"6s\">Just some<br/>words</p></div></body></tt>");

        Assert.AreSequenceEqual(["Just", "some", "words"], [.. document.Lines[0].Words.Select(static w => w.Text)]);
        Assert.IsTrue(document.Lines[0].Words.All(static w => w.Start is null));
    }

    [TestMethod]
    public void MalformedXmlIsRefused()
    {
        Assert.ThrowsExactly<ChartFormatException>(() => TtmlParser.Parse("<tt><body><p>"));
    }
}
