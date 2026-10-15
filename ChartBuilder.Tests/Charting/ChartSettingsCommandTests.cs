using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Commands;
using ChartShared.Core.Charts;
using static ChartBuilder.Tests.Charting.CommandChecks;

namespace ChartBuilder.Tests.Charting;

/// <summary>
/// What Chart settings changes on an open song (charter-spec C1): the resolution and its warning, the starting tempo and
/// time signature, the drum variant, and OK's one command.
/// </summary>
[TestClass]
public sealed class ChartSettingsCommandTests
{
    private const ChartInstrument Guitar = ChartInstrument.Guitar;
    private const ChartInstrument Bass = ChartInstrument.Bass;
    private const ChartInstrument Drums = ChartInstrument.Drums;
    private const ChartDifficulty Expert = ChartDifficulty.Expert;

    // A track holding a note-on at tick 1 and the end of the track there.
    private static readonly byte[] NoteAtTickOne = [.. "MTrk"u8, 0, 0, 0, 8, 1, 0x90, 0x40, 0x64, 0, 0xFF, 0x2F, 0];

    // Changing the resolution.

    [TestMethod]
    public void DoublingTheResolutionDoublesEveryTickAndRevertsExactly()
    {
        // AC C1.6: 480 to 960 moves nothing in time.
        var chart = EditableChartSamples.Full();
        var before = chart.Clone();

        var after = AppliesRevertsAndRedoes(chart, new ChangeResolution(960));

        Assert.AreEqual(960, after.Resolution);
        Assert.AreSequenceEqual([new TempoChange(0, 500_000), new TempoChange(3840, 400_000)], after.TempoMap.Tempos.ToList());
        Assert.AreSequenceEqual([new TempoAnchor(3840, 2_000_000)], after.TempoMap.Anchors.ToList());
        Assert.AreEqual(7680, after.TempoMap.TimeSignatures[^1].Tick);
        var notes = before.PartOf(Guitar)![Expert].Notes.Select(n => n with { Tick = n.Tick * 2, Length = n.Length * 2 }).ToList();
        Assert.AreSequenceEqual(notes, [.. after.PartOf(Guitar)![Expert].Notes]);
        Assert.AreEqual(1920, after.Events.Single(e => e.Text == "[section Intro]").Tick);
        Assert.AreEqual(3840, after.PartOf(Drums)!.Phrases.Single(p => p.Kind == PhraseKind.DrumFill).Tick);
        Assert.AreSame(before.Passthrough[0], after.Passthrough[0], "A track with nothing after tick 0 isn't re-ticked.");
    }

    public static IEnumerable<object[]> FixtureNames => MidiFixtures.Names;

    [TestMethod]
    [DynamicData(nameof(FixtureNames))]
    public void ARescaledFixtureWritesAndReadsBackAsItIs(string name)
    {
        var fixture = MidiFixtures.Named(name);
        var chart = MidiChartLoader.Load(fixture.Midi, fixture.Options).Chart;
        new ChangeResolution(chart.Resolution * 2).Apply(chart);

        var result = MidiChartWriter.WriteChecked(chart, MidiChartWriter.SettingsAfterSave(chart, fixture.Options));

        Assert.IsTrue(result.IsValid, $"{MidiChartWriterTests.DescribeModel(result.ModelDifferences)} {GameViewTests.Describe(result.GameDifferences)} {string.Join("; ", result.ReloadIssues)}");
    }

    [TestMethod]
    [DynamicData(nameof(FixtureNames))]
    public void DoublingAFixturesResolutionGivesNoWarning(string name)
    {
        var fixture = MidiFixtures.Named(name);
        var chart = MidiChartLoader.Load(fixture.Midi, fixture.Options).Chart;

        var report = ResolutionChangeReport.Analyze(chart, chart.Resolution * 2);

        Assert.IsFalse(report.HasWarning, string.Join("; ", report.Moves.Select(m => m.ToString()).Concat(report.LeftOut)));
    }

    [TestMethod]
    public void TheSameResolutionChangesNothing()
    {
        var chart = EditableChartSamples.Full();
        var before = chart.Clone();
        var command = new ChangeResolution(480);

        command.Apply(chart);

        Assert.IsTrue(command.Affected.IsEmpty);
        AssertSame(before, chart, "after applying");
        command.Revert(chart);
        AssertSame(before, chart, "after reverting");
    }

    [TestMethod]
    public void LoweringTheResolutionRevertsEveryTickExactly()
    {
        // AC C1.6: undoing gives a model equal to the one before, though re-ticking rounded.
        var chart = EditableChartSamples.Full();
        chart.PartOf(Guitar)![Expert].Notes.Add(new EditableNote(1, 0, 4));
        chart.AddPassthrough(new PassthroughTrack(NoteAtTickOne, null));

        var after = AppliesRevertsAndRedoes(chart, new ChangeResolution(192));

        Assert.AreEqual(192, after.Resolution);
        Assert.IsFalse(after.Passthrough[^1].IsOriginal);
    }

    [TestMethod]
    public void ResolutionsMidiCanNotHoldAreRefused()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ChangeResolution(0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ChangeResolution(40_000));
    }

    // The warning.

    [TestMethod]
    public void DoublingTheResolutionGivesNoWarning()
    {
        // AC C1.6.
        var chart = EditableChartSamples.Full();
        chart.AddPassthrough(new PassthroughTrack(NoteAtTickOne, null));

        var report = ResolutionChangeReport.Analyze(chart, 960);

        Assert.IsFalse(report.HasWarning);
        Assert.AreEqual(0, report.Moved);
        Assert.AreEqual(0, report.LargestMoveMs);
        Assert.AreEqual(480, chart.Resolution, "The chart itself is left as it was.");
    }

    [TestMethod]
    public void ANoteAtTickOneWarnsHowManyMoveAndHowFar()
    {
        // AC C1.6: 480 to 192, with a note at tick 1.
        var chart = NewChart(Guitar);
        chart.PartOf(Guitar)![Expert].Notes.Add(new EditableNote(1, 0, 0));
        chart.PartOf(Guitar)![Expert].Notes.Add(new EditableNote(480, 0, 1));
        var before = chart.Clone();

        var report = ResolutionChangeReport.Analyze(chart, 192);

        Assert.IsTrue(report.HasWarning);
        Assert.AreSequenceEqual([new ResolutionChangeMoves(ResolutionChangeGroup.Part, Guitar, 1)], report.Moves.ToList());
        Assert.AreEqual(1, report.Moved);
        // Tick 1 at 120 BPM is 1/960 s; it moves to tick 0.
        Assert.AreEqual(1000.0 / 960, report.LargestMoveMs, 1e-6);
        Assert.IsEmpty(report.LeftOut);
        AssertSame(before, chart, "after the dry run");
    }

    [TestMethod]
    public void TheWarningCountsEachGroupAndListsWhatIsLeftOut()
    {
        var chart = NewChart(Guitar);
        chart.Events.Add(TextEvent.Of(1, "[section Verse]"));
        chart.PartOf(Guitar)![Expert].Notes.Add(new EditableNote(0, 0, 0));
        chart.PartOf(Guitar)![Expert].Notes.Add(new EditableNote(1, 0, 0));
        chart.AddPassthrough(new PassthroughTrack(NoteAtTickOne, null));

        var report = ResolutionChangeReport.Analyze(chart, 192);

        Assert.AreSequenceEqual(
            [
                new ResolutionChangeMoves(ResolutionChangeGroup.Part, Guitar, 1),
                new ResolutionChangeMoves(ResolutionChangeGroup.Events, null, 1),
                new ResolutionChangeMoves(ResolutionChangeGroup.Passthrough, null, 2),
            ],
            report.Moves.ToList());
        Assert.HasCount(1, report.LeftOut);
        Assert.Contains("tick 1 moved to tick 0", report.LeftOut[0]);
    }

    [TestMethod]
    public void TempoMarkersThatLandOnOneTickAreWarnedOf()
    {
        var chart = NewChart(Guitar);
        chart.TempoMap.Tempos.Add(new TempoChange(1, 400_000));

        var report = ResolutionChangeReport.Analyze(chart, 192);

        Assert.AreSequenceEqual([new ResolutionChangeMoves(ResolutionChangeGroup.TempoMap, null, 1)], report.Moves.ToList());
        Assert.HasCount(1, report.LeftOut);
        Assert.Contains("tempo marker at tick 0", report.LeftOut[0]);
    }

    [TestMethod]
    public void ASustainEndThatMovesCountsItsNoteOnce()
    {
        var chart = NewChart(Guitar);
        chart.PartOf(Guitar)![Expert].Notes.Add(new EditableNote(5, 1, 0));

        var report = ResolutionChangeReport.Analyze(chart, 192);

        // Tick 5 is 2 at 192, but its end at 6 is 2.4.
        Assert.AreSequenceEqual([new ResolutionChangeMoves(ResolutionChangeGroup.Part, Guitar, 1)], report.Moves.ToList());
    }

    // The starting tempo and time signature.

    [TestMethod]
    public void SettingTheStartingTempoKeepingTicksLeavesEveryNoteWhereItIs()
    {
        var chart = EditableChartSamples.Full();
        var before = chart.Clone();

        var after = AppliesRevertsAndRedoes(chart, new SetStartingTempo(400_000, 3, 4, keepTimes: false));

        Assert.AreEqual(new TempoChange(0, 400_000), after.TempoMap.Tempos[0]);
        Assert.AreEqual(new TimeSignatureMarker(0, 3, 4), after.TempoMap.TimeSignatures[0]);
        Assert.AreSequenceEqual(before.PartOf(Guitar)![Expert].Notes.ToList(), [.. after.PartOf(Guitar)![Expert].Notes]);
    }

    [TestMethod]
    public void SettingTheStartingTempoKeepingTimesMovesTheNotesToKeepTheirTimes()
    {
        var chart = EditableChartSamples.Full();

        var after = AppliesRevertsAndRedoes(chart, new SetStartingTempo(250_000, null, null, keepTimes: true));

        // Twice as fast from the start, so what was at 0.5 s, tick 480, is at tick 960. The other markers keep their
        // ticks, as any tempo edit leaves them.
        Assert.AreSequenceEqual([new TempoChange(0, 250_000), new TempoChange(1920, 400_000)], after.TempoMap.Tempos.ToList());
        Assert.Contains(new EditableNote(960, 480, 2), after.PartOf(Guitar)![Expert].Notes.ToList());
        Assert.AreSequenceEqual([new TempoAnchor(1920, 2_000_000)], after.TempoMap.Anchors.ToList(), "Anchors stay where they are.");
    }

    [TestMethod]
    public void TheStartingTempoAsItIsChangesNothing()
    {
        var chart = EditableChartSamples.Full();
        var command = new SetStartingTempo(500_000, 4, 4, keepTimes: true);

        command.Apply(chart);

        Assert.IsTrue(command.Affected.IsEmpty);
    }

    [TestMethod]
    public void ATickZeroWithoutMarkersGetsThem()
    {
        var chart = NewChart(Guitar);
        chart.TempoMap.Tempos.Clear();
        chart.TempoMap.TimeSignatures.Clear();

        var after = AppliesRevertsAndRedoes(chart, new SetStartingTempo(400_000, 6, 8, keepTimes: false));

        Assert.AreSequenceEqual([new TempoChange(0, 400_000)], after.TempoMap.Tempos.ToList());
        Assert.AreSequenceEqual([new TimeSignatureMarker(0, 6, 8)], after.TempoMap.TimeSignatures.ToList());
    }

    // The drum variant.

    [TestMethod]
    public void TheDrumVariantChangesWhileTheDrumsHaveNoNotes()
    {
        var chart = NewChart(Drums);

        var after = AppliesRevertsAndRedoes(chart, new SetDrumsVariant(DrumsVariant.FiveLane));

        Assert.AreEqual(DrumsVariant.FiveLane, after.DrumsVariant);
    }

    [TestMethod]
    public void DrumsWithNotesSwitchBetweenFourLaneAndProKeepingTheirNotesAndMarkers()
    {
        // Charter-spec C6 item 9: a song setting, not a conversion.
        var chart = EditableChartSamples.Full();
        var drums = chart.PartOf(Drums)!;
        var notes = drums.Tracks.Select(t => t.Notes.ToList()).ToList();
        var markers = drums.TomMarkers.ToList();

        var after = AppliesRevertsAndRedoes(chart, new SetDrumsVariant(DrumsVariant.FourLane));

        Assert.AreEqual(DrumsVariant.FourLane, after.DrumsVariant);
        var afterDrums = after.PartOf(Drums)!;
        for (int i = 0; i < notes.Count; i++)
            Assert.AreSequenceEqual(notes[i], [.. afterDrums.Tracks[i].Notes]);
        Assert.AreSequenceEqual(markers, [.. afterDrums.TomMarkers]);
    }

    [TestMethod]
    public void DrumsWithNotesAreConvertedToFiveLaneNotSwitched()
    {
        var chart = EditableChartSamples.Full();

        Refuses<InvalidOperationException>(chart, new SetDrumsVariant(DrumsVariant.FiveLane));
    }

    // OK's command.

    [TestMethod]
    public void OkWithNothingChangedGivesNoCommand()
    {
        // AC C1.7.
        var chart = EditableChartSamples.Full();

        Assert.IsNull(ChartSettingsEdit.CommandFor(chart, ChartSettingsValues.Of(chart), keepTimes: false));
        Assert.IsNull(ChartSettingsEdit.CommandFor(chart, ChartSettingsValues.Of(chart), keepTimes: true));
    }

    [TestMethod]
    public void AChartWithoutMarkersAtTickZeroIsTakenAsTheDefaults()
    {
        var chart = NewChart(Guitar);
        chart.TempoMap.Tempos.Clear();
        chart.TempoMap.TimeSignatures.Clear();

        var values = ChartSettingsValues.Of(chart);

        Assert.AreEqual(TempoMap.DefaultMicrosecondsPerQuarter, values.MicrosecondsPerQuarter);
        Assert.AreEqual((4, 4), (values.Numerator, values.Denominator));
        Assert.IsNull(ChartSettingsEdit.CommandFor(chart, values, keepTimes: false));
    }

    [TestMethod]
    public void EverythingOkChangesIsOneUndoStep()
    {
        var chart = EditableChartSamples.Full();
        var stack = new ChartUndoStack(chart);
        var before = chart.Clone();
        var values = ChartSettingsValues.Of(chart) with
        {
            Resolution = 960,
            MicrosecondsPerQuarter = 400_000,
            Numerator = 6,
            Denominator = 8,
            Parts = new HashSet<ChartInstrument> { Guitar, Bass, Drums },
        };

        stack.Execute(ChartSettingsEdit.CommandFor(chart, values, keepTimes: true)!);

        Assert.AreEqual(1, stack.UndoCount);
        Assert.AreEqual(960, chart.Resolution);
        Assert.AreEqual(new TempoChange(0, 400_000), chart.TempoMap.Tempos[0]);
        Assert.AreEqual(new TimeSignatureMarker(0, 6, 8), chart.TempoMap.TimeSignatures[0]);
        Assert.AreSequenceEqual([Guitar, Bass, Drums], chart.Parts.Select(p => p.Instrument).ToList());
        Assert.AreSequenceEqual(new[] { ChartInstrument.GhlGuitar }, before.Parts.Select(p => p.Instrument).Except(chart.Parts.Select(p => p.Instrument)).ToList());

        stack.Undo();

        AssertSame(before, chart, "after undoing");
    }

    [TestMethod]
    public void DrumsAddedWithAnotherVariantThanTheSongsChangeIt()
    {
        // song.ini said Pro, but the chart has no drums.
        var chart = NewChart(Guitar);
        new SetDrumsVariant(DrumsVariant.Pro).Apply(chart);
        var values = ChartSettingsValues.Of(chart) with { Parts = new HashSet<ChartInstrument> { Guitar, Drums }, Drums = DrumsVariant.FiveLane };

        var after = AppliesRevertsAndRedoes(chart, ChartSettingsEdit.CommandFor(chart, values, keepTimes: false)!);

        Assert.AreEqual(DrumsVariant.FiveLane, after.DrumsVariant);
        Assert.IsNotNull(after.PartOf(Drums));
    }

    [TestMethod]
    public void ChartSettingsConvertsDrumsWithNotesToFiveLaneAsPartOfItsOneStep()
    {
        var chart = EditableChartSamples.Full();
        var values = ChartSettingsValues.Of(chart) with { Resolution = 960, Drums = DrumsVariant.FiveLane };
        var command = ChartSettingsEdit.CommandFor(chart, values, keepTimes: false)!;

        var after = AppliesRevertsAndRedoes(chart, command);

        Assert.AreEqual(960, after.Resolution);
        Assert.AreEqual(DrumsVariant.FiveLane, after.DrumsVariant);
        Assert.IsEmpty(after.PartOf(Drums)!.TomMarkers);
        Assert.IsInstanceOfType<ConvertDrums>(((CompositeCommand)command).Commands[^1]);
    }

    [TestMethod]
    public void ChartSettingsSwitchesDrumsWithNotesBetweenFourLaneAndPro()
    {
        var chart = EditableChartSamples.Full();
        var values = ChartSettingsValues.Of(chart) with { Drums = DrumsVariant.FourLane };
        var command = ChartSettingsEdit.CommandFor(chart, values, keepTimes: false)!;

        var after = AppliesRevertsAndRedoes(chart, command);

        Assert.AreEqual(DrumsVariant.FourLane, after.DrumsVariant);
        Assert.IsInstanceOfType<SetDrumsVariant>(((CompositeCommand)command).Commands.Single());
    }

    internal static EditableChart NewChart(params ChartInstrument[] parts) =>
        EditableChart.CreateNew(new NewChartSettings("Song", 480, 120, 4, 4, parts.ToHashSet(), DrumsVariant.Pro));
}
