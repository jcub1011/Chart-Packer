using ChartShared.Core.Charting;
using ChartShared.Core.Lyrics;

namespace ChartShared.Core.Tests.Lyrics;

[TestClass]
public sealed class UltraStarWriterTests
{
    // 480 ticks a beat at 120 BPM: a tick is 1/960 of a second, here a second into the audio. At #BPM 120 an UltraStar beat
    // is 15 / 120 = 1/8 of a second: 120 ticks, a 16th note.
    private const int Resolution = 480;

    private static double Seconds(long tick) => 1 + (tick / 960.0);

    private static readonly UltraStarSong Song = new("Song", "Band", Audio: "song.ogg");

    // Every start and end on an eighth note.
    private static VocalTrackContent Part() => VocalTrackContent.Empty("PART VOCALS").With(
        notes: [new(0, 240, 60), new(480, 240, 62), new(720, 240, 64), new(1920, 480, 57)],
        lyrics: [new(0, "Hel-"), new(480, "lo"), new(720, "+"), new(1920, "bye#")],
        phrases: [new(0, 960), new(1920, 480)]);

    private static VocalTrackContent Notes(params (long Tick, long Length)[] notes) => VocalTrackContent.Empty("PART VOCALS").With(
        notes: notes.Select(static n => new VocalNoteEvent(n.Tick, n.Length, 60)),
        lyrics: notes.Select(static (n, i) => new VocalLyricEvent(n.Tick, ((char)('a' + i)).ToString())));

    private static string BpmOf(string text) => text.Split('\n').Single(static l => l.StartsWith("#BPM:", StringComparison.Ordinal))[5..];

    [TestMethod]
    public void APartIsWrittenWithItsPitchesBeatsAndLines()
    {
        string text = UltraStarWriter.Write([Part()], Seconds, Resolution, Song);

        Assert.AreEqual(
            "#VERSION:1.1.0\n#TITLE:Song\n#ARTIST:Band\n#MP3:song.ogg\n#AUDIO:song.ogg\n#BPM:120\n#GAP:1000\n"
            + ": 0 2 0 Hel\n: 4 2 2 lo \n: 6 2 4 ~\n- 8\n"
            + "R 16 4 -3 bye \nE\n",
            text);
    }

    [TestMethod]
    public void NotesOnTheGridNeedNoMoreThanTheSongsTempo()
    {
        Assert.AreEqual("120", BpmOf(UltraStarWriter.Write([Part()], Seconds, Resolution, Song)));
    }

    [TestMethod]
    public void TripletsAreFitByAMultipleThatIsntAPowerOfTwo()
    {
        // 160 ticks is a triplet eighth: three times the tempo puts a beat on each.
        string text = UltraStarWriter.Write([Notes((0, 160), (160, 160), (320, 160))], Seconds, Resolution, Song);

        Assert.AreEqual("360", BpmOf(text));
    }

    [TestMethod]
    public void TheMultipleIsTheFirstToPutEveryNoteWithinTheLimit()
    {
        // The second note is 500 ticks in, 0.5208 s: 20.8 ms off at 1 to 3 times the tempo, 10.4 ms at four times, and
        // 4.2 ms at five times.
        string text = UltraStarWriter.Write([Notes((0, 480), (500, 460))], Seconds, Resolution, Song);

        Assert.AreEqual("600", BpmOf(text));
        var start = UltraStarParser.Parse(text).Lines[0].Words[1].Start!.Value.TotalSeconds;
        Assert.AreEqual(Seconds(500), start, UltraStarWriter.MaxErrorSeconds);
    }

    [TestMethod]
    public void TheTempoIsTheAverageFromTheFirstNoteToTheLast()
    {
        // 90 BPM until the singing starts at tick 960, then 120: the notes are on 120's eighths, which 90 would need
        // twice its tempo, 180, to fit.
        static double Changing(long tick) => tick < 960 ? tick / 720.0 : (960 / 720.0) + ((tick - 960) / 960.0);

        string text = UltraStarWriter.Write([Notes((960, 240), (1200, 240), (1440, 480), (1920, 240))], Changing, Resolution, Song);

        Assert.AreEqual("120", BpmOf(text));
    }

    [TestMethod]
    public void NotesTooCloseForAnyBeatStopTheSearchOnceABeatIsShortEnough()
    {
        // 5 ticks apart: they're moved a beat apart at any tempo, so the search ends at the first beat no longer than twice
        // the limit, 840, seven times 120. Of two notes on a tick only the lowest is written.
        var content = VocalTrackContent.Empty("PART VOCALS").With(
            notes: [new(0, 5, 60), new(0, 5, 64), new(5, 100, 62)],
            lyrics: [new(0, "a"), new(5, "b")]);

        string text = UltraStarWriter.Write([content], Seconds, Resolution, Song);

        Assert.AreEqual("840", BpmOf(text));
        Assert.EndsWith(": 0 1 0 a \n: 1 5 2 b \nE\n", text);
    }

    [TestMethod]
    public void TwoPartsAreWrittenAsADuetWithOneTiming()
    {
        var other = VocalTrackContent.Empty("HARM2").With(notes: [new(960, 480, 67)], lyrics: [new(960, "two")], phrases: [new(960, 480)]);

        string text = UltraStarWriter.Write([Part(), other], Seconds, Resolution, Song);

        Assert.Contains("\nP1\n: 0 2 0 Hel\n", text);
        Assert.Contains("\nP2\n: 8 4 7 two \nE\n", text);
        var document = UltraStarParser.Parse(text);
        Assert.AreSequenceEqual([1, 2], [.. document.Singers]);
        Assert.AreEqual("two", document.ForSinger(2).Lines.Single().Words.Single().Text);
    }

    [TestMethod]
    public void WhatTheWriterWritesTheParserReadsBack()
    {
        var document = UltraStarParser.Parse(UltraStarWriter.Write([Part()], Seconds, Resolution, Song));

        Assert.HasCount(2, document.Lines);
        var first = document.Lines[0].Words;
        Assert.AreSequenceEqual(["Hel-", "lo", "+"], [.. first.Select(static w => w.Text)]);
        Assert.AreSequenceEqual([60, 62, 64], [.. first.Select(static w => w.Pitch!.Value)]);
        Assert.AreSequenceEqual([1.0, 1.5, 1.75], [.. first.Select(static w => w.Start!.Value.TotalSeconds)]);
        var spoken = document.Lines[1].Words.Single();
        Assert.AreEqual("bye", spoken.Text);
        Assert.IsNull(spoken.Pitch);
        Assert.AreEqual(3.0, spoken.Start!.Value.TotalSeconds);
    }

    [TestMethod]
    public void APartWithNoLyricsWritesNothing()
    {
        var content = VocalTrackContent.Empty("PART VOCALS").With(notes: [new(0, 100, 60)]);

        Assert.AreEqual(string.Empty, UltraStarWriter.Write([content], Seconds, Resolution, Song));
        Assert.AreEqual(string.Empty, UltraStarWriter.Write([VocalTrackContent.Empty("PART VOCALS")], Seconds, Resolution, Song));
    }
}
