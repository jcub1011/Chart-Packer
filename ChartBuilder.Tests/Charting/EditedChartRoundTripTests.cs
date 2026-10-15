using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Commands;
using ChartShared.Core.Charts;
using static ChartBuilder.Tests.Charting.DrumCommandTests;
using static ChartBuilder.Tests.Charting.MidiChartWriterTests;

namespace ChartBuilder.Tests.Charting;

/// <summary>
/// Charts made with the editor's commands, as an author would make them, save and read back with everything in them
/// (AC C6.1, C7.1 and C8.1), both as the model and as the games read them.
/// </summary>
[TestClass]
public sealed class EditedChartRoundTripTests
{
    private const ChartInstrument Drums = ChartInstrument.Drums;
    private const ChartInstrument Guitar = ChartInstrument.Guitar;
    private const ChartDifficulty Expert = ChartDifficulty.Expert;
    private const ChartDifficulty Hard = ChartDifficulty.Hard;
    private const int Kick = 0, Red = 1, Yellow = 2, Blue = 3, Green = 4;

    [TestMethod]
    public void AProChartWithEveryDrumKindSavesAndReadsBackWithThemAll()
    {
        // AC C6.1.
        var chart = DrumsChart();
        Add(chart, Expert,
            Pad(0, Kick), Pad(0, Red), Pad(480, Kick), Pad(480, Red),
            Pad(960, Yellow), Pad(1440, Yellow), Pad(1920, Blue), Pad(2400, Blue), Pad(2880, Green), Pad(3360, Green),
            Pad(3840, Red), Pad(4200, Green),
            Pad(4800, Red), Pad(4920, Red), Pad(5040, Red),
            Pad(5760, Yellow), Pad(5880, Blue), Pad(6000, Yellow),
            Pad(7000, Red));
        Add(chart, Hard, Pad(4800, Red), Pad(4920, Red), Pad(4200, Green));
        Apply(chart,
            new SetToms([(Yellow, 1440L), (Blue, 2400L), (Green, 3360L)], tom: true),
            new SetDynamics(Drums, Expert, [Pad(0, Red)], NoteDynamics.Accent),
            new SetDynamics(Drums, Expert, [Pad(480, Red)], NoteDynamics.Ghost),
            new SetDoubleKick([Pad(480, Kick)], doubleKick: true),
            new AddPhrase(Drums, new Phrase(PhraseKind.DrumFill, 3840, 480)),
            new AddPhrase(Drums, new Phrase(PhraseKind.Roll, 4800, 480, Hard)),
            new AddPhrase(Drums, new Phrase(PhraseKind.Trill, 5760, 480, Expert)),
            new AddDiscoFlip(Expert, 6720, 7680));
        var after = MidiChartWriter.SettingsAfterSave(chart, ChartReadOptions.Default);

        var result = MidiChartWriter.WriteChecked(chart, after);

        Assert.IsTrue(result.IsValid, DescribeModel(result.ModelDifferences));
        var drums = chart.PartOf(Drums)!;
        var saved = result.Saved.PartOf(Drums)!;
        for (int i = 0; i < drums.Tracks.Count; i++)
        {
            Assert.AreSequenceEqual(drums.Tracks[i].Notes.ToList(), [.. saved.Tracks[i].Notes]);
            Assert.AreSequenceEqual(drums.Tracks[i].Mixes.ToList(), [.. saved.Tracks[i].Mixes]);
        }

        Assert.AreSequenceEqual(drums.Phrases.ToList(), [.. saved.Phrases]);
        Assert.AreSequenceEqual(drums.TomMarkers.ToList(), [.. saved.TomMarkers]);
        var game = MidiChartReader.Read(result.Bytes, after).Tracks[new ChartTrackKey(Drums, Expert)].Notes;
        ChartNote At(long tick, int lane) => game.Single(n => n.Tick == tick && n.Lane == lane);
        foreach (var (tick, lane) in (ReadOnlySpan<(long, int)>)[(960, 1), (1920, 2), (2880, 3)])
            Assert.IsTrue(IsCymbal(At(tick, lane)), $"a cymbal at {tick}");
        foreach (var (tick, lane) in (ReadOnlySpan<(long, int)>)[(1440, 1), (2400, 2), (3360, 3)])
            Assert.IsFalse(IsCymbal(At(tick, lane)), $"a tom at {tick}");
        Assert.IsTrue(At(0, 0).Flags.HasFlag(NoteFlags.Accent));
        Assert.IsTrue(At(480, 0).Flags.HasFlag(NoteFlags.Ghost));
        Assert.IsTrue(At(480, ChartNote.OpenLane).Flags.HasFlag(NoteFlags.DoubleKick));
        Assert.IsTrue(IsCymbal(At(7000, 1)), "red in the disco flip section plays as a yellow cymbal");
    }

    [TestMethod]
    public void EveryGhlLaneABarreChordAndEachForcingSaveAndReadBackUnchanged()
    {
        // AC C7.1.
        var chart = EditableChart.CreateNew(new NewChartSettings("Song", 480, 120, 4, 4, new HashSet<ChartInstrument> { ChartInstrument.GhlGuitar }, DrumsVariant.Pro));
        var notes = new List<EditableNote> { new(0, 1, ChartNote.OpenLane) };
        for (int lane = 0; lane <= 5; lane++)
            notes.Add(new EditableNote(480 * (lane + 1), 1, lane));
        notes.AddRange([new(3360, 1, 0), new(3360, 240, 3), new(3840, 1, 0), new(3840, 1, 1), new(4320, 1, 4), new(4800, 1, 2)]);
        Apply(chart,
            new AddNotes(ChartInstrument.GhlGuitar, Expert, notes),
            new SetChordFlags(ChartInstrument.GhlGuitar, Expert, [3840L], ChordFlags.ForceHopo, on: true),
            new SetChordFlags(ChartInstrument.GhlGuitar, Expert, [4320L], ChordFlags.ForceStrum, on: true),
            new SetChordFlags(ChartInstrument.GhlGuitar, Expert, [4800L], ChordFlags.Tap, on: true));

        var result = MidiChartWriter.WriteChecked(chart, MidiChartWriter.SettingsAfterSave(chart, ChartReadOptions.Default));

        Assert.IsTrue(result.IsValid, DescribeModel(result.ModelDifferences));
        var track = chart.PartOf(ChartInstrument.GhlGuitar)![Expert];
        var saved = result.Saved.PartOf(ChartInstrument.GhlGuitar)![Expert];
        Assert.AreSequenceEqual(track.Notes.ToList(), [.. saved.Notes]);
        Assert.AreSequenceEqual(track.FlaggedChords.ToList(), [.. saved.FlaggedChords]);
    }

    [TestMethod]
    public void EveryPhraseAndEventKindRoundTripsAndFreeTextSurvivesAnUnrelatedEdit()
    {
        // AC C8.1.
        var chart = EditableChart.CreateNew(new NewChartSettings("Song", 480, 120, 4, 4, new HashSet<ChartInstrument> { Guitar, Drums }, DrumsVariant.Pro));
        new AddNotes(Guitar, Expert, [.. Enumerable.Range(0, 16).Select(i => new EditableNote(i * 480L, 1, i % 5))]).Apply(chart);
        Add(chart, Expert, [.. Enumerable.Range(0, 16).Select(i => Pad(i * 480L, 1 + (i % 4)))]);
        Apply(chart,
            new AddTextEvent(null, TextEvent.Of(0, "[section Intro]")),
            new AddTextEvent(null, TextEvent.Of(1920, "[prc_verse_1]")),
            new AddTextEvent(null, TextEvent.Of(2400, "[crowd_intense]")),
            new AddTextEvent(null, TextEvent.Of(2880, "a note to the charter")),
            new AddTextEvent(null, TextEvent.Of(5760, "[coda]")),
            new AddTextEvent(null, TextEvent.Of(7200, "[music_end]")),
            new AddTextEvent(Guitar, TextEvent.Of(0, "[idle]")),
            new AddTextEvent(Drums, TextEvent.Of(960, "[play]")),
            new AddPhrase(Guitar, new Phrase(PhraseKind.StarPower, 0, 960)),
            new AddPhrase(Guitar, new Phrase(PhraseKind.Solo, 1920, 960)),
            new AddPhrase(Guitar, new Phrase(PhraseKind.BigRockEnding, 6240, 960)),
            new AddPhrase(Drums, new Phrase(PhraseKind.StarPower, 0, 480)),
            new AddPhrase(Drums, new Phrase(PhraseKind.Solo, 960, 480)),
            new AddPhrase(Drums, new Phrase(PhraseKind.DrumFill, 1440, 480)),
            new AddPhrase(Drums, new Phrase(PhraseKind.Roll, 1920, 240, ChartDifficulty.Easy)),
            new AddPhrase(Drums, new Phrase(PhraseKind.Roll, 2400, 240, ChartDifficulty.Medium)),
            new AddPhrase(Drums, new Phrase(PhraseKind.Trill, 2880, 240, Hard)),
            new AddPhrase(Drums, new Phrase(PhraseKind.Trill, 3360, 240, Expert)),
            new AddPhrase(Drums, new Phrase(PhraseKind.BigRockEnding, 6240, 960)));
        var after = MidiChartWriter.SettingsAfterSave(chart, ChartReadOptions.Default);

        var first = MidiChartWriter.WriteChecked(chart, after);
        Assert.IsTrue(first.IsValid, DescribeModel(first.ModelDifferences));
        CommandChecks.AssertSame(chart, first.Saved, "as saved");

        var reopened = MidiChartLoader.Load(first.Bytes, after).Chart;
        new AddNotes(Guitar, Hard, [new EditableNote(0, 1, 0)]).Apply(reopened);
        var second = MidiChartWriter.WriteChecked(reopened, after);

        Assert.IsTrue(second.IsValid, DescribeModel(second.ModelDifferences));
        var events = second.Saved.Events.Select(e => e.Text).ToList();
        foreach (string text in (string[])["[section Intro]", "[prc_verse_1]", "[crowd_intense]", "a note to the charter", "[coda]", "[music_end]"])
            Assert.Contains(text, events);
        Assert.Contains(TextEvent.Of(0, "[idle]"), second.Saved.PartOf(Guitar)!.TextEvents.ToList());
        Assert.Contains(TextEvent.Of(960, "[play]"), second.Saved.PartOf(Drums)!.TextEvents.ToList());
        Assert.HasCount(9, second.Saved.PartOf(Drums)!.Phrases.Concat(second.Saved.PartOf(Guitar)!.Phrases).Where(p => p.Kind != PhraseKind.StarPower).ToList());
    }

    private static void Apply(EditableChart chart, params IChartCommand[] commands)
    {
        foreach (var command in commands)
            command.Apply(chart);
    }
}
