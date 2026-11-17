using ChartShared.Core.Charts;
using ChartShared.Core.Lyrics;

namespace ChartShared.Core.Tests.Lyrics;

[TestClass]
public sealed class UltraStarParserTests
{
    // At 120 BPM a beat is 15 / 120 = 0.125 s.
    private const string Song =
        "#TITLE:Test\n#BPM:120,0\n#GAP:1000\n"
        + ": 0 4 5 Hel\n: 4 4 7 lo \n* 8 4 7 world\n- 16\n"
        + "F 16 4 0 Free\n: 20 2 3 ~\n: 24 4 3 fall\nE\n";

    [TestMethod]
    public void BeatsAreTimedByTheBpmAndGap()
    {
        var document = UltraStarParser.Parse(Song);

        Assert.HasCount(2, document.Lines);
        var line = document.Lines[0];
        Assert.AreSequenceEqual(["Hel-", "lo", "world"], [.. line.Words.Select(static w => w.Text)]);
        Assert.AreSequenceEqual([1.0, 1.5, 2.0], [.. line.Words.Select(static w => w.Start!.Value.TotalSeconds)]);
        Assert.AreEqual(TimeSpan.FromSeconds(1.5), line.Words[0].End);
        Assert.AreEqual(TimeSpan.FromSeconds(1), line.Start);
        Assert.AreEqual(TimeSpan.FromSeconds(2.5), line.End);
    }

    [TestMethod]
    public void AFreestyleLineCountsAndATildeOnlyLengthensTheSyllableBefore()
    {
        var document = UltraStarParser.Parse(Song);

        var line = document.Lines[1];
        Assert.AreEqual("Free-", line.Words[0].Text);
        Assert.AreEqual("fall", line.Words[1].Text);
        Assert.AreEqual(TimeSpan.FromSeconds(1) + TimeSpan.FromSeconds(22 * 0.125), line.Words[0].End);
    }

    [TestMethod]
    public void OnlyTheFirstSingersLinesOfADuetAreRead()
    {
        var document = UltraStarParser.Parse("#BPM:60\n#GAP:0\nP1\n: 0 4 5 one \n- 8\nP2\n: 0 4 5 other \nE\n");

        Assert.HasCount(1, document.Lines);
        Assert.AreEqual("one", document.Lines[0].Words[0].Text);
    }

    [TestMethod]
    public void ASongWithNoBpmCantBeTimed()
    {
        Assert.ThrowsExactly<ChartFormatException>(() => UltraStarParser.Parse("#GAP:0\n: 0 4 5 one\nE\n"));
    }

    [TestMethod]
    public void OnlyASongWithABpmLooksLikeUltraStar()
    {
        Assert.IsTrue(UltraStarParser.LooksLikeUltraStar(Song));
        Assert.IsFalse(UltraStarParser.LooksLikeUltraStar("plain notes\nnot a song"));
    }
}
