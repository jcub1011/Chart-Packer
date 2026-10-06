using ChartPacker.Core.Charting;
using ChartPacker.Core.Charts;
using static ChartBuilder.Tests.Charting.MidiChartLoaderTests;

namespace ChartBuilder.Tests.Charting;

[TestClass]
public sealed class GameViewTests
{
    private const ChartDifficulty Expert = ChartDifficulty.Expert;

    public static IEnumerable<object[]> FixtureNames => MidiFixtures.Names;

    [TestMethod]
    [DynamicData(nameof(FixtureNames))]
    public void EveryFixtureProjectsAsTheReaderReadsItAtEveryHopoThreshold(string name)
    {
        // AC C5.2 and AC C17.2 basis: what the editor shows is what the reader reads.
        var fixture = MidiFixtures.Named(name);
        var chart = MidiChartLoader.Load(fixture.Midi, fixture.Options).Chart;

        var thresholds = fixture.Options.MidiHopoThresholds(chart.Resolution);
        foreach (long threshold in thresholds)
        {
            var options = fixture.Options with { MidiHopoTicks = threshold };
            var read = MidiChartReader.Read(fixture.Midi, options);

            var view = GameView.Project(chart, options);

            var differences = ChartReadBack.Compare(view, read, GameView.Instruments);
            Assert.IsEmpty(differences, $"At a HOPO threshold of {threshold}: {Describe(differences)}");
            Assert.AreSequenceEqual(read.Instruments.Where(i => i != ChartInstrument.Keys).ToList(), view.Instruments.ToList());
        }

        Assert.HasCount(2, thresholds);
    }

    [TestMethod]
    public void TheHopoThresholdsTheFixturesAreReadWithGiveDifferentHopos()
    {
        // The note 165 ticks after the one before is a HOPO only at Rock Band's threshold of 170.
        var fixture = MidiFixtures.Named(nameof(MidiFixtures.LeadGuitar));
        var chart = MidiChartLoader.Load(fixture.Midi, fixture.Options).Chart;

        bool HopoAt(long threshold) => GameView.Project(chart, fixture.Options with { MidiHopoTicks = threshold })
            .Tracks[new ChartTrackKey(ChartInstrument.Guitar, Expert)].Notes.Single(n => n.Tick == 1086).Flags.HasFlag(NoteFlags.Hopo);

        Assert.IsFalse(HopoAt(161));
        Assert.IsTrue(HopoAt(170));
    }

    [TestMethod]
    public void PartKeysIsLeftOutOfTheGameView()
    {
        var fixture = MidiFixtures.Named(nameof(MidiFixtures.Passthrough));

        var view = GameView.Project(MidiChartLoader.Load(fixture.Midi, fixture.Options).Chart, fixture.Options);

        Assert.DoesNotContain(ChartInstrument.Keys, GameView.Instruments);
        Assert.Contains(ChartInstrument.Keys, MidiChartReader.Read(fixture.Midi, fixture.Options).Instruments);
        Assert.AreSequenceEqual([ChartInstrument.Guitar], view.Instruments.ToList());
    }

    [TestMethod]
    public void APartWithoutNotesHasNoTracks()
    {
        var chart = new EditableChart(480);
        chart.AddPart(ChartInstrument.Bass);
        chart.AddPart(ChartInstrument.Guitar)[Expert].Notes.Add(new EditableNote(0, 1, 0));

        var view = GameView.Project(chart, ChartReadOptions.Default);

        Assert.AreSequenceEqual([new ChartTrackKey(ChartInstrument.Guitar, Expert)], view.Tracks.Keys.ToList());
        Assert.IsFalse(view.IsProDrums);
    }

    // Frets.

    [TestMethod]
    public void TheSustainCutoffAndHopoThresholdComeFromTheOptions()
    {
        var chart = new EditableChart(480);
        var expert = chart.AddPart(ChartInstrument.Guitar)[Expert];
        expert.Notes.Add(new EditableNote(0, 100, 0));
        expert.Notes.Add(new EditableNote(150, 200, 1));

        List<ChartNote> NotesWith(ChartReadOptions options) => [.. GameView.Project(chart, options).Tracks[new ChartTrackKey(ChartInstrument.Guitar, Expert)].Notes];

        // A third of a beat, 160 ticks, is the cutoff; the threshold is that and a tick.
        Assert.AreSequenceEqual([new ChartNote(0, 0, 0, NoteFlags.None), new ChartNote(150, 200, 1, NoteFlags.Hopo)], NotesWith(ChartReadOptions.Default));
        Assert.AreEqual(100, NotesWith(new ChartReadOptions(SustainCutoff: 50))[0].Length);
        Assert.AreEqual(NoteFlags.None, NotesWith(new ChartReadOptions(HopoFrequency: 100))[1].Flags);
        Assert.AreEqual(NoteFlags.None, NotesWith(ChartReadOptions.Default with { MidiHopoTicks = 149 })[1].Flags);
        Assert.AreEqual(NoteFlags.Hopo, NotesWith(ChartReadOptions.Default with { MidiHopoTicks = 150 })[1].Flags);
    }

    [TestMethod]
    public void FlaggedChordsAreForcedOrTappedAsTheirMarkersWouldBe()
    {
        var chart = new EditableChart(480);
        var expert = chart.AddPart(ChartInstrument.Guitar)[Expert];
        foreach (long tick in (long[])[0, 480, 600, 1440])
            expert.Notes.Add(new EditableNote(tick, 1, tick == 480 ? 1 : 0));
        expert.Notes.Add(new EditableNote(1440, 1, 2));
        expert.SetFlags(480, ChordFlags.ForceHopo | ChordFlags.ForceStrum);
        expert.SetFlags(600, ChordFlags.ForceStrum);
        expert.SetFlags(1440, ChordFlags.Tap | ChordFlags.ForceHopo);

        var notes = GameView.Project(chart, ChartReadOptions.Default).Tracks[new ChartTrackKey(ChartInstrument.Guitar, Expert)].Notes;

        // Forcing a HOPO wins over forcing a strum, and a tap over both; a flag covers its own chord alone.
        Assert.AreSequenceEqual(
            [NoteFlags.None, NoteFlags.Hopo, NoteFlags.None, NoteFlags.Tap, NoteFlags.Tap],
            notes.Select(n => n.Flags).ToList());
    }

    [TestMethod]
    public void StarPowerComesFromThePhrasesWhateverTheMultiplierNote()
    {
        var chart = new EditableChart(480);
        var guitar = chart.AddPart(ChartInstrument.Guitar);
        foreach (long tick in (long[])[0, 600, 1200])
            guitar[Expert].Notes.Add(new EditableNote(tick, 1, 0));
        guitar.Phrases.Add(new Phrase(PhraseKind.StarPower, 0, 480));
        guitar.Phrases.Add(new Phrase(PhraseKind.Solo, 480, 960));

        foreach (var options in (ChartReadOptions[])[ChartReadOptions.Default, new ChartReadOptions(MultiplierNote: 103)])
        {
            var track = GameView.Project(chart, options).Tracks[new ChartTrackKey(ChartInstrument.Guitar, Expert)];

            Assert.AreSequenceEqual([new StarPowerPhrase(0, 480)], track.StarPower.ToList());
            Assert.AreSequenceEqual([true, false, false], track.Notes.Select(n => n.Flags.HasFlag(NoteFlags.StarPower)).ToList());
        }
    }

    // Drums.

    [TestMethod]
    public void TheKicksAccentsAndGhostsAreDroppedAndThePadsKept()
    {
        var chart = new EditableChart(480);
        var drums = chart.AddPart(ChartInstrument.Drums);
        drums.ChartDynamics = true;
        var expert = drums[Expert];
        expert.Notes.Add(new EditableNote(0, 1, 0, NoteDynamics.Accent));
        expert.Notes.Add(new EditableNote(0, 1, 1, NoteDynamics.Accent));
        expert.Notes.Add(new EditableNote(480, 1, 0, NoteDynamics.Ghost, DoubleKick: true));
        expert.Notes.Add(new EditableNote(480, 1, 2, NoteDynamics.Ghost));
        byte[] midi = Song()
            .Track("PART DRUMS")
            .Text(0, "[ENABLE_CHART_DYNAMICS]")
            .Note(0, 96, 1, velocity: 127).Note(0, 97, 1, velocity: 127).Note(480, 95, 1, velocity: 1).Note(480, 98, 1, velocity: 1)
            .Build();

        var view = GameView.Project(chart, ChartReadOptions.Default);

        Assert.AreSequenceEqual(
            [
                new ChartNote(0, 0, ChartNote.OpenLane, NoteFlags.None),
                new ChartNote(0, 0, 0, NoteFlags.Accent),
                new ChartNote(480, 0, ChartNote.OpenLane, NoteFlags.DoubleKick),
                new ChartNote(480, 0, 1, NoteFlags.Cymbal | NoteFlags.Ghost),
            ],
            view.Tracks[new ChartTrackKey(ChartInstrument.Drums, Expert)].Notes.ToList());
        Assert.IsEmpty(ChartReadBack.Compare(view, MidiChartReader.Read(midi, ChartReadOptions.Default), GameView.Instruments));

        // Without [ENABLE_CHART_DYNAMICS] they aren't written, so none are read.
        drums.ChartDynamics = false;
        var plain = GameView.Project(chart, ChartReadOptions.Default).Tracks[new ChartTrackKey(ChartInstrument.Drums, Expert)].Notes;
        Assert.IsTrue(plain.All(n => (n.Flags & (NoteFlags.Accent | NoteFlags.Ghost)) == 0));
    }

    [TestMethod]
    public void AtATickMixesComeBeforeOtherTextSoAMixKeptAsTextWins()
    {
        // A mix in the model, then one kept as text in a spelling the writer doesn't write, which the games read too. A
        // red and a yellow tom: disco flip makes them a red and a yellow cymbal.
        var chart = new EditableChart(480);
        var drums = chart.AddPart(ChartInstrument.Drums);
        drums[Expert].Notes.Add(new EditableNote(0, 1, 1));
        drums[Expert].Notes.Add(new EditableNote(0, 1, 2));
        drums.TomMarkers.Add(new TomMarker(TomMarker.FirstPad, 0, 10));
        drums[Expert].Mixes.Add(new DrumMix(0, 0, DrumMix.DiscoFlipSetting));
        drums.TextEvents.Add(TextEvent.Of(0, "mix 3 drums0"));

        Assert.AreSequenceEqual([(0L, "mix 3 drums0d"), (0L, "mix 3 drums0")], GameView.TextsOf(drums));
        Assert.AreSequenceEqual(
            [new ChartNote(0, 0, 0, NoteFlags.None), new ChartNote(0, 0, 1, NoteFlags.None)],
            GameView.Project(chart, ChartReadOptions.Default).Tracks[new ChartTrackKey(ChartInstrument.Drums, Expert)].Notes.ToList());

        // The other way round, disco flip is on: red plays as a yellow cymbal, and the yellow tom as red.
        drums[Expert].Mixes.Clear();
        drums[Expert].Mixes.Add(new DrumMix(0, 0, ""));
        drums.TextEvents.Clear();
        drums.TextEvents.Add(TextEvent.Of(0, "mix 3 drums0d"));
        Assert.AreSequenceEqual(
            [new ChartNote(0, 0, 0, NoteFlags.None), new ChartNote(0, 0, 1, NoteFlags.Cymbal)],
            GameView.Project(chart, ChartReadOptions.Default).Tracks[new ChartTrackKey(ChartInstrument.Drums, Expert)].Notes.ToList());
    }

    [TestMethod]
    public void AFileReadsAsItsGameViewOnlyWhenItsMixesComeFirst()
    {
        // Written as PartEventOrder says, the file reads as its game view; with the text first, it doesn't.
        byte[] ordered = Song().Track("PART DRUMS").Text(0, "[mix 3 drums0d]").Text(0, "mix 3 drums0").Note(0, 97).Note(0, 98).Note(0, 110, 10).Build();
        byte[] reversed = Song().Track("PART DRUMS").Text(0, "mix 3 drums0").Text(0, "[mix 3 drums0d]").Note(0, 97).Note(0, 98).Note(0, 110, 10).Build();

        foreach (var (midi, same) in ((byte[], bool)[])[(ordered, true), (reversed, false)])
        {
            var view = GameView.Project(MidiChartLoader.Load(midi, ChartReadOptions.Default).Chart, ChartReadOptions.Default);
            var differences = ChartReadBack.Compare(view, MidiChartReader.Read(midi, ChartReadOptions.Default), GameView.Instruments);
            Assert.AreEqual(same, differences.Count == 0, Describe(differences));
        }
    }

    [TestMethod]
    public void OfAKickAndA2xKickOnATickTheOneLetGoFirstIsKept()
    {
        // The shorter is let go first; of two as long, the lower pitch, the 2x kick's.
        foreach (var (kick, doubleKick, keptDouble) in ((long, long, bool)[])[(10, 20, false), (20, 10, true), (10, 10, true)])
        {
            var chart = new EditableChart(480);
            var expert = chart.AddPart(ChartInstrument.Drums)[Expert];
            expert.Notes.Add(new EditableNote(0, kick, 0));
            expert.Notes.Add(new EditableNote(0, doubleKick, 0, DoubleKick: true));
            byte[] midi = Song().Track("PART DRUMS").Note(0, 95, doubleKick).Note(0, 96, kick).Build();

            var view = GameView.Project(chart, ChartReadOptions.Default);

            var note = view.Tracks[new ChartTrackKey(ChartInstrument.Drums, Expert)].Notes.Single();
            Assert.AreEqual(keptDouble, note.Flags.HasFlag(NoteFlags.DoubleKick), $"{kick} and {doubleKick}");
            Assert.IsEmpty(ChartReadBack.Compare(view, MidiChartReader.Read(midi, ChartReadOptions.Default), GameView.Instruments));
        }
    }

    [TestMethod]
    public void TheFirstOfAFifthPadNoteAndATomMarkerSettlesTheLayout()
    {
        // At one tick the fifth-pad note starts first, on every difficulty, and a marker held for no ticks too.
        Assert.AreEqual(DrumsLayout.FiveLane, LayoutOf(ChartReadOptions.Default, fifthPadAt: 480, tomAt: 480));
        Assert.AreEqual(DrumsLayout.FiveLane, LayoutOf(ChartReadOptions.Default, fifthPadAt: 480, tomAt: 480, ChartDifficulty.Easy));
        Assert.AreEqual(DrumsLayout.FiveLane, LayoutOf(ChartReadOptions.Default, fifthPadAt: 480, tomAt: 480, tomLength: 0));
        Assert.AreEqual(DrumsLayout.FiveLane, LayoutOf(ChartReadOptions.Default, fifthPadAt: 0, tomAt: 480));
        Assert.AreEqual(DrumsLayout.Pro, LayoutOf(ChartReadOptions.Default, fifthPadAt: 481, tomAt: 480));
        Assert.AreEqual(DrumsLayout.Pro, LayoutOf(ChartReadOptions.Default, fifthPadAt: null, tomAt: null));
        Assert.AreEqual(DrumsLayout.FiveLane, LayoutOf(ChartReadOptions.Default, fifthPadAt: 480, tomAt: null));
    }

    [TestMethod]
    public void TheSongIniSettlesTheLayoutBeforeAnyNoteOrMarker()
    {
        Assert.AreEqual(DrumsLayout.FiveLane, LayoutOf(new ChartReadOptions(FiveLaneDrums: true), fifthPadAt: 480, tomAt: 0));
        Assert.AreEqual(DrumsLayout.FiveLane, LayoutOf(new ChartReadOptions(FiveLaneDrums: true), fifthPadAt: null, tomAt: null));
        Assert.AreEqual(DrumsLayout.Pro, LayoutOf(new ChartReadOptions(FiveLaneDrums: false), fifthPadAt: 0, tomAt: null));
        Assert.AreEqual(DrumsLayout.FourLane, LayoutOf(new ChartReadOptions(ProDrums: false, FiveLaneDrums: false), fifthPadAt: 0, tomAt: null));
        // A tom marker makes them pro drums even so, as YARG has it.
        Assert.AreEqual(DrumsLayout.Pro, LayoutOf(new ChartReadOptions(ProDrums: false, FiveLaneDrums: false), fifthPadAt: null, tomAt: 480));
        Assert.AreEqual(DrumsLayout.FiveLane, LayoutOf(new ChartReadOptions(ProDrums: true), fifthPadAt: 0, tomAt: 480));
        Assert.AreEqual(DrumsLayout.FourLane, LayoutOf(new ChartReadOptions(ProDrums: false), fifthPadAt: null, tomAt: null));
    }

    [TestMethod]
    public void TheLayoutIsTheReadersForAFileWrittenInPartEventOrder()
    {
        // At one tick: the fifth-pad note-on before the tom marker's, as the writer writes them, and the other way round.
        byte[] ordered = Song().Track("PART DRUMS").Note(480, 101).Note(480, 110, 10).Build();
        byte[] reversed = Song().Track("PART DRUMS").Note(480, 110, 10).Note(480, 101).Build();
        var chart = MidiChartLoader.Load(ordered, ChartReadOptions.Default).Chart;

        var view = GameView.Project(chart, ChartReadOptions.Default);

        Assert.IsTrue(view.IsFiveLaneDrums);
        Assert.IsEmpty(ChartReadBack.Compare(view, MidiChartReader.Read(ordered, ChartReadOptions.Default), GameView.Instruments));
        Assert.IsTrue(MidiChartReader.Read(reversed, ChartReadOptions.Default).IsProDrums);
    }

    /// <summary>The layout of drums with a fifth-pad note and a tom marker over yellow at the ticks given, if any.</summary>
    private static DrumsLayout LayoutOf(ChartReadOptions options, long? fifthPadAt, long? tomAt, ChartDifficulty fifthPadDifficulty = Expert, long tomLength = 10)
    {
        var drums = new EditableChart(480).AddPart(ChartInstrument.Drums);
        drums[Expert].Notes.Add(new EditableNote(0, 1, 1));
        if (fifthPadAt is { } fifth)
            drums[fifthPadDifficulty].Notes.Add(new EditableNote(fifth, 1, 5));
        if (tomAt is { } tom)
            drums.TomMarkers.Add(new TomMarker(TomMarker.FirstPad, tom, tomLength));
        return GameView.DrumsLayoutOf(drums, options);
    }

    internal static string Describe(IEnumerable<ReadBackDifference> differences) =>
        string.Join("; ", differences.Select(d => $"{d.Kind} {d.Instrument} {d.Difficulty} at {string.Join(",", d.Ticks)} ({d.Count}) {d.Flags}"));
}
