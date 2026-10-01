using System.Text;
using ChartPacker.Core.Charts;
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
        Assert.AreEqual(LibraryDifficulties.Expert, parts["guitar"].Difficulties);
        Assert.AreEqual(LibraryDifficulties.Hard, parts["bass"].Difficulties);
        Assert.AreEqual(LibraryDifficulties.Expert, parts["drums"].Difficulties);
        Assert.AreEqual(LibraryDifficulties.Expert, parts["guitar_ghl"].Difficulties);
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
        Assert.AreEqual(LibraryDifficulties.Easy | LibraryDifficulties.Expert, parts["guitar"].Difficulties);
        Assert.AreEqual(LibraryDifficulties.Hard, parts["pro_keys"].Difficulties);
        // 60 is a pro guitar track's marker, not a string, so it adds no difficulty.
        Assert.AreEqual(LibraryDifficulties.Easy | LibraryDifficulties.Expert, parts["pro_guitar"].Difficulties);
        Assert.AreEqual(LibraryDifficulties.Medium, parts["pro_bass_22"].Difficulties);
        Assert.AreEqual(LibraryDifficulties.None, parts["vocals"].Difficulties);
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
    public void ChartPartsCountAChordAsOneNote()
    {
        var parts = ChartContents.ReadParts(Encoding.UTF8.GetBytes(ChartReaderTests.Chart), ChartFiles.ChartName);

        Assert.IsNotNull(parts);
        // Guitar's chord at 0 is one note; the forcing and tap markers at 240 and 600 are none.
        CollectionAssert.AreEqual(new[] { new NoteCount(ChartDifficulty.Expert, 0, 5) }, Counts(parts, "guitar"));
        CollectionAssert.AreEqual(new[] { new NoteCount(ChartDifficulty.Hard, 0, 1) }, Counts(parts, "bass"));
        // The kick, 2x kick and snare at 0 are one note, as are a gem and its cymbal marker.
        CollectionAssert.AreEqual(new[] { new NoteCount(ChartDifficulty.Expert, 0, 3) }, Counts(parts, "drums"));
        CollectionAssert.AreEqual(new[] { new NoteCount(ChartDifficulty.Expert, 0, 3) }, Counts(parts, "guitar_ghl"));
    }

    [TestMethod]
    public void MidiPartsCountEachDifficulty()
    {
        byte[] midi = new MidiBuilder()
            .Track().Tempo(0, 120)
            .Track("PART GUITAR").Note(0, 60).Note(0, 61).Note(480, 62).Note(0, 96)
            .Build();

        var parts = ChartContents.ReadParts(midi, ChartFiles.MidiName);

        Assert.IsNotNull(parts);
        CollectionAssert.AreEqual(
            new[] { new NoteCount(ChartDifficulty.Easy, 0, 2), new NoteCount(ChartDifficulty.Expert, 0, 1) },
            Counts(parts, "guitar"));
    }

    [TestMethod]
    public void ExpertDrumsLeaveOutTwoTimesKicks()
    {
        byte[] midi = new MidiBuilder()
            .Track().Tempo(0, 120)
            .Track("PART DRUMS").Note(0, 95).Note(480, 95).Note(480, 96).Note(960, 98)
            .Build();

        var parts = ChartContents.ReadParts(midi, ChartFiles.MidiName);

        Assert.IsNotNull(parts);
        // The lone 2x kick at 0 is Expert+ only; the 2x kick at 480 goes with a kick, which is one note.
        CollectionAssert.AreEqual(new[] { new NoteCount(ChartDifficulty.Expert, 0, 2) }, Counts(parts, "drums"));
    }

    [TestMethod]
    public void OnlyTwoTimesKicksStillChartExpert()
    {
        byte[] midi = new MidiBuilder().Track().Tempo(0, 120).Track("PART DRUMS").Note(0, 95).Build();

        var parts = ChartContents.ReadParts(midi, ChartFiles.MidiName);

        Assert.IsNotNull(parts);
        Assert.AreEqual(LibraryDifficulties.Expert, parts["drums"].Difficulties);
        Assert.IsEmpty(parts["drums"].NoteCounts);
    }

    [TestMethod]
    public void ProKeysCountTheKeysOfEachTrack()
    {
        byte[] midi = new MidiBuilder()
            .Track().Tempo(0, 120)
            .Track("PART REAL_KEYS_X").Note(0, 48).Note(0, 52).Note(480, 72).Note(480, 116).Note(960, 47).Note(960, 73)
            .Track("PART REAL_KEYS_H").Note(0, 116)
            .Track("PART REAL_KEYS_E").Note(0, 60)
            .Build();

        var parts = ChartContents.ReadParts(midi, ChartFiles.MidiName);

        Assert.IsNotNull(parts);
        // The overdrive marker alone still charts hard, with no notes to count.
        Assert.AreEqual(LibraryDifficulties.Easy | LibraryDifficulties.Hard | LibraryDifficulties.Expert, parts["pro_keys"].Difficulties);
        CollectionAssert.AreEqual(
            new[] { new NoteCount(ChartDifficulty.Easy, 0, 1), new NoteCount(ChartDifficulty.Expert, 0, 2) },
            Counts(parts, "pro_keys"));
    }

    [TestMethod]
    public void ProGuitarCountsTheStringsOfEachDifficulty()
    {
        byte[] midi = new MidiBuilder()
            .Track().Tempo(0, 120)
            .Track("PART REAL_GUITAR").Note(0, 24).Note(0, 29).Note(480, 25).Note(480, 30).Note(0, 101).Note(0, 60)
            .Build();

        var parts = ChartContents.ReadParts(midi, ChartFiles.MidiName);

        Assert.IsNotNull(parts);
        // 30 is past easy's six strings and 60 is a marker, so neither counts.
        CollectionAssert.AreEqual(
            new[] { new NoteCount(ChartDifficulty.Easy, 0, 2), new NoteCount(ChartDifficulty.Expert, 0, 1) },
            Counts(parts, "pro_guitar"));
    }

    [TestMethod]
    public void VocalsAndEachHarmonyLineCountTheirSungNotes()
    {
        byte[] midi = new MidiBuilder()
            .Track().Tempo(0, 120)
            .Track("PART VOCALS").Note(0, 105, 1920).Note(0, 60).Note(480, 36).Note(960, 84).Note(1440, 96)
            .Track("HARM1").Note(0, 60).Note(480, 62)
            .Track("HARM2").Note(0, 64)
            .Track("HARM3").Note(0, 105)
            .Build();

        var parts = ChartContents.ReadParts(midi, ChartFiles.MidiName);

        Assert.IsNotNull(parts);
        // The phrase marker and the percussion note aren't sung.
        CollectionAssert.AreEqual(new[] { new NoteCount(null, 0, 3) }, Counts(parts, "vocals"));
        CollectionAssert.AreEqual(new[] { new NoteCount(null, 1, 2), new NoteCount(null, 2, 1) }, Counts(parts, "harmonies"));
        Assert.AreEqual(LibraryDifficulties.None, parts["harmonies"].Difficulties);
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

    private static NoteCount[] Counts(IReadOnlyDictionary<string, ChartPart> parts, string id) => [.. parts[id].NoteCounts];
}
