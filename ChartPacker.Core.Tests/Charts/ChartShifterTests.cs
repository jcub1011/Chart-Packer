using System.Text;
using ChartPacker.Core.Charts;

namespace ChartPacker.Core.Tests.Charts;

[TestClass]
public sealed class ChartShifterTests
{
    private static readonly ChartTrackKey ExpertGuitar = new(ChartInstrument.Guitar, ChartDifficulty.Expert);
    private static readonly ChartTrackKey HardGuitar = new(ChartInstrument.Guitar, ChartDifficulty.Hard);
    private static readonly ChartTrackKey ExpertDrums = new(ChartInstrument.Drums, ChartDifficulty.Expert);

    [TestMethod]
    public void NoOffsetsKeepTheBytes()
    {
        byte[] chart = Encoding.UTF8.GetBytes(ChartReaderTests.Chart);
        byte[] midi = ChartReaderTests.SampleMidi();

        Assert.AreSame(chart, ChartShifter.Shift(chart, "notes.chart", new Dictionary<ChartTrackKey, int>()).Bytes);
        Assert.AreSame(midi, ChartShifter.Shift(midi, "notes.mid", new Dictionary<ChartTrackKey, int> { [ExpertGuitar] = 0 }).Bytes);
    }

    [TestMethod]
    public void ChartMovesOnlyTheSectionsNotes()
    {
        string text = ChartReaderTests.Chart;
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        var before = ChartReader.Read(bytes, "notes.chart");

        var result = ChartShifter.Shift(bytes, "notes.chart", new Dictionary<ChartTrackKey, int> { [ExpertGuitar] = 500 });

        string shifted = Encoding.UTF8.GetString(result.Bytes);
        int start = text.IndexOf("[ExpertSingle]", StringComparison.Ordinal);
        int end = text.IndexOf('}', start);
        Assert.AreEqual(text[..start], shifted[..start]);
        Assert.AreEqual(text[end..], shifted[(shifted.IndexOf('}', start))..]);
        StringAssert.Contains(shifted, "192 = S 2 300");

        var moved = ChartReaderTests.Notes(result.Document, ChartInstrument.Guitar);
        CollectionAssert.AreEqual(
            ChartReaderTests.Notes(before, ChartInstrument.Guitar).Select(n => before.Tempo.Shift(n.Tick, TimeSpan.FromMilliseconds(500))).ToArray(),
            moved.Select(n => n.Tick).ToArray());
        CollectionAssert.AreEqual(ChartReaderTests.Notes(before, ChartInstrument.Drums), ChartReaderTests.Notes(result.Document, ChartInstrument.Drums));
        Assert.IsEmpty(result.ChangedMarkings);
    }

    [TestMethod]
    public void SustainsKeepTheirLengthInTimeAcrossATempoChange()
    {
        // 120 BPM until tick 768, then 60 BPM; the sustain crosses the change.
        byte[] bytes = Encoding.UTF8.GetBytes("""
            [Song]
            {
              Resolution = 192
            }
            [SyncTrack]
            {
              0 = B 120000
              768 = B 60000
            }
            [ExpertSingle]
            {
              700 = N 0 136
            }
            """);
        var tempo = ChartReader.Read(bytes, "notes.chart").Tempo;

        var note = ChartReaderTests.Notes(ChartShifter.Shift(bytes, "notes.chart", new Dictionary<ChartTrackKey, int> { [ExpertGuitar] = 500 }).Document, ChartInstrument.Guitar).Single();

        Assert.AreEqual(830, note.Tick);
        double seconds = tempo.SecondsAt(note.Tick + note.Length) - tempo.SecondsAt(note.Tick);
        Assert.AreEqual(tempo.SecondsAt(836) - tempo.SecondsAt(700), seconds, 1.0 / 192);
    }

    [TestMethod]
    public void MovingNotesBeforeTheStartThrows()
    {
        byte[] bytes = Encoding.UTF8.GetBytes(ChartReaderTests.Chart);

        Assert.ThrowsExactly<ChartFormatException>(() => ChartShifter.Shift(bytes, "notes.chart", new Dictionary<ChartTrackKey, int> { [ExpertGuitar] = -1 }));
    }

    [TestMethod]
    public void MidiMovesOnlyTheDifficultysNotesAndKeepsOtherTracksBytes()
    {
        byte[] bytes = ChartReaderTests.SampleMidi();
        var before = ChartReader.Read(bytes, "notes.mid");

        var result = ChartShifter.Shift(bytes, "notes.mid", new Dictionary<ChartTrackKey, int> { [ExpertGuitar] = 250 });

        CollectionAssert.AreEqual(
            ChartReaderTests.Notes(before, ChartInstrument.Guitar).Select(n => before.Tempo.Shift(n.Tick, TimeSpan.FromMilliseconds(250))).ToArray(),
            ChartReaderTests.Notes(result.Document, ChartInstrument.Guitar).Select(n => n.Tick).ToArray());
        CollectionAssert.AreEqual(
            ChartReaderTests.Notes(before, ChartInstrument.Guitar, ChartDifficulty.Hard),
            ChartReaderTests.Notes(result.Document, ChartInstrument.Guitar, ChartDifficulty.Hard));

        // The drum and vocal tracks follow the guitar, byte for byte.
        int drums = IndexOf(bytes, "PART DRUMS"u8) - 12;
        CollectionAssert.AreEqual(bytes[drums..], result.Bytes[^(bytes.Length - drums)..]);
    }

    [TestMethod]
    public void MidiReportsNotesThatLeaveASharedMarker()
    {
        byte[] bytes = ChartReaderTests.SampleMidi();

        var result = ChartShifter.Shift(bytes, "notes.mid", new Dictionary<ChartTrackKey, int> { [ExpertGuitar] = 250 });

        // The first note moves out of the star power phrase every difficulty shares.
        CollectionAssert.AreEqual(new[] { ExpertGuitar }, result.ChangedMarkings.ToArray());
        Assert.AreEqual(NoteFlags.None, ChartReaderTests.Notes(result.Document, ChartInstrument.Guitar)[0].Flags);
    }

    [TestMethod]
    public void MidiMovesTheWholeTrackWhenEveryDifficultyMovesTogether()
    {
        byte[] bytes = ChartReaderTests.SampleMidi();
        var before = ChartReader.Read(bytes, "notes.mid");

        var result = ChartShifter.Shift(bytes, "notes.mid", new Dictionary<ChartTrackKey, int> { [ExpertGuitar] = 250, [HardGuitar] = 250 });

        Assert.IsEmpty(result.ChangedMarkings);
        CollectionAssert.AreEqual(
            ChartReaderTests.Notes(before, ChartInstrument.Guitar).Select(n => n.Flags).ToArray(),
            ChartReaderTests.Notes(result.Document, ChartInstrument.Guitar).Select(n => n.Flags).ToArray());
        CollectionAssert.AreEqual(ChartReaderTests.Notes(before, ChartInstrument.Drums), ChartReaderTests.Notes(result.Document, ChartInstrument.Drums));
    }

    [TestMethod]
    public void MidiDrumTomMarkersStayPut()
    {
        byte[] bytes = ChartReaderTests.SampleMidi();

        var result = ChartShifter.Shift(bytes, "notes.mid", new Dictionary<ChartTrackKey, int> { [ExpertDrums] = 100 });

        // Expert is the drums' only difficulty, so the whole track moves, its tom marker too.
        Assert.IsEmpty(result.ChangedMarkings);
        Assert.AreEqual(NoteFlags.None, ChartReaderTests.Notes(result.Document, ChartInstrument.Drums)[2].Flags);
    }

    private static int IndexOf(byte[] bytes, ReadOnlySpan<byte> value) => bytes.AsSpan().IndexOf(value);
}
