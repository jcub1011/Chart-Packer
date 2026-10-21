using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Difficulty;
using ChartShared.Core.Charts;

namespace ChartBuilder.Tests.Charting.DifficultyAnalysis;

/// <summary>How each instrument's notes are read into the difficulty model's lanes (docs/difficulty-model.md §14).</summary>
[TestClass]
public sealed class InstrumentNotesTests
{
    private static readonly TempoMap Tempo = DifficultyCharts.Chart([]).TempoMap.ToTempoMap();

    [TestMethod]
    public void TheSixthFretIsReadOnlyBySixFret()
    {
        var track = Track(new ChartNote(0, 0, 5, NoteFlags.None), new ChartNote(480, 0, 2, NoteFlags.None), new ChartNote(480, 0, 5, NoteFlags.None));

        var six = DifficultyNotes.Of(track, Tempo, parameters: DifficultyParameters.For(DifficultyInstrument.SixFret));
        var five = DifficultyNotes.Of(track, Tempo);

        Assert.AreSequenceEqual(new byte[] { 0b10_0000, 0b10_0100 }, six.Select(n => n.Lanes).ToList());
        Assert.AreSequenceEqual(new byte[] { 0, 0b100 }, five.Select(n => n.Lanes).ToList());
    }

    [TestMethod]
    public void TheKickIsTheTopBitAndProCymbalsArePadsOfTheirOwn()
    {
        var track = Track(
            new ChartNote(0, 0, ChartNote.OpenLane, NoteFlags.None),
            new ChartNote(0, 0, 1, NoteFlags.Cymbal),
            new ChartNote(480, 0, 1, NoteFlags.None),
            new ChartNote(480, 0, 3, NoteFlags.Cymbal));

        var fourLane = DifficultyNotes.Of(track, Tempo, parameters: DifficultyParameters.For(DifficultyInstrument.Drums));
        var pro = DifficultyNotes.Of(track, Tempo, parameters: DifficultyParameters.For(DifficultyInstrument.ProDrums));

        Assert.AreSequenceEqual(new byte[] { DrumPads.Kick | 0b10, 0b1010 }, fourLane.Select(n => n.Lanes).ToList());
        Assert.AreSequenceEqual(new byte[] { DrumPads.Kick | 0b1_0000, 0b100_0010 }, pro.Select(n => n.Lanes).ToList());
        Assert.IsTrue(pro.All(n => n.Kind == NoteKind.Strum && n.Held == 0));
    }

    [TestMethod]
    public void ExpertPlusKicksAreNotRead()
    {
        var track = Track(
            new ChartNote(0, 0, ChartNote.OpenLane, NoteFlags.DoubleKick),
            new ChartNote(240, 0, ChartNote.OpenLane, NoteFlags.None),
            new ChartNote(480, 0, ChartNote.OpenLane, NoteFlags.DoubleKick),
            new ChartNote(480, 0, 0, NoteFlags.None));

        var notes = DifficultyNotes.Of(track, Tempo, parameters: DifficultyParameters.For(DifficultyInstrument.Drums));

        Assert.AreSequenceEqual(new byte[] { DrumPads.Kick, 0b1 }, notes.Select(n => n.Lanes).ToList());
    }

    [TestMethod]
    public void DrumNotesInARollOrTrillLaneAreRolls()
    {
        var track = Track([.. Enumerable.Range(0, 8).Select(i => new ChartNote(i * 120L, 0, 0, NoteFlags.None))]);
        Phrase[] lanes = [new(PhraseKind.Roll, 240, 480, ChartDifficulty.Expert)];

        var notes = DifficultyNotes.Of(track, Tempo, parameters: DifficultyParameters.For(DifficultyInstrument.Drums), freeLanes: lanes);

        Assert.AreSequenceEqual(
            [NoteKind.Strum, NoteKind.Strum, NoteKind.Roll, NoteKind.Roll, NoteKind.Roll, NoteKind.Roll, NoteKind.Strum, NoteKind.Strum],
            notes.Select(n => n.Kind).ToList());
    }

    [TestMethod]
    public void ALaneExtendedDownReachesTheDifficultiesItsExtendedTo()
    {
        var chart = EditableChart.CreateNew(new NewChartSettings("Song", 480, 120, 4, 4, new HashSet<ChartInstrument> { ChartInstrument.Drums }, DrumsVariant.Pro));
        var drums = chart.PartOf(ChartInstrument.Drums)!;
        drums.Phrases.Add(new Phrase(PhraseKind.Roll, 0, 480, ChartDifficulty.Hard));
        drums.Phrases.Add(new Phrase(PhraseKind.Trill, 960, 480, ChartDifficulty.Expert));
        drums.Phrases.Add(new Phrase(PhraseKind.Solo, 1920, 480));

        Assert.HasCount(2, DifficultyNotes.FreeLanesOf(drums, ChartDifficulty.Expert));
        Assert.HasCount(1, DifficultyNotes.FreeLanesOf(drums, ChartDifficulty.Hard));
        Assert.IsEmpty(DifficultyNotes.FreeLanesOf(drums, ChartDifficulty.Medium));
    }

    [TestMethod]
    public void KeysCanBeReadAsPressedRatherThanStrummedOrHammered()
    {
        var track = Track(new ChartNote(0, 0, 0, NoteFlags.None), new ChartNote(120, 0, 1, NoteFlags.Hopo), new ChartNote(240, 0, 2, NoteFlags.Tap));

        var pressed = DifficultyNotes.Of(track, Tempo, parameters: DifficultyParameters.For(DifficultyInstrument.Keys) with { ReadsNoteKinds = false });

        Assert.IsTrue(pressed.All(n => n.Kind == NoteKind.Strum));
        Assert.AreSequenceEqual([NoteKind.Strum, NoteKind.Hopo, NoteKind.Tap], DifficultyNotes.Of(track, Tempo).Select(n => n.Kind).ToList());
    }

    private static NoteTrack Track(params ChartNote[] notes) => new(notes, []);
}
