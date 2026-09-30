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
    public void PartsHaveTheirOwnDifficulties()
    {
        var parts = ChartContents.ReadParts(Encoding.UTF8.GetBytes(ChartReaderTests.Chart), ChartFiles.ChartName);

        Assert.IsNotNull(parts);
        Assert.AreEqual(LibraryDifficulties.Expert, parts["guitar"]);
        Assert.AreEqual(LibraryDifficulties.Hard, parts["bass"]);
        Assert.AreEqual(LibraryDifficulties.Expert, parts["drums"]);
        Assert.AreEqual(LibraryDifficulties.Expert, parts["guitar_ghl"]);
        Assert.HasCount(4, parts);
    }

    [TestMethod]
    public void MidiPartsTheNoteReaderSkipsHaveTheirDifficulties()
    {
        byte[] midi = new MidiBuilder()
            .Track().Tempo(0, 120)
            .Track("PART GUITAR").Note(0, 60).Note(0, 96)
            .Track("PART REAL_KEYS_H").Note(0, 60)
            .Track("PART REAL_GUITAR").Note(0, 24).Note(0, 101).Note(0, 60)
            .Track("PART REAL_BASS_22").Note(0, 50)
            .Track("PART VOCALS").Note(0, 60)
            .Build();

        var parts = ChartContents.ReadParts(midi, ChartFiles.MidiName);

        Assert.IsNotNull(parts);
        Assert.AreEqual(LibraryDifficulties.Easy | LibraryDifficulties.Expert, parts["guitar"]);
        Assert.AreEqual(LibraryDifficulties.Hard, parts["pro_keys"]);
        // 60 is a pro guitar track's marker, not a string, so it adds no difficulty.
        Assert.AreEqual(LibraryDifficulties.Easy | LibraryDifficulties.Expert, parts["pro_guitar"]);
        Assert.AreEqual(LibraryDifficulties.Medium, parts["pro_bass_22"]);
        Assert.AreEqual(LibraryDifficulties.None, parts["vocals"]);
        Assert.HasCount(5, parts);
    }

    [TestMethod]
    public void ChartContentsAreItsPartsCombined()
    {
        byte[] midi = new MidiBuilder()
            .Track().Tempo(0, 120)
            .Track("PART DRUMS").Note(0, 98)
            .Track("PART REAL_BASS").Note(0, 48)
            .Build();

        Assert.AreEqual(
            new ChartContents(LibraryInstruments.Drums | LibraryInstruments.ProBass, LibraryDifficulties.Medium | LibraryDifficulties.Expert),
            ChartContents.Read(midi, ChartFiles.MidiName));
    }

    [TestMethod]
    public void MalformedChartHasNoParts() =>
        Assert.IsNull(ChartContents.ReadParts([0x4D, 0x54, 0x68, 0x64], ChartFiles.MidiName));

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
