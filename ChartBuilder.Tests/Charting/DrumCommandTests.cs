using ChartPacker.Core.Charting;
using ChartPacker.Core.Charting.Commands;
using ChartPacker.Core.Charts;
using static ChartBuilder.Tests.Charting.CommandChecks;
using static ChartBuilder.Tests.Charting.MidiChartWriterTests;

namespace ChartBuilder.Tests.Charting;

/// <summary>
/// The drums commands (charter-spec C6): cymbal and tom, the 2× kick, disco flip sections, the 5-lane conversion, and
/// the drum fills and big rock endings <c>[coda]</c> decides.
/// </summary>
[TestClass]
public sealed class DrumCommandTests
{
    private const ChartInstrument Drums = ChartInstrument.Drums;
    private const ChartDifficulty Expert = ChartDifficulty.Expert;
    private const ChartDifficulty Hard = ChartDifficulty.Hard;
    private const int Red = 1, Yellow = 2, Blue = 3, Green = 4, FifthPad = 5;

    // Cymbal and tom.

    [TestMethod]
    public void ToggleTheMiddleOfThreeBlueCymbalsLeavesOneMarkerOverItAloneOnEveryDifficulty()
    {
        // AC C6.2.
        var chart = DrumsChart();
        Add(chart, Expert, Pad(0, Blue), Pad(480, Blue), Pad(960, Blue));
        Add(chart, Hard, Pad(480, Blue));
        var command = new SetToms([(Blue, 480L)], tom: true, Expert);

        var after = AppliesRevertsAndRedoes(chart, command);

        Assert.AreSequenceEqual([new TomMarker(Blue, 480, 1)], after.PartOf(Drums)!.TomMarkers.ToList());
        Assert.AreSequenceEqual([Hard], command.SharedWith.ToList());
        Assert.AreSequenceEqual([true, false, true], Game(after, Expert).Select(IsCymbal).ToList());
        Assert.IsFalse(IsCymbal(Game(after, Hard).Single()));
    }

    [TestMethod]
    [DataRow("0,480", "0:1", 480, true, "0:481", DisplayName = "Joins the marker before")]
    [DataRow("480,960", "960:1", 480, true, "480:481", DisplayName = "Joins the marker after")]
    [DataRow("0,480,960", "0:1;960:1", 480, true, "0:961", DisplayName = "Joins both")]
    [DataRow("0,240,480", "0:1", 480, true, "0:1;480:1", DisplayName = "Doesn't join over a cymbal")]
    [DataRow("0,480,960", "0:961", 480, false, "0:480;481:480", DisplayName = "Splits round a cymbal")]
    [DataRow("480,960", "0:961", 480, false, "481:480", DisplayName = "Leaves out a piece covering no note")]
    [DataRow("480", "480:1", 480, false, "", DisplayName = "Removes a marker left covering nothing")]
    public void SettingTomsChangesTheFewestMarkersAndOnlyThePlacesGiven(string ticks, string markers, long tick, bool tom, string expected)
    {
        var chart = DrumsChart();
        var notes = ticks.Split(',').Select(t => Pad(long.Parse(t, System.Globalization.CultureInfo.InvariantCulture), Blue)).ToArray();
        Add(chart, Expert, notes);
        var drums = chart.PartOf(Drums)!;
        foreach (var marker in Markers(markers))
            drums.TomMarkers.Add(marker);
        var states = notes.Select(n => DrumCymbals.IsTom(drums, Blue, n.Tick)).ToList();

        var after = AppliesRevertsAndRedoes(chart, new SetToms([(Blue, tick)], tom));

        var afterDrums = after.PartOf(Drums)!;
        Assert.AreSequenceEqual(Markers(expected).ToList(), [.. afterDrums.TomMarkers]);
        for (int i = 0; i < notes.Length; i++)
            Assert.AreEqual(notes[i].Tick == tick ? tom : states[i], DrumCymbals.IsTom(afterDrums, Blue, notes[i].Tick), $"tick {notes[i].Tick}");
    }

    [TestMethod]
    public void SettingTomsOnPlacesAlreadySoChangesNothing()
    {
        var chart = DrumsChart();
        Add(chart, Expert, Pad(0, Blue));
        var before = chart.Clone();

        var command = new SetToms([(Blue, 0L)], tom: false);
        command.Apply(chart);

        Assert.IsTrue(command.Affected.IsEmpty);
        AssertSame(before, chart, "after a change of nothing");
    }

    [TestMethod]
    public void SettingTomsIsRefusedOffProDrumsAndWhereThereIsNoNote()
    {
        var chart = DrumsChart();
        Add(chart, Expert, Pad(0, Yellow));

        Refuses<InvalidOperationException>(chart, new SetToms([(Yellow, 480L)], tom: true));
        new SetDrumsVariant(DrumsVariant.FourLane).Apply(chart);
        Refuses<InvalidOperationException>(chart, new SetToms([(Yellow, 0L)], tom: true));
        Assert.Throws<ArgumentException>(() => new SetToms([(Red, 0L)], tom: true));
    }

    [TestMethod]
    public void RemoveTomMarkersTakesThemOut()
    {
        var chart = DrumsChart();
        chart.PartOf(Drums)!.TomMarkers.Add(new TomMarker(Green, 960, 10));

        var after = AppliesRevertsAndRedoes(chart, new RemoveTomMarkers([new TomMarker(Green, 960, 10)]));

        Assert.IsEmpty(after.PartOf(Drums)!.TomMarkers);
        Refuses<InvalidOperationException>(after, new RemoveTomMarkers([new TomMarker(Green, 960, 10)]));
    }

    // The 2x kick.

    [TestMethod]
    public void SetDoubleKickMakesKicksTwoTimesKicksAndBack()
    {
        var chart = DrumsChart();
        Add(chart, Expert, Pad(0, 0), Pad(480, 0));

        var after = AppliesRevertsAndRedoes(chart, new SetDoubleKick([Pad(0, 0), Pad(480, 0)], doubleKick: true));

        Assert.IsTrue(after.PartOf(Drums)![Expert].Notes.All(n => n.DoubleKick));
        AppliesRevertsAndRedoes(after, new SetDoubleKick([Pad(0, 0) with { DoubleKick = true }], doubleKick: false));
    }

    [TestMethod]
    public void SetDoubleKickIsRefusedWhereTheOtherKickIsAlready()
    {
        var chart = DrumsChart();
        Add(chart, Expert, Pad(0, 0), Pad(0, 0) with { DoubleKick = true });

        Refuses<InvalidOperationException>(chart, new SetDoubleKick([Pad(0, 0)], doubleKick: true));
        Assert.Throws<ArgumentException>(() => new SetDoubleKick([Pad(0, Red)], doubleKick: true));
    }

    // Disco flip.

    [TestMethod]
    public void DiscoFlipSectionsAreReadFromTheMixes()
    {
        var chart = DrumsChart();
        var mixes = chart.PartOf(Drums)![Expert].Mixes;
        foreach (var mix in (DrumMix[])[new(0, 1, ""), new(480, 1, "d"), new(600, 2, "d"), new(960, 2, ""), new(1920, 3, "d")])
            mixes.Add(mix);

        var ranges = DiscoFlips.RangesOf(chart.PartOf(Drums)![Expert]);

        Assert.AreSequenceEqual([new DiscoFlipRange(480, 960, 1), new DiscoFlipRange(1920, null, 3)], ranges.ToList());
        Assert.IsTrue(DiscoFlips.IsFlippedAt(chart.PartOf(Drums)![Expert], 700));
        Assert.IsFalse(DiscoFlips.IsFlippedAt(chart.PartOf(Drums)![Expert], 960));
    }

    [TestMethod]
    [DataRow("", 480, 960, "480:0:d;960:0:", DisplayName = "On a track with no mixes")]
    [DataRow("0:2:", 480, 960, "0:2:;480:2:d;960:2:", DisplayName = "Keeps the configuration in effect")]
    [DataRow("480:1:", 480, 960, "480:1:d;960:1:", DisplayName = "Turns a mix at its start on")]
    [DataRow("0:1:;600:2:", 480, 960, "0:1:;480:1:d;600:2:d;960:2:", DisplayName = "Keeps a configuration change inside it")]
    [DataRow("960:1:", 480, 960, "480:1:d;960:1:", DisplayName = "Ends at a mix already there")]
    [DataRow("0:0:;0:0:d;480:0:", 480, 960, "0:0:;0:0:d;480:0:d;960:0:", DisplayName = "Joins a section ending where it starts")]
    public void AddingADiscoFlipSectionWritesTheMixesItNeeds(string mixes, long tick, long end, string expected)
    {
        var chart = DrumsChart();
        foreach (var mix in Mixes(mixes))
            chart.PartOf(Drums)![Expert].Mixes.Add(mix);

        var after = AppliesRevertsAndRedoes(chart, new AddDiscoFlip(Expert, tick, end));

        Assert.AreSequenceEqual(Mixes(expected).ToList(), [.. after.PartOf(Drums)![Expert].Mixes]);
    }

    [TestMethod]
    public void AddingADiscoFlipSectionIsRefusedOverAnotherOrOverAMixWithAnotherSetting()
    {
        var chart = DrumsChart();
        var mixes = chart.PartOf(Drums)![Expert].Mixes;
        mixes.Add(new DrumMix(480, 0, "d"));
        mixes.Add(new DrumMix(960, 0, ""));
        mixes.Add(new DrumMix(2400, 0, "easy"));

        Refuses<InvalidOperationException>(chart, new AddDiscoFlip(Expert, 0, 600));
        Refuses<InvalidOperationException>(chart, new AddDiscoFlip(Expert, 1920, 2880));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AddDiscoFlip(Expert, 480, 480));
    }

    [TestMethod]
    public void RemovingAndMovingADiscoFlipSectionKeepTheConfiguration()
    {
        var chart = DrumsChart();
        foreach (var mix in Mixes("0:2:;480:2:d;960:2:"))
            chart.PartOf(Drums)![Expert].Mixes.Add(mix);
        var range = new DiscoFlipRange(480, 960, 2);

        var removed = AppliesRevertsAndRedoes(chart.Clone(), new RemoveDiscoFlip(Expert, range));
        var moved = AppliesRevertsAndRedoes(chart.Clone(), new ChangeDiscoFlip(Expert, range, 960, 1920));

        Assert.AreSequenceEqual(Mixes("0:2:").ToList(), [.. removed.PartOf(Drums)![Expert].Mixes]);
        Assert.AreSequenceEqual(Mixes("0:2:;960:2:d;1920:2:").ToList(), [.. moved.PartOf(Drums)![Expert].Mixes]);
        Refuses<InvalidOperationException>(chart, new RemoveDiscoFlip(Expert, range with { EndTick = 1000 }));
    }

    [TestMethod]
    public void DiscoFlipSwapsRedAndYellowInsideTheSectionOnlyAndLeavesTheNotesAlone()
    {
        // Charter-spec C6 item 7.
        var chart = DrumsChart();
        Add(chart, Expert, Pad(0, Red), Pad(480, Red), Pad(960, Red));
        var notes = chart.PartOf(Drums)![Expert].Notes.ToList();

        new AddDiscoFlip(Expert, 480, 960).Apply(chart);

        Assert.AreSequenceEqual(notes, [.. chart.PartOf(Drums)![Expert].Notes]);
        Assert.AreSequenceEqual([0, 1, 0], Game(chart, Expert).Select(n => n.Lane).ToList());
    }

    // Converting to and from 5-lane.

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    public void ConvertingFiveLaneToProAndBackGivesTheNotesBack(int seed)
    {
        // AC C6.3: no tick has both orange and green.
        var chart = DrumsChart(DrumsVariant.FiveLane);
        var random = new Random(seed);
        var greenTicks = new Dictionary<long, int>();
        foreach (var difficulty in Enum.GetValues<ChartDifficulty>())
        {
            var notes = new List<EditableNote>();
            for (long tick = 0; tick < 48 * 120; tick += 120)
            {
                foreach (int lane in Enumerable.Range(0, 6).Where(_ => random.Next(3) == 0))
                {
                    if (lane is Green or FifthPad && greenTicks.TryGetValue(tick, out int other) && other != lane)
                        continue;
                    if (lane is Green or FifthPad)
                        greenTicks[tick] = lane;
                    var dynamics = lane == 0 ? NoteDynamics.None : (NoteDynamics)random.Next(3);
                    notes.Add(new EditableNote(tick, random.Next(2) == 0 ? 1 : 120, lane, dynamics));
                }
            }

            if (difficulty == Expert)
                notes.Add(new EditableNote(48 * 120, 1, 0, DoubleKick: true));
            Add(chart, difficulty, [.. notes]);
        }

        var before = chart.Clone();

        var toPro = new ConvertDrums(DrumsVariant.Pro);
        var pro = AppliesRevertsAndRedoes(chart, toPro);
        var back = new ConvertDrums(DrumsVariant.FiveLane);
        back.Apply(chart);

        Assert.AreEqual(DrumsVariant.Pro, pro.DrumsVariant);
        Assert.IsTrue(pro.PartOf(Drums)!.Tracks.All(t => t.Notes.All(n => n.Lane <= Green)));
        Assert.IsEmpty(toPro.Report!.Notes);
        Assert.IsEmpty(back.Report!.Notes);
        AssertSame(before, chart, "converted to Pro and back");
    }

    [TestMethod]
    public void ConvertingProToFiveLaneKeepsTheNoteThatConvertsBackAndReportsTheOther()
    {
        var chart = DrumsChart();
        Add(chart, Expert, Pad(0, Yellow), Pad(0, Blue), Pad(480, Blue), Pad(480, Green), Pad(960, Green), Pad(960, FifthPad));
        foreach (var place in (int[])[Yellow, Blue])
            new SetToms([(place, 0L)], tom: true).Apply(chart);
        new SetToms([(Green, 960L)], tom: true).Apply(chart);
        var command = new ConvertDrums(DrumsVariant.FiveLane);

        var after = AppliesRevertsAndRedoes(chart, command);

        // The yellow tom and the blue tom land on blue; the blue cymbal and the green cymbal on orange; the green tom and
        // the fifth pad on green.
        Assert.AreSequenceEqual([Pad(0, 3), Pad(480, 4), Pad(960, 5)], after.PartOf(Drums)![Expert].Notes.ToList());
        Assert.IsEmpty(after.PartOf(Drums)!.TomMarkers);
        Assert.AreSequenceEqual(
            [
                new DrumConversionNote(Expert, 0, Yellow, true, DrumConversionOutcome.LeftOut),
                new DrumConversionNote(Expert, 480, Blue, false, DrumConversionOutcome.LeftOut),
                new DrumConversionNote(Expert, 960, FifthPad, false, DrumConversionOutcome.LeftOut),
            ],
            command.Report!.Notes.ToList());
        Assert.IsFalse(command.Report.FromPlainFourLane);
    }

    [TestMethod]
    public void ConvertingFiveLaneToProLeavesOutGreenUnderOrangeAndTheHardestDifficultyDecidesCymbalOrTom()
    {
        var chart = DrumsChart(DrumsVariant.FiveLane);
        Add(chart, Expert, Pad(0, Green), Pad(0, FifthPad), Pad(480, Green), Pad(960, Blue));
        Add(chart, Hard, Pad(480, FifthPad), Pad(960, Blue));
        var command = new ConvertDrums(DrumsVariant.Pro);

        var after = AppliesRevertsAndRedoes(chart, command);

        var drums = after.PartOf(Drums)!;
        Assert.AreSequenceEqual([Pad(0, Green), Pad(480, Green), Pad(960, Blue)], drums[Expert].Notes.ToList());
        Assert.AreSequenceEqual([Pad(480, Green), Pad(960, Blue)], drums[Hard].Notes.ToList());
        Assert.AreSequenceEqual([new TomMarker(Blue, 960, 1)], drums.TomMarkers.ToList());
        Assert.AreSequenceEqual(
            [
                new DrumConversionNote(Expert, 0, FifthPad, false, DrumConversionOutcome.LeftOut),
                new DrumConversionNote(Hard, 480, FifthPad, false, DrumConversionOutcome.CymbalOrTomChanged),
            ],
            command.Report!.Notes.ToList());
    }

    [TestMethod]
    public void ConvertingPlainFourLaneSaysSoAndSwitchingFourLaneAndProIsNoConversion()
    {
        var chart = DrumsChart(DrumsVariant.FourLane);
        Add(chart, Expert, Pad(0, Blue));
        var command = new ConvertDrums(DrumsVariant.FiveLane);

        Refuses<InvalidOperationException>(chart, new ConvertDrums(DrumsVariant.Pro));
        Assert.IsInstanceOfType<SetDrumsVariant>(DrumVariantChange.For(chart, DrumsVariant.Pro));
        Assert.IsInstanceOfType<ConvertDrums>(DrumVariantChange.For(chart, DrumsVariant.FiveLane));
        Assert.IsInstanceOfType<SetDrumsVariant>(DrumVariantChange.For(DrumsChart(DrumsVariant.Pro), DrumsVariant.FiveLane));
        var after = AppliesRevertsAndRedoes(chart, command);

        Assert.IsTrue(command.Report!.FromPlainFourLane);
        Assert.AreSequenceEqual([Pad(0, 4)], after.PartOf(Drums)![Expert].Notes.ToList());
    }

    // Fills, big rock endings and [coda].

    [TestMethod]
    public void AddingCodaBeforeAFillMakesItABigRockEndingInTheSameStep()
    {
        var chart = DrumsChart();
        new AddPhrase(Drums, new Phrase(PhraseKind.DrumFill, 1920, 480)).Apply(chart);

        var after = AppliesRevertsAndRedoes(chart, new AddTextEvent(null, TextEvent.Of(960, "[coda]")));

        Assert.AreSequenceEqual([new Phrase(PhraseKind.BigRockEnding, 1920, 480)], after.PartOf(Drums)!.Phrases.ToList());
    }

    [TestMethod]
    public void RemovingOrMovingTheFirstCodaMakesWhatTheNextOneDecides()
    {
        var chart = DrumsChart();
        new AddTextEvent(null, TextEvent.Of(960, "[coda]")).Apply(chart);
        new AddTextEvent(null, TextEvent.Of(2400, "[coda]")).Apply(chart);
        new AddPhrase(Drums, new Phrase(PhraseKind.BigRockEnding, 1920, 480)).Apply(chart);

        var removed = AppliesRevertsAndRedoes(chart.Clone(), new RemoveTextEvent(null, TextEvent.Of(960, "[coda]")));
        var moved = AppliesRevertsAndRedoes(chart.Clone(), new ChangeTextEvent(null, TextEvent.Of(960, "[coda]"), TextEvent.Of(2880, "[coda]")));

        Assert.AreEqual(PhraseKind.DrumFill, removed.PartOf(Drums)!.Phrases.Single().Kind);
        Assert.AreEqual(PhraseKind.DrumFill, moved.PartOf(Drums)!.Phrases.Single().Kind);
    }

    [TestMethod]
    public void AFillAfterCodaOrABigRockEndingBeforeItIsRefusedAndAMovedFillBecomesOne()
    {
        var chart = DrumsChart();
        new AddTextEvent(null, TextEvent.Of(960, "[coda]")).Apply(chart);
        new AddPhrase(Drums, new Phrase(PhraseKind.DrumFill, 0, 480)).Apply(chart);

        Refuses<InvalidOperationException>(chart, new AddPhrase(Drums, new Phrase(PhraseKind.DrumFill, 1920, 480)));
        Refuses<InvalidOperationException>(chart, new AddPhrase(Drums, new Phrase(PhraseKind.BigRockEnding, 480, 480)));
        Refuses<InvalidOperationException>(chart, new ChangePhrase(Drums, new Phrase(PhraseKind.DrumFill, 0, 480), new Phrase(PhraseKind.DrumFill, 1920, 480)));
        var after = AppliesRevertsAndRedoes(chart, new ChangePhrase(Drums, new Phrase(PhraseKind.DrumFill, 0, 480), new Phrase(PhraseKind.BigRockEnding, 1920, 480)));

        Assert.AreEqual(PhraseKind.BigRockEnding, after.PartOf(Drums)!.Phrases.Single().Kind);
        Assert.AreEqual(PhraseKind.BigRockEnding, DrumFills.KindAt(after, 1920));
    }

    [TestMethod]
    public void AFillReTickedOntoCodaBecomesABigRockEndingAndUndoingPutsItBack()
    {
        // At 192 ticks per beat, ticks 2 and 3 of 480 both land on tick 1.
        var chart = DrumsChart();
        new AddTextEvent(null, TextEvent.Of(3, "[coda]")).Apply(chart);
        new AddPhrase(Drums, new Phrase(PhraseKind.DrumFill, 2, 480)).Apply(chart);

        var after = AppliesRevertsAndRedoes(chart, new ChangeResolution(192));

        Assert.AreEqual(DrumFills.FirstCoda(after), after.PartOf(Drums)!.Phrases.Single().Tick);
        Assert.AreEqual(PhraseKind.BigRockEnding, after.PartOf(Drums)!.Phrases.Single().Kind);
    }

    // Saving 4-lane drums.

    [TestMethod]
    public void FourLaneDrumsAreSavedWithoutTheirTomMarkersAndShownSo()
    {
        // Charter-spec C6 item 9: the games would read any marker as Pro.
        var chart = DrumsChart();
        Add(chart, Expert, Pad(0, Blue), Pad(480, Blue));
        new SetToms([(Blue, 0L)], tom: true).Apply(chart);
        new SetDrumsVariant(DrumsVariant.FourLane).Apply(chart);
        var after = MidiChartWriter.SettingsAfterSave(chart, ChartReadOptions.Default);

        var result = MidiChartWriter.WriteChecked(chart, after);

        Assert.IsTrue(result.IsValid, DescribeModel(result.ModelDifferences));
        Assert.IsEmpty(result.Saved.PartOf(Drums)!.TomMarkers);
        Assert.HasCount(1, chart.PartOf(Drums)!.TomMarkers);
        Assert.IsFalse(MidiChartReader.Read(result.Bytes, after).IsProDrums);
        Assert.IsTrue(Game(chart, Expert).All(n => !IsCymbal(n)));
    }

    internal static EditableChart DrumsChart(DrumsVariant variant = DrumsVariant.Pro) =>
        EditableChart.CreateNew(new NewChartSettings("Song", 480, 120, 4, 4, new HashSet<ChartInstrument> { Drums }, variant));

    internal static void Add(EditableChart chart, ChartDifficulty difficulty, params EditableNote[] notes) =>
        new AddNotes(Drums, difficulty, notes).Apply(chart);

    internal static EditableNote Pad(long tick, int lane) => new(tick, EditableNote.PlainLength, lane);

    internal static IReadOnlyList<ChartNote> Game(EditableChart chart, ChartDifficulty difficulty) =>
        GameView.Project(chart, new ChartTrackKey(Drums, difficulty), MidiChartWriter.SettingsAfterSave(chart, ChartReadOptions.Default)).Notes;

    internal static bool IsCymbal(ChartNote note) => (note.Flags & NoteFlags.Cymbal) != 0;

    // Markers written "tick:length;…", on blue.
    private static IEnumerable<TomMarker> Markers(string text) =>
        text.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(m =>
        {
            var parts = m.Split(':');
            return new TomMarker(Blue, long.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture), long.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));
        });

    // Mixes written "tick:configuration:setting;…".
    private static IEnumerable<DrumMix> Mixes(string text) =>
        text.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(m =>
        {
            var parts = m.Split(':');
            return new DrumMix(long.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture), int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture), parts[2]);
        });
}
