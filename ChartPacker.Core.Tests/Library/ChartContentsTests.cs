using System.Text;
using ChartPacker.Core.Library;
using ChartPacker.Core.Scanning;
using ChartPacker.Core.Tests.Charts;

namespace ChartPacker.Core.Tests.Library;

[TestClass]
public sealed class ChartContentsTests
{
    [TestMethod]
    public void ChartGivesThePartsAndDifficultiesWithNotes()
    {
        var contents = ChartContents.Read(Encoding.UTF8.GetBytes(ChartReaderTests.Chart), ChartFiles.ChartName);

        Assert.AreEqual(
            new ChartContents(
                LibraryInstruments.Guitar | LibraryInstruments.Bass | LibraryInstruments.Drums | LibraryInstruments.GuitarGhl,
                LibraryDifficulties.Hard | LibraryDifficulties.Expert),
            contents);
    }

    [TestMethod]
    public void MidiCountsPartsTheNoteReaderSkipsOnlyWhenTheyHaveNotes()
    {
        byte[] empty = new MidiBuilder().Track().Tempo(0, 120).Track("PART DRUMS").Note(0, 98).Track("PART VOCALS").Build();
        byte[] sung = new MidiBuilder().Track().Tempo(0, 120).Track("PART DRUMS").Note(0, 98).Track("PART VOCALS").Note(0, 60).Build();

        Assert.AreEqual(new ChartContents(LibraryInstruments.Drums, LibraryDifficulties.Expert), ChartContents.Read(empty, ChartFiles.MidiName));
        Assert.AreEqual(
            new ChartContents(LibraryInstruments.Drums | LibraryInstruments.Vocals, LibraryDifficulties.Expert),
            ChartContents.Read(sung, ChartFiles.MidiName));
    }

    [TestMethod]
    public void MidiReadsNotesOfTheNoteReadersPartsNotTheirTracks()
    {
        // A template's empty guitar track isn't a guitar part.
        byte[] midi = new MidiBuilder().Track().Tempo(0, 120).Track("PART GUITAR").Track("PART BASS").Note(0, 60).Build();

        Assert.AreEqual(new ChartContents(LibraryInstruments.Bass, LibraryDifficulties.Easy), ChartContents.Read(midi, ChartFiles.MidiName));
    }

    [TestMethod]
    public void ProKeysDifficultiesComeFromTheirTracks()
    {
        byte[] midi = new MidiBuilder()
            .Track().Tempo(0, 120)
            .Track("PART REAL_KEYS_X").Note(0, 60)
            .Track("PART REAL_KEYS_M").Note(0, 60)
            .Track("PART REAL_KEYS_E")
            .Build();

        Assert.AreEqual(
            new ChartContents(LibraryInstruments.ProKeys, LibraryDifficulties.Medium | LibraryDifficulties.Expert),
            ChartContents.Read(midi, ChartFiles.MidiName));
    }

    [TestMethod]
    public void MalformedChartIsUnknown() =>
        Assert.IsNull(ChartContents.Read([0x4D, 0x54, 0x68, 0x64], ChartFiles.MidiName));

    [TestMethod]
    public void KnownPartsHaveFlags()
    {
        foreach (var part in Core.Merging.MidiPartCatalog.Known)
            Assert.AreNotEqual(LibraryInstruments.None, ChartContents.FlagOf(part.Id), part.Id);
    }
}
