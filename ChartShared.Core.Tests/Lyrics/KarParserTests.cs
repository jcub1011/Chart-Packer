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
    public void AMidiFileWithNoLyricsIsRefused()
    {
        byte[] file = new MidiBuilder(480).Track("Melody").Note(0, 60).Build();

        Assert.ThrowsExactly<ChartFormatException>(() => KarParser.Parse(file));
    }
}
