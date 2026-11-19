using ChartShared.Core.Charts;
using ChartShared.Core.Lyrics;
using ChartShared.Core.Tests.Charts;

namespace ChartShared.Core.Tests.Lyrics;

[TestClass]
public sealed class KarParserTests
{
    // 480 ticks a beat at 120 BPM is half a second a beat.
    [TestMethod]
    public void SyllablesAreTimedAndSlashesBreakLines()
    {
        byte[] file = new MidiBuilder(480)
            .Track("Master").Tempo(0, 120)
            .Track("Words")
            .Text(0, "@LENGL").Text(0, "@TSong title")
            .Text(0, "\\Hel").Text(480, "lo ").Text(960, "world")
            .Text(1920, "/Next ").Text(2400, "one")
            .Build();

        var document = KarParser.Parse(file);

        Assert.HasCount(2, document.Lines);
        Assert.AreSequenceEqual(["Hel-", "lo", "world"], [.. document.Lines[0].Words.Select(static w => w.Text)]);
        Assert.AreSequenceEqual([0.0, 0.5, 1.0], [.. document.Lines[0].Words.Select(static w => w.Start!.Value.TotalSeconds)]);
        Assert.AreEqual(TimeSpan.FromSeconds(2), document.Lines[1].Start);
        Assert.AreSequenceEqual(["Next", "one"], [.. document.Lines[1].Words.Select(static w => w.Text)]);
    }

    [TestMethod]
    public void TheTrackWithTheMostLyricsIsRead()
    {
        byte[] file = new MidiBuilder(480)
            .Track("Melody").Text(0, "x")
            .Track("Words").Text(0, "a ").Text(480, "b ").Text(960, "c")
            .Build();

        var document = KarParser.Parse(file);

        Assert.AreSequenceEqual(["a", "b", "c"], [.. document.Lines.SelectMany(static l => l.Words).Select(static w => w.Text)]);
    }

    [TestMethod]
    public void SyllablesAreSungAtTheMelodysPitchesAndANoteBetweenThemIsASlide()
    {
        // The melody's third note, between "lo" and "world", carries "lo" on to a new pitch.
        byte[] file = new MidiBuilder(480)
            .Track("Master").Tempo(0, 120)
            .Track("Words").Text(0, "\\Hel").Text(480, "lo ").Text(960, "world")
            .Track("Melody").Note(0, 60, 400).Note(480, 62, 240).Note(720, 64, 200).Note(960, 65, 480)
            .Track("Drums").Raw(0, 0x99, 36, 100).Raw(480, 0x99, 38, 100).Raw(960, 0x99, 36, 100).Raw(1000, 0x89, 36, 0)
            .Build();

        var document = KarParser.Parse(file);

        var words = document.Lines.Single().Words;
        Assert.AreSequenceEqual(["Hel-", "lo", "+", "world"], [.. words.Select(static w => w.Text)]);
        Assert.AreSequenceEqual([60, 62, 64, 65], [.. words.Select(static w => w.Pitch!.Value)]);
        Assert.IsTrue(words[2].IsSlide);
        Assert.AreEqual(TimeSpan.FromSeconds(0.75), words[2].Start);
        Assert.AreEqual(TimeSpan.FromSeconds(1.5), words[3].End);
    }

    [TestMethod]
    public void WithNoTrackOnHalfTheSyllablesNoneArePitched()
    {
        byte[] file = new MidiBuilder(480)
            .Track("Master").Tempo(0, 120)
            .Track("Words").Text(0, "a ").Text(480, "b ").Text(960, "c")
            .Track("Melody").Note(0, 60, 240).Note(1200, 62, 240)
            .Build();

        var document = KarParser.Parse(file);

        Assert.IsFalse(document.HasPitches);
    }

    [TestMethod]
    public void AMidiFileWithNoLyricsIsRefused()
    {
        byte[] file = new MidiBuilder(480).Track("Melody").Note(0, 60).Build();

        Assert.ThrowsExactly<ChartFormatException>(() => KarParser.Parse(file));
    }
}
