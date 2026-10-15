using ChartShared.Core.Metadata;

namespace ChartShared.Core.Tests.Metadata;

[TestClass]
public sealed class SongHeadlineTests
{
    [TestMethod]
    public void ReadsEachValue()
    {
        var headline = SongHeadline.From(Pairs(
            ("name", "Song"), ("artist", "Band"), ("charter", "Someone"), ("song_length", "225123")));

        Assert.AreEqual(new SongHeadline("Song", "Band", "Someone", 225_123), headline);
    }

    [TestMethod]
    public void EmptyPairsGiveNothing() =>
        Assert.AreEqual(new SongHeadline(null, null, null, null), SongHeadline.From([]));

    [TestMethod]
    public void KeysMatchIgnoringCase() =>
        Assert.AreEqual("Song", SongHeadline.From(Pairs(("NAME", "Song"))).Name);

    [TestMethod]
    public void FirstDuplicateWins() =>
        Assert.AreEqual("First", SongHeadline.From(Pairs(("name", "First"), ("Name", "Second"))).Name);

    [TestMethod]
    public void CharterFallsBackToFrets() =>
        Assert.AreEqual("Legacy", SongHeadline.From(Pairs(("frets", "Legacy"))).Charter);

    [TestMethod]
    public void CharterWinsOverFrets() =>
        Assert.AreEqual("Current", SongHeadline.From(Pairs(("frets", "Legacy"), ("charter", "Current"))).Charter);

    [TestMethod]
    public void BlankCharterFallsBackToFrets() =>
        Assert.AreEqual("Legacy", SongHeadline.From(Pairs(("charter", "  "), ("frets", "Legacy"))).Charter);

    [TestMethod]
    public void RichTextIsStripped()
    {
        var headline = SongHeadline.From(Pairs(("name", "<b>Song</b>"), ("charter", "<color=#FF0000>Someone</color>")));

        Assert.AreEqual("Song", headline.Name);
        Assert.AreEqual("Someone", headline.Charter);
    }

    [TestMethod]
    public void CharterMarkupKeepsTheTags() =>
        Assert.AreEqual("<color=#FF0000>Someone</color>", SongHeadline.CharterMarkup(Pairs(("charter", " <color=#FF0000>Someone</color> "))));

    [TestMethod]
    public void CharterMarkupFallsBackToFrets() =>
        Assert.AreEqual("Legacy", SongHeadline.CharterMarkup(Pairs(("charter", "<b></b>"), ("frets", "Legacy"))));

    [TestMethod]
    public void CharterMarkupIsNullWithoutVisibleText()
    {
        Assert.IsNull(SongHeadline.CharterMarkup(Pairs(("charter", "<b></b>"))));
        Assert.IsNull(SongHeadline.CharterMarkup([]));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("<b></b>")]
    public void BlankTextIsUnset(string value) =>
        Assert.IsNull(SongHeadline.From(Pairs(("artist", value))).Artist);

    [TestMethod]
    public void TextIsTrimmed() =>
        Assert.AreEqual("Band", SongHeadline.From(Pairs(("artist", " Band "))).Artist);

    [TestMethod]
    [DataRow("0")]
    [DataRow("-1")]
    [DataRow("3:45")]
    [DataRow("1.5")]
    [DataRow("")]
    public void InvalidLengthIsUnset(string value) =>
        Assert.IsNull(SongHeadline.From(Pairs(("song_length", value))).LengthMs);

    private static List<KeyValuePair<string, string>> Pairs(params (string Key, string Value)[] pairs) =>
        [.. pairs.Select(p => KeyValuePair.Create(p.Key, p.Value))];
}
