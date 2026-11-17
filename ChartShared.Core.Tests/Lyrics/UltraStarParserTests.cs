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
    public void NormalAndGoldenNotesArePitchedFromC4AndTheOthersAreNot()
    {
        var document = UltraStarParser.Parse(Song);

        Assert.AreSequenceEqual([65, 67, 67], [.. document.Lines[0].Words.Select(static w => w.Pitch!.Value)]);
        Assert.IsNull(document.Lines[1].Words[0].Pitch);
        Assert.IsTrue(document.HasPitches);

        var rap = UltraStarParser.Parse("#BPM:60\nR 0 4 0 rap \nG 4 4 0 gold \n: 8 4 -12 low\nE\n");
        Assert.AreSequenceEqual(new int?[] { null, null, 48 }, [.. rap.Lines[0].Words.Select(static w => w.Pitch)]);
    }

    [TestMethod]
    public void ATildeAtAnotherPitchIsASlideAndAtTheSameOneOnlyHoldsItOn()
    {
        // At 60 BPM a beat is a quarter of a second.
        var document = UltraStarParser.Parse("#BPM:60\n#GAP:0\n: 0 4 0 Hel\n: 4 4 2 ~\n: 8 4 2 ~\n: 12 4 4 lo \nE\n");

        var words = document.Lines[0].Words;
        Assert.AreSequenceEqual(["Hel-", "+", "lo"], [.. words.Select(static w => w.Text)]);
        Assert.AreSequenceEqual([60, 62, 64], [.. words.Select(static w => w.Pitch!.Value)]);
        Assert.IsTrue(words[1].IsSlide);
        Assert.AreEqual(TimeSpan.FromSeconds(1), words[1].Start);
        Assert.AreEqual(TimeSpan.FromSeconds(3), words[1].End);
    }

    [TestMethod]
    public void ADuetsSingersAreReadAsTheirOwnAndP3AsBoths()
    {
        var document = UltraStarParser.Parse("#BPM:60\n#GAP:0\nP1\n: 0 4 5 one \n- 8\nP2\n: 0 4 5 other \nP3\n: 16 4 5 both \nE\n");

        Assert.AreSequenceEqual([1, 2], [.. document.Singers]);
        Assert.AreSequenceEqual(["one", "both"], [.. document.ForSinger(1).Lines.SelectMany(static l => l.Words).Select(static w => w.Text)]);
        Assert.AreSequenceEqual(["other", "both"], [.. document.ForSinger(2).Lines.SelectMany(static l => l.Words).Select(static w => w.Text)]);
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
