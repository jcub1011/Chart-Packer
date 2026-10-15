using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Commands;
using ChartShared.Core.Charts;
using ChartShared.Core.Validation;
using static ChartBuilder.Tests.Charting.CommandChecks;
using static ChartBuilder.Tests.Charting.MidiChartWriterTests;

namespace ChartBuilder.Tests.Charting;

/// <summary>The tempo map and part commands: what each does, that it reverts and redoes exactly, and what it refuses.</summary>
[TestClass]
public sealed class TempoAndPartCommandTests
{
    private const ChartInstrument Guitar = ChartInstrument.Guitar;
    private const ChartInstrument Drums = ChartInstrument.Drums;
    private const ChartDifficulty Expert = ChartDifficulty.Expert;

    // Tempo markers, keeping ticks.

    [TestMethod]
    public void AddTempoChangesOnlyTheTempoMap()
    {
        var chart = EditableChartSamples.Full();
        var before = chart.Clone();
        var command = new AddTempo(new TempoChange(960, 600_000));

        var after = AppliesRevertsAndRedoes(chart, command);

        Assert.AreSequenceEqual([new TempoChange(0, 500_000), new TempoChange(960, 600_000), new TempoChange(1920, 400_000)], after.TempoMap.Tempos.ToList());
        ChartTickSnapshot.Refill(after.TempoMap.Tempos, [.. before.TempoMap.Tempos]);
        AssertSame(before, after, "with the tempo markers put back");
        Assert.IsTrue(command.Affected.TempoMapChanged);
        Assert.IsEmpty(command.Affected.Regions);
    }

    [TestMethod]
    public void AddTempoRefusesATickThatHasAMarker()
    {
        var chart = EditableChartSamples.Full();

        Refuses<InvalidOperationException>(chart, new AddTempo(new TempoChange(1920, 600_000)));
        Refuses<ArgumentException>(chart, new AddTempo(new TempoChange(960, 0)));
    }

    [TestMethod]
    public void RemovingAnAnchoredTempoMarkerRemovesItsAnchorAndRevertingRestoresIt()
    {
        var chart = EditableChartSamples.Full();

        var after = AppliesRevertsAndRedoes(chart, new RemoveTempo(new TempoChange(1920, 400_000)));

        Assert.AreSequenceEqual([new TempoChange(0, 500_000)], after.TempoMap.Tempos.ToList());
        Assert.IsEmpty(after.TempoMap.Anchors);
    }

    [TestMethod]
    public void RemovingOneOfTwoMarkersAtATickKeepsItsAnchor()
    {
        var chart = EditableChartSamples.Full();
        chart.TempoMap.Tempos.Add(new TempoChange(1920, 450_000));

        var after = AppliesRevertsAndRedoes(chart, new RemoveTempo(new TempoChange(1920, 400_000)));

        Assert.AreSequenceEqual([new TempoChange(0, 500_000), new TempoChange(1920, 450_000)], after.TempoMap.Tempos.ToList());
        Assert.AreSequenceEqual([new TempoAnchor(1920, 2_000_000)], after.TempoMap.Anchors.ToList());
    }

    [TestMethod]
    public void RemoveTempoRefusesTheStartingMarkerAndAMarkerThereIsNot()
    {
        var chart = EditableChartSamples.Full();

        Refuses<InvalidOperationException>(chart, new RemoveTempo(new TempoChange(0, 500_000)));
        Refuses<InvalidOperationException>(chart, new RemoveTempo(new TempoChange(1920, 400_001)));
    }

    [TestMethod]
    public void SetTempoChangesAMarkerInItsPlace()
    {
        var chart = EditableChartSamples.Full();
        chart.TempoMap.Tempos.Add(new TempoChange(1920, 450_000));

        var after = AppliesRevertsAndRedoes(chart, new SetTempo(new TempoChange(1920, 400_000), EditableTempoMap.MicrosecondsPerQuarterOf(100.5)));

        Assert.AreSequenceEqual([new TempoChange(0, 500_000), new TempoChange(1920, 597_015), new TempoChange(1920, 450_000)], after.TempoMap.Tempos.ToList());
        Refuses<ArgumentException>(chart, new SetTempo(new TempoChange(0, 500_000), EditableTempoMap.MaxMicrosecondsPerQuarter + 1));
    }

    // Time signatures.

    [TestMethod]
    public void AddTimeSignatureAddsAMarkerOfUpTo64Beats()
    {
        var chart = EditableChartSamples.Full();
        var command = new AddTimeSignature(new TimeSignatureMarker(1920, 64, 8));

        var after = AppliesRevertsAndRedoes(chart, command);

        Assert.AreSequenceEqual([new TimeSignatureMarker(0, 4, 4), new TimeSignatureMarker(1920, 64, 8), new TimeSignatureMarker(3840, 3, 4)], after.TempoMap.TimeSignatures.ToList());
        Assert.IsTrue(command.Affected.TempoMapChanged);
    }

    [TestMethod]
    public void SetTimeSignatureChangesTheSignatureKeepingTheMetronomeBytes()
    {
        var chart = EditableChartSamples.Full();
        Swap(chart.TempoMap.TimeSignatures, new TimeSignatureMarker(3840, 3, 4), new TimeSignatureMarker(3840, 3, 4, 12, 4));

        var after = AppliesRevertsAndRedoes(chart, new SetTimeSignature(new TimeSignatureMarker(3840, 3, 4, 12, 4), 7, 8));

        Assert.AreSequenceEqual([new TimeSignatureMarker(0, 4, 4), new TimeSignatureMarker(3840, 7, 8, 12, 4)], after.TempoMap.TimeSignatures.ToList());
    }

    [TestMethod]
    public void RemoveTimeSignatureRemovesAMarker()
    {
        var chart = EditableChartSamples.Full();

        var after = AppliesRevertsAndRedoes(chart, new RemoveTimeSignature(new TimeSignatureMarker(3840, 3, 4)));

        Assert.AreSequenceEqual([new TimeSignatureMarker(0, 4, 4)], after.TempoMap.TimeSignatures.ToList());
    }

    [TestMethod]
    public void TimeSignatureCommandsRefuseMoreThan64BeatsOrAnotherNoteValue()
    {
        var chart = EditableChartSamples.Full();

        Refuses<ArgumentException>(chart, new AddTimeSignature(new TimeSignatureMarker(1920, 65, 4)));
        Refuses<ArgumentException>(chart, new AddTimeSignature(new TimeSignatureMarker(1920, 4, 6)));
        Refuses<ArgumentException>(chart, new SetTimeSignature(new TimeSignatureMarker(3840, 3, 4), 0, 4));
        Refuses<InvalidOperationException>(chart, new AddTimeSignature(new TimeSignatureMarker(3840, 4, 4)));
        Refuses<InvalidOperationException>(chart, new RemoveTimeSignature(new TimeSignatureMarker(0, 4, 4)));
    }

    // Anchors.

    [TestMethod]
    public void SetAnchorAnchorsATempoMarkerOrMovesItsAnchor()
    {
        var chart = EditableChartSamples.Full();
        var anchor = new SetAnchor(0, 5_000);
        var move = new SetAnchor(1920, 2_100_000);

        var afterAnchor = AppliesRevertsAndRedoes(chart, anchor);
        anchor.Revert(chart);
        var afterMove = AppliesRevertsAndRedoes(chart, move);

        Assert.AreSequenceEqual([new TempoAnchor(0, 5_000), new TempoAnchor(1920, 2_000_000)], afterAnchor.TempoMap.Anchors.ToList());
        Assert.AreSequenceEqual([new TempoAnchor(1920, 2_100_000)], afterMove.TempoMap.Anchors.ToList());
        Assert.IsTrue(move.Affected.TempoMapChanged);
    }

    [TestMethod]
    public void AnchorCommandsRefuseATickWithoutAMarkerOrAnAnchor()
    {
        var chart = EditableChartSamples.Full();

        Refuses<InvalidOperationException>(chart, new SetAnchor(960, 1_000_000));
        Refuses<InvalidOperationException>(chart, new ClearAnchor(0));
    }

    [TestMethod]
    public void ClearAnchorUnanchorsAMarker()
    {
        var chart = EditableChartSamples.Full();

        var after = AppliesRevertsAndRedoes(chart, new ClearAnchor(1920));

        Assert.IsEmpty(after.TempoMap.Anchors);
        Assert.HasCount(2, after.TempoMap.Tempos);
    }

    // A whole tempo map.

    [TestMethod]
    public void ChangeTempoMapKeepingTicksChangesOnlyTheTempoMap()
    {
        var chart = EditableChartSamples.Full();
        var before = chart.Clone();
        var command = new ChangeTempoMap([new TempoChange(0, 400_000), new TempoChange(960, 300_000)], [new TimeSignatureMarker(0, 3, 4)], [new TempoAnchor(960, 1_000_000)], keepTimes: false);

        var after = AppliesRevertsAndRedoes(chart, command);

        Assert.AreSequenceEqual(command.Tempos, [.. after.TempoMap.Tempos]);
        Assert.AreSequenceEqual(command.TimeSignatures, [.. after.TempoMap.TimeSignatures]);
        Assert.AreSequenceEqual(command.Anchors, [.. after.TempoMap.Anchors]);
        Assert.IsNull(command.Report);
        Assert.IsEmpty(command.Collisions);
        ChartTickSnapshot.Refill(after.TempoMap.Tempos, [.. before.TempoMap.Tempos]);
        ChartTickSnapshot.Refill(after.TempoMap.TimeSignatures, [.. before.TempoMap.TimeSignatures]);
        ChartTickSnapshot.Refill(after.TempoMap.Anchors, [.. before.TempoMap.Anchors]);
        AssertSame(before, after, "with the tempo map put back");
    }

    [TestMethod]
    public void ChangeTempoMapKeepingTimesRevertsExactlyToTheOriginalPassthroughTracks()
    {
        var fixture = MidiFixtures.Named(nameof(MidiFixtures.Passthrough));
        var chart = MidiChartLoader.Load(fixture.Midi, fixture.Options).Chart;
        var original = chart.Passthrough.ToList();
        var tempos = chart.TempoMap.Tempos.Select(t => t with { MicrosecondsPerQuarter = t.MicrosecondsPerQuarter * 4 / 5 }).Append(new TempoChange(1000, 300_000)).OrderBy(t => t.Tick);
        var command = new ChangeTempoMap(tempos, chart.TempoMap.TimeSignatures, chart.TempoMap.Anchors, keepTimes: true);

        var after = AppliesRevertsAndRedoes(chart, command);

        Assert.IsNotNull(command.Report);
        Assert.IsGreaterThan(0, command.Report.PassthroughTracks);
        Assert.Contains(p => !p.IsOriginal, after.Passthrough);
        Assert.IsTrue(command.Affected.TempoMapChanged);
        command.Revert(chart);
        Assert.AreSequenceEqual(original, chart.Passthrough);
        for (int i = 0; i < original.Count; i++)
        {
            Assert.AreSame(original[i], chart.Passthrough[i]);
            Assert.IsTrue(chart.Passthrough[i].IsOriginal);
        }
    }

    [TestMethod]
    public void ChangeTempoMapKeepingTimesReportsTheItemsItLeavesOut()
    {
        var chart = NewChart(Guitar);
        var expert = chart.PartOf(Guitar)![Expert];
        expert.Notes.Add(new EditableNote(480, 100, 0));
        expert.Notes.Add(new EditableNote(481, 200, 0));
        expert.Notes.Add(new EditableNote(481, 1, 1));
        expert.SetFlags(480, ChordFlags.ForceHopo);
        expert.SetFlags(481, ChordFlags.Tap);

        // A tenth of the tempo: ten old ticks to a new one.
        var command = new ChangeTempoMap([new TempoChange(0, 5_000_000)], chart.TempoMap.TimeSignatures, [], keepTimes: true);
        var after = AppliesRevertsAndRedoes(chart, command);

        Assert.AreSequenceEqual([new EditableNote(48, 10, 0), new EditableNote(48, 0, 1)], after.PartOf(Guitar)![Expert].Notes.ToList());
        Assert.AreSequenceEqual([(ModelDifferenceKind.Note, 481L, 48L), (ModelDifferenceKind.ChordFlags, 481L, 48L)], command.Collisions.Select(c => (c.Kind, c.FromTick, c.ToTick)));
    }

    [TestMethod]
    public void ChangeTempoMapRefusesAnAnchorWithoutAMarkerAndAMarkerTheMapCanNotHold()
    {
        var chart = EditableChartSamples.Full();

        Assert.ThrowsExactly<ArgumentException>(() => new ChangeTempoMap([new TempoChange(0, 500_000)], [], [new TempoAnchor(960, 0)], keepTimes: false));
        Assert.ThrowsExactly<ArgumentException>(() => new ChangeTempoMap([new TempoChange(0, 500_000)], [], [new TempoAnchor(0, 0), new TempoAnchor(0, 1)], keepTimes: false));
        Refuses<ArgumentException>(chart, new ChangeTempoMap([new TempoChange(0, 500_000), new TempoChange(960, -1)], [], [], keepTimes: true));
        Refuses<ArgumentException>(chart, new ChangeTempoMap([new TempoChange(0, 500_000)], [new TimeSignatureMarker(0, 65, 4)], [], keepTimes: true));
    }

    // Parts.

    [TestMethod]
    public void AddPartAddsAnEmptyPartAndRevertsExactly()
    {
        var chart = EditableChartSamples.Full();
        var command = new AddPart(ChartInstrument.Bass);

        var after = AppliesRevertsAndRedoes(chart, command);

        Assert.AreSequenceEqual([Guitar, ChartInstrument.Bass, Drums, ChartInstrument.GhlGuitar], after.Parts.Select(p => p.Instrument));
        Assert.IsTrue(after.PartOf(ChartInstrument.Bass)!.Tracks.All(t => t.Notes.Count == 0));
        Assert.AreEqual(SongChanges.Parts, command.Affected.Song);
        Assert.AreSequenceEqual([new ChartLocation(ChartInstrument.Bass, null, 0, long.MaxValue)], command.Affected.Regions);
    }

    [TestMethod]
    public void AddPartRefusesAPartTheChartHasOrThatIsNotEdited()
    {
        var chart = EditableChartSamples.Full();

        Refuses<InvalidOperationException>(chart, new AddPart(Guitar));
        Assert.ThrowsExactly<ArgumentException>(() => new AddPart(ChartInstrument.Keys));
        Assert.ThrowsExactly<ArgumentException>(() => new AddPart(Guitar, DrumsVariant.Pro));
    }

    [TestMethod]
    public void AddingDrumsNeedsTheirVariantWhenTheChartHasNone()
    {
        var chart = NewChart(Guitar);
        var command = new AddPart(Drums, DrumsVariant.FiveLane);

        Assert.IsNull(chart.DrumsVariant);
        Refuses<InvalidOperationException>(chart, new AddPart(Drums));
        var after = AppliesRevertsAndRedoes(chart, command);

        Assert.AreEqual(DrumsVariant.FiveLane, after.DrumsVariant);
        Assert.AreEqual(SongChanges.Parts | SongChanges.DrumsVariant, command.Affected.Song);
        command.Revert(chart);
        Assert.IsNull(chart.DrumsVariant);
    }

    [TestMethod]
    public void AddingDrumsTakesTheVariantTheChartHas()
    {
        var chart = NewChart(Guitar);
        chart.DrumsVariant = DrumsVariant.Pro;

        Refuses<InvalidOperationException>(chart, new AddPart(Drums, DrumsVariant.FourLane));
        var after = AppliesRevertsAndRedoes(chart, new AddPart(Drums));

        Assert.AreEqual(DrumsVariant.Pro, after.DrumsVariant);
        Assert.IsNotNull(after.PartOf(Drums));
    }

    [TestMethod]
    public void RemovePartKeepsThePartAndRevertingPutsItBackWithItsVariant()
    {
        var chart = EditableChartSamples.Full();
        var drums = chart.PartOf(Drums);
        var command = new RemovePart(Drums);

        var after = AppliesRevertsAndRedoes(chart, command);

        Assert.IsNull(after.PartOf(Drums));
        Assert.IsNull(after.DrumsVariant);
        Assert.AreEqual(SongChanges.Parts | SongChanges.DrumsVariant, command.Affected.Song);
        command.Revert(chart);
        Assert.AreSame(drums, chart.PartOf(Drums));
        Assert.AreEqual(DrumsVariant.Pro, chart.DrumsVariant);
        Refuses<InvalidOperationException>(chart, new RemovePart(ChartInstrument.Bass));
    }

    [TestMethod]
    public void RemovePartTakesThePassthroughTracksReadAsItsInstrumentSoTheChartStillSaves()
    {
        // The second PART BASS passes through; left behind, it would be read as the bass part once that's gone.
        byte[] midi = MidiChartLoaderTests.Song()
            .Track("PART BASS").Note(0, 96)
            .Track("PART VOCALS").Note(0, 60)
            .Track("PART BASS").Note(480, 97)
            .Track("PART KEYS").Note(0, 96)
            .Track("PART GUITAR").Note(0, 96)
            .Build();
        var chart = MidiChartLoader.Load(midi, ChartReadOptions.Default).Chart;
        Assert.AreSequenceEqual(["PART VOCALS", "PART BASS", "PART KEYS"], chart.Passthrough.Select(t => t.Name).ToList());

        var after = AppliesRevertsAndRedoes(chart, new RemovePart(ChartInstrument.Bass));

        Assert.AreSequenceEqual(["PART VOCALS", "PART KEYS"], after.Passthrough.Select(t => t.Name).ToList());
        var result = MidiChartWriter.WriteChecked(after, MidiChartWriter.SettingsAfterSave(after, ChartReadOptions.Default));
        Assert.IsTrue(result.IsValid, $"{DescribeModel(result.ModelDifferences)} {GameViewTests.Describe(result.GameDifferences)} {string.Join("; ", result.ReloadIssues)}");
    }

    private static void Swap<T>(TickList<T> list, T from, T to)
    {
        Assert.IsTrue(list.Remove(from));
        Assert.IsTrue(list.Add(to));
    }
}
