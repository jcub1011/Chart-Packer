using System.Text;
using ChartPacker.Core.Charts;

namespace ChartPacker.Core.Tests.Charts;

[TestClass]
public sealed class ChartToMidiTests
{
    [TestMethod]
    public void ConvertedChartReadsTheSameAsTheChart()
    {
        byte[] chart = Encoding.UTF8.GetBytes(ChartReaderTests.Chart);
        var before = ChartReader.Read(chart, "notes.chart");

        var conversion = ChartToMidi.Convert(chart);
        var after = ChartReader.Read(conversion.Midi, "notes.mid", new ChartReadOptions(conversion.ProDrums));

        Assert.IsTrue(after.Tempo.SameAs(before.Tempo));
        CollectionAssert.AreEquivalent(before.Tracks.Keys.ToArray(), after.Tracks.Keys.ToArray());
        foreach (var (key, track) in before.Tracks)
            CollectionAssert.AreEqual(track.Notes.ToArray(), after.Tracks[key].Notes.ToArray(), key.ToString());
        Assert.IsEmpty(conversion.Warnings);
    }

    [TestMethod]
    public void OffsetAndProDrumsAreHandedBack()
    {
        var conversion = ChartToMidi.Convert(Encoding.UTF8.GetBytes(ChartReaderTests.Chart));

        Assert.AreEqual(TimeSpan.FromSeconds(0.25), conversion.Offset);
        // The chart's drums have cymbal markers.
        Assert.IsTrue(conversion.ProDrums);
    }

    [TestMethod]
    public void ForcedNoteKeepsItsStrumThoughMidiWouldCallItAHopo()
    {
        // 48 ticks apart at 192 is a natural HOPO for both formats, so the forced flag makes it a strum.
        byte[] chart = Chart("""
            [ExpertSingle]
            {
              0 = N 0 0
              48 = N 1 0
              48 = N 5 0
              96 = N 2 0
            }
            """);

        var conversion = ChartToMidi.Convert(chart);
        var notes = ChartReaderTests.Notes(ChartReader.Read(conversion.Midi, "notes.mid"), ChartInstrument.Guitar);

        CollectionAssert.AreEqual(
            new[] { NoteFlags.None, NoteFlags.None, NoteFlags.Hopo },
            notes.Select(n => n.Flags).ToArray());
        Assert.IsEmpty(conversion.Warnings);
    }

    [TestMethod]
    public void TapsAndOpensStayWithTheirDifficulty()
    {
        byte[] chart = Chart("""
            [ExpertSingle]
            {
              0 = N 0 0
              0 = N 6 0
              480 = N 7 0
            }
            [HardSingle]
            {
              0 = N 0 0
              480 = N 1 0
            }
            """);

        var midi = ChartReader.Read(ChartToMidi.Convert(chart).Midi, "notes.mid");

        CollectionAssert.AreEqual(
            new[] { new ChartNote(0, 0, 0, NoteFlags.Tap), new ChartNote(480, 0, ChartNote.OpenLane, NoteFlags.None) },
            ChartReaderTests.Notes(midi, ChartInstrument.Guitar));
        CollectionAssert.AreEqual(
            new[] { new ChartNote(0, 0, 0, NoteFlags.None), new ChartNote(480, 0, 1, NoteFlags.None) },
            ChartReaderTests.Notes(midi, ChartInstrument.Guitar, ChartDifficulty.Hard));
    }

    [TestMethod]
    public void ProDrumsWithOnlyCymbalsStillReadAsCymbals()
    {
        byte[] chart = Chart("""
            [ExpertDrums]
            {
              0 = N 2 0
              0 = N 66 0
              96 = N 4 0
              96 = N 68 0
            }
            """);

        var conversion = ChartToMidi.Convert(chart);
        var notes = ChartReaderTests.Notes(ChartReader.Read(conversion.Midi, "notes.mid", new ChartReadOptions(conversion.ProDrums)), ChartInstrument.Drums);

        Assert.IsTrue(conversion.ProDrums);
        Assert.IsTrue(notes.All(n => n.Flags == NoteFlags.Cymbal));
    }

    [TestMethod]
    public void SectionsBecomeEventsAndLyricsAVocalsTrack()
    {
        byte[] chart = Chart("""
            [Events]
            {
              0 = E "section Intro"
              192 = E "phrase_start"
              192 = E "lyric Hel-"
              288 = E "lyric lo"
              384 = E "phrase_end"
              768 = E "end"
            }
            [ExpertSingle]
            {
              0 = N 0 0
            }
            """);

        string midi = Encoding.Latin1.GetString(ChartToMidi.Convert(chart).Midi);

        StringAssert.Contains(midi, "EVENTS");
        StringAssert.Contains(midi, "[section Intro]");
        StringAssert.Contains(midi, "[end]");
        StringAssert.Contains(midi, "PART VOCALS");
        StringAssert.Contains(midi, "Hel-");
        Assert.DoesNotContain("[lyric", midi);
    }

    [TestMethod]
    public void SustainsKeepTheirLength()
    {
        byte[] chart = Chart("""
            [ExpertSingle]
            {
              0 = N 0 192
              192 = N 0 0
            }
            """);

        var notes = ChartReaderTests.Notes(ChartReader.Read(ChartToMidi.Convert(chart).Midi, "notes.mid"), ChartInstrument.Guitar);

        CollectionAssert.AreEqual(new long[] { 192, 0 }, notes.Select(n => n.Length).ToArray());
    }

    [TestMethod]
    public void SustainsOfAThirdOfABeatStaySustainsOnEveryDifficulty()
    {
        // A third of a beat at 192 is the default MIDI cutoff, so the converted chart needs one just under it.
        string Difficulty(string name) => $$"""
            [{{name}}Single]
            {
              0 = N 0 64
              192 = N 1 64
              384 = N 2 0
            }
            """;
        byte[] chart = Chart(string.Join("\n", new[] { "Easy", "Medium", "Hard", "Expert" }.Select(Difficulty)));

        var conversion = ChartToMidi.Convert(chart);
        var midi = ChartReader.Read(conversion.Midi, "notes.mid", new ChartReadOptions(conversion.ProDrums, conversion.SustainCutoff));

        Assert.IsEmpty(conversion.Warnings);
        Assert.AreEqual(63L, conversion.SustainCutoff);
        foreach (var difficulty in Enum.GetValues<ChartDifficulty>())
            CollectionAssert.AreEqual(new long[] { 64, 64, 0 }, ChartReaderTests.Notes(midi, ChartInstrument.Guitar, difficulty).Select(n => n.Length).ToArray(), difficulty.ToString());
    }

    [TestMethod]
    public void ShortSustainsGetTheCutoffTheyNeedAndNotesStayNotes()
    {
        byte[] chart = Chart("""
            [ExpertSingle]
            {
              0 = N 0 10
              192 = N 1 0
            }
            """);

        var conversion = ChartToMidi.Convert(chart);
        var notes = ChartReaderTests.Notes(ChartReader.Read(conversion.Midi, "notes.mid", new ChartReadOptions(SustainCutoff: conversion.SustainCutoff)), ChartInstrument.Guitar);

        Assert.AreEqual(9L, conversion.SustainCutoff);
        CollectionAssert.AreEqual(new long[] { 10, 0 }, notes.Select(n => n.Length).ToArray());
        Assert.IsEmpty(conversion.Warnings);
    }

    [TestMethod]
    public void SustainsLongerThanAThirdOfABeatNeedNoCutoff()
    {
        var conversion = ChartToMidi.Convert(Chart("""
            [ExpertSingle]
            {
              0 = N 0 65
            }
            """));

        Assert.IsNull(conversion.SustainCutoff);
        Assert.IsEmpty(conversion.Warnings);
    }

    [TestMethod]
    public void OneTickSustainIsHeldLongEnoughToStayASustain()
    {
        byte[] chart = Chart("""
            [ExpertSingle]
            {
              0 = N 0 1
              192 = N 1 0
            }
            """);

        var conversion = ChartToMidi.Convert(chart);
        var notes = ChartReaderTests.Notes(ChartReader.Read(conversion.Midi, "notes.mid", new ChartReadOptions(SustainCutoff: conversion.SustainCutoff)), ChartInstrument.Guitar);

        // Clear of the lowest cutoff, 2, that a one-tick note is under.
        Assert.AreEqual(2L, conversion.SustainCutoff);
        CollectionAssert.AreEqual(new long[] { 3, 0 }, notes.Select(n => n.Length).ToArray());
        Assert.IsEmpty(conversion.Warnings);
    }

    [TestMethod]
    public void SongIniSustainCutoffMakesShortChartSustainsNotes()
    {
        byte[] chart = Chart("""
            [ExpertSingle]
            {
              0 = N 0 50
            }
            """);

        var conversion = ChartToMidi.Convert(chart, ChartReadOptions.From([new("sustain_cutoff_threshold", "64")]));
        var notes = ChartReaderTests.Notes(ChartReader.Read(conversion.Midi, "notes.mid"), ChartInstrument.Guitar);

        Assert.AreEqual(0, notes.Single().Length);
        Assert.IsEmpty(conversion.Warnings);
    }

    [TestMethod]
    public void ExpertCymbalsWinOverTheTomsOfADifficultyWithoutCymbalMarkers()
    {
        byte[] chart = Chart("""
            [ExpertDrums]
            {
              0 = N 2 0
              0 = N 66 0
              192 = N 3 0
            }
            [HardDrums]
            {
              0 = N 2 0
              192 = N 3 0
            }
            """);

        var conversion = ChartToMidi.Convert(chart);
        var midi = ChartReader.Read(conversion.Midi, "notes.mid", new ChartReadOptions(conversion.ProDrums));

        Assert.IsEmpty(conversion.Warnings);
        foreach (var difficulty in new[] { ChartDifficulty.Expert, ChartDifficulty.Hard })
        {
            CollectionAssert.AreEqual(
                new[] { new ChartNote(0, 0, 1, NoteFlags.Cymbal), new ChartNote(192, 0, 2, NoteFlags.None) },
                ChartReaderTests.Notes(midi, ChartInstrument.Drums, difficulty),
                difficulty.ToString());
        }
    }

    [TestMethod]
    public void DifficultyWithCymbalMarkersThatDisagreesIsWarnedWithWhere()
    {
        // Hard marks its blue as a cymbal, so its yellow at 384 is meant as a tom; Expert's is a cymbal.
        byte[] chart = Chart("""
            [ExpertDrums]
            {
              0 = N 3 0
              0 = N 67 0
              384 = N 2 0
              384 = N 66 0
            }
            [HardDrums]
            {
              0 = N 3 0
              0 = N 67 0
              384 = N 2 0
            }
            """);

        var conversion = ChartToMidi.Convert(chart);

        var warning = conversion.Warnings.Single();
        Assert.AreEqual((ChartMergeIssue.MarkingsChanged, "PART DRUMS", ChartDifficulty.Hard), (warning.Issue, warning.TrackName, warning.Difficulty));
        // Two beats at 120 BPM.
        CollectionAssert.AreEqual(new[] { TimeSpan.FromSeconds(1) }, warning.Times.ToArray());
    }

    [TestMethod]
    public void StarPowerThatDiffersOnAnEasierDifficultyIsWarnedWithWhere()
    {
        byte[] chart = Chart("""
            [ExpertSingle]
            {
              0 = N 0 0
              0 = S 2 96
              768 = N 0 0
            }
            [HardSingle]
            {
              0 = N 0 0
              768 = N 0 0
              768 = S 2 96
            }
            """);

        var conversion = ChartToMidi.Convert(chart);

        var warning = conversion.Warnings.Single();
        Assert.AreEqual(ChartDifficulty.Hard, warning.Difficulty);
        Assert.AreEqual(ChartMergeIssue.MarkingsChanged, warning.Issue);
        CollectionAssert.AreEqual(new[] { TimeSpan.Zero, TimeSpan.FromSeconds(2) }, warning.Times.ToArray());
    }

    [TestMethod]
    public void HopoIsForcedThoughStarPowerDiffersToo()
    {
        // 66 ticks at 192 is a strum in a .chart, but within Rock Band's 68 in MIDI. Hard's note there also
        // takes Expert's star power, which Hard doesn't have.
        byte[] chart = Chart("""
            [ExpertSingle]
            {
              0 = N 0 0
              66 = N 1 0
              66 = S 2 10
            }
            [HardSingle]
            {
              0 = N 0 0
              66 = N 1 0
            }
            """);

        var conversion = ChartToMidi.Convert(chart);
        var hard = ChartReaderTests.Notes(ChartReader.Read(conversion.Midi, "notes.mid"), ChartInstrument.Guitar, ChartDifficulty.Hard);

        Assert.AreEqual(new ChartNote(66, 0, 1, NoteFlags.StarPower), hard[1]);
        Assert.AreEqual(ChartDifficulty.Hard, conversion.Warnings.Single().Difficulty);
    }

    [TestMethod]
    public void NoteYargWouldReadDifferentlyIsForced()
    {
        // At 480, 162 ticks is a .chart HOPO and within Rock Band's 170, but past YARG's third of a beat and a tick.
        byte[] chart = Chart("""
            [ExpertSingle]
            {
              0 = N 0 0
              162 = N 1 0
            }
            """, resolution: 480);

        var conversion = ChartToMidi.Convert(chart);
        var yarg = ChartReader.Read(conversion.Midi, "notes.mid", ChartReadOptions.Default with { MidiHopoTicks = 161 });

        Assert.AreEqual(NoteFlags.Hopo, ChartReaderTests.Notes(yarg, ChartInstrument.Guitar)[1].Flags);
        Assert.AreEqual(NoteFlags.Hopo, ChartReaderTests.Notes(ChartReader.Read(conversion.Midi, "notes.mid"), ChartInstrument.Guitar)[1].Flags);
        Assert.IsEmpty(conversion.Warnings);
    }

    [TestMethod]
    public void DoubleKickBelowExpertIsNoNoteEitherSide()
    {
        byte[] chart = Chart("""
            [ExpertDrums]
            {
              0 = N 32 0
              192 = N 1 0
            }
            [HardDrums]
            {
              0 = N 32 0
              192 = N 1 0
            }
            """);

        var conversion = ChartToMidi.Convert(chart);
        var midi = ChartReader.Read(conversion.Midi, "notes.mid");

        Assert.IsEmpty(conversion.Warnings);
        CollectionAssert.AreEqual(
            new[] { new ChartNote(0, 0, ChartNote.OpenLane, NoteFlags.DoubleKick), new ChartNote(192, 0, 0, NoteFlags.None) },
            ChartReaderTests.Notes(midi, ChartInstrument.Drums));
        CollectionAssert.AreEqual(
            new[] { new ChartNote(192, 0, 0, NoteFlags.None) },
            ChartReaderTests.Notes(midi, ChartInstrument.Drums, ChartDifficulty.Hard));
    }

    [TestMethod]
    public void LyricsOutsideLatin1KeepTheirText()
    {
        byte[] chart = Chart("""
            [Events]
            {
              192 = E "phrase_start"
              192 = E "lyric 愛"
              288 = E "lyric café"
              384 = E "phrase_end"
            }
            """);

        var vocals = VocalReader.Read(ChartToMidi.Convert(chart).Midi, "notes.mid");

        CollectionAssert.AreEqual(new[] { "愛", "café" }, vocals.Main!.Lyrics.Select(l => l.Text).ToArray());
    }

    [TestMethod]
    public void TextInLatin1IsWrittenAsLatin1()
    {
        CollectionAssert.AreEqual(new byte[] { 0xFF, 0x05, 2, (byte)'e', 0xE9 }, MidiMessage.Text(0, 0x05, "eé").Bytes);
        CollectionAssert.AreEqual(new byte[] { 0xFF, 0x05, 3, 0xE6, 0x84, 0x9B }, MidiMessage.Text(0, 0x05, "愛").Bytes);
    }

    [TestMethod]
    public void APhraseStartsBeforeItsFirstLyric()
    {
        byte[] chart = Chart("""
            [Events]
            {
              192 = E "phrase_start"
              192 = E "lyric Hel-"
              288 = E "phrase_end"
              288 = E "phrase_start"
              288 = E "lyric lo"
              384 = E "phrase_end"
            }
            """);
        byte[] midi = ChartToMidi.Convert(chart).Midi;

        var (_, _, chunks) = MidiFile.ReadChunks(midi);
        var events = chunks.Where(c => c.Id == "MTrk").Select(c => MidiFile.ReadEvents(midi, c)).First(e => MidiFile.NameOf(midi, e) == "PART VOCALS");
        string Kind(MidiEvent e) => MidiFile.TextOf(midi, e, 0x05) is { } lyric ? lyric
            : e.Kind == 0x90 ? "on"
            : e.Kind == 0x80 ? "off"
            : "other";

        CollectionAssert.AreEqual(
            new[] { "on", "Hel-", "off", "on", "lo", "off" },
            events.Where(e => e.Tick > 0).Select(Kind).Where(k => k != "other").ToArray());
    }

    private static byte[] Chart(string tracks, int resolution = 192) => Encoding.UTF8.GetBytes($$"""
        [Song]
        {
          Resolution = {{resolution}}
        }
        [SyncTrack]
        {
          0 = B 120000
        }
        {{tracks}}
        """);
}
