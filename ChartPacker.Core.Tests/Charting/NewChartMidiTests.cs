using ChartPacker.Core.Charting;
using ChartPacker.Core.Charts;

namespace ChartPacker.Core.Tests.Charting;

[TestClass]
public sealed class NewChartMidiTests
{
    [TestMethod]
    [DataRow(192)]
    [DataRow(480)]
    [DataRow(960)]
    public void ItReadsAsTheChartTheSettingsDescribe(int resolution)
    {
        var settings = new NewChartSettings(
            "Song", resolution, 140, 3, 4, new HashSet<ChartInstrument> { ChartInstrument.Guitar, ChartInstrument.Drums }, DrumsVariant.Pro);

        byte[] midi = NewChartMidi.Build(settings);

        var chart = ChartReader.Read(midi, "notes.mid");
        Assert.AreEqual(resolution, chart.Tempo.Resolution);
        Assert.AreSequenceEqual([new TempoChange(0, EditableTempoMap.MicrosecondsPerQuarterOf(140))], chart.Tempo.Tempos.ToList());
        Assert.AreSequenceEqual([new TimeSignature(0, 3, 4)], chart.Tempo.TimeSignatures.ToList());
        Assert.IsEmpty(chart.Tracks);

        // The tempo track named after the song, each part's track, empty, then EVENTS and BEAT.
        var (_, division, chunks) = MidiFile.ReadChunks(midi);
        Assert.AreEqual(resolution, division);
        Assert.AreSequenceEqual(
            ["Song", "PART GUITAR", "PART DRUMS", "EVENTS", "BEAT"],
            chunks.Skip(1).Select(c => MidiFile.NameOf(midi, c)).ToList());
        var events = MidiFile.ReadEvents(midi, chunks[4]);
        Assert.AreSequenceEqual(
            ["EVENTS", "[music_start]", "[end]"],
            events.Where(e => e.IsMeta && MidiFile.MetaOf(midi, e).Type is 0x01 or 0x03).Select(e => MidiFile.TextOf(midi, e, 0x01) ?? MidiFile.TextOf(midi, e, 0x03)).ToList());
    }
}
