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

    private static byte[] Chart(string tracks) => Encoding.UTF8.GetBytes($$"""
        [Song]
        {
          Resolution = 192
        }
        [SyncTrack]
        {
          0 = B 120000
        }
        {{tracks}}
        """);
}
