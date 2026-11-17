using ChartShared.Core.Charting;
using ChartShared.Core.Lyrics;

namespace ChartShared.Core.Tests.Lyrics;

[TestClass]
public sealed class LrcWriterTests
{
    // 480 ticks a beat at 120 BPM: a tick is 1/960 of a second.
    private static double Seconds(long tick) => tick / 960.0;

    private static VocalTrackContent Part() => VocalTrackContent.Empty("PART VOCALS").With(
        notes: [new(0, 240, 60), new(480, 240, 62), new(720, 240, 62), new(1920, 480, 60)],
        lyrics: [new(0, "Hel-"), new(480, "lo#"), new(720, "+"), new(1920, "bye^")],
        phrases: [new(0, 960), new(1920, 480)]);

    [TestMethod]
    public void EachPhraseIsALineStampedWithItsFirstLyric()
    {
        string lrc = LrcWriter.Write(Part(), Seconds, includeWordTimes: false);

        Assert.AreEqual("[00:00.00]Hello\n[00:02.00]bye\n", lrc);
    }

    [TestMethod]
    public void A2GivesEachSyllableATagAndTheLineAnEnd()
    {
        string lrc = LrcWriter.Write(Part(), Seconds, includeWordTimes: true);

        Assert.AreEqual("[00:00.00]<00:00.00>Hel<00:00.50>lo<00:00.75>\n[00:02.00]<00:02.00>bye<00:02.50>\n", lrc);
    }

    [TestMethod]
    public void LyricsOutsideEveryPhraseAreLinesOfTheirOwn()
    {
        var content = VocalTrackContent.Empty("PART VOCALS").With(
            notes: [new(0, 100, 60), new(480, 100, 60)],
            lyrics: [new(0, "one"), new(480, "two")],
            phrases: [new(0, 200)]);

        string lrc = LrcWriter.Write(content, Seconds, includeWordTimes: false);

        Assert.AreEqual("[00:00.00]one\n[00:00.50]two\n", lrc);
    }

    [TestMethod]
    public void WhatTheWriterWritesTheParserReadsBack()
    {
        string lrc = LrcWriter.Write(Part(), Seconds, includeWordTimes: true);

        var document = LrcParser.Parse(lrc);

        Assert.HasCount(2, document.Lines);
        Assert.AreSequenceEqual(["Hel-", "lo"], [.. document.Lines[0].Words.Select(static w => w.Text)]);
        Assert.AreEqual(0.5, document.Lines[0].Words[1].Start!.Value.TotalSeconds);
        Assert.AreEqual(0.75, document.Lines[0].End!.Value.TotalSeconds);
    }

    [TestMethod]
    public void APartWithNoLyricsWritesNothing()
    {
        Assert.AreEqual(string.Empty, LrcWriter.Write(VocalTrackContent.Empty("PART VOCALS"), Seconds, includeWordTimes: true));
    }
}
