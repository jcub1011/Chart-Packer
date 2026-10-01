using System.Text;
using ChartPacker.Core.Charts;
using ChartPacker.Core.Merging;
using ChartPacker.Core.Tests.Charts;

namespace ChartPacker.Core.Tests.Merging;

[TestClass]
public sealed class MidiMergerTests
{
    private static readonly MidiPart Guitar = MidiPartCatalog.PartOf("PART GUITAR")!;
    private static readonly MidiPart Drums = MidiPartCatalog.PartOf("PART DRUMS")!;
    private static readonly MidiPart Bass = MidiPartCatalog.PartOf("PART BASS")!;

    [TestMethod]
    public void SameTempoMapCopiesTheTicks()
    {
        var baseChart = Chart(new MidiBuilder()
            .Track("tempo").Tempo(0, 120)
            .Track("EVENTS").Text(0, "[section Base]")
            .Track("PART GUITAR").Note(0, 96).Note(480, 97)
            .Track("PART BASS").Note(0, 96)
            .Build());
        var other = Chart(new MidiBuilder()
            .Track("tempo").Tempo(0, 120)
            .Track("EVENTS").Text(0, "[section Other]")
            .Track("PART DRUMS").Note(0, 96).Note(240, 97)
            .Build());

        var result = MidiMerger.Merge(baseChart, [(baseChart, Guitar), (other, Drums)]);
        var merged = ChartReader.Read(result.Midi, "notes.mid");

        CollectionAssert.AreEqual(ChartReaderTests.Notes(Read(baseChart), ChartInstrument.Guitar), ChartReaderTests.Notes(merged, ChartInstrument.Guitar));
        CollectionAssert.AreEqual(ChartReaderTests.Notes(Read(other), ChartInstrument.Drums), ChartReaderTests.Notes(merged, ChartInstrument.Drums));
        CollectionAssert.AreEqual(new[] { ChartInstrument.Guitar, ChartInstrument.Drums }, merged.Instruments.ToArray());

        string text = Encoding.Latin1.GetString(result.Midi);
        StringAssert.Contains(text, "[section Base]");
        Assert.DoesNotContain("[section Other]", text);
        Assert.IsEmpty(result.Warnings);
    }

    [TestMethod]
    public void OtherTempoMapPlacesNotesBySongTime()
    {
        var baseChart = Chart(new MidiBuilder(480).Track("tempo").Tempo(0, 120).Track("PART GUITAR").Note(0, 96).Build());
        // At 60 BPM and 192 ticks a beat, tick 192 is one second in, and a sustain of 192 lasts a second.
        var other = Chart(new MidiBuilder(192).Track("tempo").Tempo(0, 60).Track("PART BASS").Note(192, 96, 192).Build());

        var merged = ChartReader.Read(MidiMerger.Merge(baseChart, [(other, Bass)]).Midi, "notes.mid");

        Assert.AreEqual(480, merged.Tempo.Resolution);
        CollectionAssert.AreEqual(new[] { new ChartNote(960, 960, 0, NoteFlags.None) }, ChartReaderTests.Notes(merged, ChartInstrument.Bass));
        Assert.IsFalse(merged.Tracks.Keys.Any(k => k.Instrument == ChartInstrument.Guitar));
    }

    [TestMethod]
    public void DelayDifferenceMovesTheNotes()
    {
        var baseChart = Chart(new MidiBuilder().Track("tempo").Tempo(0, 120).Build());
        var other = new MidiChart(
            new MidiBuilder().Track("tempo").Tempo(0, 120).Track("PART DRUMS").Note(0, 96).Build(),
            TimeSpan.FromMilliseconds(500),
            ChartReadOptions.Default);

        var merged = ChartReader.Read(MidiMerger.Merge(baseChart, [(other, Drums)]).Midi, "notes.mid");

        // Half a second at 120 BPM is a beat.
        Assert.AreEqual(480, ChartReaderTests.Notes(merged, ChartInstrument.Drums).Single().Tick);
    }

    [TestMethod]
    public void HopoThatTheNewTempoMapWouldLoseIsForced()
    {
        var baseChart = Chart(new MidiBuilder().Track("tempo").Tempo(0, 120).Build());
        // A third of a beat apart at 60 BPM: a natural HOPO. At 120 BPM that's two thirds of a beat, a strum.
        var other = Chart(new MidiBuilder().Track("tempo").Tempo(0, 60).Track("PART GUITAR").Note(0, 96).Note(160, 97).Build());
        Assert.AreEqual(NoteFlags.Hopo, ChartReaderTests.Notes(Read(other), ChartInstrument.Guitar)[1].Flags);

        var result = MidiMerger.Merge(baseChart, [(other, Guitar)]);
        var notes = ChartReaderTests.Notes(ChartReader.Read(result.Midi, "notes.mid"), ChartInstrument.Guitar);

        Assert.AreEqual(new ChartNote(320, 0, 1, NoteFlags.Hopo), notes[1]);
        Assert.IsEmpty(result.Warnings);
    }

    [TestMethod]
    public void StrumThatTheNewTempoMapWouldMakeAHopoIsForced()
    {
        var baseChart = Chart(new MidiBuilder().Track("tempo").Tempo(0, 60).Build());
        var other = Chart(new MidiBuilder().Track("tempo").Tempo(0, 120).Track("PART GUITAR").Note(0, 96).Note(240, 97).Build());

        var notes = ChartReaderTests.Notes(ChartReader.Read(MidiMerger.Merge(baseChart, [(other, Guitar)]).Midi, "notes.mid"), ChartInstrument.Guitar);

        Assert.AreEqual(new ChartNote(120, 0, 1, NoteFlags.None), notes[1]);
    }

    [TestMethod]
    public void PartTracksMoveTogetherAndKeepTheirTempoOut()
    {
        var baseChart = Chart(new MidiBuilder().Track("tempo").Tempo(0, 120).Build());
        var other = Chart(new MidiBuilder()
            .Track("tempo").Tempo(0, 120)
            .Track("HARM1").Note(0, 60)
            .Track("HARM2").Note(0, 62)
            .Track("HARM3").Tempo(0, 90).Note(0, 64)
            .Build());

        var result = MidiMerger.Merge(baseChart, [(other, MidiPartCatalog.PartOf("HARM1")!)]);
        string text = Encoding.Latin1.GetString(result.Midi);

        StringAssert.Contains(text, "HARM1");
        StringAssert.Contains(text, "HARM2");
        StringAssert.Contains(text, "HARM3");
        Assert.IsTrue(ChartReader.Read(result.Midi, "notes.mid").Tempo.SameAs(Read(baseChart).Tempo));
    }

    [TestMethod]
    public void BaseTempoInAPartThatIsntKeptThrows()
    {
        var baseChart = Chart(new MidiBuilder().Track("PART GUITAR").Tempo(0, 90).Note(0, 96).Build());
        var other = Chart(new MidiBuilder().Track("PART DRUMS").Note(0, 96).Build());

        Assert.ThrowsExactly<ChartFormatException>(() => MidiMerger.Merge(baseChart, [(other, Drums)]));
    }

    [TestMethod]
    public void RetimeRoundsToTheNearestTickAndNeverBeforeTheStart()
    {
        var from = new TempoMap(192, [new TempoChange(0, 500_000)], []);
        var to = new TempoMap(480, [new TempoChange(0, 500_000)], []);

        Assert.AreEqual(3, MidiMerger.Retime(1, from, TimeSpan.Zero, to, TimeSpan.Zero));
        Assert.AreEqual(0, MidiMerger.Retime(0, from, TimeSpan.Zero, to, TimeSpan.FromSeconds(1)));
    }

    private static MidiChart Chart(byte[] midi) => new(midi, TimeSpan.Zero, ChartReadOptions.Default);

    private static ChartDocument Read(MidiChart chart) => ChartReader.Read(chart.Midi, "notes.mid");
}
