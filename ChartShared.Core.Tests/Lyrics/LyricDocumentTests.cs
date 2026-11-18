using ChartShared.Core.Lyrics;

namespace ChartShared.Core.Tests.Lyrics;

[TestClass]
public sealed class LyricDocumentTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private static LyricDocument Sung() => new(
    [
        new LyricLine(S(0), S(3),
        [
            new LyricWord("Oh", S(0), S(1), 60),
            new LyricWord("+", S(1), S(2), 62),
            new LyricWord("yeah", S(2), S(3), 64),
        ]),
    ]);

    [TestMethod]
    public void WithoutPitchesASlideHoldsTheWordBeforeItOn()
    {
        var document = Sung().WithoutPitches();

        var words = document.Lines.Single().Words;
        Assert.IsFalse(document.HasPitches);
        Assert.AreSequenceEqual(["Oh", "yeah"], [.. words.Select(static w => w.Text)]);
        Assert.AreEqual(S(2), words[0].End);
    }

    [TestMethod]
    public void WithoutWordTimesThePitchesGoToo()
    {
        var document = Sung().WithoutWordTimes();

        Assert.IsFalse(document.HasWordTimes);
        Assert.IsFalse(document.HasPitches);
        Assert.HasCount(2, document.Lines.Single().Words);
    }

    [TestMethod]
    public void ASingersLinesAreTheirOwnDocument()
    {
        var document = LyricDocument.Of(
        [
            LyricLine.OfText(S(2), null, "two") with { Singer = 2 },
            LyricLine.OfText(S(1), null, "one"),
        ]);

        Assert.AreSequenceEqual([1, 2], [.. document.Singers]);
        Assert.AreEqual("two", document.ForSinger(2).Lines.Single().Words.Single().Text);
        Assert.AreEqual("one", document.ForSinger(1).Lines.Single().Words.Single().Text);
    }
}
