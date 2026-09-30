using System.Text;
using ChartPacker.Core.Charts;
using ChartPacker.Core.Merging;
using ChartPacker.Core.Tests.Charts;

namespace ChartPacker.Core.Tests.Merging;

[TestClass]
public sealed class VocalsEditorTests
{
    private static readonly KeyValuePair<string, string>[] NoMetadata = [];

    [TestMethod]
    public void MidiVocalsAreReplacedAndTheOtherTracksKeptAsTheyAre()
    {
        byte[] target = new MidiBuilder()
            .Track("tempo").Tempo(0, 120)
            .Track("EVENTS").Text(0, "[section Intro]")
            .Track("PART VOCALS").Note(0, 60).Meta(0, 0x05, Latin1("old"))
            .Track("PART GUITAR").Note(0, 96).Note(480, 97)
            .Build();
        // At 60 BPM and 192 ticks a beat, tick 192 is one second in: tick 960 of the target.
        byte[] source = new MidiBuilder(192)
            .Track("tempo").Tempo(0, 60)
            .Track("PART VOCALS").Note(192, 105, 192).Note(192, 62, 96).Meta(192, 0x05, Latin1("new"))
            .Track("HARM1").Note(192, 64, 96)
            .Build();

        var result = VocalsEditor.Apply(target, "notes.mid", NoMetadata, Chart(source));
        var vocals = VocalReader.Read(result.Bytes, "notes.mid");

        Assert.IsFalse(result.LyricsOnly);
        CollectionAssert.AreEqual(new[] { "PART VOCALS", "HARM1" }, vocals.Parts.Select(p => p.TrackName).ToArray());
        CollectionAssert.AreEqual(new[] { new VocalLyric(960, "new") }, vocals.Main!.Lyrics.ToArray());
        CollectionAssert.AreEqual(new[] { new VocalNote(960, 480, 62) }, vocals.Main.Notes.ToArray());
        CollectionAssert.AreEqual(new[] { new VocalPhrase(960, 960) }, vocals.Main.Phrases.ToArray());
        CollectionAssert.AreEqual(TrackChunk(target, "PART GUITAR"), TrackChunk(result.Bytes, "PART GUITAR"));
        CollectionAssert.AreEqual(TrackChunk(target, "EVENTS"), TrackChunk(result.Bytes, "EVENTS"));
        Assert.IsTrue(ChartReader.Read(result.Bytes, "notes.mid").Tempo.SameAs(ChartReader.Read(target, "notes.mid").Tempo));
    }

    [TestMethod]
    public void MidiVocalsAreAddedToAChartWithout()
    {
        byte[] target = new MidiBuilder().Track("tempo").Tempo(0, 120).Track("PART GUITAR").Note(0, 96).Build();
        byte[] source = new MidiBuilder().Track("tempo").Tempo(0, 120).Track("PART VOCALS").Note(480, 60).Meta(480, 0x05, Latin1("hi")).Build();

        var vocals = VocalReader.Read(VocalsEditor.Apply(target, "notes.mid", NoMetadata, Chart(source)).Bytes, "notes.mid");

        CollectionAssert.AreEqual(new[] { new VocalLyric(480, "hi") }, vocals.Main!.Lyrics.ToArray());
    }

    [TestMethod]
    public void TheDelaysPlaceTheVocalsBySongTime()
    {
        byte[] target = new MidiBuilder().Track("tempo").Tempo(0, 120).Track("PART GUITAR").Note(0, 96).Build();
        byte[] source = new MidiBuilder().Track("tempo").Tempo(0, 120).Track("PART VOCALS").Note(960, 60).Meta(960, 0x05, Latin1("hi")).Build();

        // The source plays 250 ms later than its ticks; the target 500 ms later, so the lyric moves a quarter-second earlier.
        var result = VocalsEditor.Apply(target, "notes.mid", [KeyValuePair.Create("delay", "500")], Source(new MidiChart(source, TimeSpan.FromMilliseconds(250), ChartReadOptions.Default)));

        Assert.AreEqual(720, VocalReader.Read(result.Bytes, "notes.mid").Main!.Lyrics.Single().Tick);
    }

    [TestMethod]
    public void VocalsFromASongWithAShorterDelayCantStartBeforeTheSong()
    {
        byte[] target = new MidiBuilder().Track("tempo").Tempo(0, 120).Track("PART GUITAR").Note(0, 96).Build();
        // A tenth of a second in, where the target's half-second delay has no tick for it.
        byte[] source = new MidiBuilder().Track("tempo").Tempo(0, 120).Track("PART VOCALS").Note(96, 60).Meta(96, 0x05, Latin1("hi")).Build();
        KeyValuePair<string, string>[] delayed = [KeyValuePair.Create("delay", "500")];

        Assert.ThrowsExactly<ChartFormatException>(() => VocalsEditor.Apply(target, "notes.mid", delayed, Chart(source)));

        var result = VocalsEditor.Apply(target, "notes.mid", delayed, Chart(source), TimeSpan.FromMilliseconds(500));
        Assert.AreEqual(96, VocalReader.Read(result.Bytes, "notes.mid").Main!.Lyrics.Single().Tick);
    }

    [TestMethod]
    public void StartOfIsTheFirstEventInTheTargetsSongTime()
    {
        // Two beats at 120 BPM, then a beat at 60 BPM: tick 1440 is two seconds in.
        byte[] midi = new MidiBuilder().Track("tempo").Tempo(0, 120).Tempo(960, 60).Track("PART VOCALS").Note(1440, 60).Build();
        var vocals = VocalReader.Read(midi, "notes.mid");

        Assert.AreEqual(TimeSpan.FromSeconds(1.5), VocalsEditor.StartOf(vocals, TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(750)));
        Assert.IsNull(VocalsEditor.StartOf(VocalReader.Read(new MidiBuilder().Track("PART GUITAR").Note(0, 96).Build(), "notes.mid"), TimeSpan.Zero, TimeSpan.Zero));
    }

    [TestMethod]
    public void RemovingDropsTheVocalsAndHarmonies()
    {
        byte[] target = new MidiBuilder()
            .Track("tempo").Tempo(0, 120)
            .Track("PART VOCALS").Note(0, 60)
            .Track("HARM1").Note(0, 60)
            .Track("HARM2").Note(0, 62)
            .Track("PART GUITAR").Note(0, 96)
            .Build();

        byte[] result = VocalsEditor.Apply(target, "notes.mid", NoMetadata, null).Bytes;

        Assert.IsEmpty(VocalReader.Read(result, "notes.mid").Parts);
        CollectionAssert.AreEqual(TrackChunk(target, "PART GUITAR"), TrackChunk(result, "PART GUITAR"));
    }

    [TestMethod]
    public void TempoInTheVocalTrackCantBeDropped()
    {
        byte[] target = new MidiBuilder()
            .Track("tempo").Tempo(0, 120)
            .Track("PART VOCALS").Tempo(480, 90).Note(0, 60)
            .Build();

        Assert.ThrowsExactly<ChartFormatException>(() => VocalsEditor.Apply(target, "notes.mid", NoMetadata, null));
    }

    [TestMethod]
    public void ChartEventsGetTheNewLyricsAndKeepTheirOtherEvents()
    {
        byte[] target = ChartText("""
            [Events]
            {
              0 = E "section Intro"
              96 = E "phrase_start"
              96 = E "lyric old"
              192 = E "phrase_end"
              768 = E "end"
            }
            [ExpertSingle]
            {
              0 = N 0 0
            }
            """);
        // Tick 480 at 480 a beat is half a second in: tick 192 at 192 a beat.
        byte[] source = new MidiBuilder()
            .Track("tempo").Tempo(0, 120)
            .Track("PART VOCALS").Note(480, 105, 480).Note(480, 60, 240).Meta(480, 0x05, Latin1("new\"er"))
            .Build();

        var result = VocalsEditor.Apply(target, "notes.chart", NoMetadata, Chart(source));
        string text = Encoding.UTF8.GetString(result.Bytes);

        Assert.IsTrue(result.LyricsOnly);
        Assert.DoesNotContain("lyric old", text);
        Assert.DoesNotContain("96 = E", text);
        StringAssert.Contains(text, """
              0 = E "section Intro"
              192 = E "phrase_start"
              192 = E "lyric newer"
              384 = E "phrase_end"
              768 = E "end"
            """.ReplaceLineEndings("\n"));
        CollectionAssert.AreEqual(new[] { new VocalLyric(192, "newer") }, VocalReader.Read(result.Bytes, "notes.chart").Main!.Lyrics.ToArray());
    }

    [TestMethod]
    public void AChartWithoutEventsGetsThem()
    {
        byte[] target = ChartText("""
            [ExpertSingle]
            {
              0 = N 0 0
            }
            """);
        byte[] source = new MidiBuilder().Track("tempo").Tempo(0, 120).Track("PART VOCALS").Meta(480, 0x05, Latin1("hi")).Build();

        var result = VocalsEditor.Apply(target, "notes.chart", NoMetadata, Chart(source));

        Assert.IsFalse(result.LyricsOnly);
        CollectionAssert.AreEqual(new[] { new VocalLyric(192, "hi") }, VocalReader.Read(result.Bytes, "notes.chart").Main!.Lyrics.ToArray());
        Assert.AreEqual(1, ChartReaderTests.Notes(ChartReader.Read(result.Bytes, "notes.chart"), ChartInstrument.Guitar).Length);
    }

    [TestMethod]
    public void RemovingAChartsLyricsLeavesTheRestAsItWas()
    {
        const string Before = """
            [Events]
            {
              0 = E "section Intro"
              96 = E "lyric old"
            }
            [ExpertSingle]
            {
              0 = N 0 0
            }
            """;

        byte[] result = VocalsEditor.Apply(ChartText(Before), "notes.chart", NoMetadata, null).Bytes;

        Assert.AreEqual(Encoding.UTF8.GetString(ChartText(Before)).Replace("  96 = E \"lyric old\"\n", "", StringComparison.Ordinal), Encoding.UTF8.GetString(result));
    }

    [TestMethod]
    public void ShiftMovesAMidisVocalsAndKeepsTheOtherTracks()
    {
        byte[] target = new MidiBuilder()
            .Track("tempo").Tempo(0, 120)
            .Track("PART VOCALS").Note(480, 105, 960).Note(480, 60, 240).Meta(480, 0x05, Latin1("hi"))
            .Track("HARM1").Note(480, 62, 240)
            .Track("PART GUITAR").Note(0, 96)
            .Build();

        // Half a second is a beat at 120 BPM: 480 ticks.
        byte[] result = VocalsEditor.Shift(target, "notes.mid", NoMetadata, TimeSpan.FromMilliseconds(500)).Bytes;
        var vocals = VocalReader.Read(result, "notes.mid");

        CollectionAssert.AreEqual(new[] { new VocalLyric(960, "hi") }, vocals.Main!.Lyrics.ToArray());
        CollectionAssert.AreEqual(new[] { new VocalNote(960, 240, 60) }, vocals.Main.Notes.ToArray());
        CollectionAssert.AreEqual(new[] { new VocalPhrase(960, 960) }, vocals.Main.Phrases.ToArray());
        Assert.AreEqual(960, vocals.Parts.Single(p => p.TrackName == "HARM1").Notes.Single().Tick);
        CollectionAssert.AreEqual(TrackChunk(target, "PART GUITAR"), TrackChunk(result, "PART GUITAR"));
    }

    [TestMethod]
    public void ShiftMovesAChartsLyrics()
    {
        byte[] target = ChartText("""
            [Events]
            {
              0 = E "section Intro"
              192 = E "phrase_start"
              192 = E "lyric hi"
              384 = E "phrase_end"
            }
            """);

        // A quarter-second earlier is half a beat at 120 BPM: 96 ticks.
        byte[] result = VocalsEditor.Shift(target, "notes.chart", NoMetadata, TimeSpan.FromMilliseconds(-250)).Bytes;
        var part = VocalReader.Read(result, "notes.chart").Main!;

        CollectionAssert.AreEqual(new[] { new VocalLyric(96, "hi") }, part.Lyrics.ToArray());
        CollectionAssert.AreEqual(new[] { new VocalPhrase(96, 192) }, part.Phrases.ToArray());
        StringAssert.Contains(Encoding.UTF8.GetString(result), "0 = E \"section Intro\"");
    }

    [TestMethod]
    public void ShiftBeforeTheStartThrows()
    {
        byte[] target = new MidiBuilder().Track("tempo").Tempo(0, 120).Track("PART VOCALS").Note(480, 60).Build();

        Assert.ThrowsExactly<ChartFormatException>(() => VocalsEditor.Shift(target, "notes.mid", NoMetadata, TimeSpan.FromMilliseconds(-501)));
        Assert.AreEqual(0, VocalReader.Read(VocalsEditor.Shift(target, "notes.mid", NoMetadata, TimeSpan.FromMilliseconds(-500)).Bytes, "notes.mid").Main!.Notes.Single().Tick);
    }

    [TestMethod]
    public void ShiftWithoutVocalsOrOffsetLeavesTheChart()
    {
        byte[] withVocals = new MidiBuilder().Track("PART VOCALS").Note(0, 60).Build();
        byte[] without = new MidiBuilder().Track("PART GUITAR").Note(0, 96).Build();

        Assert.AreSame(withVocals, VocalsEditor.Shift(withVocals, "notes.mid", NoMetadata, TimeSpan.Zero).Bytes);
        Assert.AreSame(without, VocalsEditor.Shift(without, "notes.mid", NoMetadata, TimeSpan.FromMilliseconds(100)).Bytes);
    }

    [TestMethod]
    public void ApplyMovesTheNewVocalsByTheOffset()
    {
        byte[] target = new MidiBuilder().Track("tempo").Tempo(0, 120).Track("PART GUITAR").Note(0, 96).Build();
        byte[] source = new MidiBuilder().Track("tempo").Tempo(0, 120).Track("PART VOCALS").Note(960, 60).Meta(960, 0x05, Latin1("hi")).Build();

        var result = VocalsEditor.Apply(target, "notes.mid", NoMetadata, Chart(source), TimeSpan.FromMilliseconds(-250));

        Assert.AreEqual(720, VocalReader.Read(result.Bytes, "notes.mid").Main!.Lyrics.Single().Tick);
    }

    private static VocalsSource Chart(byte[] midi) => Source(new MidiChart(midi, TimeSpan.Zero, ChartReadOptions.Default));

    private static VocalsSource Source(MidiChart chart) => new("source.mid", chart, VocalReader.Read(chart.Midi, "notes.mid"));

    private static byte[] Latin1(string text) => Encoding.Latin1.GetBytes(text);

    private static byte[] ChartText(string tracks) => Encoding.UTF8.GetBytes($$"""
        [Song]
        {
          Resolution = 192
        }
        [SyncTrack]
        {
          0 = B 120000
        }
        {{tracks}}

        """.ReplaceLineEndings("\n"));

    /// <summary>The bytes of the first track named <paramref name="name"/>, header included.</summary>
    private static byte[] TrackChunk(byte[] midi, string name)
    {
        var (_, _, chunks) = MidiFile.ReadChunks(midi);
        var chunk = chunks.Where(c => c.Id == "MTrk").First(c => MidiFile.NameOf(midi, MidiFile.ReadEvents(midi, c)) == name);
        return midi[chunk.Start..chunk.End];
    }
}
