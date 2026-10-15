using ChartShared.Core.Charting;
using ChartShared.Core.Charts;
using ChartShared.Core.Tests.Charts;
using static ChartBuilder.Tests.Charting.MidiChartLoaderTests;

namespace ChartBuilder.Tests.Charting;

[TestClass]
public sealed class MidiChartLoaderPartTests
{
    private const ChartDifficulty Easy = ChartDifficulty.Easy;
    private const ChartDifficulty Medium = ChartDifficulty.Medium;
    private const ChartDifficulty Hard = ChartDifficulty.Hard;
    private const ChartDifficulty Expert = ChartDifficulty.Expert;

    private static (EditablePart Part, IReadOnlyList<ChartLoadIssue> Issues) LoadPart(ChartInstrument instrument, MidiBuilder midi, ChartReadOptions? options = null)
    {
        var result = Load(midi, options);
        return (result.Chart.PartOf(instrument)!, result.Issues);
    }

    private static List<EditableNote> NotesOf(EditablePart part, ChartDifficulty difficulty) => [.. part[difficulty].Notes];

    // Five-fret and six-fret.

    [TestMethod]
    public void FiveFretNotesKeepTheirLanesAndExactLengths()
    {
        var (part, issues) = LoadPart(ChartInstrument.Guitar, Song()
            .Track("PART GUITAR")
            .Note(0, 96, 1).Note(0, 97, 1).Note(480, 98, 240).Note(960, 99, 0).Note(1440, 100, 481)
            .Note(0, 60, 120).Note(0, 76, 7));

        Assert.AreSequenceEqual(
            [new EditableNote(0, 1, 0), new EditableNote(0, 1, 1), new EditableNote(480, 240, 2), new EditableNote(960, 0, 3), new EditableNote(1440, 481, 4)],
            NotesOf(part, Expert));
        Assert.AreSequenceEqual([new EditableNote(0, 120, 0)], NotesOf(part, Easy));
        Assert.AreSequenceEqual([new EditableNote(0, 7, 4)], NotesOf(part, Medium));
        Assert.IsEmpty(issues);
        Assert.IsEmpty(part.RawEvents);
        Assert.IsEmpty(part.TextEvents);
        Assert.IsFalse(part.EnhancedOpens);
    }

    [TestMethod]
    public void NotesBelowGreenAreOpensWhenLetGoAfterEnhancedOpens()
    {
        var (part, issues) = LoadPart(ChartInstrument.Guitar, Song()
            .Track("PART GUITAR")
            .Note(0, 95, 10)
            .Text(100, "[ENHANCED_OPENS]")
            .Note(480, 95)
            .Note(480, 59));

        Assert.AreSequenceEqual([new EditableNote(480, 60, ChartNote.OpenLane)], NotesOf(part, Expert));
        Assert.AreSequenceEqual([new EditableNote(480, 60, ChartNote.OpenLane)], NotesOf(part, Easy));
        Assert.IsTrue(part.EnhancedOpens);
        Assert.IsEmpty(part.TextEvents);
        Assert.IsEmpty(part.RawEvents);
        Assert.AreSequenceEqual([new ChartLoadIssue(ChartLoadIssueKind.OpenBeforeEnhancedOpens, ChartInstrument.Guitar, Expert, 0, 95)], issues.ToList());
    }

    [TestMethod]
    public void PhaseShiftOpenPhrasesMakeTheNotesTheyCoverOpen()
    {
        var (part, issues) = LoadPart(ChartInstrument.Guitar, Song()
            .Track("PART GUITAR")
            .PhaseShift(0, 3, MidiLayout.SysexOpen, true).Note(0, 97, 10).PhaseShift(10, 3, MidiLayout.SysexOpen, false)
            .PhaseShift(480, 0xFF, MidiLayout.SysexOpen, true).Note(480, 84, 10).Note(480, 85, 10).PhaseShift(490, 0xFF, MidiLayout.SysexOpen, false)
            .PhaseShift(960, 3, MidiLayout.SysexOpen, true).PhaseShift(970, 3, MidiLayout.SysexOpen, false)
            .Note(1440, 96));

        Assert.AreSequenceEqual([new EditableNote(0, 10, ChartNote.OpenLane), new EditableNote(1440, 60, 0)], NotesOf(part, Expert));
        // Both of Hard's chord become open, and the model holds the first.
        Assert.AreSequenceEqual([new EditableNote(480, 10, ChartNote.OpenLane)], NotesOf(part, Hard));
        // Written as notes below green, which need the event.
        Assert.IsTrue(part.EnhancedOpens);
        Assert.IsEmpty(part.RawEvents);
        Assert.AreSequenceEqual(
            [
                new ChartLoadIssue(ChartLoadIssueKind.DuplicateOpen, ChartInstrument.Guitar, Hard, 480, 85),
                new ChartLoadIssue(ChartLoadIssueKind.UnusedMarker, ChartInstrument.Guitar, Expert, 960),
            ],
            issues.ToList());
    }

    [TestMethod]
    public void ForceAndTapMarkersFlagTheChordsTheyCover()
    {
        var (part, issues) = LoadPart(ChartInstrument.Guitar, Song()
            .Track("PART GUITAR")
            .Note(0, 96).Note(0, 97).Note(0, 101, 1)
            .Note(480, 98).Note(480, 102, 10)
            .Note(960, 99).Note(960, 104, 10)
            .Note(1440, 100).PhaseShift(1440, 3, MidiLayout.SysexTap, true).PhaseShift(1450, 3, MidiLayout.SysexTap, false)
            .Note(1920, 96).Note(1920, 101, 10).Note(1920, 102, 10)
            .Note(2400, 101, 10)
            .Note(2880, 104, 10)
            .Note(3360, 96).Note(3400, 97).Note(3360, 102, 100)
            .Note(3840, 96).Note(3840, 89, 10));

        Assert.AreSequenceEqual(
            [
                new FlaggedChord(0, ChordFlags.ForceHopo),
                new FlaggedChord(480, ChordFlags.ForceStrum),
                new FlaggedChord(960, ChordFlags.Tap),
                new FlaggedChord(1440, ChordFlags.Tap),
                new FlaggedChord(1920, ChordFlags.ForceHopo | ChordFlags.ForceStrum),
                new FlaggedChord(3360, ChordFlags.ForceStrum),
                new FlaggedChord(3400, ChordFlags.ForceStrum),
            ],
            part[Expert].FlaggedChords.ToList());
        // Hard's force-HOPO marker is Hard's alone.
        Assert.IsEmpty(part[Hard].Notes);
        Assert.AreSequenceEqual(
            [
                new ChartLoadIssue(ChartLoadIssueKind.UnusedMarker, ChartInstrument.Guitar, Hard, 3840, 89),
                new ChartLoadIssue(ChartLoadIssueKind.UnusedMarker, ChartInstrument.Guitar, Expert, 2400, 101),
                new ChartLoadIssue(ChartLoadIssueKind.UnusedMarker, ChartInstrument.Guitar, null, 2880, 104),
            ],
            issues.ToList());
        Assert.IsEmpty(part.RawEvents);
    }

    [TestMethod]
    public void StarPowerAndSolosArePhrasesAndTheReadersStarPowerOfTwoAtATickIsKept()
    {
        var (part, issues) = LoadPart(ChartInstrument.Guitar, Song()
            .Track("PART GUITAR")
            .Note(0, 96)
            .Raw(0, 0x90, 116, 100).Raw(0, 0x91, 116, 100).Raw(480, 0x81, 116, 0).Raw(960, 0x80, 116, 0)
            .Note(1920, 103, 960));

        // Let go first, so the reader takes it.
        Assert.AreSequenceEqual([new Phrase(PhraseKind.StarPower, 0, 480), new Phrase(PhraseKind.Solo, 1920, 960)], part.Phrases.ToList());
        Assert.AreSequenceEqual([new ChartLoadIssue(ChartLoadIssueKind.DuplicateStarPower, ChartInstrument.Guitar, null, 0, 116)], issues.ToList());
        Assert.IsEmpty(part.RawEvents);
    }

    [TestMethod]
    public void StarPowerOn103LeavesOutTheNotesOn116()
    {
        var (part, issues) = LoadPart(
            ChartInstrument.Guitar,
            Song().Track("PART GUITAR").Note(0, 96).Note(0, 103, 480).Note(960, 116, 480),
            new ChartReadOptions(MultiplierNote: 103));

        Assert.AreSequenceEqual([new Phrase(PhraseKind.StarPower, 0, 480)], part.Phrases.ToList());
        Assert.AreSequenceEqual([new ChartLoadIssue(ChartLoadIssueKind.UnreadStarPower, ChartInstrument.Guitar, null, 960, 116)], issues.ToList());
        Assert.IsEmpty(part.RawEvents);
    }

    [TestMethod]
    public void SixFretNotesKeepTheirLanesAndForcing()
    {
        var (part, issues) = LoadPart(ChartInstrument.GhlGuitar, Song()
            .Track("PART GUITAR GHL")
            .Note(0, 94).Note(480, 95).Note(480, 100).Note(960, 96).Note(960, 101, 1).Note(1440, 97).Note(1440, 102, 1)
            .Note(1920, 58));

        Assert.AreSequenceEqual(
            [new EditableNote(0, 60, ChartNote.OpenLane), new EditableNote(480, 60, 0), new EditableNote(480, 60, 5), new EditableNote(960, 60, 1), new EditableNote(1440, 60, 2)],
            NotesOf(part, Expert));
        Assert.AreSequenceEqual([new FlaggedChord(960, ChordFlags.ForceHopo), new FlaggedChord(1440, ChordFlags.ForceStrum)], part[Expert].FlaggedChords.ToList());
        Assert.AreSequenceEqual([new EditableNote(1920, 60, ChartNote.OpenLane)], NotesOf(part, Easy));
        Assert.IsFalse(part.EnhancedOpens);
        Assert.IsEmpty(issues);
    }

    [TestMethod]
    public void AllFiveFillPitchesHeldTogetherOnGuitarAreABigRockEnding()
    {
        var (part, issues) = LoadPart(ChartInstrument.Guitar, Song()
            .Track("PART GUITAR")
            .Note(0, 96)
            .Note(3840, 120, 960).Note(3840, 121, 960).Note(3840, 122, 960).Note(3840, 123, 960).Note(3840, 124, 960)
            .Note(5760, 120, 100));

        Assert.AreSequenceEqual([new Phrase(PhraseKind.BigRockEnding, 3840, 960)], part.Phrases.ToList());
        Assert.AreSequenceEqual([new RawEvent(5760, [0x90, 120, 100]), new RawEvent(5860, [0x90, 120, 0])], part.RawEvents.ToList());
        Assert.IsEmpty(issues);
    }

    // Drums.

    [TestMethod]
    public void DrumNotesKeepTheirPadsAndThe2xKick()
    {
        var (part, issues) = LoadPart(ChartInstrument.Drums, Song()
            .Track("PART DRUMS")
            .Note(0, 96).Note(0, 97).Note(0, 98).Note(480, 99).Note(480, 100).Note(960, 101).Note(960, 95).Note(1440, 84));

        Assert.AreSequenceEqual(
            [
                new EditableNote(0, 60, 0), new EditableNote(0, 60, 1), new EditableNote(0, 60, 2), new EditableNote(480, 60, 3),
                new EditableNote(480, 60, 4), new EditableNote(960, 60, 0, DoubleKick: true), new EditableNote(960, 60, 5),
            ],
            NotesOf(part, Expert));
        Assert.AreSequenceEqual([new EditableNote(1440, 60, 0)], NotesOf(part, Hard));
        Assert.IsEmpty(issues);
    }

    [TestMethod]
    public void VelocitiesAreAccentsAndGhostsOnlyAfterEnableChartDynamics()
    {
        var without = LoadPart(ChartInstrument.Drums, Song().Track("PART DRUMS").Note(0, 96, velocity: 127).Note(0, 97, velocity: 1)).Part;
        var (with, issues) = LoadPart(ChartInstrument.Drums, Song()
            .Track("PART DRUMS")
            .Note(0, 97, 10, velocity: 127)
            .Text(20, "[ENABLE_CHART_DYNAMICS]")
            .Note(480, 96, velocity: 127).Note(480, 97, velocity: 1).Note(480, 98, velocity: 100).Note(960, 95, velocity: 127));

        Assert.IsTrue(NotesOf(without, Expert).All(n => n.Dynamics == NoteDynamics.None));
        Assert.IsFalse(without.ChartDynamics);
        // Let go before the event, so read without dynamics; the kicks' velocities are left, as the games ignore them.
        Assert.AreSequenceEqual(
            [
                new EditableNote(0, 10, 1),
                new EditableNote(480, 60, 0),
                new EditableNote(480, 60, 1, NoteDynamics.Ghost),
                new EditableNote(480, 60, 2),
                new EditableNote(960, 60, 0, DoubleKick: true),
            ],
            NotesOf(with, Expert));
        Assert.IsTrue(with.ChartDynamics);
        Assert.IsEmpty(with.TextEvents);
        Assert.IsEmpty(issues);
    }

    [TestMethod]
    public void TomMarkersAreKeptAsWritten()
    {
        var part = LoadPart(ChartInstrument.Drums, Song()
            .Track("PART DRUMS")
            .Note(0, 98).Note(0, 110, 480).Note(0, 111, 1).Note(960, 112, 0)).Part;

        Assert.AreSequenceEqual([new TomMarker(2, 0, 480), new TomMarker(3, 0, 1), new TomMarker(4, 960, 0)], part.TomMarkers.ToList());
        Assert.IsEmpty(part.RawEvents);
    }

    [TestMethod]
    public void RollsAndTrillsReachDownAsTheirVelocitySays()
    {
        var part = LoadPart(ChartInstrument.Drums, Song()
            .Track("PART DRUMS")
            .Note(0, 126, 480, velocity: 25).Note(480, 127, 480, velocity: 35).Note(960, 126, 480, velocity: 45)
            .Note(1440, 127, 480, velocity: 100).Note(1920, 126, 480, velocity: 51).Note(2400, 127, 480, velocity: 21)
            .Note(2880, 126, 480, velocity: 50)).Part;

        Assert.AreSequenceEqual(
            [
                new Phrase(PhraseKind.Roll, 0, 480, Easy),
                new Phrase(PhraseKind.Trill, 480, 480, Medium),
                new Phrase(PhraseKind.Roll, 960, 480, Hard),
                new Phrase(PhraseKind.Trill, 1440, 480, Expert),
                new Phrase(PhraseKind.Roll, 1920, 480, Expert),
                new Phrase(PhraseKind.Trill, 2400, 480, Easy),
                new Phrase(PhraseKind.Roll, 2880, 480, Hard),
            ],
            part.Phrases.ToList());
    }

    [TestMethod]
    public void MixEventsSpelledAsTheWriterWritesThemAreMixesAndOthersText()
    {
        var part = LoadPart(ChartInstrument.Drums, Song()
            .Track("PART DRUMS")
            .Note(0, 97)
            .Text(0, "[mix 3 drums0d]")
            .Text(0, "[mix 2 drums1]")
            .Text(480, "[mix_3_drums0]")
            .Text(960, "mix 3 drums0d")).Part;

        Assert.AreSequenceEqual([new DrumMix(0, 0, "d")], part[Expert].Mixes.ToList());
        Assert.AreSequenceEqual([new DrumMix(0, 1, "")], part[Hard].Mixes.ToList());
        Assert.AreSequenceEqual([TextEvent.Of(480, "[mix_3_drums0]"), TextEvent.Of(960, "mix 3 drums0d")], part.TextEvents.ToList());
    }

    [TestMethod]
    public void FillsBeforeCodaAreFillsAndAfterItBigRockEndings()
    {
        var midi = Song().Track("PART DRUMS").Note(0, 96);
        foreach (int pitch in Enumerable.Range(120, 5))
            midi.Note(960, pitch, 240).Note(3840, pitch, 480);
        foreach (int pitch in Enumerable.Range(120, 4))
            midi.Note(1920, pitch, 240);
        midi.Track("EVENTS").Text(3840, "[coda]");

        var (part, issues) = LoadPart(ChartInstrument.Drums, midi);

        Assert.AreSequenceEqual([new Phrase(PhraseKind.DrumFill, 960, 240), new Phrase(PhraseKind.BigRockEnding, 3840, 480)], part.Phrases.ToList());
        // Four aren't a fill: kept as they are.
        Assert.AreSequenceEqual(
            [
                .. Enumerable.Range(120, 4).Select(p => new RawEvent(1920, [0x90, (byte)p, 100])),
                .. Enumerable.Range(120, 4).Select(p => new RawEvent(2160, [0x90, (byte)p, 0])),
            ],
            part.RawEvents.ToList());
        Assert.IsEmpty(issues);
    }

    // Raw and text events.

    [TestMethod]
    public void EventsTheModelDoesNotReadAreKeptAtTheirTicksWithTheirBytes()
    {
        // AC C2.1: an animation note and face-off markers are kept to be written back at their ticks.
        var (part, issues) = LoadPart(ChartInstrument.Guitar, Song()
            .Track("PART GUITAR")
            .Note(0, 96)
            .Note(100, 40, 20)
            .Note(480, 105, 960).Note(480, 106, 960)
            .Raw(600, 0xF0, 5, 1, 2, 3, 4, 0xF7)
            .Raw(700, 0xB0, 7, 100)
            .Meta(800, 0x7F, [1, 2, 3])
            .Meta(900, 0x02, "(c) me"u8.ToArray())
            .Text(1000, "[idle]")
            .PhaseShift(1100, 3, 9, true).PhaseShift(1200, 3, 9, false));

        Assert.AreSequenceEqual(
            [
                new RawEvent(100, [0x90, 40, 100]),
                new RawEvent(120, [0x90, 40, 0]),
                new RawEvent(480, [0x90, 105, 100]),
                new RawEvent(480, [0x90, 106, 100]),
                new RawEvent(600, [0xF0, 5, 1, 2, 3, 4, 0xF7]),
                new RawEvent(700, [0xB0, 7, 100]),
                new RawEvent(800, [0xFF, 0x7F, 3, 1, 2, 3]),
                new RawEvent(1100, [0xF0, 8, 0x50, 0x53, 0, 0, 3, 9, 1, 0xF7]),
                new RawEvent(1200, [0xF0, 8, 0x50, 0x53, 0, 0, 3, 9, 0, 0xF7]),
                new RawEvent(1440, [0x90, 105, 0]),
                new RawEvent(1440, [0x90, 106, 0]),
            ],
            part.RawEvents.ToList());
        Assert.AreSequenceEqual([new TextEvent(900, 0x02, "(c) me"), TextEvent.Of(1000, "[idle]")], part.TextEvents.ToList());
        Assert.IsEmpty(issues);
    }

    [TestMethod]
    public void DrumEventsTheModelDoesNotReadAreKeptRaw()
    {
        // 125 is the lane marker ChartToMidi writes; a tap marker and Phase Shift phrases mean nothing on drums.
        var part = LoadPart(ChartInstrument.Drums, Song()
            .Track("PART DRUMS")
            .Note(0, 96).Note(0, 125, 480).Note(0, 104, 10).Text(0, "[ENHANCED_OPENS]")
            .PhaseShift(0, 3, MidiLayout.SysexTap, true).PhaseShift(10, 3, MidiLayout.SysexTap, false)).Part;

        Assert.AreSequenceEqual(
            [
                new RawEvent(0, [0x90, 125, 100]),
                new RawEvent(0, [0x90, 104, 100]),
                new RawEvent(0, [0xF0, 8, 0x50, 0x53, 0, 0, 3, 4, 1, 0xF7]),
                new RawEvent(10, [0x90, 104, 0]),
                new RawEvent(10, [0xF0, 8, 0x50, 0x53, 0, 0, 3, 4, 0, 0xF7]),
                new RawEvent(480, [0x90, 125, 0]),
            ],
            part.RawEvents.ToList());
        Assert.AreSequenceEqual([TextEvent.Of(0, "[ENHANCED_OPENS]")], part.TextEvents.ToList());
        Assert.IsFalse(part.EnhancedOpens);
    }

    // What's left out.

    [TestMethod]
    public void TheFirstEventIsLeftOutWhenItIsNotTheName()
    {
        // The reader never reads the first event, so its note-off pairs with nothing.
        var (part, issues) = LoadPart(ChartInstrument.Guitar, Song()
            .Track()
            .Raw(0, 0x90, 96, 100)
            .Meta(0, 0x03, "PART GUITAR"u8.ToArray())
            .Raw(60, 0x80, 96, 0)
            .Note(480, 97));

        Assert.AreSequenceEqual([new EditableNote(480, 60, 1)], NotesOf(part, Expert));
        Assert.IsEmpty(part.RawEvents);
        Assert.IsEmpty(part.TextEvents);
        Assert.AreSequenceEqual(
            [
                new ChartLoadIssue(ChartLoadIssueKind.FirstEventDropped, ChartInstrument.Guitar, null, 0, 96),
                new ChartLoadIssue(ChartLoadIssueKind.UnpairedNoteEvent, ChartInstrument.Guitar, null, 60, 96),
            ],
            issues.ToList());
    }

    [TestMethod]
    public void OfTwoNotesOnATickAndLaneTheOneTheReaderKeepsIsKept()
    {
        var guitar = LoadPart(ChartInstrument.Guitar, Song()
            .Track("PART GUITAR")
            .Raw(0, 0x90, 96, 100).Raw(0, 0x91, 96, 100).Raw(50, 0x81, 96, 0).Raw(100, 0x80, 96, 0));
        var drums = LoadPart(ChartInstrument.Drums, Song().Track("PART DRUMS").Note(0, 95, 10).Note(0, 96, 20));

        // The first let go.
        Assert.AreSequenceEqual([new EditableNote(0, 50, 0)], NotesOf(guitar.Part, Expert));
        Assert.AreSequenceEqual([new ChartLoadIssue(ChartLoadIssueKind.DuplicateNote, ChartInstrument.Guitar, Expert, 0, 96)], guitar.Issues.ToList());
        // The kick and the 2x kick are one pad to the reader.
        Assert.AreSequenceEqual([new EditableNote(0, 10, 0, DoubleKick: true)], NotesOf(drums.Part, Expert));
        Assert.AreSequenceEqual([new ChartLoadIssue(ChartLoadIssueKind.DuplicateNote, ChartInstrument.Drums, Expert, 0, 96)], drums.Issues.ToList());
        Assert.IsEmpty(guitar.Part.RawEvents);
        Assert.IsEmpty(drums.Part.RawEvents);
    }

    [TestMethod]
    public void AKickTheReaderLosesToAnEasierDifficultys2xKickPitchIsLeftOut()
    {
        // 71 reads as Medium's 2x kick, which the games drop, but it's kept over the kick let go after it.
        byte[] midi = Song().Track("PART DRUMS").Note(0, 71, 10).Note(0, 72, 20).Note(480, 72).Build();

        var result = MidiChartLoader.Load(midi, ChartReadOptions.Default);
        var part = result.Chart.PartOf(ChartInstrument.Drums)!;

        Assert.AreSequenceEqual([new EditableNote(480, 60, 0)], NotesOf(part, Medium));
        Assert.AreSequenceEqual([new RawEvent(0, [0x90, 71, 100]), new RawEvent(10, [0x90, 71, 0])], part.RawEvents.ToList());
        Assert.AreSequenceEqual([new ChartLoadIssue(ChartLoadIssueKind.DuplicateNote, ChartInstrument.Drums, Medium, 0, 72)], result.Issues.ToList());
        AssertNotesMatchReader(result.Chart, MidiChartReader.Read(midi, ChartReadOptions.Default));
    }

    [TestMethod]
    public void NoteEventsTheReaderPairsWithNothingAreLeftOutWhereTheModelReadsThePitch()
    {
        var (part, issues) = LoadPart(ChartInstrument.Guitar, Song()
            .Track("PART GUITAR")
            .Raw(0, 0x90, 97, 100).Raw(10, 0x90, 97, 100).Raw(20, 0x80, 97, 0)
            .Raw(30, 0x80, 98, 0)
            .Raw(40, 0x90, 99, 100)
            .Raw(50, 0x80, 105, 0));

        Assert.AreSequenceEqual([new EditableNote(0, 20, 1)], NotesOf(part, Expert));
        Assert.AreSequenceEqual(
            [
                new ChartLoadIssue(ChartLoadIssueKind.UnpairedNoteEvent, ChartInstrument.Guitar, null, 10, 97),
                new ChartLoadIssue(ChartLoadIssueKind.UnpairedNoteEvent, ChartInstrument.Guitar, null, 30, 98),
                new ChartLoadIssue(ChartLoadIssueKind.UnpairedNoteEvent, ChartInstrument.Guitar, null, 40, 99),
            ],
            issues.ToList());
        // A pitch the model doesn't read is kept as it is.
        Assert.AreSequenceEqual([new RawEvent(50, [0x80, 105, 0])], part.RawEvents.ToList());
    }

    [TestMethod]
    public void PhaseShiftEventsTheReaderPairsWithNothingAreLeftOut()
    {
        var (part, issues) = LoadPart(ChartInstrument.Guitar, Song()
            .Track("PART GUITAR")
            .Note(0, 96)
            .PhaseShift(0, 3, MidiLayout.SysexTap, true)
            .PhaseShift(100, 0xFF, MidiLayout.SysexOpen, false));

        Assert.AreSequenceEqual([new EditableNote(0, 60, 0)], NotesOf(part, Expert));
        Assert.IsEmpty(part[Expert].FlaggedChords);
        Assert.AreSequenceEqual(
            [
                new ChartLoadIssue(ChartLoadIssueKind.UnpairedSysexEvent, ChartInstrument.Guitar, Expert, 0),
                new ChartLoadIssue(ChartLoadIssueKind.UnpairedSysexEvent, ChartInstrument.Guitar, null, 100),
            ],
            issues.ToList());
        Assert.IsEmpty(part.RawEvents);
    }
}
